using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoxSense.Core;

/// <summary>
/// Auto-fetches the latest offsets from sezzyaep/CS2-OFFSETS on startup.
/// Parses C++ .hpp format (constexpr std::ptrdiff_t name = 0xHEX;)
/// Falls back to a2x/cs2-dumper JSON if primary fails, then to hardcoded values.
/// Caches fetched data to %APPDATA%/FoxSense/ for offline use.
/// </summary>
public static class OffsetUpdater
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxSense");

    // Primary source — sezzyaep (updates fastest, uses .hpp format)
    private const string PRIMARY_OFFSETS = "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/offsets.hpp";
    private const string PRIMARY_CLIENT = "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/client_dll.hpp";

    // Secondary source — a2x (JSON format)
    private const string SECONDARY_OFFSETS = "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/offsets.json";
    private const string SECONDARY_CLIENT = "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/client_dll.json";

    // Regex for parsing C++ hpp: constexpr std::ptrdiff_t NAME = 0xHEX;
    private static readonly Regex HppRegex = new(
        @"constexpr\s+std::ptrdiff_t\s+(\w+)\s*=\s*(0x[0-9A-Fa-f]+)\s*;",
        RegexOptions.Compiled);

    /// <summary>
    /// Fetches and applies the latest offsets. Call once at startup.
    /// Returns true if offsets were updated from a remote source.
    /// </summary>
    public static async Task<bool> UpdateAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        bool updated = false;

        // Try primary (.hpp) → secondary (.json) → cache
        string? offsetsHpp = await TryFetch(PRIMARY_OFFSETS);
        string? clientHpp = await TryFetch(PRIMARY_CLIENT);

        if (offsetsHpp != null)
        {
            ApplyHppOffsets(offsetsHpp);
            SaveCache("offsets.hpp", offsetsHpp);
            updated = true;
        }
        else
        {
            // Try secondary JSON source
            string? offsetsJson = await TryFetch(SECONDARY_OFFSETS)
                               ?? TryReadCache("offsets.json");
            if (offsetsJson != null)
            {
                ApplyBaseOffsetsJson(offsetsJson);
                SaveCache("offsets.json", offsetsJson);
                updated = true;
            }
            else
            {
                // Try cached hpp
                string? cached = TryReadCache("offsets.hpp");
                if (cached != null) { ApplyHppOffsets(cached); updated = true; }
            }
        }

        if (clientHpp != null)
        {
            ApplyHppClientOffsets(clientHpp);
            SaveCache("client_dll.hpp", clientHpp);
            updated = true;
        }
        else
        {
            // Try secondary JSON source
            string? clientJson = await TryFetch(SECONDARY_CLIENT)
                              ?? TryReadCache("client_dll.json");
            if (clientJson != null)
            {
                ApplySkinOffsetsJson(clientJson);
                SaveCache("client_dll.json", clientJson);
                updated = true;
            }
            else
            {
                // Try cached hpp
                string? cached = TryReadCache("client_dll.hpp");
                if (cached != null) { ApplyHppClientOffsets(cached); updated = true; }
            }
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
    //  HPP PARSER (sezzyaep format)
    // ═══════════════════════════════════════════════════

    private static Dictionary<string, int> ParseHpp(string hpp)
    {
        var result = new Dictionary<string, int>();
        foreach (Match m in HppRegex.Matches(hpp))
        {
            string name = m.Groups[1].Value;
            if (int.TryParse(m.Groups[2].Value[2..], System.Globalization.NumberStyles.HexNumber, null, out int val))
                result[name] = val;
        }
        return result;
    }

    // ═══════════════════════════════════════════════════
    //  APPLY HPP BASE OFFSETS (offsets.hpp)
    // ═══════════════════════════════════════════════════

    private static void ApplyHppOffsets(string hpp)
    {
        var offsets = ParseHpp(hpp);

        TryApply(offsets, "dwEntityList", v => Offsets.dwEntityList = v);
        TryApply(offsets, "dwLocalPlayerController", v => Offsets.dwLocalPlayerController = v);
        TryApply(offsets, "dwLocalPlayerPawn", v => Offsets.dwLocalPlayerPawn = v);
        TryApply(offsets, "dwViewMatrix", v => Offsets.dwViewMatrix = v);
        TryApply(offsets, "dwViewAngles", v => Offsets.dwViewAngles = v);

        // Engine2
        TryApply(offsets, "dwNetworkGameClient", v => SkinOffsets.dwNetworkGameClient = v);
        TryApply(offsets, "dwNetworkGameClient_deltaTick", v => SkinOffsets.dwNetworkGameClient_deltaTick = v);
    }

    // ═══════════════════════════════════════════════════
    //  APPLY HPP CLIENT OFFSETS (client_dll.hpp)
    // ═══════════════════════════════════════════════════

    private static void ApplyHppClientOffsets(string hpp)
    {
        var offsets = ParseHpp(hpp);

        // Weapon services
        TryApply(offsets, "m_pWeaponServices", v => SkinOffsets.m_pWeaponServices = v);
        TryApply(offsets, "m_hMyWeapons", v => SkinOffsets.m_hMyWeapons = v);
        TryApply(offsets, "m_hActiveWeapon", v => SkinOffsets.m_hActiveWeapon = v);

        // Econ entity — NOTE: hpp may have multiple m_AttributeManager from different classes.
        // We handle this by only applying values we can verify are from the right class.
        // The fallback values are already set to sezzyaep Build 14160.

        // Fallback paints (these are unique field names, safe to apply)
        TryApply(offsets, "m_nFallbackPaintKit", v => SkinOffsets.m_nFallbackPaintKit = v);
        TryApply(offsets, "m_nFallbackSeed", v => SkinOffsets.m_nFallbackSeed = v);
        TryApply(offsets, "m_flFallbackWear", v => SkinOffsets.m_flFallbackWear = v);
        TryApply(offsets, "m_nFallbackStatTrak", v => SkinOffsets.m_nFallbackStatTrak = v);
        TryApply(offsets, "m_OriginalOwnerXuidLow", v => SkinOffsets.m_OriginalOwnerXuidLow = v);

        // Econ item view
        TryApply(offsets, "m_iItemDefinitionIndex", v => SkinOffsets.m_iItemDefinitionIndex = v);
        TryApply(offsets, "m_iItemIDHigh", v => SkinOffsets.m_iItemIDHigh = v);
        TryApply(offsets, "m_iAccountID", v => SkinOffsets.m_iAccountID = v);
        TryApply(offsets, "m_iEntityQuality", v => SkinOffsets.m_iEntityQuality = v);
        TryApply(offsets, "m_bInitialized", v => SkinOffsets.m_bInitialized = v);
        TryApply(offsets, "m_AttributeList", v => SkinOffsets.m_AttributeList = v);
        TryApply(offsets, "m_NetworkedDynamicAttributes", v => SkinOffsets.m_NetworkedDynamicAttributes = v);
        TryApply(offsets, "m_Attributes", v => SkinOffsets.m_Attributes = v);
        TryApply(offsets, "m_Item", v => SkinOffsets.m_Item = v);

        // Knife / Glove
        TryApply(offsets, "m_nSubclassID", v => SkinOffsets.m_nSubclassID = v);
        TryApply(offsets, "m_bNeedToReApplyGloves", v => SkinOffsets.m_bNeedToReApplyGloves = v);
        TryApply(offsets, "m_hOwnerEntity", v => SkinOffsets.m_hOwnerEntity = v);
        TryApply(offsets, "m_MeshGroupMask", v => SkinOffsets.m_MeshGroupMask = v);

        // Model
        TryApply(offsets, "m_pGameSceneNode", v => { SkinOffsets.m_pGameSceneNode_skin = v; Offsets.m_pGameSceneNode = v; });
        TryApply(offsets, "m_modelState", v => SkinOffsets.m_modelState_skin = v);

        // Pawn offsets (also update ESP offsets)
        TryApply(offsets, "m_iHealth", v => Offsets.m_iHealth = v);
        TryApply(offsets, "m_iTeamNum", v => Offsets.m_iTeamNum = v);
        TryApply(offsets, "m_vOldOrigin", v => Offsets.m_vOldOrigin = v);

        // Inventory
        TryApply(offsets, "m_pInventoryServices", v => SkinOffsets.m_pInventoryServices = v);
        TryApply(offsets, "m_unMusicID", v => SkinOffsets.m_unMusicID = v);
    }

    // ═══════════════════════════════════════════════════
    //  JSON PARSERS (a2x fallback)
    // ═══════════════════════════════════════════════════

    private static void ApplyBaseOffsetsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("client.dll", out var client))
            {
                TrySetJson(client, "dwEntityList", v => Offsets.dwEntityList = v);
                TrySetJson(client, "dwLocalPlayerController", v => Offsets.dwLocalPlayerController = v);
                TrySetJson(client, "dwLocalPlayerPawn", v => Offsets.dwLocalPlayerPawn = v);
                TrySetJson(client, "dwViewMatrix", v => Offsets.dwViewMatrix = v);
                TrySetJson(client, "dwViewAngles", v => Offsets.dwViewAngles = v);
            }

            if (root.TryGetProperty("engine2.dll", out var engine))
            {
                TrySetJson(engine, "dwNetworkGameClient", v => SkinOffsets.dwNetworkGameClient = v);
                TrySetJson(engine, "dwNetworkGameClient_deltaTick", v => SkinOffsets.dwNetworkGameClient_deltaTick = v);
            }
        }
        catch { }
    }

    private static void ApplySkinOffsetsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("client.dll", out var dll)) return;
            if (!dll.TryGetProperty("classes", out var classes)) return;

            TrySetField(classes, "C_EconEntity", "m_AttributeManager", v => SkinOffsets.m_AttributeManager = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackPaintKit", v => SkinOffsets.m_nFallbackPaintKit = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackSeed", v => SkinOffsets.m_nFallbackSeed = v);
            TrySetField(classes, "C_EconEntity", "m_flFallbackWear", v => SkinOffsets.m_flFallbackWear = v);
            TrySetField(classes, "C_EconEntity", "m_nFallbackStatTrak", v => SkinOffsets.m_nFallbackStatTrak = v);
            TrySetField(classes, "C_AttributeContainer", "m_Item", v => SkinOffsets.m_Item = v);
            TrySetField(classes, "C_EconItemView", "m_iItemDefinitionIndex", v => SkinOffsets.m_iItemDefinitionIndex = v);
            TrySetField(classes, "C_EconItemView", "m_iItemIDHigh", v => SkinOffsets.m_iItemIDHigh = v);
            TrySetField(classes, "C_EconItemView", "m_iAccountID", v => SkinOffsets.m_iAccountID = v);
            TrySetField(classes, "C_EconItemView", "m_iEntityQuality", v => SkinOffsets.m_iEntityQuality = v);
            TrySetField(classes, "C_EconItemView", "m_bInitialized", v => SkinOffsets.m_bInitialized = v);
            TrySetField(classes, "C_BaseEntity", "m_nSubclassID", v => SkinOffsets.m_nSubclassID = v);
            TrySetField(classes, "CModelState", "m_MeshGroupMask", v => SkinOffsets.m_MeshGroupMask = v);
            TrySetField(classes, "C_BaseEntity", "m_pGameSceneNode", v => { SkinOffsets.m_pGameSceneNode_skin = v; Offsets.m_pGameSceneNode = v; });
            TrySetField(classes, "CSkeletonInstance", "m_modelState", v => SkinOffsets.m_modelState_skin = v);
            TrySetField(classes, "C_BasePlayerPawn", "m_pWeaponServices", v => SkinOffsets.m_pWeaponServices = v);
            TrySetField(classes, "CPlayer_WeaponServices", "m_hMyWeapons", v => SkinOffsets.m_hMyWeapons = v);
            TrySetField(classes, "CPlayer_WeaponServices", "m_hActiveWeapon", v => SkinOffsets.m_hActiveWeapon = v);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════

    private static void TryApply(Dictionary<string, int> offsets, string key, Action<int> setter)
    {
        if (offsets.TryGetValue(key, out int val))
            setter(val);
    }

    private static void TrySetJson(JsonElement parent, string key, Action<int> setter)
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
