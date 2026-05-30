using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FoxSense.Core;
using FoxSense.Game;
using static FoxSense.Features.SkinDatabase;

namespace FoxSense.Features;

/// <summary>
/// CS2 external skin changer.
/// Key fixes vs previous build:
///  - Weapon ptr/count read order corrected (was swapped)
///  - CleanupAllocations now zeros game-memory pointers before freeing (stops-working fix)
///  - Mesh mask reduced from 700-write loop to 3 writes (crash fix)
///  - Thread.Sleep removed from hot path
///  - sig-scan retries if it failed first time
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
    private const int m_pChild        = 0x40;
    private const int m_pNextSibling  = 0x48;
    private const int m_pOwner        = 0x30;

    // Dirty-model-data offsets for mesh mask
    private const int m_pDirtyModelData    = 0xD8;
    private const int m_DirtyMeshGroupMask = 0x10;

    // Sig-scanned function
    private long _regenerateSkinsFn;
    private bool _initialized;

    // Attribute struct size (CEconItemAttribute = 0x48 bytes)
    private const int ATTR_SIZE = 0x48;

    // Track (item-address-in-game, remote-block-address) pairs for proper cleanup
    private readonly List<(long ItemAddr, long BlockAddr)> _allocatedBlocks = new();

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
        => _weaponSkins.TryGetValue(weaponDefIndex, out var s) ? s : null;

    // ═══════════════════════════════════════════════════
    //  INITIALIZATION  (sig scan + code patch, once)
    // ═══════════════════════════════════════════════════

    private void Initialize(Memory mem)
    {
        // Don't mark initialized until we actually have the function address
        int clientSize = mem.GetModuleSize("client.dll");
        _regenerateSkinsFn = mem.SigScan(mem.ClientBase, clientSize,
            "48 83 EC ?? E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B 10");

        LogDebug($"[SKIN] RegenerateWeaponSkins = 0x{_regenerateSkinsFn:X}");

        if (_regenerateSkinsFn == 0)
        {
            LogDebug("[SKIN] Sig scan failed — will retry next tick");
            return; // _initialized stays false, retry next tick
        }

        // Patch: tell RegenerateWeaponSkins where our attribute list is
        ushort patchValue = (ushort)(SkinOffsets.m_AttributeManager + SkinOffsets.m_Item
                                     + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes);
        mem.Write<ushort>(_regenerateSkinsFn + 0x52, patchValue);
        LogDebug($"[SKIN] Patched +0x52 = 0x{patchValue:X}");

        _initialized = true; // only set after success
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

            if (_regenerateSkinsFn == 0) return;

            long localPawn = state.LocalPawn;
            if (localPawn == 0) return;

            int health = mem.Read<int>(localPawn + Offsets.m_iHealth);
            if (health <= 0)
            {
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

                // ForceUpdate: reset IDHigh to 0 so we re-apply
                if (ForceUpdate)
                    mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0);

                // Already applied — skip
                if (mem.Read<uint>(item + SkinOffsets.m_iItemIDHigh) == 0xFFFFFFFF)
                    continue;

                // Mark as applied immediately
                mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0xFFFFFFFF);

                ushort defIndex = mem.Read<ushort>(item + SkinOffsets.m_iItemDefinitionIndex);
                if (defIndex == 0) continue;

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

                // Write fallback values
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, skin.PaintKit);
                mem.Write<float>(weapon + SkinOffsets.m_flFallbackWear, 0.01f);
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackSeed, 0);

                // Mesh mask (legacy = 2, normal = 1)
                ulong mask = skin.LegacyModel ? 2UL : 1UL;
                SetMeshMask(mem, weapon, mask);

                long hudWeapon = GetHudWeapon(mem, localPawn, weapon);
                if (hudWeapon != 0)
                    SetMeshMask(mem, hudWeapon, mask);

                // Write attribute list entries
                CreateAttributes(mem, item, skin.PaintKit);

                LogDebug($"[SKIN] def={defIndex} PK={skin.PaintKit} mask={mask}");
                shouldUpdate = true;
            }

            if (shouldUpdate || ForceUpdate)
                UpdateWeapons(mem, weapons);

            ForceUpdate = false;
        }
        catch (Exception ex)
        {
            LogDebug($"[SKIN] Tick error: {ex.Message}");
            CleanupAllocations(mem);
        }
    }

    // ═══════════════════════════════════════════════════
    //  UPDATE WEAPONS
    // ═══════════════════════════════════════════════════

    private void UpdateWeapons(Memory mem, List<long> weapons)
    {
        // Call RegenerateWeaponSkins in the game process
        mem.CallThread(_regenerateSkinsFn);
        LogDebug("[SKIN] Called RegenerateWeaponSkins");

        // Cleanup MUST happen right after — zero game pointers then free blocks
        CleanupAllocations(mem);

        // Reset fallback paint kit sentinel (so it doesn't stay permanently)
        foreach (long weapon in weapons)
        {
            if (weapon == 0 || weapon < 0x10000) continue;
            try { mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, -1); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════
    //  CLEANUP
    //  BUG FIX: must zero game-memory pointers BEFORE freeing the remote block,
    //  otherwise CreateAttributes sees stale non-zero ptr and permanently skips.
    // ═══════════════════════════════════════════════════

    private void CleanupAllocations(Memory mem)
    {
        foreach (var (itemAddr, blockAddr) in _allocatedBlocks)
        {
            // 1. Zero the game-side attribute list vector so CreateAttributes
            //    doesn't think attributes already exist next application
            if (itemAddr > 0x10000)
            {
                try
                {
                    long attrListAddr = itemAddr + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
                    mem.Write<long>(attrListAddr, 0);       // size = 0
                    mem.Write<long>(attrListAddr + 8, 0);   // ptr = null
                }
                catch { }
            }

            // 2. Free the remote memory block
            if (blockAddr > 0x10000)
                try { mem.FreeRemote(blockAddr); } catch { }
        }
        _allocatedBlocks.Clear();
    }

    // ═══════════════════════════════════════════════════
    //  ATTRIBUTE LIST
    // ═══════════════════════════════════════════════════

    private void CreateAttributes(Memory mem, long item, int paintKit)
    {
        long attrListAddr = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
        long existingSize = mem.Read<long>(attrListAddr);
        long existingPtr  = mem.Read<long>(attrListAddr + 8);

        // Already has attributes (not yet cleaned up from this cycle)
        if (existingSize != 0 || existingPtr != 0) return;

        int numAttrs = 3;
        long memBlock = mem.AllocateRemote((uint)(numAttrs * ATTR_SIZE));
        if (memBlock == 0) return;

        // Track with item address so cleanup can zero game memory
        _allocatedBlocks.Add((item, memBlock));

        // Paint kit (defIndex 6)
        WriteAttr(mem, memBlock + 0 * ATTR_SIZE, 6, (float)paintKit);
        // Seed/pattern (defIndex 7)
        WriteAttr(mem, memBlock + 1 * ATTR_SIZE, 7, 0f);
        // Wear (defIndex 8)
        WriteAttr(mem, memBlock + 2 * ATTR_SIZE, 8, 0.01f);

        // Write size + ptr
        mem.Write<long>(attrListAddr, numAttrs);
        mem.Write<long>(attrListAddr + 8, memBlock);
    }

    private static void WriteAttr(Memory mem, long addr, ushort defIndex, float value)
    {
        byte[] zeros = new byte[ATTR_SIZE];
        mem.WriteBytes(addr, zeros);
        mem.Write<ushort>(addr + 0x30, defIndex);
        mem.Write<float>(addr + 0x34, value);
        mem.Write<float>(addr + 0x38, value);
    }

    // ═══════════════════════════════════════════════════
    //  MESH MASK
    //  FIX: removed 700-write loop (caused crashes).
    //  3 writes is enough to win the race with the network system.
    // ═══════════════════════════════════════════════════

    private static void SetMeshMask(Memory mem, long entity, ulong mask)
    {
        long sceneNode = mem.Read<long>(entity + SkinOffsets.m_pGameSceneNode_skin);
        if (sceneNode == 0 || sceneNode < 0x10000) return;

        long modelState = sceneNode + SkinOffsets.m_modelState_skin;

        // Dirty data mask
        long dirtyAttr = mem.Read<long>(modelState + m_pDirtyModelData);
        if (dirtyAttr > 0x10000)
            mem.Write<ulong>(dirtyAttr + m_DirtyMeshGroupMask, mask);

        // Write the mesh group mask — 3 writes wins the race without causing crashes
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
    }

    // ═══════════════════════════════════════════════════
    //  HUD WEAPON LOOKUP
    // ═══════════════════════════════════════════════════

    private static long GetHudWeapon(Memory mem, long localPawn, long weapon)
    {
        int armsHandle = mem.Read<int>(localPawn + m_hHudModelArms);
        if (armsHandle == 0 || armsHandle == -1) return 0;

        long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
        if (entityList == 0) return 0;

        long armsBase = EntityResolver.ResolvePawn(mem, entityList, armsHandle);
        if (armsBase == 0) return 0;

        long armsNode = mem.Read<long>(armsBase + SkinOffsets.m_pGameSceneNode_skin);
        if (armsNode == 0 || armsNode < 0x10000) return 0;

        long viewModel = mem.Read<long>(armsNode + m_pChild);
        int iterations = 0;
        while (viewModel > 0x10000 && iterations++ < 32)
        {
            long owner = mem.Read<long>(viewModel + m_pOwner);
            if (owner > 0x10000)
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
    //  FIX: CNetworkUtlVectorBase layout is ptr@+0, count@+8 (int, not long).
    //  Previous code had them swapped, so weapons were never found.
    // ═══════════════════════════════════════════════════

    private static List<long> GetWeaponEntities(Memory mem, long weaponServices)
    {
        var result = new List<long>(16);

        long arrayBase   = weaponServices + SkinOffsets.m_hMyWeapons;
        long weaponEntry = mem.Read<long>(arrayBase);       // ptr to handle array  (was wrongly read as count)
        int  weaponCount = mem.Read<int>(arrayBase + 8);    // element count as int (was wrongly read as ptr)

        if (weaponCount <= 0 || weaponCount > 64 || weaponEntry < 0x10000)
            return result;

        long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
        if (entityList == 0) return result;

        for (int i = 0; i < weaponCount; i++)
        {
            int handle = mem.Read<int>(weaponEntry + i * 4);
            if (handle == 0 || handle == -1) continue;
            long entity = EntityResolver.ResolvePawn(mem, entityList, handle);
            if (entity > 0x10000)
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

        long gloveItem = localPawn + SkinOffsets.m_EconGloves;

        ushort currentDef = mem.Read<ushort>(gloveItem + SkinOffsets.m_iItemDefinitionIndex);
        if (currentDef == SelectedGlove.DefIndex) return;

        mem.Write<ushort>(gloveItem + SkinOffsets.m_iItemDefinitionIndex, SelectedGlove.DefIndex);
        mem.Write<int>(gloveItem + SkinOffsets.m_iEntityQuality, 4);
        mem.Write<uint>(gloveItem + SkinOffsets.m_iItemIDHigh, 0xFFFFFFFF);
        mem.Write<bool>(gloveItem + SkinOffsets.m_bInitialized, true);
        mem.Write<bool>(localPawn + SkinOffsets.m_bNeedToReApplyGloves, true);

        LogDebug($"[SKIN] Glove def={SelectedGlove.DefIndex}");
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
