using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FoxSense.Core;
using FoxSense.Game;
using static FoxSense.Features.SkinDatabase;

namespace FoxSense.Features;

/// <summary>
/// CS2 external skin changer — full port of wompwomp6/cs2-skin-changer.
/// 1. Patches RegenerateWeaponSkins at startup (code patch)
/// 2. Creates attribute list entries before calling
/// 3. Calls RegenerateWeaponSkins via CreateRemoteThread
/// 4. Removes attribute list entries immediately after (prevents crash)
/// </summary>
public class SkinChanger
{
    public bool Enabled { get; set; }
    private readonly Dictionary<ushort, SkinInfo> _weaponSkins = new();
    public KnifeInfo? SelectedKnife { get; set; }
    public SkinInfo? SelectedKnifeSkin { get; set; }
    public GloveInfo? SelectedGlove { get; set; }
    public SkinInfo? SelectedGloveSkin { get; set; }
    public bool ForceUpdate { get; set; }

    private static readonly HashSet<ushort> DefaultKnives = new() { 42, 59 };

    // Scene node offsets for HUD weapon lookup
    private const int m_hHudModelArms = 0x1B58;
    private const int m_pChild = 0x40;
    private const int m_pNextSibling = 0x48;
    private const int m_pOwner = 0x30;

    // Sig-scanned function
    private long _regenerateSkinsFn;
    private bool _initialized;

    // Attribute struct size (CEconItemAttribute = 0x48 bytes)
    private const int ATTR_SIZE = 0x48;

    // Track remote memory allocations for safe cleanup
    private readonly List<long> _allocatedBlocks = new();

    public void SetWeaponSkin(ushort weaponDefIndex, SkinInfo skin)
    {
        _weaponSkins[weaponDefIndex] = skin;
        ForceUpdate = true;
    }

    public void ClearWeaponSkin(ushort weaponDefIndex)
    {
        _weaponSkins.Remove(weaponDefIndex);
        ForceUpdate = true;
    }

    public SkinInfo? GetConfiguredSkin(ushort weaponDefIndex)
    {
        return _weaponSkins.TryGetValue(weaponDefIndex, out var skin) ? skin : null;
    }

    // ═══════════════════════════════════════════════════
    //  INITIALIZATION (once)
    // ═══════════════════════════════════════════════════

    private void Initialize(Memory mem)
    {
        _initialized = true;

        // 1. Sig scan RegenerateWeaponSkins
        int clientSize = mem.GetModuleSize("client.dll");
        _regenerateSkinsFn = mem.SigScan(mem.ClientBase, clientSize,
            "48 83 EC ?? E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B 10");
        LogDebug($"[SKIN] RegenerateWeaponSkins = 0x{_regenerateSkinsFn:X}");

        if (_regenerateSkinsFn == 0) return;

        // 2. Patch the function to read from our attribute list offset
        // Reference: mem.Write<uint16_t>(Sigs::RegenerateWeaponSkins + 0x52,
        //     m_AttributeManager + m_Item + m_AttributeList + m_Attributes);
        ushort patchValue = (ushort)(SkinOffsets.m_AttributeManager + SkinOffsets.m_Item
                                     + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes);
        mem.Write<ushort>(_regenerateSkinsFn + 0x52, patchValue);
        LogDebug($"[SKIN] Patched RegenerateWeaponSkins+0x52 = 0x{patchValue:X}");
    }

    // ═══════════════════════════════════════════════════
    //  MAIN TICK
    // ═══════════════════════════════════════════════════

    public void Tick(Memory mem, GameState state)
    {
        if (!Enabled || !mem.IsAttached || !mem.HasWriteAccess || !state.InGame)
            return;

        try
        {
            if (!_initialized)
                Initialize(mem);

            long localPawn = state.LocalPawn;
            if (localPawn == 0) return;

            int health = mem.Read<int>(localPawn + Offsets.m_iHealth);
            if (health <= 0)
            {
                // Player is dead — clean up any pending allocations
                CleanupAllocations(mem);
                return;
            }

            long weaponServices = mem.Read<long>(localPawn + SkinOffsets.m_pWeaponServices);
            if (weaponServices == 0 || weaponServices < 0x10000) return;

            bool shouldUpdate = false;
            var weapons = GetWeaponEntities(mem, weaponServices);

            foreach (long weapon in weapons)
            {
                if (weapon == 0 || weapon < 0x10000) continue;

                long item = weapon + SkinOffsets.m_AttributeManager + SkinOffsets.m_Item;

                // ForceUpdate: reset m_iItemIDHigh to 0 to trigger re-apply
                if (ForceUpdate)
                    mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0);

                // Already applied? Skip.
                if (mem.Read<uint>(item + SkinOffsets.m_iItemIDHigh) == 0xFFFFFFFF)
                    continue;

                // Mark as applied
                mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0xFFFFFFFF);

                ushort defIndex = mem.Read<ushort>(item + SkinOffsets.m_iItemDefinitionIndex);
                if (defIndex == 0) continue;

                // Look up skin for this weapon
                SkinInfo? skin = null;

                if (DefaultKnives.Contains(defIndex))
                {
                    if (SelectedKnifeSkin != null && SelectedKnifeSkin.PaintKit != 0)
                        skin = SelectedKnifeSkin;
                    else
                        continue;
                }
                else
                {
                    skin = GetConfiguredSkin(defIndex);
                }

                if (skin == null || skin.PaintKit == 0) continue;

                // Write fallback paint kit
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, skin.PaintKit);

                // Mesh mask (legacy model = 2, normal = 1)
                ulong mask = (ulong)(skin.LegacyModel ? 2 : 1);
                SetMeshMask(mem, weapon, mask);

                // Also set HUD weapon mesh mask
                long hudWeapon = GetHudWeapon(mem, localPawn, weapon);
                if (hudWeapon != 0)
                    SetMeshMask(mem, hudWeapon, mask);

                // Create attribute list entries
                CreateAttributes(mem, item, skin.PaintKit);

                LogDebug($"[SKIN] Applied PK={skin.PaintKit} def={defIndex} mask={mask}");
                shouldUpdate = true;
            }

            // Call RegenerateWeaponSkins + cleanup
            if (shouldUpdate || ForceUpdate)
                UpdateWeapons(mem, weapons);

            ForceUpdate = false;
        }
        catch (Exception ex)
        {
            LogDebug($"[SKIN] Error: {ex.Message}");
            CleanupAllocations(mem);
        }
    }

    // ═══════════════════════════════════════════════════
    //  UPDATE WEAPONS (matching reference UpdateWeapons)
    // ═══════════════════════════════════════════════════

    private void UpdateWeapons(Memory mem, List<long> weapons)
    {
        // Call RegenerateWeaponSkins
        if (_regenerateSkinsFn != 0)
        {
            mem.CallThread(_regenerateSkinsFn);
            LogDebug("[SKIN] Called RegenerateWeaponSkins");
        }

        // IMMEDIATELY clean up all remote allocations
        // This MUST happen right after the call, before death can destroy entities
        CleanupAllocations(mem);

        // Reset fallback paint kit
        foreach (long weapon in weapons)
        {
            if (weapon == 0 || weapon < 0x10000) continue;
            try { mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, -1); } catch { }
        }
    }

    /// <summary>
    /// Safely frees all tracked remote memory allocations and zeros attribute pointers.
    /// </summary>
    private void CleanupAllocations(Memory mem)
    {
        foreach (long block in _allocatedBlocks)
        {
            if (block > 0x10000)
            {
                try { mem.FreeRemote(block); } catch { }
            }
        }
        _allocatedBlocks.Clear();
    }

    // ═══════════════════════════════════════════════════
    //  ATTRIBUTE LIST (matching reference econItemAttributeManager)
    // ═══════════════════════════════════════════════════

    private void CreateAttributes(Memory mem, long item, int paintKit)
    {
        // Read existing
        long attrListAddr = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
        long existingSize = mem.Read<long>(attrListAddr);
        long existingPtr = mem.Read<long>(attrListAddr + 8);

        // Don't overwrite existing attributes
        if (existingSize != 0 || existingPtr != 0) return;

        // Allocate remote memory for 3 attributes
        int numAttrs = 3;
        long memBlock = mem.AllocateRemote((uint)(numAttrs * ATTR_SIZE));
        if (memBlock == 0) return;

        // Track for cleanup
        _allocatedBlocks.Add(memBlock);

        // Paint (defIndex=6)
        WriteAttr(mem, memBlock + 0 * ATTR_SIZE, 6, (float)paintKit);
        // Pattern (defIndex=7)
        WriteAttr(mem, memBlock + 1 * ATTR_SIZE, 7, 0f);
        // Wear (defIndex=8)
        WriteAttr(mem, memBlock + 2 * ATTR_SIZE, 8, 0.01f);

        // Write CPtrGameVector {size, ptr}
        mem.Write<long>(attrListAddr, numAttrs);
        mem.Write<long>(attrListAddr + 8, memBlock);
    }

    private void RemoveAttributes(Memory mem, long item)
    {
        long attrListAddr = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
        long size = mem.Read<long>(attrListAddr);
        long ptr = mem.Read<long>(attrListAddr + 8);

        if (size == 0) return;

        // Clear the vector
        mem.Write<long>(attrListAddr, 0);
        mem.Write<long>(attrListAddr + 8, 0);

        // Free remote memory
        if (ptr > 0x10000)
            mem.FreeRemote(ptr);
    }

    private static void WriteAttr(Memory mem, long addr, ushort defIndex, float value)
    {
        byte[] zeros = new byte[ATTR_SIZE];
        mem.WriteBytes(addr, zeros);
        mem.Write<ushort>(addr + 0x30, defIndex);  // defIndex
        mem.Write<float>(addr + 0x34, value);       // value
        mem.Write<float>(addr + 0x38, value);       // initValue
    }

    // ═══════════════════════════════════════════════════
    //  MESH MASK (matching reference SetMeshMask — aggressive write)
    //  Reference uses 700-write loop because network sync overrides single writes
    // ═══════════════════════════════════════════════════

    // Dirty model data offsets (hardcoded in reference)
    private const int m_pDirtyModelData = 0xD8;
    private const int m_DirtyMeshGroupMask = 0x10;

    private static void SetMeshMask(Memory mem, long entity, ulong mask)
    {
        long sceneNode = mem.Read<long>(entity + SkinOffsets.m_pGameSceneNode_skin);
        if (sceneNode == 0 || sceneNode < 0x10000) return;

        long modelState = sceneNode + SkinOffsets.m_modelState_skin;

        // Write dirty model data mask (tells game the mesh needs updating)
        long dirtyAttributes = mem.Read<long>(modelState + m_pDirtyModelData);
        if (dirtyAttributes != 0 && dirtyAttributes > 0x10000)
            mem.Write<ulong>(dirtyAttributes + m_DirtyMeshGroupMask, mask);

        // Aggressive write loop — network sync keeps resetting, so we spam
        for (int i = 0; i < 700; i++)
            mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);

        // Verify
        Thread.Sleep(5);
        ulong current = mem.Read<ulong>(modelState + SkinOffsets.m_MeshGroupMask);
        if (current != mask)
        {
            // Retry once
            for (int i = 0; i < 700; i++)
                mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
        }
    }

    // ═══════════════════════════════════════════════════
    //  HUD WEAPON LOOKUP (matching reference GetHudWeapon)
    // ═══════════════════════════════════════════════════

    private static long GetHudWeapon(Memory mem, long localPawn, long weapon)
    {
        // Get HUD model arms entity
        int armsHandle = mem.Read<int>(localPawn + m_hHudModelArms);
        if (armsHandle == 0 || armsHandle == -1) return 0;

        long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
        if (entityList == 0) return 0;

        long armsBase = EntityResolver.ResolvePawn(mem, entityList, armsHandle);
        if (armsBase == 0) return 0;

        // Traverse scene node children to find the HUD weapon
        long armsNode = mem.Read<long>(armsBase + SkinOffsets.m_pGameSceneNode_skin);
        if (armsNode == 0 || armsNode < 0x10000) return 0;

        long viewModel = mem.Read<long>(armsNode + m_pChild);
        int iterations = 0;
        while (viewModel != 0 && viewModel > 0x10000 && iterations++ < 32)
        {
            long owner = mem.Read<long>(viewModel + m_pOwner);
            if (owner != 0 && owner > 0x10000)
            {
                int ownerHandle = mem.Read<int>(owner + SkinOffsets.m_hOwnerEntity);
                long ownerEntity = EntityResolver.ResolvePawn(mem, entityList, ownerHandle);
                if (ownerEntity == weapon)
                    return owner;
            }
            viewModel = mem.Read<long>(viewModel + m_pNextSibling);
        }
        return 0;
    }

    // ═══════════════════════════════════════════════════
    //  WEAPON ENUMERATION
    // ═══════════════════════════════════════════════════

    private static List<long> GetWeaponEntities(Memory mem, long weaponServices)
    {
        var result = new List<long>(16);
        long arrayBase = weaponServices + SkinOffsets.m_hMyWeapons;
        long weaponCount = mem.Read<long>(arrayBase);
        long weaponEntry = mem.Read<long>(arrayBase + 8);

        if (weaponCount <= 0 || weaponCount > 64 || weaponEntry < 0x10000)
            return result;

        long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
        if (entityList == 0) return result;

        for (long i = 0; i < weaponCount; i++)
        {
            int handle = mem.Read<int>(weaponEntry + i * 4);
            if (handle == 0 || handle == -1) continue;
            long entity = EntityResolver.ResolvePawn(mem, entityList, handle);
            if (entity != 0 && entity > 0x10000)
                result.Add(entity);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════
    //  GLOVE APPLICATION
    // ═══════════════════════════════════════════════════

    private void ApplyGlovesSafe(Memory mem, long localPawn)
    {
        if (SelectedGlove == null || SelectedGlove.DefIndex == 0) return;

        // m_EconGloves is a C_EconItemView embedded in the pawn
        long gloveItem = localPawn + SkinOffsets.m_EconGloves;

        // Only write once (check if already set)
        ushort currentDef = mem.Read<ushort>(gloveItem + SkinOffsets.m_iItemDefinitionIndex);
        if (currentDef == SelectedGlove.DefIndex) return;

        // Write glove definition
        mem.Write<ushort>(gloveItem + SkinOffsets.m_iItemDefinitionIndex, SelectedGlove.DefIndex);
        mem.Write<int>(gloveItem + SkinOffsets.m_iEntityQuality, 4); // 4 = glove quality
        mem.Write<uint>(gloveItem + SkinOffsets.m_iItemIDHigh, 0xFFFFFFFF);
        mem.Write<bool>(gloveItem + SkinOffsets.m_bInitialized, true);

        // Force glove re-apply
        mem.Write<bool>(localPawn + SkinOffsets.m_bNeedToReApplyGloves, true);

        LogDebug($"[SKIN] Glove set def={SelectedGlove.DefIndex}");
    }

    // ═══════════════════════════════════════════════════
    //  MURMURHASH2 (for m_nSubclassID)
    //  Reference uses STRINGTOKEN_MURMURHASH_SEED = 0x31415926
    // ═══════════════════════════════════════════════════

    private const uint MURMURHASH_SEED = 0x31415926;

    private static uint MurmurHash2(byte[] data)
    {
        const uint m = 0x5BD1E995;
        const int r = 24;
        uint len = (uint)data.Length;
        uint h = MURMURHASH_SEED ^ len;
        int i = 0;

        while (len >= 4)
        {
            uint k = BitConverter.ToUInt32(data, i);
            k *= m;
            k ^= k >> r;
            k *= m;
            h *= m;
            h ^= k;
            i += 4;
            len -= 4;
        }

        switch (len)
        {
            case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
            case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
            case 1: h ^= data[i]; h *= m; break;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }

    // ═══════════════════════════════════════════════════
    //  LOGGING
    // ═══════════════════════════════════════════════════

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FoxSense", "skin_debug.log");
    private static int _logCount;

    private static void LogDebug(string msg)
    {
        try
        {
            if (_logCount++ > 500) { _logCount = 0; File.WriteAllText(_logPath, ""); }
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
