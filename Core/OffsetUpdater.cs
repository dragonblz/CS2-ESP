using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FoxSense.Core;

/// <summary>
/// Auto-fetches the latest offsets from sezzyaep/CS2-OFFSETS on startup.
/// Falls back to cache if fetch fails.
/// </summary>
public static class OffsetUpdater
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxSense");

    private const string OFFSETS_URL = "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/offsets.json";
    private const string CLIENT_URL  = "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/client_dll.json";

    public static async Task<bool> UpdateAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        bool updated = false;

        string? offsetsJson = await TryFetch(OFFSETS_URL) ?? TryReadCache("offsets.json");
        if (offsetsJson != null)
        {
            ApplyBaseOffsets(offsetsJson);
            SaveCache("offsets.json", offsetsJson);
            updated = true;
        }

        string? clientJson = await TryFetch(CLIENT_URL) ?? TryReadCache("client_dll.json");
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
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  BASE OFFSETS (offsets.json — flat number format)
    // ═══════════════════════════════════════════════════

    private static void ApplyBaseOffsets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("client.dll", out var client))
            {
                TrySet(client, "dwEntityList",            v => Offsets.dwEntityList = v);
                TrySet(client, "dwLocalPlayerController", v => Offsets.dwLocalPlayerController = v);
                TrySet(client, "dwLocalPlayerPawn",       v => Offsets.dwLocalPlayerPawn = v);
                TrySet(client, "dwViewMatrix",            v => Offsets.dwViewMatrix = v);
                TrySet(client, "dwViewAngles",            v => Offsets.dwViewAngles = v);
            }

            if (root.TryGetProperty("engine2.dll", out var engine))
            {
                TrySet(engine, "dwNetworkGameClient",          v => SkinOffsets.dwNetworkGameClient = v);
                TrySet(engine, "dwNetworkGameClient_deltaTick", v => SkinOffsets.dwNetworkGameClient_deltaTick = v);
            }
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  CLIENT OFFSETS (client_dll.json)
    //  sezzyaep format: "fieldName": {"offset": N, "type": "..."}
    // ═══════════════════════════════════════════════════

    private static void ApplyClientOffsets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("client.dll", out var dll)) return;
            if (!dll.TryGetProperty("classes", out var classes)) return;

            // ── ESP ──
            SetField(classes, "C_BaseEntity",      "m_pGameSceneNode", v => { Offsets.m_pGameSceneNode = v; SkinOffsets.m_pGameSceneNode_skin = v; });
            SetField(classes, "C_BaseEntity",      "m_iHealth",        v => Offsets.m_iHealth = v);
            SetField(classes, "C_BaseEntity",      "m_iTeamNum",       v => Offsets.m_iTeamNum = v);
            SetField(classes, "C_BasePlayerPawn",  "m_vOldOrigin",     v => Offsets.m_vOldOrigin = v);
            SetField(classes, "CSkeletonInstance", "m_modelState",     v => SkinOffsets.m_modelState_skin = v);

            // ── Weapon services ──
            SetField(classes, "C_BasePlayerPawn",       "m_pWeaponServices", v => SkinOffsets.m_pWeaponServices = v);
            SetField(classes, "CPlayer_WeaponServices", "m_hMyWeapons",      v => SkinOffsets.m_hMyWeapons = v);
            SetField(classes, "CPlayer_WeaponServices", "m_hActiveWeapon",   v => SkinOffsets.m_hActiveWeapon = v);

            // ── Skin changer ──
            SetField(classes, "C_EconEntity", "m_AttributeManager",    v => SkinOffsets.m_AttributeManager = v);
            SetField(classes, "C_EconEntity", "m_nFallbackPaintKit",   v => SkinOffsets.m_nFallbackPaintKit = v);
            SetField(classes, "C_EconEntity", "m_nFallbackSeed",       v => SkinOffsets.m_nFallbackSeed = v);
            SetField(classes, "C_EconEntity", "m_flFallbackWear",      v => SkinOffsets.m_flFallbackWear = v);
            SetField(classes, "C_EconEntity", "m_nFallbackStatTrak",   v => SkinOffsets.m_nFallbackStatTrak = v);
            SetField(classes, "C_EconEntity", "m_OriginalOwnerXuidLow",v => SkinOffsets.m_OriginalOwnerXuidLow = v);

            SetField(classes, "C_AttributeContainer", "m_Item", v => SkinOffsets.m_Item = v);

            SetField(classes, "C_EconItemView", "m_iItemDefinitionIndex", v => SkinOffsets.m_iItemDefinitionIndex = v);
            SetField(classes, "C_EconItemView", "m_iItemIDHigh",          v => SkinOffsets.m_iItemIDHigh = v);
            SetField(classes, "C_EconItemView", "m_iAccountID",           v => SkinOffsets.m_iAccountID = v);
            SetField(classes, "C_EconItemView", "m_iEntityQuality",       v => SkinOffsets.m_iEntityQuality = v);
            SetField(classes, "C_EconItemView", "m_bInitialized",         v => SkinOffsets.m_bInitialized = v);

            SetField(classes, "CAttributeList", "m_Attributes",    v => SkinOffsets.m_Attributes = v);
            SetField(classes, "CModelState",    "m_MeshGroupMask", v => SkinOffsets.m_MeshGroupMask = v);

            SetField(classes, "C_BaseEntity", "m_nSubclassID", v => SkinOffsets.m_nSubclassID = v);
            SetField(classes, "CCSPlayerController_InventoryServices", "m_unMusicID", v => SkinOffsets.m_unMusicID = v);

            // ── Controller ──
            SetField(classes, "CBasePlayerController", "m_hPawn",         v => Offsets.m_hPawn = v);
            SetField(classes, "CCSPlayerController",   "m_hPlayerPawn",   v => Offsets.m_hPawn_Fallback = v);
            SetField(classes, "CBasePlayerController", "m_iszPlayerName", v => Offsets.m_iszPlayerName = v);
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

    /// <summary>
    /// Reads a field in sezzyaep format: "fieldName": {"offset": N, "type": "..."}
    /// Also handles flat format: "fieldName": N (a2x compatibility).
    /// </summary>
    private static void SetField(JsonElement classes, string className, string fieldName, Action<int> setter)
    {
        if (!classes.TryGetProperty(className, out var cls)) return;
        if (!cls.TryGetProperty("fields", out var fields)) return;
        if (!fields.TryGetProperty(fieldName, out var val)) return;

        if (val.ValueKind == JsonValueKind.Number)
            setter((int)val.GetInt64());
        else if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("offset", out var off) && off.ValueKind == JsonValueKind.Number)
            setter((int)off.GetInt64());
    }
}
