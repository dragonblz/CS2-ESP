using System.IO;
using FoxSense.Core;
using FoxSense.Game;
using static FoxSense.Features.SkinDatabase;

namespace FoxSense.Features;

/// <summary>
/// CS2 external skin changer — stable, no-crash implementation.
///
/// Approach: write m_nFallbackPaintKit + wear + mesh mask directly.
/// No CreateRemoteThread, no code patching, no VirtualAllocEx.
/// The game reads fallback values on every weapon model update tick.
/// ForceUpdate resets m_iItemIDHigh=0 which triggers a re-read.
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

    // Dirty-model-data offsets for mesh mask
    private const int m_pDirtyModelData    = 0xD8;
    private const int m_DirtyMeshGroupMask = 0x10;

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
    //  MAIN TICK
    // ═══════════════════════════════════════════════════

    public void Tick(Memory mem, GameState state)
    {
        if (!Enabled || !mem.IsAttached || !mem.HasWriteAccess || !state.InGame)
            return;

        try
        {
            long localPawn = state.LocalPawn;
            if (localPawn == 0) return;

            int health = mem.Read<int>(localPawn + Offsets.m_iHealth);
            if (health <= 0) return;

            long weaponServices = mem.Read<long>(localPawn + SkinOffsets.m_pWeaponServices);
            if (weaponServices == 0 || weaponServices < 0x10000) return;

            var weapons = GetWeaponEntities(mem, weaponServices);

            foreach (long weapon in weapons)
            {
                if (weapon < 0x10000) continue;

                long item = weapon + SkinOffsets.m_AttributeManager + SkinOffsets.m_Item;

                // ForceUpdate: reset IDHigh so game re-reads paint kit on next tick
                if (ForceUpdate)
                    mem.Write<uint>(item + SkinOffsets.m_iItemIDHigh, 0);

                // Already applied this tick — skip
                if (mem.Read<uint>(item + SkinOffsets.m_iItemIDHigh) == 0xFFFFFFFF)
                    continue;

                ushort defIndex = mem.Read<ushort>(item + SkinOffsets.m_iItemDefinitionIndex);
                if (defIndex == 0) continue;

                // Resolve skin
                SkinInfo? skin = null;
                if (DefaultKnives.Contains(defIndex))
                {
                    if (SelectedKnifeSkin?.PaintKit != 0)
                        skin = SelectedKnifeSkin;
                    else
                        continue;
                }
                else
                {
                    skin = GetConfiguredSkin(defIndex);
                }

                if (skin == null || skin.PaintKit == 0) continue;

                // ── Write fallback paint kit values ──────────────────
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackPaintKit,  skin.PaintKit);
                mem.Write<float>(weapon + SkinOffsets.m_flFallbackWear,   0.0001f);
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackSeed,      0);
                mem.Write<int>(weapon + SkinOffsets.m_nFallbackStatTrak,  -1);
                mem.Write<uint>(item   + SkinOffsets.m_iItemIDHigh,       0xFFFFFFFF);

                // ── Mesh mask (legacy model uses mask=2, normal=1) ───
                ulong mask = skin.LegacyModel ? 2UL : 1UL;
                SetMeshMask(mem, weapon, mask);

                // Also apply to HUD view-model weapon
                long hudWeapon = GetHudWeapon(mem, localPawn, weapon);
                if (hudWeapon != 0)
                    SetMeshMask(mem, hudWeapon, mask);

                LogDebug($"[SKIN] def={defIndex} PK={skin.PaintKit}");
            }

            ForceUpdate = false;
        }
        catch (Exception ex)
        {
            LogDebug($"[SKIN] Tick error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  MESH MASK  — 3 writes wins the network-sync race
    // ═══════════════════════════════════════════════════

    private static void SetMeshMask(Memory mem, long entity, ulong mask)
    {
        long sceneNode = mem.Read<long>(entity + SkinOffsets.m_pGameSceneNode_skin);
        if (sceneNode < 0x10000) return;

        long modelState = sceneNode + SkinOffsets.m_modelState_skin;

        // Dirty-data mask (tells engine the mesh needs updating)
        try
        {
            long dirtyData = mem.Read<long>(modelState + m_pDirtyModelData);
            if (dirtyData > 0x10000)
                mem.Write<ulong>(dirtyData + m_DirtyMeshGroupMask, mask);
        }
        catch { }

        // Write mesh group mask — 3 writes is enough
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
        mem.Write<ulong>(modelState + SkinOffsets.m_MeshGroupMask, mask);
    }

    // ═══════════════════════════════════════════════════
    //  HUD WEAPON LOOKUP
    // ═══════════════════════════════════════════════════

    private const int m_hHudModelArms = 0x1B58;
    private const int m_pChild        = 0x40;
    private const int m_pNextSibling  = 0x48;
    private const int m_pOwner        = 0x30;

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

            long viewModel = mem.Read<long>(armsNode + m_pChild);
            int iterations = 0;
            while (viewModel > 0x10000 && iterations++ < 32)
            {
                long owner = mem.Read<long>(viewModel + m_pOwner);
                if (owner > 0x10000)
                {
                    int ownerHandle = mem.Read<int>(owner + SkinOffsets.m_hOwnerEntity);
                    long ownerEntity = EntityResolver.ResolvePawn(mem, entityList, ownerHandle);
                    if (ownerEntity == weapon) return owner;
                }
                viewModel = mem.Read<long>(viewModel + m_pNextSibling);
            }
        }
        catch { }
        return 0;
    }

    // ═══════════════════════════════════════════════════
    //  WEAPON ENUMERATION
    //  m_hMyWeapons = fixed embedded handle array in weapon services
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
