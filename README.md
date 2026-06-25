# MapModHelper

ExileCore2 plugin for highlighting Path of Exile 2 waystones in stash and inventory. It supports both controller and keyboard/mouse UI layouts.

## Features

- Highlights waystones with a configurable affix count, defaulting to 8 affixes.
- Uses a bundled, self-contained `data/waystone_data.json` file for waystone affixes and generated stat mappings.
- Optional badges for generated map stats:
  - `E` Monster Effectiveness
  - `R` Item Rarity
  - `P` Pack Size
  - `MR` Monster Rarity
  - `W` Waystone Drop Chance
- Custom affix groups with selectable waystone affixes, per-group colors, and text or colored-block badges.
- Hover debug dumps for inspecting map components and parsed stats.

## Overlay Layout

- Top-left badge: explicit affix count, shown when the waystone meets the configured affix-count target.
- Top-right badges: selected generated map stats such as `E28` or `R42`.
- Left-side badges or blocks: custom affix-group matches, colored by the group color.
- Border color and thickness: driven by generated stat value tiers and the 8-affix highlight.

This keeps fixed categories in fixed places so multiple active rules do not overwrite each other.

## Setup

Place this folder under `Plugins/Source/MapModHelper` in an ExileCore2 install and let ExileCore2 build source plugins on launch.

The bundled `data/waystone_data.json` file is included with the plugin. When game patches add or change waystone information, new plugin builds will include an updated data file.
