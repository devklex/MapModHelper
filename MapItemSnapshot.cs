using System.Collections.Generic;
using System.Linq;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace MapModHelper;

internal sealed class MapItemSnapshot
{
    public Entity? Entity { get; init; }
    public Element? Element { get; init; }
    public RectangleF Rect { get; init; }
    public string Source { get; init; } = string.Empty;
    public string BaseName { get; init; } = string.Empty;
    public string UniqueName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public ItemRarity Rarity { get; init; }
    public bool IsIdentified { get; init; }
    public int ExplicitAffixCount { get; init; }
    public List<string> ModLines { get; init; } = [];
    public List<MapExplicitModInfo> ExplicitMods { get; init; } = [];
    public List<MapExplicitModInfo> AllMods { get; init; } = [];
    public List<MapGeneratedPropertyInfo> GeneratedProperties { get; init; } = [];
    public List<MapTooltipPropertyInfo> TooltipProperties { get; set; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(UniqueName) ? BaseName : UniqueName;
}

internal sealed record MapExplicitModInfo(
    string Name,
    string RawName,
    string DisplayName,
    string Group,
    string Translation,
    IReadOnlyList<int> Values)
{
    public string SearchText => string.Join(" ", new[] { Name, RawName, DisplayName, Group, Translation }
        .Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
}

internal sealed record MapTooltipPropertyInfo(
    string Name,
    string TextNoTags,
    double Value,
    double? MinValue,
    double? MaxValue);

internal sealed record MapGeneratedPropertyInfo(
    string Name,
    string StatId,
    int Value);
