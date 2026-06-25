using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;

namespace MapModHelper;

internal sealed class MapItemReader
{
    private static readonly Regex TooltipPropertyRegex = new(
        @"^(?<name>Revives Available|Item Rarity|Pack Size|Monster Pack Size|Monster Rarity|Monster Effectiveness|Waystone Drop Chance):\s*(?<value>[+\-]?\d[\d,]*(?:\.\d+)?)\s*%?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly GameController _gameController;
    private readonly MapStatData? _mapStatData;
    private readonly Dictionary<long, CachedMapData> _snapshotCache = new();

    public Action<string, Exception>? DiagnosticLogger { get; set; }

    public MapItemReader(GameController gameController, MapStatData? mapStatData)
    {
        _gameController = gameController;
        _mapStatData = mapStatData;
    }

    public void ClearCache()
    {
        _snapshotCache.Clear();
    }

    public int GetVisibleContextHash()
    {
        try
        {
            unchecked
            {
                var ui = _gameController.Game.IngameState.IngameUi;
                var hash = 17;

                if (ui.InventoryPanel?.IsVisible == true)
                    AddContext(ref hash, true, GetStableObjectHash(ui.InventoryPanel), GetNormalInventoryItemsHash(ui.InventoryPanel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems));
                if (ui.StashElement?.IsVisible == true)
                    AddContext(ref hash, true, GetInventoryContextHash(ui.StashElement.VisibleStash, ui.StashElement), GetInventoryContentHash(ui.StashElement.VisibleStash));
                if (ui.GuildStashElement?.IsVisible == true)
                    AddContext(ref hash, true, GetInventoryContextHash(ui.GuildStashElement.VisibleStash, ui.GuildStashElement), GetInventoryContentHash(ui.GuildStashElement.VisibleStash));

                return hash;
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic("context hash", ex);
            return 0;
        }
    }

    public List<MapItemSnapshot> ReadAllVisibleItems()
    {
        var items = new List<MapItemSnapshot>();
        items.AddRange(ReadPlayerInventory());
        items.AddRange(ReadVisibleStashItems());
        return Deduplicate(items);
    }

    public MapItemSnapshot? ReadHoveredItem()
    {
        try
        {
            var uiHover = _gameController.Game.IngameState.UIHover;
            if (uiHover == null || uiHover.Address == 0)
                return null;

            var snapshot = ReadInventoryItem(uiHover.AsObject<NormalInventoryItem>(), "hover");
            if (snapshot == null)
                return null;

            snapshot.TooltipProperties = ReadTooltipProperties(uiHover);
            return snapshot;
        }
        catch (Exception ex)
        {
            LogDiagnostic("hovered item read", ex);
            return null;
        }
    }

    private List<MapItemSnapshot> ReadPlayerInventory()
    {
        var items = new List<MapItemSnapshot>();
        try
        {
            var inventory = _gameController.Game.IngameState.IngameUi.InventoryPanel;
            if (inventory?.IsVisible != true)
                return items;

            try
            {
                AddItems(items, inventory[InventoryIndex.PlayerInventory].VisibleInventoryItems, "inventory-visible");
            }
            catch (Exception ex) { LogDiagnostic("player inventory visible list", ex); }

            if (items.Count == 0)
                AddItems(items, GetNormalInventoryItemsFromElementTree(inventory), "inventory-tree");
        }
        catch (Exception ex) { LogDiagnostic("player inventory read", ex); }

        return Deduplicate(items);
    }

    private List<MapItemSnapshot> ReadVisibleStashItems()
    {
        var items = new List<MapItemSnapshot>();
        try
        {
            var ui = _gameController.Game.IngameState.IngameUi;
            var stash = ui.StashElement?.IsVisible == true ? ui.StashElement :
                ui.GuildStashElement?.IsVisible == true ? ui.GuildStashElement : null;
            if (stash == null)
                return items;

            try
            {
                AddInventoryItems(items, stash.VisibleStash, "stash-visible");
            }
            catch (Exception ex) { LogDiagnostic("stash visible inventory", ex); }

            try
            {
                AddItems(items, GetNormalInventoryItemsFromElementTree(stash), "stash-tree");
            }
            catch (Exception ex) { LogDiagnostic("stash tree fallback", ex); }
        }
        catch (Exception ex) { LogDiagnostic("visible stash read", ex); }

        return Deduplicate(items);
    }

    private void AddItems(List<MapItemSnapshot> output, IEnumerable<NormalInventoryItem>? items, string source)
    {
        if (items == null)
            return;

        try
        {
            foreach (var item in items)
            {
                try
                {
                    var snap = ReadInventoryItem(item, source);
                    if (snap != null)
                        output.Add(snap);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"item read ({source})", ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic($"item enumeration ({source})", ex);
        }
    }

    private void AddInventoryItems(List<MapItemSnapshot> output, Inventory? inventory, string source)
    {
        if (inventory == null)
            return;

        try
        {
            var subInventories = inventory.SubInventories;
            if (subInventories is { Count: > 0 })
            {
                foreach (var subInventory in subInventories)
                    AddItems(output, subInventory?.VisibleInventoryItems, source);
            }
        }
        catch (Exception ex) { LogDiagnostic($"subinventory read ({source})", ex); }

        try
        {
            AddItems(output, inventory.VisibleInventoryItems, source);
        }
        catch (Exception ex) { LogDiagnostic($"inventory visible list ({source})", ex); }
    }

    private static List<NormalInventoryItem> GetNormalInventoryItemsFromElementTree(Element root)
    {
        var result = new List<NormalInventoryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(Element element, int depth)
        {
            if (element == null || depth > 35)
                return;

            try
            {
                var item = element.AsObject<NormalInventoryItem>();
                if (item?.Item?.IsValid == true && item.Item.Address != 0)
                {
                    var key = $"{item.Address}:{item.Item.Address}";
                    if (seen.Add(key))
                        result.Add(item);
                }
            }
            catch { }

            try
            {
                foreach (var child in element.Children)
                    Walk(child, depth + 1);
            }
            catch { }
        }

        Walk(root, 0);
        return result;
    }

    private static List<MapItemSnapshot> Deduplicate(IEnumerable<MapItemSnapshot> items)
    {
        var result = new List<MapItemSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = $"{item.Element?.Address ?? 0}:{item.Entity?.Address ?? 0}";
            if (seen.Add(key))
                result.Add(item);
        }

        return result;
    }

    private MapItemSnapshot? ReadInventoryItem(NormalInventoryItem? inventoryItem, string source)
    {
        try
        {
            if (inventoryItem?.Item?.IsValid != true || inventoryItem.Item.Address == 0)
                return null;
            if (!IsVisibleOrUnknown(inventoryItem))
                return null;

            var entity = inventoryItem.Item;
            var baseItemType = _gameController.Files.BaseItemTypes.Translate(entity.Path);
            if (baseItemType == null)
                return null;

            var path = entity.Path ?? string.Empty;
            var baseName = baseItemType.BaseName ?? string.Empty;
            var className = baseItemType.ClassName ?? string.Empty;
            if (!IsMapBase(path, baseName, className))
                return null;

            var rarity = default(ItemRarity);
            var identified = false;
            var uniqueName = string.Empty;
            var modFingerprint = string.Empty;
            Mods? modsComponent = null;
            IReadOnlyList<ItemMod> explicitItemMods = [];
            IReadOnlyList<ItemMod> allItemMods = [];
            if (entity.TryGetComponent<Mods>(out var mods))
            {
                modsComponent = mods;
                rarity = mods.ItemRarity;
                identified = mods.Identified;
                uniqueName = mods.UniqueName?.Replace('\u2019', '\'') ?? string.Empty;
                explicitItemMods = GetModCollection(mods, "ExplicitMods");
                allItemMods = GetAllModCollections(mods);
                modFingerprint = BuildModFingerprint(explicitItemMods, allItemMods);
            }

            var key = GetEntityKey(entity, inventoryItem);
            if (_snapshotCache.TryGetValue(key, out var cached)
                && string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.BaseName, baseName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.ClassName, className, StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase)
                && cached.Rarity == rarity
                && cached.IsIdentified == identified
                && string.Equals(cached.ModFingerprint, modFingerprint, StringComparison.Ordinal))
            {
                return cached.ToSnapshot(entity, inventoryItem, inventoryItem.GetClientRectCache, source);
            }

            var explicitAffixCount = 0;
            var modLines = new List<string>();
            var explicitMods = new List<MapExplicitModInfo>();
            var allMods = new List<MapExplicitModInfo>();
            var generatedProperties = new List<MapGeneratedPropertyInfo>();

            if (modsComponent != null)
            {
                explicitMods = explicitItemMods
                    .Select(ToMapExplicitModInfo)
                    .ToList();
                allMods = allItemMods
                    .Select(ToMapExplicitModInfo)
                    .GroupBy(mod => string.Join("|", mod.Name, mod.RawName, mod.DisplayName, mod.Group, mod.Translation, string.Join(",", mod.Values)), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                explicitAffixCount = explicitMods.Count;
                AddModLines(modLines, modsComponent);

                var propertySourceMods = allMods.Count > 0 ? allMods : explicitMods;
                generatedProperties = _mapStatData?.ComputeProperties(propertySourceMods) ?? [];
            }

            var distinctMods = modLines
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _snapshotCache[key] = new CachedMapData(path, baseName, className, uniqueName, rarity, identified, modFingerprint, explicitAffixCount, distinctMods, explicitMods, allMods, generatedProperties);
            if (_snapshotCache.Count > 2000)
                _snapshotCache.Clear();

            return new MapItemSnapshot
            {
                Entity = entity,
                Element = inventoryItem,
                Rect = inventoryItem.GetClientRectCache,
                Source = source,
                Path = path,
                BaseName = baseName,
                ClassName = className,
                UniqueName = uniqueName,
                Rarity = rarity,
                IsIdentified = identified,
                ExplicitAffixCount = explicitAffixCount,
                ModLines = distinctMods,
                ExplicitMods = explicitMods,
                AllMods = allMods,
                GeneratedProperties = generatedProperties
            };
        }
        catch (Exception ex)
        {
            LogDiagnostic($"inventory item read ({source})", ex);
            return null;
        }
    }

    private static bool IsMapBase(string path, string baseName, string className)
    {
        return string.Equals(className, "Map", StringComparison.OrdinalIgnoreCase)
               || baseName.Contains("Waystone", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/Maps/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Waystone", StringComparison.OrdinalIgnoreCase);
    }

    private static List<MapTooltipPropertyInfo> ReadTooltipProperties(Element uiHover)
    {
        try
        {
            var hoverItem = uiHover.AsObject<HoverItemIcon>();
            var tooltip = hoverItem?.Tooltip;
            if (tooltip?.IsVisible != true)
                return [];

            return ExtractTooltipProperties(tooltip);
        }
        catch
        {
            return [];
        }
    }

    private static List<MapTooltipPropertyInfo> ExtractTooltipProperties(Element tooltip)
    {
        var results = new Dictionary<string, MapTooltipPropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in CollectTextLines(tooltip))
        {
            var match = TooltipPropertyRegex.Match(line);
            if (!match.Success)
                continue;

            var value = ParseDouble(match.Groups["value"].Value.Replace(",", string.Empty));
            var name = NormalizePropertyName(match.Groups["name"].Value);
            if (results.ContainsKey(name))
                continue;

            results[name] = new MapTooltipPropertyInfo(name, line, value, value, value);
        }

        return results.Values.ToList();
    }

    private static IEnumerable<string> CollectTextLines(Element element)
    {
        string text;
        try
        {
            text = element.TextNoTags ?? string.Empty;
        }
        catch
        {
            text = string.Empty;
        }

        foreach (var line in SplitLines(text))
            yield return line;

        IEnumerable<Element> children;
        try
        {
            children = element.Children ?? [];
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            if (child == null)
                continue;

            foreach (var line in CollectTextLines(child))
                yield return line;
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    private static double ParseDouble(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string NormalizePropertyName(string name)
        => name.Trim().ToLowerInvariant() switch
        {
            "pack size" => "Monster Pack Size",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Trim().ToLowerInvariant())
        };

    private static IReadOnlyList<ItemMod> GetAllModCollections(Mods mods)
    {
        string[] names =
        [
            "ImplicitMods",
            "CorruptionImplicitMods",
            "EnchantedMods",
            "ExplicitMods",
            "ItemMods",
            "Mods",
            "Affixes"
        ];

        var result = new List<ItemMod>();
        foreach (var name in names)
            result.AddRange(GetModCollection(mods, name));

        return result;
    }

    private static IReadOnlyList<ItemMod> GetModCollection(Mods mods, string propertyName)
    {
        var result = new List<ItemMod>();
        object? value;
        try
        {
            value = mods.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(mods);
        }
        catch
        {
            return result;
        }

        if (value is not IEnumerable enumerable)
            return result;

        try
        {
            foreach (var entry in enumerable)
                if (entry is ItemMod itemMod)
                    result.Add(itemMod);
        }
        catch
        {
            return result;
        }

        return result;
    }

    private static MapExplicitModInfo ToMapExplicitModInfo(ItemMod mod)
    {
        var values = new List<int>();
        try
        {
            if (mod.Values != null)
                values.AddRange(mod.Values);
        }
        catch { }

        return new MapExplicitModInfo(
            mod.Name ?? string.Empty,
            TryGetString(mod, "RawName"),
            mod.DisplayName ?? string.Empty,
            TryGetString(mod, "Group"),
            TryGetString(mod, "Translation"),
            values);
    }

    private static string BuildModFingerprint(IReadOnlyList<ItemMod> explicitMods, IReadOnlyList<ItemMod> allMods)
    {
        var explicitSignature = explicitMods
            .Select(GetItemModSignature)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToList();
        var allSignature = allMods
            .Select(GetItemModSignature)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToList();

        return "e:" + string.Join("||", explicitSignature) + "\na:" + string.Join("||", allSignature);
    }

    private static string GetItemModSignature(ItemMod mod)
    {
        var values = string.Empty;
        try
        {
            if (mod.Values != null)
                values = string.Join(",", mod.Values);
        }
        catch { }

        return string.Join("|",
            mod.Name ?? string.Empty,
            TryGetString(mod, "RawName"),
            mod.DisplayName ?? string.Empty,
            TryGetString(mod, "Group"),
            TryGetString(mod, "Translation"),
            values);
    }

    private static void AddModLines(List<string> output, object mods)
    {
        string[] names =
        [
            "ImplicitStats",
            "HumanImpStats",
            "ImplicitMods",
            "CorruptionImplicitMods",
            "EnchantedStats",
            "EnchantedMods",
            "ExplicitStats",
            "ExplicitMods",
            "ItemMods",
            "HumanStats",
            "Mods",
            "RawMods",
            "Stats",
            "Affixes"
        ];

        foreach (var name in names)
        {
            try
            {
                var prop = mods.GetType().GetProperty(name);
                var value = prop?.GetValue(mods);
                AddAny(output, value);
            }
            catch { }
        }
    }

    private static void AddAny(List<string> output, object? value)
    {
        if (value == null)
            return;

        if (value is ItemMod itemMod)
        {
            AddItemMod(output, itemMod);
            return;
        }

        if (value is string text)
        {
            AddString(output, text);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                AddAny(output, item);
            return;
        }

        AddString(output, value.ToString());
    }

    private static void AddItemMod(List<string> output, ItemMod mod)
    {
        if (AddString(output, TryGetString(mod, "Translation")))
            return;
        if (IsLikelyHumanStatText(mod.DisplayName) && AddString(output, mod.DisplayName))
            return;
        if (IsLikelyHumanStatText(mod.Name))
            AddString(output, mod.Name);
    }

    private static bool AddString(List<string> output, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        output.Add(value.Trim());
        return true;
    }

    private static bool IsLikelyHumanStatText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.Contains(' ') || trimmed.Contains('%') || trimmed.Contains('+') || trimmed.Contains('-');
    }

    private static int GetInventoryContextHash(Inventory? inventory, object? owner)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + GetStableObjectHash(inventory);
            hash = hash * 31 + GetStableObjectHash(owner);
            hash = hash * 31 + TryGetIntProperty(owner, "IndexVisibleStash");
            return hash;
        }
    }

    private static int GetInventoryContentHash(Inventory? inventory)
    {
        if (inventory == null)
            return 0;

        try
        {
            var visibleHash = GetNormalInventoryItemsHash(inventory.VisibleInventoryItems);
            if (visibleHash != 0)
                return visibleHash;
        }
        catch { }

        var hash = 17;
        try
        {
            var subInventories = inventory.SubInventories;
            if (subInventories == null)
                return hash;

            foreach (var subInventory in subInventories)
                hash = hash * 31 + GetNormalInventoryItemsHash(subInventory?.VisibleInventoryItems);
        }
        catch { }

        return hash;
    }

    private static int GetNormalInventoryItemsHash(IEnumerable<NormalInventoryItem>? items)
    {
        if (items == null)
            return 0;

        unchecked
        {
            var hash = 17;
            var count = 0;
            try
            {
                foreach (var item in items)
                {
                    if (item?.Item?.IsValid != true || item.Item.Address == 0 || !IsVisibleOrUnknown(item))
                        continue;

                    count++;
                    hash = hash * 31 + item.Item.Address.GetHashCode();
                    hash = hash * 31 + GetStableObjectHash(item);
                    try
                    {
                        var rect = item.GetClientRectCache;
                        hash = hash * 31 + MathF.Round(rect.X).GetHashCode();
                        hash = hash * 31 + MathF.Round(rect.Y).GetHashCode();
                        hash = hash * 31 + MathF.Round(rect.Width).GetHashCode();
                        hash = hash * 31 + MathF.Round(rect.Height).GetHashCode();
                    }
                    catch { }
                }
            }
            catch { }

            return count == 0 ? 0 : hash;
        }
    }

    private static void AddContext(ref int hash, bool visible, int panelHash, int contentHash)
    {
        hash = hash * 31 + (visible ? 1 : 0);
        if (!visible)
            return;

        hash = hash * 31 + panelHash;
        hash = hash * 31 + contentHash;
    }

    private static bool IsVisibleOrUnknown(object? element)
    {
        var visible = TryGetBoolProperty(element, "IsVisible");
        return visible != false;
    }

    private static bool? TryGetBoolProperty(object? element, string propertyName)
    {
        if (element == null)
            return null;

        try
        {
            var property = element.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.PropertyType == typeof(bool))
                return (bool)property.GetValue(element)!;
        }
        catch { }

        return null;
    }

    private static long TryGetLongProperty(object? element, string propertyName)
    {
        if (element == null)
            return 0;

        try
        {
            var property = element.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            var value = property?.GetValue(element);
            if (value is long longValue)
                return longValue;
            if (value is int intValue)
                return intValue;
            if (value is uint uintValue)
                return uintValue;
            if (value is ulong ulongValue && ulongValue <= long.MaxValue)
                return (long)ulongValue;
            if (value != null && long.TryParse(value.ToString(), out var parsed))
                return parsed;
        }
        catch { }

        return 0;
    }

    private static int TryGetIntProperty(object? element, string propertyName)
    {
        var value = TryGetLongProperty(element, propertyName);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;
    }

    private static int GetStableObjectHash(object? value)
    {
        if (value == null)
            return 0;

        var address = TryGetLongProperty(value, "Address");
        if (address != 0)
            return address.GetHashCode();

        return StringComparer.Ordinal.GetHashCode(value.GetType().FullName ?? value.GetType().Name);
    }

    private static long GetEntityKey(Entity entity, object fallback)
    {
        try
        {
            if (entity.Address != 0)
                return entity.Address;
        }
        catch { }

        return fallback.GetHashCode();
    }

    private static string TryGetString(object? obj, string propertyName)
    {
        if (obj == null)
            return string.Empty;

        try
        {
            var property = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(obj)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void LogDiagnostic(string context, Exception ex)
    {
        DiagnosticLogger?.Invoke(context, ex);
    }
}

internal sealed record CachedMapData(
    string Path,
    string BaseName,
    string ClassName,
    string UniqueName,
    ItemRarity Rarity,
    bool IsIdentified,
    string ModFingerprint,
    int ExplicitAffixCount,
    List<string> ModLines,
    List<MapExplicitModInfo> ExplicitMods,
    List<MapExplicitModInfo> AllMods,
    List<MapGeneratedPropertyInfo> GeneratedProperties)
{
    public MapItemSnapshot ToSnapshot(Entity entity, NormalInventoryItem element, ExileCore2.Shared.RectangleF rect, string source)
    {
        return new MapItemSnapshot
        {
            Entity = entity,
            Element = element,
            Rect = rect,
            Source = source,
            Path = Path,
            BaseName = BaseName,
            ClassName = ClassName,
            UniqueName = UniqueName,
            Rarity = Rarity,
            IsIdentified = IsIdentified,
            ExplicitAffixCount = ExplicitAffixCount,
            ModLines = ModLines,
            ExplicitMods = ExplicitMods,
            AllMods = AllMods,
            GeneratedProperties = GeneratedProperties
        };
    }
}
