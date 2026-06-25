using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MapModHelper;

internal sealed class MapStatData
{
    private const string DataFileName = "waystone_data.json";

    public const string ItemRarityStat = "map_item_drop_rarity_+%_final_from_map";
    public const string PackSizeStat = "map_pack_size_+%_final_from_map";
    public const string MonsterRarityStat = "map_number_of_magic_and_rare_packs_+%_final_and_rare_monster_modifiers_chance_+%_final_from_map";
    public const string MonsterEffectivenessStat = "map_monster_potency_+%_final_from_map";
    public const string WaystoneDropChanceStat = "map_map_item_drop_chance_+%_final_from_map";

    public static IReadOnlyList<MapGeneratedStatDefinition> GeneratedStatDefinitions { get; } =
    [
        new(MonsterEffectivenessStat, "Monster Effectiveness", "E"),
        new(ItemRarityStat, "Item Rarity", "R"),
        new(PackSizeStat, "Monster Pack Size", "P"),
        new(MonsterRarityStat, "Monster Rarity", "MR"),
        new(WaystoneDropChanceStat, "Waystone Drop Chance", "W")
    ];

    private static readonly Dictionary<string, MapGeneratedStatDefinition> DefinitionsByStatId = GeneratedStatDefinitions
        .ToDictionary(definition => definition.StatId, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string[]> _modStatsById;

    private MapStatData(WaystoneDataFile data, string sourcePath)
    {
        _modStatsById = new Dictionary<string, string[]>(data.ModStatIdsById ?? [], StringComparer.OrdinalIgnoreCase);
        Affixes = data.Affixes ?? [];
        SourcePath = sourcePath;
        Validation = data.Validation;
    }

    public string SourcePath { get; }
    public int LoadedModCount => _modStatsById.Count;
    public IReadOnlyList<MapAffixDefinition> Affixes { get; }
    public WaystoneDataValidation? Validation { get; }

    public static MapStatData? LoadDefault(out string message)
    {
        var dataPath = FindDataFile();
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            message = $"Could not find data\\{DataFileName}. Reinstall the latest MapModHelper build with the bundled data folder.";
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            using var stream = File.OpenRead(dataPath);
            var data = JsonSerializer.Deserialize<WaystoneDataFile>(stream, options);
            if (data == null)
            {
                message = $"Failed to deserialize {dataPath}.";
                return null;
            }

            data.EnsureDefaults();
            if (data.ModStatIdsById.Count == 0)
            {
                message = $"Loaded {dataPath}, but it did not contain tracked waystone mod stat mappings.";
                return null;
            }

            message = $"Loaded {data.Affixes.Count} waystone affixes and {data.ModStatIdsById.Count} tracked stat mods from {dataPath}.";
            return new MapStatData(data, dataPath);
        }
        catch (Exception ex)
        {
            message = $"Failed to load {DataFileName}: {ex.Message}";
            return null;
        }
    }

    public List<MapGeneratedPropertyInfo> ComputeProperties(IEnumerable<MapExplicitModInfo> mods)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (!TryGetStatIds(mod, out var statIds))
                continue;

            var count = Math.Min(statIds.Length, mod.Values.Count);
            for (var i = 0; i < count; i++)
            {
                var statId = statIds[i];
                if (!IsTrackedStat(statId))
                    continue;

                totals.TryGetValue(statId, out var current);
                totals[statId] = current + mod.Values[i];
            }
        }

        return totals
            .Where(pair => pair.Value != 0)
            .Select(pair => new MapGeneratedPropertyInfo(DisplayName(pair.Key), pair.Key, pair.Value))
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool TryGetStatIds(MapExplicitModInfo mod, out string[] statIds)
    {
        statIds = [];

        if (!string.IsNullOrWhiteSpace(mod.RawName) && _modStatsById.TryGetValue(mod.RawName, out var rawNameStats))
        {
            statIds = rawNameStats;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(mod.Name) && _modStatsById.TryGetValue(mod.Name, out var nameStats))
        {
            statIds = nameStats;
            return true;
        }

        return false;
    }

    public static string DisplayName(string statId)
        => DefinitionsByStatId.TryGetValue(statId, out var definition) ? definition.DisplayName : statId;

    public static MapGeneratedStatDefinition DefinitionFor(string statId)
        => DefinitionsByStatId[statId];

    public static bool IsTrackedStat(string? statId)
        => !string.IsNullOrWhiteSpace(statId) && DefinitionsByStatId.ContainsKey(statId);

    private static string? FindDataFile()
    {
        foreach (var root in CandidateRoots())
        {
            var current = root;
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                var direct = Path.Combine(current, "data", DataFileName);
                if (File.Exists(direct))
                    return direct;

                var sourcePlugin = Path.Combine(current, "Plugins", "Source", "MapModHelper", "data", DataFileName);
                if (File.Exists(sourcePlugin))
                    return sourcePlugin;

                current = Directory.GetParent(current)?.FullName;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        yield return AppDomain.CurrentDomain.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}

internal sealed class WaystoneDataFile
{
    public string GeneratedAt { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = [];
    public List<MapGeneratedStatDefinition> GeneratedStats { get; set; } = [];
    public Dictionary<string, string[]> ModStatIdsById { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MapAffixDefinition> Affixes { get; set; } = [];
    public WaystoneDataValidation? Validation { get; set; }

    public void EnsureDefaults()
    {
        Notes ??= [];
        GeneratedStats ??= [];
        ModStatIdsById = new Dictionary<string, string[]>(ModStatIdsById ?? [], StringComparer.OrdinalIgnoreCase);
        Affixes ??= [];
        foreach (var affix in Affixes)
            affix.EnsureDefaults();
    }
}

internal sealed class WaystoneDataValidation
{
    public int WaystoneAffixFamilies { get; set; }
    public int WaystoneModIds { get; set; }
    public int TrackedGeneratedStatMods { get; set; }
    public int SourceRowsValidated { get; set; }
    public int SourceRowsMissing { get; set; }
    public int StatOrderMismatches { get; set; }
}

internal sealed record MapGeneratedStatDefinition(
    string StatId,
    string DisplayName,
    string BadgeLabel);
