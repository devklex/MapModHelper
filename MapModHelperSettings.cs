using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json.Serialization;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace MapModHelper;

public sealed class MapModHelperSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public ToggleNode OverlayEnabled { get; set; } = new(true);
    public ToggleNode HighlightImportantAffixes { get; set; } = new(true);
    public ToggleNode HighlightMonsterEffectiveness { get; set; } = new(true);
    public ToggleNode HighlightItemRarity { get; set; } = new(true);
    public ToggleNode HighlightMonsterPackSize { get; set; } = new(false);
    public ToggleNode HighlightMonsterRarity { get; set; } = new(false);
    public ToggleNode HighlightWaystoneDropChance { get; set; } = new(false);
    public ToggleNode ShowAffixCountBadge { get; set; } = new(true);
    public ToggleNode EnableBorderRules { get; set; } = new(true);
    public ToggleNode EnableAffixGroups { get; set; } = new(true);
    public ToggleNode ShowAffixGroupBadges { get; set; } = new(true);
    public ToggleNode HideWhenTooltipOverItem { get; set; } = new(true);
    public ToggleNode ShowHoverRuleBreakdown { get; set; } = new(true);
    public ToggleNode LogMatchedMaps { get; set; } = new(false);
    public ToggleNode LogScannedMapSamples { get; set; } = new(false);
    public ToggleNode LogPerformance { get; set; } = new(false);
    public ToggleNode LogScanExceptions { get; set; } = new(false);

    public RangeNode<int> ScanIntervalMs { get; set; } = new(650, 150, 2000);
    public RangeNode<int> TargetAffixCount { get; set; } = new(8, 0, 8);
    public RangeNode<int> BorderThickness { get; set; } = new(3, 1, 12);
    public RangeNode<float> BadgeScale { get; set; } = new(0.9f, 0.5f, 1.8f);

    public ColorNode AffixCountBadgeColor { get; set; } = new(Color.DeepSkyBlue);
    public ColorNode MonsterEffectivenessColor { get; set; } = new(Color.Red);
    public ColorNode ItemRarityColor { get; set; } = new(Color.Orange);
    public ColorNode MonsterPackSizeColor { get; set; } = new(Color.LimeGreen);
    public ColorNode MonsterRarityColor { get; set; } = new(Color.DeepSkyBlue);
    public ColorNode WaystoneDropChanceColor { get; set; } = new(Color.Gold);
    public ColorNode BadgeBackgroundColor { get; set; } = new(Color.FromArgb(220, 0, 0, 0));
    public ColorNode BadgeTextColor { get; set; } = new(Color.White);

    public List<MapAffixRuleGroup> AffixGroups { get; set; } = [];
    public List<MapBorderRule> BorderRules { get; set; } = [];

    public void EnsureDefaults()
    {
        TargetAffixCount.Value = Math.Clamp(TargetAffixCount.Value, TargetAffixCount.Min, TargetAffixCount.Max);
        BorderThickness.Value = Math.Clamp(BorderThickness.Value, BorderThickness.Min, BorderThickness.Max);

        AffixGroups ??= [];
        foreach (var group in AffixGroups)
            group.EnsureDefaults();

        BorderRules = DeduplicateBorderRules(BorderRules ?? []);
        foreach (var rule in BorderRules)
            rule.EnsureDefaults();
    }

    private static List<MapBorderRule> DeduplicateBorderRules(IEnumerable<MapBorderRule> rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MapBorderRule>();

        foreach (var rule in rules.Where(rule => rule != null))
        {
            rule.EnsureDefaults();
            if (seen.Add(BorderRuleKey(rule)))
                result.Add(rule);
        }

        return result;
    }

    private static string BorderRuleKey(MapBorderRule rule)
        => string.Join("|",
            rule.Name.Trim(),
            rule.Enabled,
            rule.RequireTargetAffixCount,
            rule.MinimumMatches,
            rule.ColorArgb,
            string.Join(",", rule.SelectedGeneratedStatIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            string.Join(",", rule.SelectedAffixGroupIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
}

public sealed class MapAffixRuleGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Group";
    public bool Enabled { get; set; } = true;
    public int MinimumMatchedAffixes { get; set; } = 1;
    public int ColorArgb { get; set; } = Color.DeepSkyBlue.ToArgb();
    public List<string> SelectedAffixIds { get; set; } = [];

    [JsonIgnore]
    public Color Color
    {
        get => Color.FromArgb(ColorArgb);
        set => ColorArgb = value.ToArgb();
    }

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name))
            Name = "New Group";
        if (MinimumMatchedAffixes < 1)
            MinimumMatchedAffixes = 1;

        SelectedAffixIds ??= [];
        SelectedAffixIds = SelectedAffixIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class MapBorderRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Border Rule";
    public bool Enabled { get; set; } = true;
    public bool RequireTargetAffixCount { get; set; }
    public int MinimumMatches { get; set; } = 1;
    public int ColorArgb { get; set; } = Color.DeepSkyBlue.ToArgb();
    public List<string> SelectedGeneratedStatIds { get; set; } = [];
    public List<string> SelectedAffixGroupIds { get; set; } = [];

    [JsonIgnore]
    public Color Color
    {
        get => Color.FromArgb(ColorArgb);
        set => ColorArgb = value.ToArgb();
    }

    public void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(Id))
            Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Name))
            Name = "New Border Rule";
        if (MinimumMatches < 1)
            MinimumMatches = 1;

        SelectedGeneratedStatIds ??= [];
        SelectedGeneratedStatIds = SelectedGeneratedStatIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SelectedAffixGroupIds ??= [];
        SelectedAffixGroupIds = SelectedAffixGroupIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
