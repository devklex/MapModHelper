using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MapModHelper;

internal sealed class MapScorer
{
    private static readonly Regex PercentRegex = new(@"([+-]?\d+)\s*%", RegexOptions.Compiled);
    private static readonly Regex PlaceholderPercentRegex = new(@"#\s*%", RegexOptions.Compiled);

    public MapScore Score(MapItemSnapshot item, MapModHelperSettings settings)
    {
        var affixCount = item.ExplicitAffixCount > 0 ? item.ExplicitAffixCount : CountFallbackAffixLines(item.ModLines);
        var displayedStatDefinitions = settings.HighlightImportantAffixes.Value
            ? GetEnabledImportantStats(settings).ToList()
            : [];
        var displayedStatIds = displayedStatDefinitions
            .Select(definition => definition.StatId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackedStatDefinitions = displayedStatDefinitions
            .Concat(GetBorderRuleStatDefinitions(settings))
            .GroupBy(definition => definition.StatId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var trackedStats = trackedStatDefinitions
            .Select(definition => ToImportantStat(item, definition))
            .Where(stat => stat.Value > 0)
            .ToList();
        var importantStats = trackedStats
            .Where(stat => displayedStatIds.Contains(stat.StatId))
            .ToList();
        var trackedAffixGroupMatches = settings.EnableAffixGroups.Value && settings.AffixGroups.Count > 0
            ? GetAffixGroupMatches(item, settings).ToList()
            : [];
        var affixGroupMatches = settings.ShowAffixGroupBadges.Value
            ? trackedAffixGroupMatches
            : [];
        var hasTargetAffixCount = affixCount >= settings.TargetAffixCount.Value;
        var hasAffixCountBadge = settings.ShowAffixCountBadge.Value && hasTargetAffixCount;
        var borderRuleMatches = settings.EnableBorderRules.Value
            ? GetBorderRuleMatches(settings, hasTargetAffixCount, trackedStats, trackedAffixGroupMatches).ToList()
            : [];

        if (!hasAffixCountBadge
            && importantStats.Count == 0
            && affixGroupMatches.Count == 0
            && borderRuleMatches.Count == 0)
            return MapScore.None;

        return new MapScore(
            affixCount,
            hasTargetAffixCount,
            hasAffixCountBadge,
            importantStats,
            affixGroupMatches,
            borderRuleMatches);
    }

    private static IEnumerable<MapAffixGroupMatch> GetAffixGroupMatches(MapItemSnapshot item, MapModHelperSettings settings)
    {
        var mods = item.ExplicitMods.Count > 0 ? item.ExplicitMods : item.AllMods;

        foreach (var group in settings.AffixGroups)
        {
            if (!group.Enabled || group.SelectedAffixIds.Count == 0)
                continue;

            var matchedAffixes = 0;
            var selectedCount = 0;
            foreach (var affixId in group.SelectedAffixIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var affix = MapAffixCatalog.Find(affixId);
                if (affix == null)
                    continue;

                selectedCount++;
                if (mods.Any(affix.Matches))
                    matchedAffixes++;
            }

            if (selectedCount > 0 && matchedAffixes >= group.MinimumMatchedAffixes)
                yield return new MapAffixGroupMatch(group.Id, group.Name, group.Color, matchedAffixes, selectedCount);
        }
    }

    private static IEnumerable<MapGeneratedStatDefinition> GetBorderRuleStatDefinitions(MapModHelperSettings settings)
    {
        if (!settings.EnableBorderRules.Value)
            yield break;

        foreach (var statId in settings.BorderRules
                     .Where(rule => rule.Enabled)
                     .SelectMany(rule => rule.SelectedGeneratedStatIds)
                     .Where(MapStatData.IsTrackedStat)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return MapStatData.DefinitionFor(statId);
        }
    }

    private static IEnumerable<MapBorderRuleMatch> GetBorderRuleMatches(
        MapModHelperSettings settings,
        bool hasTargetAffixCount,
        IReadOnlyList<MapImportantStatScore> trackedStats,
        IReadOnlyList<MapAffixGroupMatch> affixGroupMatches)
    {
        foreach (var rule in settings.BorderRules)
        {
            if (!rule.Enabled)
                continue;

            var selectedConditions = 0;
            var matchedConditions = 0;

            if (rule.RequireTargetAffixCount)
            {
                selectedConditions++;
                if (hasTargetAffixCount)
                    matchedConditions++;
            }

            foreach (var statId in rule.SelectedGeneratedStatIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!MapStatData.IsTrackedStat(statId))
                    continue;

                selectedConditions++;
                if (trackedStats.Any(stat => string.Equals(stat.StatId, statId, StringComparison.OrdinalIgnoreCase)))
                    matchedConditions++;
            }

            foreach (var groupId in rule.SelectedAffixGroupIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                selectedConditions++;
                if (affixGroupMatches.Any(group => string.Equals(group.GroupId, groupId, StringComparison.OrdinalIgnoreCase)))
                    matchedConditions++;
            }

            if (selectedConditions == 0)
                continue;

            var required = Math.Clamp(rule.MinimumMatches, 1, selectedConditions);
            if (matchedConditions >= required)
                yield return new MapBorderRuleMatch(rule.Name, rule.Color, matchedConditions, selectedConditions);
        }
    }

    private static IEnumerable<MapGeneratedStatDefinition> GetEnabledImportantStats(MapModHelperSettings settings)
    {
        if (settings.HighlightMonsterEffectiveness.Value)
            yield return MapStatData.DefinitionFor(MapStatData.MonsterEffectivenessStat);
        if (settings.HighlightItemRarity.Value)
            yield return MapStatData.DefinitionFor(MapStatData.ItemRarityStat);
        if (settings.HighlightMonsterPackSize.Value)
            yield return MapStatData.DefinitionFor(MapStatData.PackSizeStat);
        if (settings.HighlightMonsterRarity.Value)
            yield return MapStatData.DefinitionFor(MapStatData.MonsterRarityStat);
        if (settings.HighlightWaystoneDropChance.Value)
            yield return MapStatData.DefinitionFor(MapStatData.WaystoneDropChanceStat);
    }

    private static MapImportantStatScore ToImportantStat(MapItemSnapshot item, MapGeneratedStatDefinition definition)
    {
        var generatedValue = BestGeneratedPropertyValue(item, definition.StatId);
        var value = generatedValue > 0
            ? generatedValue
            : BestTooltipPropertyValue(item, definition.DisplayName);

        if (value <= 0 && string.Equals(definition.StatId, MapStatData.MonsterEffectivenessStat, StringComparison.OrdinalIgnoreCase))
            value = FindBestValue(item, IsEffectivenessMod);
        else if (value <= 0 && string.Equals(definition.StatId, MapStatData.ItemRarityStat, StringComparison.OrdinalIgnoreCase))
            value = FindBestValue(item, IsRarityMod);

        return new MapImportantStatScore(definition.BadgeLabel, definition.DisplayName, definition.StatId, value);
    }

    private static int FindBestValue(MapItemSnapshot item, Func<string, bool> matcher)
    {
        var best = 0;

        var mods = item.AllMods.Count > 0 ? item.AllMods : item.ExplicitMods;
        foreach (var mod in mods)
        {
            if (!matcher(mod.SearchText))
                continue;

            best = Math.Max(best, BestValueFromMod(mod));
        }

        foreach (var line in item.ModLines)
        {
            if (!matcher(line.ToLowerInvariant()))
                continue;

            best = Math.Max(best, BestPercentValue(line));
        }

        return best;
    }

    private static int BestTooltipPropertyValue(MapItemSnapshot item, string propertyName)
    {
        var best = 0;
        foreach (var property in item.TooltipProperties)
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            best = Math.Max(best, (int)Math.Round(Math.Abs(property.Value)));
        }

        return best;
    }

    private static int BestGeneratedPropertyValue(MapItemSnapshot item, string statId)
    {
        var best = 0;
        foreach (var property in item.GeneratedProperties)
        {
            if (!string.Equals(property.StatId, statId, StringComparison.OrdinalIgnoreCase))
                continue;

            best = Math.Max(best, property.Value);
        }

        return best;
    }

    private static bool IsEffectivenessMod(string text)
    {
        var normalized = NormalizeText(text);
        return normalized.Contains("monstereffectiveness")
               || (normalized.Contains("monster") && normalized.Contains("effectiveness"));
    }

    private static bool IsRarityMod(string text)
    {
        var normalized = NormalizeText(text);
        return (normalized.Contains("itemrarity")
                || normalized.Contains("rarityofitemsfoundinmap")
                || (normalized.Contains("rarity") && normalized.Contains("items") && normalized.Contains("found") && normalized.Contains("map")))
               && !normalized.Contains("waystone");
    }

    private static int BestValueFromMod(MapExplicitModInfo mod)
    {
        var bestTextValue = BestPercentValue(string.Join(" ", mod.DisplayName, mod.Translation, mod.Name, mod.RawName));
        var bestValue = mod.Values.Count == 0 ? 0 : mod.Values.Select(Math.Abs).DefaultIfEmpty(0).Max();

        if (bestTextValue > 0)
            return bestTextValue;

        return bestValue;
    }

    private static int BestPercentValue(string text)
    {
        var best = 0;
        foreach (Match match in PercentRegex.Matches(text))
            if (int.TryParse(match.Groups[1].Value, out var value))
                best = Math.Max(best, Math.Abs(value));

        return best;
    }

    private static int CountFallbackAffixLines(IEnumerable<string> lines)
    {
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => PlaceholderPercentRegex.Replace(line, "%"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(line => line.Contains('%') || line.Contains("Map ", StringComparison.OrdinalIgnoreCase) || line.Contains("Monsters ", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeText(string text)
    {
        return new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }
}

internal sealed record MapScore(
    int ExplicitAffixCount,
    bool HasTargetAffixCount,
    bool HasAffixCountBadge,
    IReadOnlyList<MapImportantStatScore> ImportantStats,
    IReadOnlyList<MapAffixGroupMatch> AffixGroupMatches,
    IReadOnlyList<MapBorderRuleMatch> BorderRuleMatches)
{
    public static MapScore None { get; } = new(0, false, false, Array.Empty<MapImportantStatScore>(), Array.Empty<MapAffixGroupMatch>(), Array.Empty<MapBorderRuleMatch>());
    public bool HasMatch => HasAffixCountBadge || ImportantStats.Count > 0 || AffixGroupMatches.Count > 0 || HasBorderHighlight;
    public bool HasBorderHighlight => BorderRuleMatches.Count > 0;
    public System.Drawing.Color BorderColor => BorderRuleMatches.Count > 0 ? BorderRuleMatches[0].Color : System.Drawing.Color.Empty;
    public int ImportantAffixCount => ImportantStats.Count;
}

internal sealed record MapImportantStatScore(
    string BadgeLabel,
    string Name,
    string StatId,
    int Value);

internal sealed record MapAffixGroupMatch(
    string GroupId,
    string Name,
    System.Drawing.Color Color,
    int MatchedAffixes,
    int SelectedAffixes)
{
    public string BadgeLabel => SelectedAffixes <= 1 ? MatchedAffixes.ToString() : $"{MatchedAffixes}/{SelectedAffixes}";
}

internal sealed record MapBorderRuleMatch(
    string Name,
    System.Drawing.Color Color,
    int MatchedConditions,
    int SelectedConditions);
