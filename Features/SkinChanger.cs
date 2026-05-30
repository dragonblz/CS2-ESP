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
    private const int ATTR_SIZE     = 0x48;
    private const int MAX_WEAPONS   = 16;
    private const int ATTRS_PER_WPN = 3;

    // One persistent remote buffer — allocated once, NEVER freed during gameplay.
    // Freeing attr memory while the engine's material system still reads it = crash.
    private long _attrBuffer;   // base of remote allocation
    private int  _attrSlot;     // next free slot index (reset each tick)

    // Track which game item-addresses we wrote attribute pointers into,
    // so we can zero them after RegenerateWeaponSkins finishes.
    private readonly List<long> _writtenItemAddrs = new();

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
        int clientSize = mem.GetModuleSize("client.dll");
        _regenerateSkinsFn = mem.SigScan(mem.ClientBase, clientSize,
            "48 83 EC ?? E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B 10");

        LogDebug($"[SKIN] RegenerateWeaponSkins = 0x{_regenerateSkinsFn:X}");

        if (_regenerateSkinsFn == 0)
        {
            LogDebug("[SKIN] Sig scan failed — will retry next tick");
            return;
        }

        // Patch offset inside function so it reads from our attr list
        ushort patchValue = (ushort)(SkinOffsets.m_AttributeManager + SkinOffsets.m_Item
                                     + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes);
        mem.Write<ushort>(_regenerateSkinsFn + 0x52, patchValue);
        LogDebug($"[SKIN] Patched +0x52 = 0x{patchValue:X}");

        // Allocate a single persistent remote buffer for attribute structs.
        // Large enough for MAX_WEAPONS weapons × ATTRS_PER_WPN attributes each.
        uint bufSize = (uint)(MAX_WEAPONS * ATTRS_PER_WPN * ATTR_SIZE);
        _attrBuffer = mem.AllocateRemote(bufSize);
        LogDebug($"[SKIN] Attr buffer @ 0x{_attrBuffer:X} ({bufSize} bytes)");

        _initialized = (_attrBuffer != 0); // only mark ready if both succeeded
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

            _attrSlot = 0;           // reset slot counter each tick
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

            // Only call RegenerateWeaponSkins if we actually applied a skin this tick
            if (shouldUpdate)
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
        mem.CallThread(_regenerateSkinsFn);
        LogDebug("[SKIN] Called RegenerateWeaponSkins");

        // Zero the game-side attribute list pointers ONLY.
        // Do NOT free _attrBuffer — the engine may still be reading it
        // in background threads even after the remote thread returns.
        ZeroGameAttrPointers(mem);

        // Reset fallback paint kit sentinel
        foreach (long weapon in weapons)
        {
            if (weapon < 0x10000) continue;
            try { mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit, -1); } catch { }
        }
    }

    // ═══════════════════════════════════════════════════
    //  ATTR POINTER CLEANUP  (zeros game-side pointers, does NOT free remote buffer)
    // ═══════════════════════════════════════════════════

    private void ZeroGameAttrPointers(Memory mem)
    {
        foreach (long itemAddr in _writtenItemAddrs)
        {
            if (itemAddr < 0x10000) continue;
            try
            {
                long addr = itemAddr + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;
                mem.Write<long>(addr,     0);   // size  = 0
                mem.Write<long>(addr + 8, 0);   // ptr   = null
            }
            catch { }
        }
        _writtenItemAddrs.Clear();
    }

    // Also called on death so we don't leave stale pointers
    private void CleanupAllocations(Memory mem) => ZeroGameAttrPointers(mem);

    // ═══════════════════════════════════════════════════
    //  ATTRIBUTE LIST
    // ═══════════════════════════════════════════════════

    private void CreateAttributes(Memory mem, long item, int paintKit)
    {
        if (_attrBuffer == 0) return;                       // buffer not ready
        if (_attrSlot >= MAX_WEAPONS) return;               // ran out of slots

        long attrListAddr = item + SkinOffsets.m_AttributeList + SkinOffsets.m_Attributes;

        // Don't double-write the same item in one tick
        long existingPtr = mem.Read<long>(attrListAddr + 8);
        if (existingPtr != 0) return;

        // Grab next available slot in our persistent buffer
        long slotBase = _attrBuffer + (long)_attrSlot * ATTRS_PER_WPN * ATTR_SIZE;
        _attrSlot++;

        // Write the three attribute entries (paint=6, seed=7, wear=8)
        WriteAttr(mem, slotBase + 0 * ATTR_SIZE, 6, (float)paintKit);
        WriteAttr(mem, slotBase + 1 * ATTR_SIZE, 7, 0f);
        WriteAttr(mem, slotBase + 2 * ATTR_SIZE, 8, 0.01f);

        // Point the game's attribute list at our buffer slot
        mem.Write<long>(attrListAddr,     ATTRS_PER_WPN);   // count
        mem.Write<long>(attrListAddr + 8, slotBase);        // ptr

        // Remember this item address so we can zero the ptr after the call
        _writtenItemAddrs.Add(item);
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
    //  m_hMyWeapons is a fixed embedded handle array inside weapon services
    //  (not a ptr+count vector). Iterate up to 64 slots directly.
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
