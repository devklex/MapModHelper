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
    public ToggleNode HighlightEightAffixMaps { get; set; } = new(true);
    public ToggleNode HighlightImportantAffixes { get; set; } = new(true);
    public ToggleNode HighlightMonsterEffectiveness { get; set; } = new(true);
    public ToggleNode HighlightItemRarity { get; set; } = new(true);
    public ToggleNode HighlightMonsterPackSize { get; set; } = new(false);
    public ToggleNode HighlightMonsterRarity { get; set; } = new(false);
    public ToggleNode HighlightWaystoneDropChance { get; set; } = new(false);
    public ToggleNode ShowAffixCountBadge { get; set; } = new(true);
    public ToggleNode ShowImportantAffixBadges { get; set; } = new(true);
    public ToggleNode EnableAffixGroups { get; set; } = new(true);
    public ToggleNode ShowAffixGroupBadges { get; set; } = new(true);
    public ToggleNode HideWhenTooltipOverItem { get; set; } = new(true);
    public ToggleNode LogMatchedMaps { get; set; } = new(false);
    public ToggleNode LogScannedMapSamples { get; set; } = new(false);
    public ToggleNode LogPerformance { get; set; } = new(false);

    public RangeNode<int> ScanIntervalMs { get; set; } = new(650, 150, 2000);
    public RangeNode<int> TargetAffixCount { get; set; } = new(8, 4, 12);
    public RangeNode<int> BlueMaxPercent { get; set; } = new(20, 1, 100);
    public RangeNode<int> OrangeMaxPercent { get; set; } = new(28, 1, 100);
    public RangeNode<int> RedMinPercent { get; set; } = new(29, 1, 100);
    public RangeNode<int> DeepRedMinPercent { get; set; } = new(50, 1, 150);
    public RangeNode<int> BaseBorderThickness { get; set; } = new(2, 1, 8);
    public RangeNode<int> MaxBorderThickness { get; set; } = new(6, 1, 12);
    public RangeNode<int> AffixGroupBadgeStyle { get; set; } = new((int)MapAffixGroupBadgeStyle.OneBlockPerGroup, 0, 2);
    public RangeNode<int> AffixGroupMaxBlocks { get; set; } = new(6, 1, 16);
    public RangeNode<float> BadgeScale { get; set; } = new(0.9f, 0.5f, 1.8f);

    public ColorNode EightAffixColor { get; set; } = new(Color.DeepSkyBlue);
    public ColorNode LowImportantColor { get; set; } = new(Color.DeepSkyBlue);
    public ColorNode MediumImportantColor { get; set; } = new(Color.Orange);
    public ColorNode HighImportantColor { get; set; } = new(Color.Red);
    public ColorNode BestImportantColor { get; set; } = new(Color.Firebrick);
    public ColorNode BadgeBackgroundColor { get; set; } = new(Color.FromArgb(220, 0, 0, 0));
    public ColorNode BadgeTextColor { get; set; } = new(Color.White);

    public List<MapAffixRuleGroup> AffixGroups { get; set; } = [];

    public void EnsureDefaults()
    {
        AffixGroups ??= [];
        foreach (var group in AffixGroups)
            group.EnsureDefaults();
    }
}

public enum MapAffixGroupBadgeStyle
{
    TextCounts = 0,
    OneBlockPerGroup = 1,
    MatchedAffixBlocks = 2
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
