using System;
using System.Collections.Generic;
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
