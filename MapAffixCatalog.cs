using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MapModHelper;

internal sealed class MapAffixDefinition
{
    private HashSet<string>? _modIds;

    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> ModIds { get; set; } = [];

    [JsonIgnore]
    public string ShortLabel
    {
        get
        {
            var slash = Label.IndexOf(" / ", StringComparison.Ordinal);
            return slash > 0 ? Label[..slash] : Label;
        }
    }

    public void EnsureDefaults()
    {
        Id = Id?.Trim() ?? string.Empty;
        Label = Label?.Trim() ?? string.Empty;
        Category = Category?.Trim() ?? string.Empty;
        ModIds = (ModIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _modIds = ModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Matches(MapExplicitModInfo mod)
    {
        _modIds ??= ModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(mod.RawName) && _modIds.Contains(mod.RawName))
            return true;

        if (!string.IsNullOrWhiteSpace(mod.Name) && _modIds.Contains(mod.Name))
            return true;

        if (!string.IsNullOrWhiteSpace(mod.Group) && string.Equals(mod.Group, Id, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}

internal static class MapAffixCatalog
{
    private static IReadOnlyList<MapAffixDefinition> _affixes = [];
    private static IReadOnlyDictionary<string, MapAffixDefinition> _lookup = new Dictionary<string, MapAffixDefinition>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MapAffixDefinition> All => _affixes;

    public static void Load(IEnumerable<MapAffixDefinition>? affixes)
    {
        var loaded = (affixes ?? [])
            .Where(affix => affix != null)
            .ToList();

        foreach (var affix in loaded)
            affix.EnsureDefaults();

        _affixes = loaded
            .Where(affix => !string.IsNullOrWhiteSpace(affix.Id))
            .OrderBy(affix => string.Equals(affix.Category, "Prefix", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(affix => affix.ShortLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _lookup = _affixes
            .GroupBy(affix => affix.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public static MapAffixDefinition? Find(string id)
        => _lookup.TryGetValue(id ?? string.Empty, out var affix) ? affix : null;
}
