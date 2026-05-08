using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace FoxSense.Features;

/// <summary>
/// Fetches and caches the CS2 skin catalog from the CSGO-API.
/// Provides skin lookup by weapon, knife skins, and glove skins.
/// Each skin includes a preview image URL for the UI.
/// </summary>
public class SkinDatabase
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FoxSense");

    private const string SKINS_API = "https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/skins.json";

    // ═══════════════════════════════════════════════════
    //  DATA MODELS
    // ═══════════════════════════════════════════════════

    public class SkinInfo
    {
        public int PaintKit { get; set; }
        public string Name { get; set; } = "";
        public ushort WeaponDefIndex { get; set; }
        public string WeaponName { get; set; } = "";
        public int Rarity { get; set; } = 1;
        public bool LegacyModel { get; set; }
        public string ImageUrl { get; set; } = "";
    }

    public class KnifeInfo
    {
        public ushort DefIndex { get; set; }
        public string Name { get; set; } = "";
        public string ModelPath { get; set; } = "";
    }

    public class GloveInfo
    {
        public ushort DefIndex { get; set; }
        public string Name { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════
    //  WEAPON DEFINITION INDICES (stable across updates)
    // ═══════════════════════════════════════════════════

    public static readonly Dictionary<string, ushort> WeaponNameToDefIndex = new()
    {
        ["Desert Eagle"] = 1,     ["Dual Berettas"] = 2,   ["Five-SeveN"] = 3,
        ["Glock-18"] = 4,         ["AK-47"] = 7,           ["AUG"] = 8,
        ["AWP"] = 9,              ["FAMAS"] = 10,          ["G3SG1"] = 11,
        ["Galil AR"] = 13,        ["M249"] = 14,           ["M4A4"] = 16,
        ["MAC-10"] = 17,          ["P90"] = 19,            ["MP5-SD"] = 23,
        ["UMP-45"] = 24,          ["XM1014"] = 25,         ["PP-Bizon"] = 26,
        ["MAG-7"] = 27,           ["Negev"] = 28,          ["Sawed-Off"] = 29,
        ["Tec-9"] = 30,           ["P2000"] = 32,          ["MP7"] = 33,
        ["MP9"] = 34,             ["Nova"] = 35,           ["P250"] = 36,
        ["SCAR-20"] = 38,         ["SG 553"] = 39,         ["SSG 08"] = 40,
        ["M4A1-S"] = 60,          ["USP-S"] = 61,          ["CZ75-Auto"] = 63,
        ["R8 Revolver"] = 64,     ["Zeus x27"] = 31,
    };

    // Reverse lookup
    public static readonly Dictionary<ushort, string> DefIndexToWeaponName;

    static SkinDatabase()
    {
        DefIndexToWeaponName = new Dictionary<ushort, string>();
        foreach (var kv in WeaponNameToDefIndex)
            DefIndexToWeaponName[kv.Value] = kv.Key;
    }

    // ═══════════════════════════════════════════════════
    //  KNIFE DATABASE
    // ═══════════════════════════════════════════════════

    public static readonly List<KnifeInfo> Knives = new()
    {
        new() { DefIndex = 500, Name = "Bayonet" },
        new() { DefIndex = 503, Name = "Classic Knife" },
        new() { DefIndex = 505, Name = "Flip Knife" },
        new() { DefIndex = 506, Name = "Gut Knife" },
        new() { DefIndex = 507, Name = "Karambit" },
        new() { DefIndex = 508, Name = "M9 Bayonet" },
        new() { DefIndex = 509, Name = "Huntsman Knife" },
        new() { DefIndex = 512, Name = "Falchion Knife" },
        new() { DefIndex = 514, Name = "Bowie Knife" },
        new() { DefIndex = 515, Name = "Butterfly Knife" },
        new() { DefIndex = 516, Name = "Shadow Daggers" },
        new() { DefIndex = 517, Name = "Paracord Knife" },
        new() { DefIndex = 518, Name = "Survival Knife" },
        new() { DefIndex = 519, Name = "Ursus Knife" },
        new() { DefIndex = 520, Name = "Navaja Knife" },
        new() { DefIndex = 521, Name = "Nomad Knife" },
        new() { DefIndex = 522, Name = "Stiletto Knife" },
        new() { DefIndex = 523, Name = "Talon Knife" },
        new() { DefIndex = 525, Name = "Skeleton Knife" },
        new() { DefIndex = 526, Name = "Kukri Knife" },
    };

    // ═══════════════════════════════════════════════════
    //  GLOVE DATABASE
    // ═══════════════════════════════════════════════════

    public static readonly List<GloveInfo> Gloves = new()
    {
        new() { DefIndex = 5027, Name = "Bloodhound Gloves" },
        new() { DefIndex = 5030, Name = "Sport Gloves" },
        new() { DefIndex = 5031, Name = "Driver Gloves" },
        new() { DefIndex = 5032, Name = "Hand Wraps" },
        new() { DefIndex = 5033, Name = "Moto Gloves" },
        new() { DefIndex = 5034, Name = "Specialist Gloves" },
        new() { DefIndex = 5035, Name = "Hydra Gloves" },
        new() { DefIndex = 5025, Name = "Broken Fang Gloves" },
    };

    // ═══════════════════════════════════════════════════
    //  SKIN CATALOG
    // ═══════════════════════════════════════════════════

    private List<SkinInfo> _allWeaponSkins = new();
    private List<SkinInfo> _allKnifeSkins = new();
    private List<SkinInfo> _allGloveSkins = new();

    public bool IsLoaded { get; private set; }

    private static readonly HashSet<string> KnifeTypeNames = new()
    {
        "Bayonet", "Classic Knife", "Flip Knife", "Gut Knife",
        "Karambit", "M9 Bayonet", "Huntsman Knife", "Falchion Knife",
        "Bowie Knife", "Butterfly Knife", "Shadow Daggers", "Paracord Knife",
        "Survival Knife", "Ursus Knife", "Navaja Knife", "Nomad Knife",
        "Stiletto Knife", "Talon Knife", "Skeleton Knife", "Kukri Knife"
    };

    private static readonly HashSet<string> GloveTypeNames = new()
    {
        "Bloodhound Gloves", "Sport Gloves", "Driver Gloves", "Hand Wraps",
        "Moto Gloves", "Specialist Gloves", "Hydra Gloves", "Broken Fang Gloves"
    };

    /// <summary>
    /// Load the skin catalog from the API (or local cache).
    /// Call once at startup — runs async.
    /// </summary>
    public async Task LoadAsync()
    {
        Directory.CreateDirectory(_cacheDir);
        string cachePath = Path.Combine(_cacheDir, "skins_cache.json");

        string? json = null;

        // Try online first
        try { json = await _http.GetStringAsync(SKINS_API); }
        catch { /* Offline — try cache */ }

        // Try cache
        if (json == null && File.Exists(cachePath))
        {
            try { json = await File.ReadAllTextAsync(cachePath); }
            catch { }
        }

        if (json == null) return;

        // Save to cache
        try { await File.WriteAllTextAsync(cachePath, json); }
        catch { }

        // Parse
        ParseSkins(json);
        IsLoaded = true;
    }

    private void ParseSkins(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _allWeaponSkins.Clear();
            _allKnifeSkins.Clear();
            _allGloveSkins.Clear();

            foreach (var skin in root.EnumerateArray())
            {
                var info = new SkinInfo
                {
                    PaintKit = GetInt(skin, "paint_index"),
                    Name = GetStr(skin, "name"),
                    ImageUrl = GetStr(skin, "image"),
                    LegacyModel = GetBool(skin, "legacy_model"),
                    Rarity = GetRarity(skin),
                };

                // Identify weapon from name
                string weaponField = GetStr(skin, "weapon");

                // Check if knife
                bool isKnife = KnifeTypeNames.Any(k => info.Name.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (isKnife)
                {
                    _allKnifeSkins.Add(info);
                    continue;
                }

                // Check if glove
                bool isGlove = GloveTypeNames.Any(g => info.Name.Contains(g, StringComparison.OrdinalIgnoreCase));
                if (isGlove)
                {
                    _allGloveSkins.Add(info);
                    continue;
                }

                // Regular weapon — resolve defIndex from name
                foreach (var kv in WeaponNameToDefIndex)
                {
                    if (info.Name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        info.WeaponDefIndex = kv.Value;
                        info.WeaponName = kv.Key;
                        break;
                    }
                }

                if (info.WeaponDefIndex != 0 && info.PaintKit != 0)
                    _allWeaponSkins.Add(info);
            }
        }
        catch { /* Malformed JSON */ }
    }

    // ═══════════════════════════════════════════════════
    //  QUERIES
    // ═══════════════════════════════════════════════════

    /// <summary>Get all skins for a specific weapon (by defIndex).</summary>
    public List<SkinInfo> GetSkinsForWeapon(ushort defIndex)
    {
        return _allWeaponSkins.Where(s => s.WeaponDefIndex == defIndex).ToList();
    }

    /// <summary>Get knife skins matching a knife type name.</summary>
    public List<SkinInfo> GetKnifeSkins(string knifeTypeName)
    {
        return _allKnifeSkins
            .Where(s => s.Name.Contains(knifeTypeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Get glove skins matching a glove type name.</summary>
    public List<SkinInfo> GetGloveSkins(string gloveTypeName)
    {
        return _allGloveSkins
            .Where(s => s.Name.Contains(gloveTypeName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Get all weapon types that have skins available.</summary>
    public List<string> GetAvailableWeapons()
    {
        return _allWeaponSkins
            .Where(s => s.WeaponDefIndex != 0)
            .Select(s => s.WeaponName)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    // ═══════════════════════════════════════════════════
    //  JSON HELPERS
    // ═══════════════════════════════════════════════════

    private static string GetStr(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString() ?? "";
            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("en", out var en))
                return en.GetString() ?? "";
        }
        return "";
    }

    private static int GetInt(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
            if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out int r)) return r;
        }
        return 0;
    }

    private static bool GetBool(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return false;
    }

    private static int GetRarity(JsonElement skin)
    {
        if (!skin.TryGetProperty("rarity", out var r)) return 1;
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id))
        {
            string idStr = id.GetString() ?? "";
            if (idStr.Contains("contraband")) return 7;
            if (idStr.Contains("ancient")) return 6;
            if (idStr.Contains("legendary")) return 5;
            if (idStr.Contains("mythical")) return 4;
            if (idStr.Contains("rare")) return 3;
            if (idStr.Contains("uncommon")) return 2;
            return 1;
        }
        if (r.ValueKind == JsonValueKind.Number) return r.GetInt32();
        return 1;
    }
}
