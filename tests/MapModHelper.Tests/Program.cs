using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MapModHelper;

var data = MapStatData.LoadDefault(out var message);
Require(data != null, message);

AssertComputedStats(
    data!,
    new MapExplicitModInfo("MapMonsterEvasive1", "", "", "", "", new[] { 55, 15, 6 }),
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [MapStatData.WaystoneDropChanceStat] = 15,
        [MapStatData.PackSizeStat] = 6
    });

AssertComputedStats(
    data!,
    new MapExplicitModInfo("MapPlayerEnfeeble1", "", "", "", "", new[] { 0, 20, 16 }),
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [MapStatData.WaystoneDropChanceStat] = 20,
        [MapStatData.MonsterEffectivenessStat] = 16
    });

AssertComputedStats(
    data!,
    new MapExplicitModInfo("MapMonstersElementalPenetration4", "", "", "", "", new[] { 16, 20, 16 }),
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [MapStatData.WaystoneDropChanceStat] = 20,
        [MapStatData.ItemRarityStat] = 16
    });

AssertComputedStats(
    data!,
    new MapExplicitModInfo("MapMonsterDamageIncrease4", "", "", "", "", new[] { 24, 20, 25 }),
    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [MapStatData.WaystoneDropChanceStat] = 20,
        [MapStatData.MonsterRarityStat] = 25
    });

AssertHiddenBadgesDoNotCreateVisibleMatches();
AssertBorderRulesCanUseHiddenGeneratedStats();
AssertZeroAffixTargetCanCreateAffixCountMatches();
AssertBorderRulesDoNotGrowOnEnsureDefaults();

Console.WriteLine("MapModHelper generated stat tests passed.");

static void AssertComputedStats(MapStatData data, MapExplicitModInfo mod, IReadOnlyDictionary<string, int> expected)
{
    var actual = data.ComputeProperties(new[] { mod })
        .ToDictionary(property => property.StatId, property => property.Value, StringComparer.OrdinalIgnoreCase);

    foreach (var pair in expected)
    {
        Require(actual.TryGetValue(pair.Key, out var value), $"Missing expected stat {pair.Key} for {mod.Name}.");
        Require(value == pair.Value, $"Expected {pair.Key}={pair.Value} for {mod.Name}, got {value}.");
    }

    foreach (var unexpected in actual.Keys.Where(key => !expected.ContainsKey(key)).ToList())
        throw new InvalidOperationException($"Unexpected tracked stat {unexpected} for {mod.Name}.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertHiddenBadgesDoNotCreateVisibleMatches()
{
    var settings = new MapModHelperSettings();
    settings.ShowAffixCountBadge.Value = false;
    settings.HighlightImportantAffixes.Value = false;
    settings.EnableAffixGroups.Value = false;
    settings.EnableBorderRules.Value = false;

    var item = new MapItemSnapshot
    {
        ExplicitAffixCount = 8,
        GeneratedProperties =
        [
            new MapGeneratedPropertyInfo("Monster Effectiveness", MapStatData.MonsterEffectivenessStat, 30)
        ]
    };

    var score = new MapScorer().Score(item, settings);
    Require(!score.HasMatch, "Hidden affix-count and generated-stat badges should not create a visible match by themselves.");
}

static void AssertBorderRulesCanUseHiddenGeneratedStats()
{
    var settings = new MapModHelperSettings();
    settings.ShowAffixCountBadge.Value = false;
    settings.HighlightImportantAffixes.Value = false;
    settings.EnableAffixGroups.Value = false;
    settings.EnableBorderRules.Value = true;
    settings.BorderRules =
    [
        new MapBorderRule
        {
            Name = "Effectiveness border",
            Color = Color.Red,
            SelectedGeneratedStatIds = [MapStatData.MonsterEffectivenessStat]
        }
    ];

    var item = new MapItemSnapshot
    {
        ExplicitAffixCount = 1,
        GeneratedProperties =
        [
            new MapGeneratedPropertyInfo("Monster Effectiveness", MapStatData.MonsterEffectivenessStat, 30)
        ]
    };

    var score = new MapScorer().Score(item, settings);
    Require(score.HasBorderHighlight, "Border rules should still be able to use hidden generated-stat badge conditions.");
    Require(score.BorderColor.ToArgb() == Color.Red.ToArgb(), "Border rule color should come from the first matching border rule.");
}

static void AssertZeroAffixTargetCanCreateAffixCountMatches()
{
    var settings = new MapModHelperSettings();
    settings.TargetAffixCount.Value = 0;
    settings.ShowAffixCountBadge.Value = true;
    settings.HighlightImportantAffixes.Value = false;
    settings.EnableAffixGroups.Value = false;
    settings.EnableBorderRules.Value = false;

    var item = new MapItemSnapshot
    {
        ExplicitAffixCount = 0
    };

    var score = new MapScorer().Score(item, settings);
    Require(score.HasTargetAffixCount, "A target affix count of 0 should match zero-affix maps.");
    Require(score.HasAffixCountBadge, "A visible affix-count badge should be tracked separately from the target-count condition.");
    Require(score.HasMatch, "Visible affix-count badges should create a match when the configured target is met.");
}

static void AssertBorderRulesDoNotGrowOnEnsureDefaults()
{
    var settings = new MapModHelperSettings();
    Require(settings.BorderRules.Count == 0, "Border rules should not be created by the settings constructor.");

    settings.BorderRules =
    [
        new MapBorderRule
        {
            Name = "Target affix count",
            RequireTargetAffixCount = true,
            Color = Color.DeepSkyBlue
        },
        new MapBorderRule
        {
            Name = "Target affix count",
            RequireTargetAffixCount = true,
            Color = Color.DeepSkyBlue
        }
    ];

    settings.EnsureDefaults();
    settings.EnsureDefaults();
    Require(settings.BorderRules.Count == 1, "EnsureDefaults should remove duplicate border rules and should not create new ones on reload.");
}
