using System.IO;
using FoxSense.Core;
using FoxSense.Game;
using static FoxSense.Features.SkinDatabase;

namespace FoxSense.Features;

/// <summary>
/// CS2 external skin changer.
///
/// Working flow per weapon:
///   1. Write m_nFallbackPaintKit / wear / seed
///   2. Write 3 CEconItemAttribute entries to VirtualAllocEx memory
///   3. Point weapon's attribute list at that memory
///   4. Call RegenerateWeaponSkins (patched to read our attr list)
///   5. Zero game-side attr pointer (so game cleanup won't follow stale ptr)
///   6. VirtualFreeEx the block
///   7. Write mesh mask × 3 (NO dirty-model-data write — offset 0xD8 is unverified
///      for current CS2 build and was causing random memory corruption / delayed crash)
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

    private const int m_hHudModelArms = 0x1B58;
    private const int m_pChild        = 0x40;
    private const int m_pNextSibling  = 0x48;
    private const int m_pOwner        = 0x30;

    // CEconItemAttribute struct size
    private const int ATTR_SIZE = 0x48;

    // Sig-scanned once
    private long _regenerateSkinsFn;
    private bool _initialized;

    // Per-tick tracking: (item game address, VirtualAllocEx block address)
    private readonly List<(long Item, long Block)> _pendingCleanup = new();

    public void SetWeaponSkin(ushort weaponDefIndex, SkinInfo skin)
    { _weaponSkins[weaponDefIndex] = skin; ForceUpdate = true; }

    public void ClearWeaponSkin(ushort weaponDefIndex)
    { _weaponSkins.Remove(weaponDefIndex); ForceUpdate = true; }

    public SkinInfo? GetConfiguredSkin(ushort weaponDefIndex)
        => _weaponSkins.TryGetValue(weaponDefIndex, out var s) ? s : null;

    // ═══════════════════════════════════════════════════
    //  INIT
    // ═══════════════════════════════════════════════════

    private void Initialize(Memory mem)
    {
        int sz = mem.GetModuleSize("client.dll");
        _regenerateSkinsFn = mem.SigScan(mem.ClientBase, sz,
            "48 83 EC ?? E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B 10");

        LogDebug($"[SKIN] RegenerateWeaponSkins @ 0x{_regenerateSkinsFn:X}");

        if (_regenerateSkinsFn != 0)
        {
            // Patch 2-byte displacement so function reads from
            // weapon + m_AttributeManager + m_Item + m_AttributeList + m_Attributes
            // (where we will write our attribute entries each tick)
            ushort patch = (ushort)(SkinOffsets.m_AttributeManager + SkinOffsets.m_Item
                                    + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes);
            mem.Write<ushort>(_regenerateSkinsFn + 0x52, patch);
            LogDebug($"[SKIN] Patched +0x52 = 0x{patch:X}");
        }

        _initialized = true;
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
            if (!_initialized) Initialize(mem);
            if (_regenerateSkinsFn == 0) return;

            long localPawn = state.LocalPawn;
            if (localPawn == 0) return;

            int health = mem.Read<int>(localPawn + Offsets.m_iHealth);
            if (health <= 0)
            {
                ZeroAndFreeAll(mem);
                return;
            }

            long weapSvc = mem.Read<long>(localPawn + SkinOffsets.m_pWeaponServices);
            if (weapSvc < 0x10000) return;

            bool didApply = false;
            var weapons = GetWeaponEntities(mem, weapSvc);

            foreach (long weapon in weapons)
            {
                if (weapon < 0x10000) continue;

                long item = weapon + SkinOffsets.m_AttributeManager + SkinOffsets.m_Item;

                if (ForceUpdate)
                    mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0);

                if (mem.Read<uint>(item + SkinOffsets.m_iItemIDHigh) == 0xFFFFFFFF)
                    continue;

                ushort def = mem.Read<ushort>(item + SkinOffsets.m_iItemDefinitionIndex);
                if (def == 0) continue;

                SkinInfo? skin = null;
                if (DefaultKnives.Contains(def))
                { if (SelectedKnifeSkin?.PaintKit != 0) skin = SelectedKnifeSkin; else continue; }
                else
                { skin = GetConfiguredSkin(def); }

                if (skin == null || skin.PaintKit == 0) continue;

                // ── Fallback values ──────────────────────────────────
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, skin.PaintKit);
                mem.Write<float>(weapon + SkinOffsets.m_flFallbackWear,  0.0001f);
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackSeed,     0);
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackStatTrak, -1);
                mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh,        0xFFFFFFFF);

                // ── Mesh mask (3 writes — no dirty-model-data write)  ─
                ulong mask = skin.LegacyModel ? 2UL : 1UL;
                SetMeshMask(mem, weapon, mask);
                long hudWeapon = GetHudWeapon(mem, localPawn, weapon);
                if (hudWeapon != 0) SetMeshMask(mem, hudWeapon, mask);

                // ── Attribute entries (paint=6, seed=7, wear=8) ───────
                WriteAttrList(mem, item, skin.PaintKit);

                LogDebug($"[SKIN] def={def} PK={skin.PaintKit}");
                didApply = true;
            }

            if (didApply)
            {
                mem.CallThread(_regenerateSkinsFn);
                LogDebug("[SKIN] Called RegenerateWeaponSkins");

                // Zero game ptrs first (so game cleanup won't follow them),
                // then free the remote blocks.
                ZeroAndFreeAll(mem);
            }

            ForceUpdate = false;
        }
        catch (Exception ex)
        {
            LogDebug($"[SKIN] Error: {ex.Message}");
            ZeroAndFreeAll(mem);
        }
    }

    // ═══════════════════════════════════════════════════
    //  ATTRIBUTE WRITING
    // ═══════════════════════════════════════════════════

    private void WriteAttrList(Memory mem, long item, int paintKit)
    {
        long attrAddr = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;

        // Don't overwrite a list we wrote this tick
        if (mem.Read<long>(attrAddr + 8) != 0) return;

        long block = mem.AllocateRemote((uint)(3 * ATTR_SIZE));
        if (block == 0) return;

        WriteAttr(mem, block + 0 * ATTR_SIZE, 6, (float)paintKit); // paint kit
        WriteAttr(mem, block + 1 * ATTR_SIZE, 7, 0f);               // seed
        WriteAttr(mem, block + 2 * ATTR_SIZE, 8, 0.0001f);          // wear

        mem.Write<long>(attrAddr,     3);     // count
        mem.Write<long>(attrAddr + 8, block); // ptr

        _pendingCleanup.Add((item, block));
    }

    private static void WriteAttr(Memory mem, long addr, ushort defIndex, float value)
    {
        mem.WriteBytes(addr, new byte[ATTR_SIZE]);       // zero the struct
        mem.Write<ushort>(addr + 0x30, defIndex);        // m_iAttributeDefinitionIndex
        mem.Write<float>(addr  + 0x34, value);           // m_flValue
        mem.Write<float>(addr  + 0x38, value);           // m_flInitialValue
    }

    // ═══════════════════════════════════════════════════
    //  CLEANUP — zero game ptr THEN free (order matters)
    // ═══════════════════════════════════════════════════

    private void ZeroAndFreeAll(Memory mem)
    {
        foreach (var (item, block) in _pendingCleanup)
        {
            // 1. Zero game-side pointer so game cleanup never follows a freed ptr
            if (item > 0x10000)
            {
                try
                {
                    long a = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
                    mem.Write<long>(a,     0); // count = 0
                    mem.Write<long>(a + 8, 0); // ptr   = null
                }
                catch { }
            }

            // 2. Now safe to free
            if (block > 0x10000)
                try { mem.FreeRemote(block); } catch { }
        }
        _pendingCleanup.Clear();
    }

    // ═══════════════════════════════════════════════════
    //  MESH MASK — 3 writes, NO dirty-model-data write
    //  (m_pDirtyModelData=0xD8 is not verified for current CS2
    //   and was causing random memory corruption when the pointer
    //   it read happened to be a plausible-looking bad address)
    // ═══════════════════════════════════════════════════

    private static void SetMeshMask(Memory mem, long entity, ulong mask)
    {
        long sceneNode = mem.Read<long>(entity + SkinOffsets.m_pGameSceneNode_skin);
        if (sceneNode < 0x10000) return;

        long ms = sceneNode + SkinOffsets.m_modelState_skin;
        mem.Write<ulong>(ms + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(ms + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(ms + SkinOffsets.m_MeshGroupMask, mask);
    }

    // ═══════════════════════════════════════════════════
    //  HUD WEAPON
    // ═══════════════════════════════════════════════════

    private static long GetHudWeapon(Memory mem, long localPawn, long weapon)
    {
        try
        {
            int armsHandle = mem.Read<int>(localPawn + m_hHudModelArms);
            if (armsHandle == 0 || armsHandle == -1) return 0;

            long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
            if (entityList == 0) return 0;

            long armsBase = EntityResolver.ResolvePawn(mem, entityList, armsHandle);
            if (armsBase < 0x10000) return 0;

            long armsNode = mem.Read<long>(armsBase + SkinOffsets.m_pGameSceneNode_skin);
            if (armsNode < 0x10000) return 0;

            long vm = mem.Read<long>(armsNode + m_pChild);
            int its = 0;
            while (vm > 0x10000 && its++ < 32)
            {
                long owner = mem.Read<long>(vm + m_pOwner);
                if (owner > 0x10000)
                {
                    int oh = mem.Read<int>(owner + SkinOffsets.m_hOwnerEntity);
                    long oe = EntityResolver.ResolvePawn(mem, entityList, oh);
                    if (oe == weapon) return owner;
                }
                vm = mem.Read<long>(vm + m_pNextSibling);
            }
        }
        catch { }
        return 0;
    }

    // ═══════════════════════════════════════════════════
    //  WEAPON ENUMERATION
    // ═══════════════════════════════════════════════════

    private static List<long> GetWeaponEntities(Memory mem, long weaponServices)
    {
        var result = new List<long>(16);

        long entityList = mem.Read<long>(mem.ClientAddr(Offsets.dwEntityList));
        if (entityList == 0) return result;

        long arrayBase = weaponServices + SkinOffsets.m_hMyWeapons;

        for (int i = 0; i < 64; i++)
        {
            int handle = mem.Read<int>(arrayBase + i * 4);
            if (handle == 0 || handle == -1) continue;
            long entity = EntityResolver.ResolvePawn(mem, entityList, handle);
            if (entity > 0x10000)
                result.Add(entity);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════
    //  GLOVES
    // ═══════════════════════════════════════════════════

    private void ApplyGlovesSafe(Memory mem, long localPawn)
    {
        if (SelectedGlove?.DefIndex == 0) return;
        long gi = localPawn + SkinOffsets.m_EconGloves;
        if (mem.Read<ushort>(gi + SkinOffsets.m_iItemDefinitionIndex) == SelectedGlove!.DefIndex) return;
        mem.Write<ushort>(gi + SkinOffsets.m_iItemDefinitionIndex, SelectedGlove.DefIndex);
        mem.Write<int>(gi + SkinOffsets.m_iEntityQuality, 4);
        mem.Write<uint>(gi + SkinOffsets.m_iItemIDHigh, 0xFFFFFFFF);
        mem.Write<bool>(gi + SkinOffsets.m_bInitialized, true);
        mem.Write<bool>(localPawn + SkinOffsets.m_bNeedToReApplyGloves, true);
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
