using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FoxSense.Core;

/// <summary>
/// Auto-fetches the latest offsets from a2x/cs2-dumper on startup.
/// Parses JSON format from the cs2-dumper output directory.
/// Caches fetched data to %APPDATA%/FoxSense/ for offline use.
/// </summary>
public static class OffsetUpdater
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxSense");

    // a2x/cs2-dumper — primary source (most reliable, always up to date)
    private const string A2X_OFFSETS = "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/offsets.json";
    private const string A2X_CLIENT = "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/client_dll.json";

    /// <summary>
    /// Fetches and applies the latest offsets. Call once at startup.
    /// Returns true if offsets were updated from a remote source.
    /// </summary>
    public static async Task<bool> UpdateAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        bool updated = false;

        // Fetch offsets.json (dwEntityList, dwLocalPlayerPawn, dwViewMatrix, etc.)
        string? offsetsJson = await TryFetch(A2X_OFFSETS)
                           ?? TryReadCache("offsets.json");
        if (offsetsJson != null)
        {
            ApplyBaseOffsets(offsetsJson);
            SaveCache("offsets.json", offsetsJson);
            updated = true;
        }

        // Fetch client_dll.json (m_iHealth, m_iTeamNum, m_AttributeManager, etc.)
        string? clientJson = await TryFetch(A2X_CLIENT)
                          ?? TryReadCache("client_dll.json");
        if (clientJson != null)
        {
            ApplyClientOffsets(clientJson);
            SaveCache("client_dll.json", clientJson);
            updated = true;
        }

        return updated;
    }

    // ═══════════════════════════════════════════════════
    //  FETCH / CACHE
    // ═══════════════════════════════════════════════════

    private static async Task<string?> TryFetch(string url)
    {
        try { return await _http.GetStringAsync(url); }
        catch { return null; }
    }

    private static string? TryReadCache(string filename)
    {
        string path = Path.Combine(_cacheDir, filename);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static void SaveCache(string filename, string content)
    {
        try { File.WriteAllText(Path.Combine(_cacheDir, filename), content); }
        catch { /* Non-critical */ }
    }

    // ═══════════════════════════════════════════════════
    //  APPLY BASE OFFSETS (offsets.json)
    //  Contains: dwEntityList, dwLocalPlayerController,
    //  dwLocalPlayerPawn, dwViewMatrix, dwViewAngles, etc.
    // ═══════════════════════════════════════════════════

    private static void ApplyBaseOffsets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("client.dll", out var client))
            {
                TrySet(client, "dwEntityList", v => Offsets.dwEntityList = v);
                TrySet(client, "dwLocalPlayerController", v => Offsets.dwLocalPlayerController = v);
                TrySet(client, "dwLocalPlayerPawn", v => Offsets.dwLocalPlayerPawn = v);
                TrySet(client, "dwViewMatrix", v => Offsets.dwViewMatrix = v);
                TrySet(client, "dwViewAngles", v => Offsets.dwViewAngles = v);
            }

            if (root.TryGetProperty("engine2.dll", out var engine))
            {
                TrySet(engine, "dwNetworkGameClient", v => SkinOffsets.dwNetworkGameClient = v);
                TrySet(engine, "dwNetworkGameClient_deltaTick", v => SkinOffsets.dwNetworkGameClient_deltaTick = v);
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  APPLY CLIENT OFFSETS (client_dll.json)
    //  Contains class-based offsets for all game entities.
    // ═══════════════════════════════════════════════════

    private static void ApplyClientOffsets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("client.dll", out var dll)) return;
            if (!dll.TryGetProperty("classes", out var classes)) return;

            // ── ESP offsets ──
            TrySetField(classes, "C_BaseEntity", "m_pGameSceneNode", v =>
            {
                Offsets.m_pGameSceneNode = v;
                SkinOffsets.m_pGameSceneNode_skin = v;
            });
            TrySetField(classes, "C_BaseEntity", "m_iHealth", v => Offsets.m_iHealth = v);
            TrySetField(classes, "C_BaseEntity", "m_iTeamNum", v => Offsets.m_iTeamNum = v);
            TrySetField(classes, "C_BasePlayerPawn", "m_vOldOrigin", v => Offsets.m_vOldOrigin = v);
            TrySetField(classes, "CGameSceneNode", "m_vecAbsOrigin", v => { /* Offsets.m_vecAbsOrigin = v; */ });
            TrySetField(classes, "CSkeletonInstance", "m_modelState", v => SkinOffsets.m_modelState_skin = v);

            // ── Weapon services ──
            TrySetField(classes, "C_BasePlayerPawn", "m_pWeaponServices", v => SkinOffsets.m_pWeaponServices = v);
            TrySetField(classes, "CPlayer_WeaponServices", "m_hMyWeapons", v => SkinOffsets.m_hMyWeapons = v);
            TrySetField(classes, "CPlayer_WeaponServices", "m_hActiveWeapon", v => SkinOffsets.m_hActiveWeapon = v);

            // ── Econ entity (skin changer) ──
            TrySetField(classes, "C_EconEntity", "m_AttributeManager", v => SkinOffsets.m_AttributeManager = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackPaintKit", v => SkinOffsets.m_nFallbackPaintKit = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackSeed", v => SkinOffsets.m_nFallbackSeed = v);
            TrySetField(classes, "C_EconEntity", "m_flFallbackWear", v => SkinOffsets.m_flFallbackWear = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackStatTrak", v => SkinOffsets.m_nFallbackStatTrak = v);
            TrySetField(classes, "C_EconEntity", "m_OriginalOwnerXuidLow", v => SkinOffsets.m_OriginalOwnerXuidLow = v);

            // ── Attribute container ──
            TrySetField(classes, "C_AttributeContainer", "m_Item", v => SkinOffsets.m_Item = v);

            // ── Econ item view ──
            TrySetField(classes, "C_EconItemView", "m_iItemDefinitionIndex", v => SkinOffsets.m_iItemDefinitionIndex = v);
            TrySetField(classes, "C_EconItemView", "m_iItemIDHigh", v => SkinOffsets.m_iItemIDHigh = v);
            TrySetField(classes, "C_EconItemView", "m_iAccountID", v => SkinOffsets.m_iAccountID = v);
            TrySetField(classes, "C_EconItemView", "m_iEntityQuality", v => SkinOffsets.m_iEntityQuality = v);
            TrySetField(classes, "C_EconItemView", "m_bInitialized", v => SkinOffsets.m_bInitialized = v);

            // ── Attribute list ──
            TrySetField(classes, "CAttributeList", "m_Attributes", v => SkinOffsets.m_Attributes = v);

            // ── Model state ──
            TrySetField(classes, "CModelState", "m_MeshGroupMask", v => SkinOffsets.m_MeshGroupMask = v);

            // ── Misc ──
            TrySetField(classes, "C_BaseEntity", "m_nSubclassID", v => SkinOffsets.m_nSubclassID = v);
            TrySetField(classes, "CCSPlayerController_InventoryServices", "m_unMusicID", v => SkinOffsets.m_unMusicID = v);

            // ── Controller → pawn link ──
            TrySetField(classes, "CBasePlayerController", "m_hPawn", v => Offsets.m_hPawn = v);
            TrySetField(classes, "CCSPlayerController", "m_hPlayerPawn", v => Offsets.m_hPawn_Fallback = v);
            TrySetField(classes, "CBasePlayerController", "m_iszPlayerName", v => Offsets.m_iszPlayerName = v);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════

    private static void TrySet(JsonElement parent, string key, Action<int> setter)
    {
        if (parent.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            setter((int)val.GetInt64());
    }

    private static void TrySetField(JsonElement classes, string className, string fieldName, Action<int> setter)
    {
        if (classes.TryGetProperty(className, out var cls) &&
            cls.TryGetProperty("fields", out var fields) &&
            fields.TryGetProperty(fieldName, out var val) &&
            val.ValueKind == JsonValueKind.Number)
        {
            setter((int)val.GetInt64());
        }
    }
}
