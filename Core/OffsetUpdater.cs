using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FoxSense.Core;

/// <summary>
/// Auto-fetches the latest offsets from multiple providers on startup.
/// Compares the last commit date of each provider via GitHub API and
/// applies offsets from whichever was updated most recently.
/// Providers: a2x/cs2-dumper, sezzyaep/CS2-OFFSETS
/// Caches fetched data to %APPDATA%/FoxSense/ for offline use.
/// </summary>
public static class OffsetUpdater
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxSense");

    private static string? _activeProvider;

    // ═══════════════════════════════════════════════════
    //  PROVIDER DEFINITIONS
    // ═══════════════════════════════════════════════════

    private record OffsetProvider(
        string Name,
        string OffsetsUrl,
        string ClientUrl,
        string CommitApiUrl,    // GitHub API to check last commit date
        bool NestedFieldFormat  // true = {"offset": N, "type": "..."}, false = N
    );

    private static readonly OffsetProvider[] _providers =
    {
        new("a2x/cs2-dumper",
            "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/offsets.json",
            "https://raw.githubusercontent.com/a2x/cs2-dumper/main/output/client_dll.json",
            "https://api.github.com/repos/a2x/cs2-dumper/commits?path=output/offsets.json&per_page=1",
            NestedFieldFormat: false),

        new("sezzyaep/CS2-OFFSETS",
            "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/offsets.json",
            "https://raw.githubusercontent.com/sezzyaep/CS2-OFFSETS/main/client_dll.json",
            "https://api.github.com/repos/sezzyaep/CS2-OFFSETS/commits?path=offsets.json&per_page=1",
            NestedFieldFormat: true),
    };

    /// <summary>
    /// Returns the name of the provider whose offsets are currently active.
    /// </summary>
    public static string? ActiveProvider => _activeProvider;

    // ═══════════════════════════════════════════════════
    //  MAIN ENTRY POINT
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Fetches offsets from all providers, picks the freshest, and applies them.
    /// Falls back to cache if all fetches fail. Returns true if offsets were updated.
    /// </summary>
    public static async Task<bool> UpdateAsync()
    {
        Directory.CreateDirectory(_cacheDir);

        // 1. Find the freshest provider by querying GitHub commit dates
        var freshest = await FindFreshestProvider();

        if (freshest != null)
        {
            // 2. Fetch and apply from the freshest provider
            bool ok = await FetchAndApply(freshest);
            if (ok)
            {
                _activeProvider = freshest.Name;
                Log($"[OFFSETS] Using {freshest.Name} (freshest)");
                return true;
            }
        }

        // 3. If freshest failed, try each provider in order
        foreach (var provider in _providers)
        {
            bool ok = await FetchAndApply(provider);
            if (ok)
            {
                _activeProvider = provider.Name;
                Log($"[OFFSETS] Using {provider.Name} (fallback)");
                return true;
            }
        }

        // 4. Last resort: try cache
        string? cachedOffsets = TryReadCache("offsets.json");
        string? cachedClient = TryReadCache("client_dll.json");
        if (cachedOffsets != null || cachedClient != null)
        {
            // Detect format from cache and apply
            if (cachedOffsets != null) ApplyBaseOffsets(cachedOffsets);
            if (cachedClient != null) ApplyClientOffsets(cachedClient, DetectNestedFormat(cachedClient));
            _activeProvider = "cache";
            Log("[OFFSETS] Using cached offsets");
            return true;
        }

        Log("[OFFSETS] No provider available, using hardcoded defaults");
        return false;
    }

    // ═══════════════════════════════════════════════════
    //  FRESHNESS CHECK
    // ═══════════════════════════════════════════════════

    private static async Task<OffsetProvider?> FindFreshestProvider()
    {
        _http.DefaultRequestHeaders.Remove("User-Agent");
        _http.DefaultRequestHeaders.Add("User-Agent", "FoxSense-OffsetUpdater");

        OffsetProvider? best = null;
        DateTime bestDate = DateTime.MinValue;

        foreach (var provider in _providers)
        {
            try
            {
                string json = await _http.GetStringAsync(provider.CommitApiUrl);
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement;
                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    var commit = arr[0];
                    if (commit.TryGetProperty("commit", out var c) &&
                        c.TryGetProperty("committer", out var committer) &&
                        committer.TryGetProperty("date", out var dateEl))
                    {
                        if (DateTime.TryParse(dateEl.GetString(), out DateTime dt))
                        {
                            Log($"[OFFSETS] {provider.Name} last updated: {dt:yyyy-MM-dd HH:mm}");
                            if (dt > bestDate)
                            {
                                bestDate = dt;
                                best = provider;
                            }
                        }
                    }
                }
            }
            catch
            {
                Log($"[OFFSETS] Failed to check freshness for {provider.Name}");
            }
        }

        return best;
    }

    // ═══════════════════════════════════════════════════
    //  FETCH & APPLY
    // ═══════════════════════════════════════════════════

    private static async Task<bool> FetchAndApply(OffsetProvider provider)
    {
        try
        {
            string? offsets = await TryFetch(provider.OffsetsUrl);
            string? client = await TryFetch(provider.ClientUrl);

            if (offsets == null && client == null) return false;

            if (offsets != null)
            {
                ApplyBaseOffsets(offsets);
                SaveCache("offsets.json", offsets);
            }

            if (client != null)
            {
                ApplyClientOffsets(client, provider.NestedFieldFormat);
                SaveCache("client_dll.json", client);
            }

            return true;
        }
        catch { return false; }
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
    //  APPLY BASE OFFSETS (offsets.json — same format for all providers)
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
    //  Handles both formats:
    //    a2x:      "fieldName": 1234
    //    sezzyaep: "fieldName": {"offset": 1234, "type": "..."}
    // ═══════════════════════════════════════════════════

    private static void ApplyClientOffsets(string json, bool nested)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("client.dll", out var dll)) return;
            if (!dll.TryGetProperty("classes", out var classes)) return;

            // ── ESP offsets ──
            SetField(classes, "C_BaseEntity", "m_pGameSceneNode", nested, v =>
            {
                Offsets.m_pGameSceneNode = v;
                SkinOffsets.m_pGameSceneNode_skin = v;
            });
            SetField(classes, "C_BaseEntity", "m_iHealth", nested, v => Offsets.m_iHealth = v);
            SetField(classes, "C_BaseEntity", "m_iTeamNum", nested, v => Offsets.m_iTeamNum = v);
            SetField(classes, "C_BasePlayerPawn", "m_vOldOrigin", nested, v => Offsets.m_vOldOrigin = v);
            SetField(classes, "CSkeletonInstance", "m_modelState", nested, v => SkinOffsets.m_modelState_skin = v);

            // ── Weapon services ──
            SetField(classes, "C_BasePlayerPawn", "m_pWeaponServices", nested, v => SkinOffsets.m_pWeaponServices = v);
            SetField(classes, "CPlayer_WeaponServices", "m_hMyWeapons", nested, v => SkinOffsets.m_hMyWeapons = v);
            SetField(classes, "CPlayer_WeaponServices", "m_hActiveWeapon", nested, v => SkinOffsets.m_hActiveWeapon = v);

            // ── Econ entity (skin changer) ──
            SetField(classes, "C_EconEntity", "m_AttributeManager", nested, v => SkinOffsets.m_AttributeManager = v);
            SetField(classes, "C_EconEntity", "m_nFallbackPaintKit", nested, v => SkinOffsets.m_nFallbackPaintKit = v);
            SetField(classes, "C_EconEntity", "m_nFallbackSeed", nested, v => SkinOffsets.m_nFallbackSeed = v);
            SetField(classes, "C_EconEntity", "m_flFallbackWear", nested, v => SkinOffsets.m_flFallbackWear = v);
            SetField(classes, "C_EconEntity", "m_nFallbackStatTrak", nested, v => SkinOffsets.m_nFallbackStatTrak = v);
            SetField(classes, "C_EconEntity", "m_OriginalOwnerXuidLow", nested, v => SkinOffsets.m_OriginalOwnerXuidLow = v);

            // ── Attribute container ──
            SetField(classes, "C_AttributeContainer", "m_Item", nested, v => SkinOffsets.m_Item = v);

            // ── Econ item view ──
            SetField(classes, "C_EconItemView", "m_iItemDefinitionIndex", nested, v => SkinOffsets.m_iItemDefinitionIndex = v);
            SetField(classes, "C_EconItemView", "m_iItemIDHigh", nested, v => SkinOffsets.m_iItemIDHigh = v);
            SetField(classes, "C_EconItemView", "m_iAccountID", nested, v => SkinOffsets.m_iAccountID = v);
            SetField(classes, "C_EconItemView", "m_iEntityQuality", nested, v => SkinOffsets.m_iEntityQuality = v);
            SetField(classes, "C_EconItemView", "m_bInitialized", nested, v => SkinOffsets.m_bInitialized = v);

            // ── Attribute list ──
            SetField(classes, "CAttributeList", "m_Attributes", nested, v => SkinOffsets.m_Attributes = v);

            // ── Model state ──
            SetField(classes, "CModelState", "m_MeshGroupMask", nested, v => SkinOffsets.m_MeshGroupMask = v);

            // ── Misc ──
            SetField(classes, "C_BaseEntity", "m_nSubclassID", nested, v => SkinOffsets.m_nSubclassID = v);
            SetField(classes, "CCSPlayerController_InventoryServices", "m_unMusicID", nested, v => SkinOffsets.m_unMusicID = v);

            // ── Controller → pawn link ──
            SetField(classes, "CBasePlayerController", "m_hPawn", nested, v => Offsets.m_hPawn = v);
            SetField(classes, "CCSPlayerController", "m_hPlayerPawn", nested, v => Offsets.m_hPawn_Fallback = v);
            SetField(classes, "CBasePlayerController", "m_iszPlayerName", nested, v => Offsets.m_iszPlayerName = v);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════

    /// <summary>Reads a flat numeric field from a JSON object.</summary>
    private static void TrySet(JsonElement parent, string key, Action<int> setter)
    {
        if (parent.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
            setter((int)val.GetInt64());
    }

    /// <summary>
    /// Reads a class field offset. Handles both formats:
    ///   flat:   "fieldName": 1234
    ///   nested: "fieldName": {"offset": 1234, "type": "..."}
    /// </summary>
    private static void SetField(JsonElement classes, string className, string fieldName,
                                  bool nested, Action<int> setter)
    {
        if (!classes.TryGetProperty(className, out var cls)) return;
        if (!cls.TryGetProperty("fields", out var fields)) return;
        if (!fields.TryGetProperty(fieldName, out var val)) return;

        if (val.ValueKind == JsonValueKind.Number)
        {
            setter((int)val.GetInt64());
        }
        else if (val.ValueKind == JsonValueKind.Object &&
                 val.TryGetProperty("offset", out var offsetVal) &&
                 offsetVal.ValueKind == JsonValueKind.Number)
        {
            setter((int)offsetVal.GetInt64());
        }
    }

    /// <summary>Auto-detect if cached client_dll.json uses nested format.</summary>
    private static bool DetectNestedFormat(string json)
    {
        try
        {
            // Quick check: if the file contains "\"offset\":" it's nested (sezzyaep)
            return json.Contains("\"offset\":");
        }
        catch { return false; }
    }

    private static void Log(string msg)
    {
        try
        {
            string logPath = Path.Combine(_cacheDir, "offset_update.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
