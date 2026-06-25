using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace MapModHelper;

public sealed class MapModHelper : BaseSettingsPlugin<MapModHelperSettings>
{
    private const string PluginVersion = "v0.1";

    private readonly Dictionary<long, ScoredVisibleMap> _visibleMaps = new();
    private readonly Dictionary<long, string> _suppressedEntityCells = new();
    private readonly Dictionary<long, List<MapTooltipPropertyInfo>> _tooltipPropertyCache = new();
    private readonly Dictionary<string, string> _affixGroupSearch = new(StringComparer.Ordinal);
    private readonly HashSet<string> _newlyAddedAffixGroupsToOpen = new(StringComparer.Ordinal);
    private MapStatData? _mapStatData;
    private MapItemReader? _itemReader;
    private MapScorer? _scorer;
    private MapItemSnapshot? _lastHoveredMap;
    private long _lastScanMs;
    private long _lastPerformanceLogMs;
    private long _lastSampleLogMs;
    private long _lastMatchedLogMs;
    private int _lastContextHash;
    private string _lastPerformanceSummary = string.Empty;
    private int _lastVisibleItems;
    private int _lastKnownMaps;
    private int _lastMatchedMaps;

    public override bool Initialise()
    {
        Settings.EnsureDefaults();
        _mapStatData = MapStatData.LoadDefault(out var loadMessage);
        MapAffixCatalog.Load(_mapStatData?.Affixes);
        DebugWindow.LogMsg("[MapModHelper] " + loadMessage, 5);
        _itemReader = new MapItemReader(GameController, _mapStatData);
        _scorer = new MapScorer();
        return base.Initialise();
    }

    public override void Tick()
    {
        if (!Settings.Enable.Value)
        {
            ClearRuntimeState();
            return;
        }

        if (!AnySupportedWindowVisible())
        {
            ClearRuntimeState();
            return;
        }

        var now = Environment.TickCount64;
        var contextHash = _itemReader?.GetVisibleContextHash() ?? 0;
        if (contextHash != _lastContextHash)
        {
            _lastContextHash = contextHash;
            ClearVisibleScanState();
        }

        if (now - _lastScanMs < Math.Max(100, Settings.ScanIntervalMs.Value))
            return;

        _lastScanMs = now;
        ScanVisibleMaps();
    }

    public override void Render()
    {
        if (!Settings.Enable.Value || !Settings.OverlayEnabled.Value)
            return;

        if (!AnySupportedWindowVisible())
            return;

        var hoveredItem = ReadHoveredMap(out var tooltipRect);
        MaybeCacheHoveredProperties(hoveredItem);
        MaybeAddHoveredMapMatch(hoveredItem);
        if (_visibleMaps.Count == 0)
            return;

        IDisposable? textScaleScope = null;
        try
        {
            textScaleScope = Graphics.SetTextScale(Settings.BadgeScale.Value);

            var staleKeys = new List<long>();
            foreach (var pair in _visibleMaps)
            {
                var scored = pair.Value;
                if (!IsUsableRect(scored.Item.Rect) || !scored.Score.HasMatch)
                    continue;
                if (!IsLiveVisibleSnapshot(scored.Item))
                {
                    staleKeys.Add(pair.Key);
                    continue;
                }

                if (Settings.HideWhenTooltipOverItem.Value && ShouldHideOverlay(scored.Item, hoveredItem, tooltipRect))
                    continue;

                var color = GetScoreColor(scored.Score);
                if (scored.Score.HasBorderHighlight)
                    DrawBorderHighlight(scored.Item.Rect, color, GetBorderThickness(scored.Score));
                DrawBadges(scored.Item.Rect, scored.Score);
            }

            foreach (var key in staleKeys)
                _visibleMaps.Remove(key);
        }
        finally
        {
            textScaleScope?.Dispose();
        }
    }

    public override void DrawSettings()
    {
        Settings.EnsureDefaults();
        if (!ImGui.CollapsingHeader("Map Mod Helper###map_mod_helper_general", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();
        ImGui.TextDisabled($"Map Mod Helper {PluginVersion}");
        ImGui.TextDisabled("Supports controller and keyboard/mouse stash and inventory UI.");
        ImGui.TextDisabled($"Last scan: visible items {_lastVisibleItems}, maps {_lastKnownMaps}, matched maps {_lastMatchedMaps}");
        if (!string.IsNullOrWhiteSpace(_lastPerformanceSummary))
            ImGui.TextDisabled(_lastPerformanceSummary);

        Checkbox("Enable", Settings.Enable);
        Checkbox("Enable overlay", Settings.OverlayEnabled);
        Checkbox("Highlight 8-affix maps", Settings.HighlightEightAffixMaps);
        Checkbox("Highlight selected generated map stats", Settings.HighlightImportantAffixes);
        ImGui.Indent();
        Checkbox("Monster Effectiveness (E)", Settings.HighlightMonsterEffectiveness);
        Checkbox("Item Rarity (R)", Settings.HighlightItemRarity);
        Checkbox("Pack Size (P)", Settings.HighlightMonsterPackSize);
        Checkbox("Monster Rarity (MR)", Settings.HighlightMonsterRarity);
        Checkbox("Waystone Drop Chance (W)", Settings.HighlightWaystoneDropChance);
        ImGui.Unindent();
        Checkbox("Show affix-count badge", Settings.ShowAffixCountBadge);
        Checkbox("Show important-affix badges", Settings.ShowImportantAffixBadges);
        Checkbox("Hide overlay when item tooltip covers item", Settings.HideWhenTooltipOverItem);
        Checkbox("Log matched maps", Settings.LogMatchedMaps);
        Checkbox("Log scanned map samples", Settings.LogScannedMapSamples);
        Checkbox("Log performance", Settings.LogPerformance);

        ImGui.Separator();
        if (ImGui.Button("Dump last hovered map stats"))
            DumpLastHoveredMapDebug();
        ImGui.SameLine();
        if (ImGui.Button("Dump raw components"))
            DumpLastHoveredMapRawComponents();

        ImGui.Separator();
        SliderInt("Scan interval ms", Settings.ScanIntervalMs);
        SliderInt("Target affix count", Settings.TargetAffixCount);
        SliderInt("Blue max %", Settings.BlueMaxPercent);
        SliderInt("Orange max %", Settings.OrangeMaxPercent);
        SliderInt("Red min %", Settings.RedMinPercent);
        SliderInt("Deep red min %", Settings.DeepRedMinPercent);
        SliderInt("Base border thickness", Settings.BaseBorderThickness);
        SliderInt("Max border thickness", Settings.MaxBorderThickness);
        SliderFloat("Badge scale", Settings.BadgeScale);
        ImGui.TextDisabled("Top-left: affix count. Top-right: selected generated stats: E, R, P, MR, W.");
        ImGui.Separator();
        DrawAffixGroupSettings();
        ImGui.Unindent();
    }

    private void DrawAffixGroupSettings()
    {
        if (!ImGui.CollapsingHeader($"Affix Groups ({Settings.AffixGroups.Count})###map_affix_groups", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Indent();
        Checkbox("Enable affix group matching", Settings.EnableAffixGroups);
        Checkbox("Show affix group badges", Settings.ShowAffixGroupBadges);
        DrawAffixGroupBadgeStyleSelector();
        if ((MapAffixGroupBadgeStyle)Settings.AffixGroupBadgeStyle.Value == MapAffixGroupBadgeStyle.MatchedAffixBlocks)
            SliderInt("Max group blocks per item", Settings.AffixGroupMaxBlocks);
        ImGui.SameLine();
        if (ImGui.Button("Add Group"))
        {
            var group = new MapAffixRuleGroup
            {
                Name = GetNextAffixGroupName(),
                Color = DefaultGroupColor(Settings.AffixGroups.Count)
            };
            Settings.AffixGroups.Add(group);
            _newlyAddedAffixGroupsToOpen.Add(group.Id);
        }

        ImGui.TextDisabled("Groups match selected affix families. Per-map values are shown separately by E/R/P/MR/W badges.");

        if (Settings.AffixGroups.Count == 0)
            ImGui.TextDisabled("No groups yet. Add Group creates a custom affix badge rule.");

        for (var i = 0; i < Settings.AffixGroups.Count; i++)
            DrawAffixGroupEditor(i);

        ImGui.Unindent();
    }

    private void DrawAffixGroupEditor(int index)
    {
        var group = Settings.AffixGroups[index];
        group.EnsureDefaults();

        ImGui.PushID(group.Id);
        var selectedCount = group.SelectedAffixIds?.Count ?? 0;
        var header = $"{group.Name}  [{selectedCount} selected]";
        if (_newlyAddedAffixGroupsToOpen.Remove(group.Id))
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);

        if (ImGui.TreeNodeEx($"{header}###map_affix_group_editor", ImGuiTreeNodeFlags.None))
        {
            var enabled = group.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
                group.Enabled = enabled;

            ImGui.SameLine();
            if (ImGui.SmallButton("Move Up") && index > 0)
            {
                (Settings.AffixGroups[index - 1], Settings.AffixGroups[index]) = (Settings.AffixGroups[index], Settings.AffixGroups[index - 1]);
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Move Down") && index < Settings.AffixGroups.Count - 1)
            {
                (Settings.AffixGroups[index + 1], Settings.AffixGroups[index]) = (Settings.AffixGroups[index], Settings.AffixGroups[index + 1]);
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Delete"))
            {
                Settings.AffixGroups.RemoveAt(index);
                ImGui.TreePop();
                ImGui.PopID();
                return;
            }

            var name = group.Name ?? string.Empty;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText("Name", ref name, 96))
                group.Name = name;

            var minimum = group.MinimumMatchedAffixes;
            ImGui.SetNextItemWidth(90);
            if (ImGui.InputInt("Minimum matched affixes", ref minimum, 1, 1))
                group.MinimumMatchedAffixes = Math.Clamp(minimum, 1, 8);

            DrawColorEdit("Group color", group.Color, color => group.Color = color);

            var searchKey = group.Id;
            if (!_affixGroupSearch.TryGetValue(searchKey, out var search))
                search = string.Empty;

            ImGui.SetNextItemWidth(320);
            if (ImGui.InputText("Search affixes", ref search, 128))
                _affixGroupSearch[searchKey] = search;

            ImGui.SameLine();
            if (ImGui.SmallButton("Clear All"))
                (group.SelectedAffixIds ??= []).Clear();

            DrawAffixSelectionList(group, search);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawAffixSelectionList(MapAffixRuleGroup group, string search)
    {
        var affixes = MapAffixCatalog.All
            .Where(affix => string.IsNullOrWhiteSpace(search)
                            || affix.ShortLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || affix.Label.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || affix.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || affix.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = group.SelectedAffixIds ??= [];
        var childHeight = Math.Clamp(affixes.Count * 24 + 28, 150, 430);
        if (ImGui.BeginChild("##map_affix_list", new Vector2(0, childHeight), ImGuiChildFlags.Border))
        {
            foreach (var affix in affixes)
            {
                var isSelected = selected.Contains(affix.Id, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{affix.ShortLabel}##{affix.Id}", ref isSelected))
                {
                    if (isSelected)
                    {
                        if (!selected.Contains(affix.Id, StringComparer.OrdinalIgnoreCase))
                            selected.Add(affix.Id);
                    }
                    else
                    {
                        selected.RemoveAll(x => string.Equals(x, affix.Id, StringComparison.OrdinalIgnoreCase));
                    }
                }
            }

            if (affixes.Count == 0)
                ImGui.TextDisabled("No affixes match the search.");
        }

        ImGui.EndChild();
    }

    private void ScanVisibleMaps()
    {
        if (_itemReader == null || _scorer == null)
            return;

        var stopwatch = Settings.LogPerformance.Value ? Stopwatch.StartNew() : null;
        var current = new Dictionary<long, ScoredVisibleMap>();
        var candidates = new List<ScoredMapCandidate>();
        var items = _itemReader.ReadAllVisibleItems();
        var knownMaps = 0;
        var sampleLogLines = new List<string>();
        var matchedLogLines = new List<string>();

        foreach (var item in items)
        {
            if (item.Entity == null || !IsUsableRect(item.Rect) || !IsLiveVisibleSnapshot(item))
                continue;

            knownMaps++;
            var key = GetEntityKey(item);
            ApplyCachedTooltipProperties(key, item);
            var score = _scorer.Score(item, Settings);
            if (!score.HasMatch)
            {
                if (Settings.LogScannedMapSamples.Value && sampleLogLines.Count < 12)
                    sampleLogLines.Add(DescribeMapSample(item, "no match"));
                continue;
            }

            var cellKey = GetCellKey(item.Rect);
            if (IsSuppressedAtCell(key, cellKey))
                continue;

            candidates.Add(new ScoredMapCandidate(key, cellKey, new ScoredVisibleMap(item, score)));

            if (Settings.LogMatchedMaps.Value && matchedLogLines.Count < 20)
                matchedLogLines.Add($"{item.DisplayName}: affixes={score.ExplicitAffixCount}, {FormatImportantStats(score)}");
        }

        foreach (var group in candidates.GroupBy(x => x.CellKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(x => MapPriority(x.Scored.Score))
                .ThenByDescending(x => Area(x.Scored.Item.Rect))
                .ThenByDescending(x => x.Key)
                .ToList();

            var chosen = ordered[0];
            current[chosen.Key] = chosen.Scored;

            foreach (var duplicate in ordered.Skip(1))
                _suppressedEntityCells[duplicate.Key] = duplicate.CellKey;
        }

        if (_suppressedEntityCells.Count > 2000)
            _suppressedEntityCells.Clear();

        _visibleMaps.Clear();
        foreach (var pair in current)
            _visibleMaps[pair.Key] = pair.Value;

        _lastVisibleItems = items.Count;
        _lastKnownMaps = knownMaps;
        _lastMatchedMaps = current.Count;
        MaybeLogMatchedMaps(matchedLogLines, current.Count);
        MaybeLogMapSamples(sampleLogLines);
        FinishPerformanceSample(stopwatch, items.Count, _visibleMaps.Count);
    }

    private MapItemSnapshot? ReadHoveredMap(out RectangleF? tooltipRect)
    {
        tooltipRect = null;
        try
        {
            var hovered = _itemReader?.ReadHoveredItem();
            if (hovered == null)
                return null;

            if (TryGetTooltipRect(hovered, out var hoverTooltip))
                tooltipRect = hoverTooltip;

            _lastHoveredMap = hovered;
            return hovered;
        }
        catch
        {
            return null;
        }
    }

    private void MaybeCacheHoveredProperties(MapItemSnapshot? hoveredItem)
    {
        if (hoveredItem == null)
            return;

        var key = GetEntityKey(hoveredItem);
        if (hoveredItem.TooltipProperties.Count > 0)
        {
            _tooltipPropertyCache[key] = hoveredItem.TooltipProperties.ToList();
            if (_tooltipPropertyCache.Count > 1000)
                _tooltipPropertyCache.Clear();
            return;
        }

        ApplyCachedTooltipProperties(key, hoveredItem);
    }

    private void MaybeAddHoveredMapMatch(MapItemSnapshot? hoveredItem)
    {
        if (hoveredItem == null || _scorer == null || !IsUsableRect(hoveredItem.Rect) || !IsLiveVisibleSnapshot(hoveredItem))
            return;

        var key = GetEntityKey(hoveredItem);
        ApplyCachedTooltipProperties(key, hoveredItem);
        var score = _scorer.Score(hoveredItem, Settings);
        if (score.HasMatch)
            _visibleMaps[key] = new ScoredVisibleMap(hoveredItem, score);
    }

    private void ApplyCachedTooltipProperties(long key, MapItemSnapshot item)
    {
        if (item.TooltipProperties.Count > 0)
            return;

        if (_tooltipPropertyCache.TryGetValue(key, out var properties))
            item.TooltipProperties = properties.ToList();
    }

    private void DumpLastHoveredMapDebug()
    {
        var item = _lastHoveredMap;
        if (item == null)
        {
            DebugWindow.LogMsg("[MapModHelper debug] No cached hovered map. Hover a waystone once, then click this button.", 8);
            return;
        }

        var key = GetEntityKey(item);
        ApplyCachedTooltipProperties(key, item);
        var score = _scorer?.Score(item, Settings) ?? MapScore.None;

        var builder = new StringBuilder();
        builder.AppendLine($"[MapModHelper debug] {item.DisplayName} source={item.Source} key={key}");
        builder.AppendLine($"base='{item.BaseName}' class='{item.ClassName}' path='{item.Path}' rect={item.Rect}");
        builder.AppendLine($"score affixes={score.ExplicitAffixCount} eight={score.HasEightAffixes} important={score.ImportantAffixCount} {FormatImportantStats(score)}");
        builder.AppendLine($"game data source='{_mapStatData?.SourcePath ?? "not loaded"}' trackedMods={_mapStatData?.LoadedModCount ?? 0}");
        builder.AppendLine("generated properties from mod stats:");
        if (item.GeneratedProperties.Count == 0)
            builder.AppendLine("  none");
        else
            foreach (var property in item.GeneratedProperties)
                builder.AppendLine($"  {property.Name} = {property.Value} ({property.StatId})");

        builder.AppendLine("tooltip properties:");
        if (item.TooltipProperties.Count == 0)
            builder.AppendLine("  none");
        else
            foreach (var property in item.TooltipProperties)
                builder.AppendLine($"  {property.Name} = {property.Value:0.##} :: {property.TextNoTags}");

        builder.AppendLine("mod lines:");
        foreach (var line in item.ModLines.Take(30))
            builder.AppendLine("  " + line);
        if (item.ModLines.Count > 30)
            builder.AppendLine($"  ... {item.ModLines.Count - 30} more");

        builder.AppendLine("all mods:");
        foreach (var mod in item.AllMods.Take(20))
            builder.AppendLine($"  name='{mod.Name}' display='{mod.DisplayName}' raw='{mod.RawName}' values=[{string.Join(",", mod.Values)}] translation='{mod.Translation}'");
        if (item.AllMods.Count > 20)
            builder.AppendLine($"  ... {item.AllMods.Count - 20} more");

        foreach (var chunk in ChunkLog(builder.ToString(), 900))
            DebugWindow.LogMsg(chunk, 8);
    }

    private void DumpLastHoveredMapRawComponents()
    {
        var item = _lastHoveredMap;
        if (item?.Entity == null)
        {
            DebugWindow.LogMsg("[MapModHelper raw] No cached hovered map. Hover a waystone once, then click this button.", 8);
            return;
        }

        try
        {
            var dump = BuildRawEntityDump(item);
            var debugDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Source", "MapModHelper", "debug");
            Directory.CreateDirectory(debugDirectory);

            var fileName = $"map-raw-components-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var filePath = Path.Combine(debugDirectory, fileName);
            File.WriteAllText(filePath, dump);

            DebugWindow.LogMsg("[MapModHelper raw] Wrote raw component dump: " + filePath, 10);
            var interesting = ExtractInterestingRawLines(dump).Take(20).ToList();
            if (interesting.Count > 0)
                DebugWindow.LogMsg("[MapModHelper raw interesting] " + string.Join(" || ", interesting), 10);
        }
        catch (Exception ex)
        {
            DebugWindow.LogMsg("[MapModHelper raw] Failed to write raw component dump: " + ex.Message, 10);
        }
    }

    private static string BuildRawEntityDump(MapItemSnapshot item)
    {
        var builder = new StringBuilder(128 * 1024);
        var entity = item.Entity!;

        builder.AppendLine("MapModHelper raw component dump");
        builder.AppendLine($"Created: {DateTime.Now:O}");
        builder.AppendLine($"Display: {item.DisplayName}");
        builder.AppendLine($"Base: {item.BaseName}");
        builder.AppendLine($"Class: {item.ClassName}");
        builder.AppendLine($"Path: {item.Path}");
        builder.AppendLine($"Entity address: {SafeGetAddress(entity)}");
        builder.AppendLine($"Rect: {item.Rect}");
        builder.AppendLine();

        builder.AppendLine("Generated properties from game data currently known:");
        if (item.GeneratedProperties.Count == 0)
            builder.AppendLine("  none");
        else
            foreach (var property in item.GeneratedProperties)
                builder.AppendLine($"  {property.Name} = {property.Value} ({property.StatId})");
        builder.AppendLine();

        builder.AppendLine("Parsed tooltip properties currently known:");
        if (item.TooltipProperties.Count == 0)
            builder.AppendLine("  none");
        else
            foreach (var property in item.TooltipProperties)
                builder.AppendLine($"  {property.Name} = {property.Value:0.##} :: {property.TextNoTags}");
        builder.AppendLine();

        builder.AppendLine("Entity scalar properties:");
        AppendObjectMembers(builder, entity, 1, 1, new HashSet<object>(ReferenceEqualityComparer.Instance));
        builder.AppendLine();

        var components = ReadEntityComponents(entity).ToList();
        builder.AppendLine($"Components found: {components.Count}");
        foreach (var (type, component) in components.OrderBy(x => x.Type.FullName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            builder.AppendLine("============================================================");
            builder.AppendLine("Component: " + type.FullName);
            AppendObjectMembers(builder, component, 1, 5, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        return builder.ToString();
    }

    private static IEnumerable<(Type Type, object Component)> ReadEntityComponents(Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in GetKnownComponentTypes())
        {
            if (!seen.Add(type.FullName ?? type.Name))
                continue;

            var component = TryReadComponent(entity, type);
            if (component != null)
                yield return (type, component);
        }
    }

    private static IEnumerable<Type> GetKnownComponentTypes()
    {
        var assemblies = new[] { typeof(Mods).Assembly, typeof(Entity).Assembly }
            .Where(assembly => assembly != null)
            .Distinct();

        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                    continue;
                if (type.Namespace?.Contains(".Components", StringComparison.OrdinalIgnoreCase) != true)
                    continue;

                yield return type;
            }
        }
    }

    private static object? TryReadComponent(Entity entity, Type componentType)
    {
        foreach (var method in typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!method.IsGenericMethodDefinition || method.Name != "GetComponent" || method.GetParameters().Length != 0)
                continue;

            try
            {
                return method.MakeGenericMethod(componentType).Invoke(entity, null);
            }
            catch { }
        }

        foreach (var method in typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            var parameters = method.GetParameters();
            if (!method.IsGenericMethodDefinition || method.Name != "TryGetComponent" || parameters.Length != 1 || !parameters[0].ParameterType.IsByRef)
                continue;

            try
            {
                var args = new object?[] { null };
                var success = method.MakeGenericMethod(componentType).Invoke(entity, args) as bool?;
                if (success == true)
                    return args[0];
            }
            catch { }
        }

        return null;
    }

    private static void AppendObjectMembers(StringBuilder builder, object? value, int depth, int maxDepth, HashSet<object> seen)
    {
        if (value == null)
        {
            AppendIndented(builder, depth, "null");
            return;
        }

        if (IsSimpleValue(value))
        {
            AppendIndented(builder, depth, FormatSimpleValue(value));
            return;
        }

        if (!seen.Add(value))
        {
            AppendIndented(builder, depth, "<cycle>");
            return;
        }

        if (depth > maxDepth)
        {
            AppendIndented(builder, depth, "<max depth>");
            return;
        }

        var type = value.GetType();
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => !IsNoisyMember(property.Name))
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Take(160);

        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception ex)
            {
                AppendIndented(builder, depth, $"{MemberVisibility(property)} property {property.Name}: <error {ex.GetType().Name}>");
                continue;
            }

            AppendNamedValue(builder, $"{MemberVisibility(property)} property {property.Name}", propertyValue, depth, maxDepth, seen);
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !IsNoisyMember(field.Name))
            .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Take(160);

        foreach (var field in fields)
        {
            object? fieldValue;
            try
            {
                fieldValue = field.GetValue(value);
            }
            catch (Exception ex)
            {
                AppendIndented(builder, depth, $"{MemberVisibility(field)} field {field.Name}: <error {ex.GetType().Name}>");
                continue;
            }

            AppendNamedValue(builder, $"{MemberVisibility(field)} field {field.Name}", fieldValue, depth, maxDepth, seen);
        }
    }

    private static void AppendNamedValue(StringBuilder builder, string name, object? value, int depth, int maxDepth, HashSet<object> seen)
    {
        if (value == null || IsSimpleValue(value))
        {
            AppendIndented(builder, depth, $"{name}: {FormatSimpleValue(value)}");
            return;
        }

        if (value is IEnumerable enumerable and not string)
        {
            AppendIndented(builder, depth, $"{name}: {value.GetType().Name}");
            var count = 0;
            foreach (var entry in enumerable)
            {
                if (count >= 20)
                {
                    AppendIndented(builder, depth + 1, "... truncated");
                    break;
                }

                if (entry == null || IsSimpleValue(entry))
                    AppendIndented(builder, depth + 1, $"[{count}] {FormatSimpleValue(entry)}");
                else if (TryFormatDictionaryEntry(entry, out var formatted))
                    AppendIndented(builder, depth + 1, $"[{count}] {formatted}");
                else
                {
                    AppendIndented(builder, depth + 1, $"[{count}] {entry.GetType().Name}");
                    AppendObjectMembers(builder, entry, depth + 2, maxDepth, seen);
                }

                count++;
            }

            if (count == 0)
                AppendIndented(builder, depth + 1, "<empty>");
            return;
        }

        AppendIndented(builder, depth, $"{name}: {value.GetType().Name}");
        AppendObjectMembers(builder, value, depth + 1, maxDepth, seen);
    }

    private static bool TryFormatDictionaryEntry(object entry, out string formatted)
    {
        formatted = string.Empty;
        var type = entry.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
            return false;

        try
        {
            var key = type.GetProperty("Key")?.GetValue(entry);
            var value = type.GetProperty("Value")?.GetValue(entry);
            formatted = $"Key={FormatSimpleValue(key)} Value={FormatSimpleValue(value)}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNoisyMember(string name)
        => name is "M" or "<M>k__BackingField" or "Owner" or "<Owner>k__BackingField" or "Process";

    private static string MemberVisibility(PropertyInfo property)
    {
        var accessor = property.GetMethod ?? property.SetMethod;
        return accessor?.IsPublic == true ? "public" : "nonpublic";
    }

    private static string MemberVisibility(FieldInfo field)
        => field.IsPublic ? "public" : "nonpublic";

    private static IEnumerable<string> ExtractInterestingRawLines(string dump)
    {
        string[] needles =
        [
            "rarity",
            "effectiveness",
            "pack",
            "waystone",
            "map",
            "monster",
            "quantity"
        ];

        foreach (var line in dump.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (needles.Any(needle => trimmed.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                yield return trimmed.Length > 180 ? trimmed[..180] + "..." : trimmed;
        }
    }

    private static void AppendIndented(StringBuilder builder, int depth, string line)
        => builder.Append(' ', depth * 2).AppendLine(line);

    private static bool IsSimpleValue(object value)
    {
        var type = value.GetType();
        return type.IsPrimitive
               || type.IsEnum
               || value is string
               || value is decimal
               || value is DateTime
               || value is Guid
               || value is IntPtr
               || value is UIntPtr;
    }

    private static string FormatSimpleValue(object? value)
    {
        if (value == null)
            return "null";

        var text = value.ToString() ?? string.Empty;
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 240 ? text[..240] + "..." : text;
    }

    private static long SafeGetAddress(object value)
    {
        try
        {
            var property = value.GetType().GetProperty("Address", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var raw = property?.GetValue(value);
            if (raw is long longValue)
                return longValue;
            if (raw is int intValue)
                return intValue;
            if (raw != null && long.TryParse(raw.ToString(), out var parsed))
                return parsed;
        }
        catch { }

        return 0;
    }

    private void DrawBadges(RectangleF rect, MapScore score)
    {
        var lineHeight = Math.Max(14f, 14f * Settings.BadgeScale.Value);
        var leftY = rect.Top + 2f;
        var leftOccupied = new List<RectangleF>();

        if (Settings.ShowAffixCountBadge.Value && score.HasEightAffixes)
        {
            leftOccupied.Add(DrawBadge(new Vector2(rect.Left + 2f, rect.Top + 2f), score.ExplicitAffixCount.ToString(), Settings.EightAffixColor.Value));
            leftY += lineHeight + 2f;
        }

        if (Settings.ShowAffixGroupBadges.Value)
        {
            var style = (MapAffixGroupBadgeStyle)Math.Clamp(Settings.AffixGroupBadgeStyle.Value, Settings.AffixGroupBadgeStyle.Min, Settings.AffixGroupBadgeStyle.Max);
            if (style == MapAffixGroupBadgeStyle.TextCounts)
                DrawTextGroupBadges(rect, score.AffixGroupMatches, leftOccupied, ref leftY, lineHeight);
            else
                DrawBlockGroupBadges(rect, score.AffixGroupMatches, leftOccupied, ref leftY, style);
        }

        if (!Settings.ShowImportantAffixBadges.Value)
            return;

        var y = rect.Top + 2f;
        foreach (var stat in score.ImportantStats)
        {
            var label = $"{stat.BadgeLabel}{stat.Value}";
            var size = GetBadgeSize(label);
            var position = FindRightBadgePosition(rect, size, y, leftOccupied);
            DrawBadge(position, label, GetImportantValueColor(stat.Value));
            y += lineHeight + 2f;
        }
    }

    private void DrawTextGroupBadges(RectangleF itemRect, IReadOnlyList<MapAffixGroupMatch> matches, List<RectangleF> occupied, ref float leftY, float lineHeight)
    {
        foreach (var groupMatch in matches)
        {
            var label = groupMatch.BadgeLabel;
            var size = GetBadgeSize(label);
            var position = FindLeftBadgePosition(itemRect, size, leftY, occupied);
            occupied.Add(DrawBadge(position, label, groupMatch.Color));
            leftY = Math.Max(leftY + lineHeight + 2f, position.Y + size.Y + 2f);
        }
    }

    private void DrawBlockGroupBadges(RectangleF itemRect, IReadOnlyList<MapAffixGroupMatch> matches, List<RectangleF> occupied, ref float leftY, MapAffixGroupBadgeStyle style)
    {
        var blockSize = Math.Max(5f, 7f * Settings.BadgeScale.Value);
        var gap = Math.Max(2f, 2f * Settings.BadgeScale.Value);
        var x = itemRect.Left + 2f;
        var y = leftY;
        var maxBlocks = style == MapAffixGroupBadgeStyle.MatchedAffixBlocks
            ? Math.Max(1, Settings.AffixGroupMaxBlocks.Value)
            : int.MaxValue;
        var drawn = 0;

        foreach (var groupMatch in matches)
        {
            var blockCount = style == MapAffixGroupBadgeStyle.MatchedAffixBlocks
                ? Math.Max(1, groupMatch.MatchedAffixes)
                : 1;

            for (var i = 0; i < blockCount && drawn < maxBlocks; i++)
            {
                if (x + blockSize > itemRect.Right - 2f)
                {
                    x = itemRect.Left + 2f;
                    y += blockSize + gap;
                }

                var block = new RectangleF(x, y, blockSize, blockSize);
                DrawGroupBlock(block, groupMatch.Color);
                occupied.Add(block);
                x += blockSize + gap;
                drawn++;
            }
        }

        if (drawn > 0)
            leftY = y + blockSize + 2f;
    }

    private Vector2 FindLeftBadgePosition(RectangleF itemRect, Vector2 size, float y, IReadOnlyList<RectangleF> occupied)
    {
        var position = new Vector2(itemRect.Left + 2f, y);
        var attempts = 0;
        while (attempts++ < 16)
        {
            var candidate = new RectangleF(position.X, position.Y, size.X, size.Y);
            if (!occupied.Any(rect => Intersects(candidate, rect)))
                return position;

            position.Y += size.Y + 2f;
        }

        return position;
    }

    private Vector2 FindRightBadgePosition(RectangleF itemRect, Vector2 size, float y, IReadOnlyList<RectangleF> leftOccupied)
    {
        var position = new Vector2(itemRect.Right - size.X - 2f, y);
        var attempts = 0;
        while (attempts++ < 16)
        {
            var candidate = new RectangleF(position.X, position.Y, size.X, size.Y);
            if (!leftOccupied.Any(rect => Intersects(candidate, rect)))
                return position;

            position.Y += size.Y + 2f;
        }

        return position;
    }

    private void DrawGroupBlock(RectangleF block, Color color)
    {
        Graphics.DrawBox(block, color);
        Graphics.DrawFrame(block, Settings.BadgeBackgroundColor.Value, 1);
    }

    private RectangleF DrawBadge(Vector2 position, string label, Color frameColor)
    {
        var size = GetBadgeSize(label);
        var width = size.X;
        var height = size.Y;
        var badge = new RectangleF(position.X, position.Y, width, height);

        Graphics.DrawBox(badge, Settings.BadgeBackgroundColor.Value);
        Graphics.DrawFrame(badge, frameColor, 1);
        Graphics.DrawText(label, new Vector2(position.X + 4f, position.Y + 1f), Settings.BadgeTextColor.Value);
        return badge;
    }

    private Vector2 GetBadgeSize(string label)
    {
        var textSize = ImGui.CalcTextSize(label) * Settings.BadgeScale.Value;
        return new Vector2(Math.Max(18f, textSize.X + 8f), Math.Max(15f, textSize.Y + 3f));
    }

    private void DrawBorderHighlight(RectangleF rect, Color color, int thickness)
    {
        var scale = thickness - 1;
        var innerX = (int)rect.X + 1 + (int)(0.5 * scale);
        var innerY = (int)rect.Y + 1 + (int)(0.5 * scale);
        var innerWidth = (int)rect.Width - 1 - scale;
        var innerHeight = (int)rect.Height - 1 - scale;
        var scaledFrame = new RectangleF(innerX, innerY, innerWidth, innerHeight);
        Graphics.DrawFrame(scaledFrame, color, thickness);
    }

    private Color GetScoreColor(MapScore score)
    {
        if (score.ImportantAffixCount > 0)
            return GetImportantValueColor(score.HighestImportantPercent);

        return Settings.EightAffixColor.Value;
    }

    private Color GetImportantValueColor(int value)
    {
        if (value >= Settings.DeepRedMinPercent.Value)
            return Settings.BestImportantColor.Value;
        if (value >= Settings.RedMinPercent.Value)
            return Settings.HighImportantColor.Value;
        if (value > Settings.BlueMaxPercent.Value && value <= Settings.OrangeMaxPercent.Value)
            return Settings.MediumImportantColor.Value;
        return Settings.LowImportantColor.Value;
    }

    private int GetBorderThickness(MapScore score)
    {
        var min = Math.Max(1, Settings.BaseBorderThickness.Value);
        var max = Math.Max(min, Settings.MaxBorderThickness.Value);
        if (score.ImportantAffixCount == 0)
            return min;

        return Math.Clamp(min + (int)MathF.Round(score.Intensity * (max - min)), min, max);
    }

    private static float MapPriority(MapScore score)
    {
        return score.ImportantAffixCount * 100f + score.Intensity * 50f + (score.HasEightAffixes ? 10f : 0f);
    }

    private static string FormatImportantStats(MapScore score)
    {
        var parts = new List<string>();
        if (score.ImportantStats.Count > 0)
            parts.Add("stats=" + string.Join(", ", score.ImportantStats.Select(stat => $"{stat.BadgeLabel}={stat.Value}")));

        if (score.AffixGroupMatches.Count > 0)
            parts.Add("groups=" + string.Join(", ", score.AffixGroupMatches.Select(match => $"{match.Name}:{match.BadgeLabel}")));

        return parts.Count == 0 ? "stats=none groups=none" : string.Join("; ", parts);
    }

    private string GetNextAffixGroupName()
    {
        var used = Settings.AffixGroups
            .Select(group => group.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < 1000; i++)
        {
            var name = $"Group {i}";
            if (!used.Contains(name))
                return name;
        }

        return "New Group";
    }

    private static Color DefaultGroupColor(int index)
    {
        Color[] colors =
        [
            Color.DeepSkyBlue,
            Color.LimeGreen,
            Color.Gold,
            Color.OrangeRed,
            Color.MediumOrchid,
            Color.White,
            Color.HotPink,
            Color.Teal
        ];

        return colors[Math.Abs(index) % colors.Length];
    }

    private void MaybeLogMatchedMaps(IReadOnlyList<string> lines, int total)
    {
        if (!Settings.LogMatchedMaps.Value || lines.Count == 0)
            return;

        var now = Environment.TickCount64;
        if (now - _lastMatchedLogMs < 3000)
            return;

        _lastMatchedLogMs = now;
        var suffix = total > lines.Count ? $" ... {total - lines.Count} more" : string.Empty;
        DebugWindow.LogMsg("[MapModHelper matches] " + string.Join(" || ", lines) + suffix, 5);
    }

    private void MaybeLogMapSamples(IReadOnlyList<string> lines)
    {
        if (!Settings.LogScannedMapSamples.Value || lines.Count == 0)
            return;

        var now = Environment.TickCount64;
        if (now - _lastSampleLogMs < 3000)
            return;

        _lastSampleLogMs = now;
        DebugWindow.LogMsg("[MapModHelper samples] " + string.Join(" || ", lines), 5);
    }

    private void FinishPerformanceSample(Stopwatch? stopwatch, int visibleItems, int matchedMaps)
    {
        if (stopwatch == null)
            return;

        stopwatch.Stop();
        _lastPerformanceSummary = $"Last scan {stopwatch.Elapsed.TotalMilliseconds:F2} ms, visible items {visibleItems}, matched maps {matchedMaps}.";

        var now = Environment.TickCount64;
        if (now - _lastPerformanceLogMs < 2000)
            return;

        _lastPerformanceLogMs = now;
        DebugWindow.LogMsg("[MapModHelper perf] " + _lastPerformanceSummary, 3);
    }

    private static string DescribeMapSample(MapItemSnapshot item, string status)
    {
        var mods = item.ModLines.Count == 0 ? "mods=none" : "mods=" + string.Join(" | ", item.ModLines.Take(8));
        var generated = item.GeneratedProperties.Count == 0
            ? "generated=none"
            : "generated=" + string.Join(" | ", item.GeneratedProperties.Select(property => $"{property.Name}:{property.Value}"));
        return $"{status}: display='{item.DisplayName}' base='{item.BaseName}' class='{item.ClassName}' path='{item.Path}' affixes={item.ExplicitAffixCount} {generated} {mods}";
    }

    private void ClearRuntimeState()
    {
        ClearVisibleScanState();
        _suppressedEntityCells.Clear();
        _tooltipPropertyCache.Clear();
        _lastHoveredMap = null;
        _lastContextHash = 0;
        _lastVisibleItems = 0;
        _lastKnownMaps = 0;
        _lastMatchedMaps = 0;
    }

    private void ClearVisibleScanState()
    {
        _visibleMaps.Clear();
        _tooltipPropertyCache.Clear();
        _itemReader?.ClearCache();
        _lastScanMs = 0;
    }

    private bool AnySupportedWindowVisible()
    {
        var ui = GameController.IngameState.IngameUi;
        return (ui.InventoryPanel?.IsVisible ?? false)
               || (ui.StashElement?.IsVisible ?? false)
               || (ui.GuildStashElement?.IsVisible ?? false);
    }

    private bool IsSuppressedAtCell(long key, string cellKey)
    {
        if (!_suppressedEntityCells.TryGetValue(key, out var suppressedCell))
            return false;

        if (string.Equals(suppressedCell, cellKey, StringComparison.OrdinalIgnoreCase))
            return true;

        _suppressedEntityCells.Remove(key);
        return false;
    }

    private static bool IsUsableRect(RectangleF rect)
    {
        return rect.Width > 4 && rect.Height > 4 && rect.Left >= -10000 && rect.Top >= -10000;
    }

    private static bool IsLiveVisibleSnapshot(MapItemSnapshot item)
    {
        if (item.Element == null || item.Entity == null)
            return false;
        if (SafeFlag(item.Element, "IsVisible") == false)
            return false;

        try
        {
            var liveItem = item.Element.AsObject<NormalInventoryItem>();
            if (liveItem?.Item?.IsValid != true || liveItem.Item.Address == 0)
                return false;

            if (item.Entity.Address != 0 && liveItem.Item.Address != item.Entity.Address)
                return false;

            var liveRect = liveItem.GetClientRectCache;
            if (!IsUsableRect(liveRect))
                return false;

            var overlap = IntersectionArea(liveRect, item.Rect) / MathF.Max(1f, MathF.Min(Area(liveRect), Area(item.Rect)));
            return overlap >= 0.80f;
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldHideOverlay(MapItemSnapshot item, MapItemSnapshot? hoveredItem, RectangleF? tooltipRect)
    {
        if (hoveredItem != null && IsSameItemSnapshot(item, hoveredItem))
            return true;

        return tooltipRect.HasValue && Intersects(item.Rect, tooltipRect.Value);
    }

    private bool TryGetTooltipRect(MapItemSnapshot item, out RectangleF rect)
    {
        rect = default;
        var tooltip = TryGetElementProperty(item.Element, "Tooltip") ?? TryGetElementProperty(item.Element, "ToolTip");
        if (tooltip == null)
        {
            try
            {
                tooltip = GameController.IngameState.UIHoverElement.Tooltip;
            }
            catch
            {
                tooltip = null;
            }
        }

        if (tooltip == null)
            return false;

        try
        {
            if (SafeVisible(tooltip) == false)
                return false;

            rect = tooltip.GetClientRectCache;
            return rect.Width > 20f && rect.Height > 20f;
        }
        catch
        {
            return false;
        }
    }

    private static Element? TryGetElementProperty(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || prop.GetIndexParameters().Length != 0)
                return null;

            return prop.GetValue(obj) as Element;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeVisible(object? element)
        => SafeFlag(element, "IsVisible") == true;

    private static bool? SafeFlag(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.PropertyType == typeof(bool))
                return (bool?)prop.GetValue(obj);
        }
        catch { }

        return null;
    }

    private static bool IsSameItemSnapshot(MapItemSnapshot first, MapItemSnapshot second)
    {
        if (first.Entity?.Address != 0 && first.Entity?.Address == second.Entity?.Address)
            return true;

        if (first.Element?.Address != 0 && first.Element?.Address == second.Element?.Address)
            return true;

        var intersection = IntersectionArea(first.Rect, second.Rect);
        if (intersection <= 0f)
            return false;

        var overlap = intersection / MathF.Max(1f, MathF.Min(Area(first.Rect), Area(second.Rect)));
        return overlap >= 0.70f;
    }

    private static bool Intersects(RectangleF a, RectangleF b)
        => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private static float IntersectionArea(RectangleF a, RectangleF b)
    {
        var left = MathF.Max(a.X, b.X);
        var top = MathF.Max(a.Y, b.Y);
        var right = MathF.Min(a.Right, b.Right);
        var bottom = MathF.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
            return 0f;

        return (right - left) * (bottom - top);
    }

    private static float Area(RectangleF rect)
        => MathF.Max(0f, rect.Width) * MathF.Max(0f, rect.Height);

    private static string GetCellKey(RectangleF rect)
    {
        var x = Quantize(rect.X);
        var y = Quantize(rect.Y);
        var width = Quantize(rect.Width);
        var height = Quantize(rect.Height);
        return $"{x}:{y}:{width}:{height}";
    }

    private static int Quantize(float value)
        => (int)MathF.Round(value / 4f) * 4;

    private static IEnumerable<string> ChunkLog(string text, int maxLength)
    {
        for (var index = 0; index < text.Length; index += maxLength)
            yield return text.Substring(index, Math.Min(maxLength, text.Length - index));
    }

    private static long GetEntityKey(MapItemSnapshot item)
    {
        try
        {
            if (item.Entity?.Address != 0)
                return item.Entity!.Address;
        }
        catch { }

        try
        {
            if (item.Element?.Address != 0)
                return item.Element!.Address;
        }
        catch { }

        return item.GetHashCode();
    }

    private static void Checkbox(string label, ToggleNode node)
    {
        var value = node.Value;
        if (ImGui.Checkbox(label, ref value))
            node.Value = value;
    }

    private static void SliderInt(string label, RangeNode<int> node)
    {
        var value = node.Value;
        if (ImGui.SliderInt(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private static void SliderFloat(string label, RangeNode<float> node)
    {
        var value = node.Value;
        if (ImGui.SliderFloat(label, ref value, node.Min, node.Max))
            node.Value = value;
    }

    private void DrawAffixGroupBadgeStyleSelector()
    {
        var value = Math.Clamp(Settings.AffixGroupBadgeStyle.Value, Settings.AffixGroupBadgeStyle.Min, Settings.AffixGroupBadgeStyle.Max);
        var preview = AffixGroupBadgeStyleLabel((MapAffixGroupBadgeStyle)value);
        ImGui.SetNextItemWidth(190);
        if (ImGui.BeginCombo("Group badge style", preview))
        {
            for (var i = Settings.AffixGroupBadgeStyle.Min; i <= Settings.AffixGroupBadgeStyle.Max; i++)
            {
                var style = (MapAffixGroupBadgeStyle)i;
                var selected = value == i;
                if (ImGui.Selectable(AffixGroupBadgeStyleLabel(style), selected))
                    Settings.AffixGroupBadgeStyle.Value = i;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private static string AffixGroupBadgeStyleLabel(MapAffixGroupBadgeStyle style)
    {
        return style switch
        {
            MapAffixGroupBadgeStyle.TextCounts => "Text counts",
            MapAffixGroupBadgeStyle.MatchedAffixBlocks => "Block per matched affix",
            _ => "Block per matched group"
        };
    }

    private static bool DrawColorEdit(string label, Color color, Action<Color> setColor)
    {
        var vec = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (!ImGui.ColorEdit4(label, ref vec))
            return false;

        var a = Math.Clamp((int)(vec.W * 255f), 0, 255);
        var r = Math.Clamp((int)(vec.X * 255f), 0, 255);
        var g = Math.Clamp((int)(vec.Y * 255f), 0, 255);
        var b = Math.Clamp((int)(vec.Z * 255f), 0, 255);
        setColor(Color.FromArgb(a, r, g, b));
        return true;
    }

}

internal sealed record ScoredVisibleMap(MapItemSnapshot Item, MapScore Score);

internal sealed record ScoredMapCandidate(long Key, string CellKey, ScoredVisibleMap Scored);
