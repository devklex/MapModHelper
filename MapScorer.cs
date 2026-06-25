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
        var importantStats = settings.HighlightImportantAffixes.Value
            ? GetEnabledImportantStats(settings)
                .Select(definition => ToImportantStat(item, definition))
                .Where(stat => stat.Value > 0)
                .ToList()
            : [];
        var affixGroupMatches = settings.EnableAffixGroups.Value && settings.ShowAffixGroupBadges.Value && settings.AffixGroups.Count > 0
            ? GetAffixGroupMatches(item, settings).ToList()
            : [];
        var hasEightAffixes = settings.HighlightEightAffixMaps.Value && affixCount >= settings.TargetAffixCount.Value;
        var importantCount = importantStats.Count;

        if (!hasEightAffixes && importantCount == 0 && affixGroupMatches.Count == 0)
            return MapScore.None;

        var intensity = importantStats.Count == 0
            ? 0f
            : importantStats.Max(stat => Math.Clamp(stat.Value / Math.Max(1f, settings.DeepRedMinPercent.Value), 0f, 1f));
        if (importantCount >= 2)
            intensity = Math.Min(1f, intensity + 0.18f);

        return new MapScore(
            affixCount,
            hasEightAffixes,
            importantStats,
            affixGroupMatches,
            intensity);
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
                yield return new MapAffixGroupMatch(group.Name, group.Color, matchedAffixes, selectedCount);
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
    bool HasEightAffixes,
    IReadOnlyList<MapImportantStatScore> ImportantStats,
    IReadOnlyList<MapAffixGroupMatch> AffixGroupMatches,
    float Intensity)
{
    public static MapScore None { get; } = new(0, false, Array.Empty<MapImportantStatScore>(), Array.Empty<MapAffixGroupMatch>(), 0f);
    public bool HasMatch => HasBorderHighlight || AffixGroupMatches.Count > 0;
    public bool HasBorderHighlight => HasEightAffixes || ImportantAffixCount > 0;
    public int ImportantAffixCount => ImportantStats.Count;
    public bool HasBothImportantAffixes => EffectivenessPercent > 0 && RarityPercent > 0;
    public int HighestImportantPercent => ImportantStats.Select(stat => stat.Value).DefaultIfEmpty(0).Max();
    public int EffectivenessPercent => StatValue(MapStatData.MonsterEffectivenessStat);
    public int RarityPercent => StatValue(MapStatData.ItemRarityStat);

    private int StatValue(string statId)
        => ImportantStats
            .Where(stat => string.Equals(stat.StatId, statId, StringComparison.OrdinalIgnoreCase))
            .Select(stat => stat.Value)
            .DefaultIfEmpty(0)
            .Max();
}

internal sealed record MapImportantStatScore(
    string BadgeLabel,
    string Name,
    string StatId,
    int Value);

internal sealed record MapAffixGroupMatch(
    string Name,
    System.Drawing.Color Color,
    int MatchedAffixes,
    int SelectedAffixes)
{
    public string BadgeLabel => SelectedAffixes <= 1 ? MatchedAffixes.ToString() : $"{MatchedAffixes}/{SelectedAffixes}";
}
