**<details><summary>Version 2.2.2</summary>**

- Changed hovertext formatting to match vanilla standard for wider compatibility.
	- (Thanks Arrowmaster)
</details>
<details><summary>Version 2.2.1</summary>

- Bulk harvest on gamepad now uses the appropriate alt key for your selected layout.
- Compiled against Valheim v1.0.15.
</details>
<details><summary>Version 2.2.0</summary>

- Compiled against Valheim v1.0.7 & BepInEx 5.4.2350.
- Added support for lingonberry bushes.
</details>
<details><summary>Version 2.1.1</summary>

- Prevent further placement attempts while still bulk planting.
- Fixed rare edge-case conflict with InfinityHammer.
    - Special thanks to Vapok for reporting and helping to troubleshoot this.
</details>
<details><summary>Version 2.1.0</summary>
## Performance:

- Multiple optimization passes for every line of code.
	- Reduced per-frame allocations, minimized GC pressure, eliminated redundancy, simplified branching, increased early returns.
- Improved per-frame placement and snapping performance by ~5500%!
	- Implemented batching for placement ghost updates.
	- Added [Performance]GhostUpdateBatchSize config option.
	- Roughly 56x faster per placement ghost (from 0.045ms to 0.0008ms).
	- Achieves ~50x higher throughput. Can process 5x more ghosts in ~10% of the previous time.
- Reduced input polling performance impact by over 50%.
	- Removed input polling entirely for tools that aren't the cultivator.
- Added object pooling for extra ghosts.
	- Avoids creating and destroying placement ghosts, recycles them whenever possible.
	- Dramatically reduces CPU usage when resizing grid, after placement, and when sheathing/equipping cultivator.
	
## User Experience:

- PlantEasily practically knows what you want to do before you do it.
- Grid orientation is remembered as you cycle through snap points.
	- Updates only when root ghost is rotated or when snapping to a different grid.
	- Still avoids overlapping placements.
- No more dead snapping positions.
- Added multiple forms of hysteresis to prevent snap point jitter.
- Snapping grids of varying spacing is now predictable and painless.
- Numerous hidden bugs and edge cases fixed across all snapping contexts.
	
## Features:

- Added [UI]ShowSnapDirection, defaults to true.
	- Draws a line between snap position to the source.
	- Added [UI]SnapStartColor & SnapEndColor to control colors.
- Added 3 new Scatter settings to reduce grid uniformity.
	- [General]EnableScatter, PositionScatterRadius, RotationScatterAngle.
- Added [Grid]PreferCardinalSnapping setting, defaults to false.
	- When enabled, avoids corner snapping while cardinal snap points are available.

## General:

- Compiled against Valheim v0.221.12 & BepInEx 5.4.2333.
- Only begin bulk planting coroutine if actually bulk planting.
</details>
<details><summary>Version 2.0.3</summary>

- Compiled against Valheim v0.220.3.
- Improved compatibility with other mods that wish to skip execution of vanilla methods.
</details>
<details><summary>Version 2.0.2</summary>

## Feature Changes:
- Changed default grid spacing for smoke puffs.

## Bug Fixes:
- Fixed mod conflict with Balrond Amazing Nature.
</details>
<details><summary>Version 2.0.1</summary>

## Bug Fixes:
- Fixed mod conflict with VNEI.
    - Special thanks to Fitzrobert for helping to troubleshoot this.
</details>
<details><summary>Version 2.0.0</summary>

## Feature Changes:
- Redesigned all snapping and grid detection logic.
- Added alternative (free) placement mode when snapping to a single plant.
    - Loosens the 16 vanilla angles of snapping to allow free 360 degree rotation.
    - Activate by enabling alternative placement, vanilla defaults are as follows.
        - M/KB: Hold left shift during snapping.
        - Gamepad: Toggle with left bumper + right stick.
    - Added [Grid]ForceAltPlacement config option as a permanent toggle (defaults to off).
- Added visual grid lines indicating row and column direction.
    - Can be toggled on/off with [UI]ShowGridDirections.
    - Colors are configurable.
- Added toggleable custom piece highlighting for root placement ghost when bulk planting.
    - Can be toggled on/off with [UI]HighlightRootGhost.
    - Highlight color is configurable.
- Added replant-on-harvest support for 3rd party modded crops.
    - Modded crops are detected automatically.
- Can now select which crop to replant on harvest.
    - Defaults to replanting existing crop.
    - Change the crop to be replanted by selecting desired crop in the cultivator build table.
    - Hover text on mature crops shows which plant will be planted in its place.
        - Toggle with [UI]ShowHoverReplantHint.
- Bulk planting is now performed in batches, no amount of concurrent placements can cause the game to hang.
    - Supports planting up to 10,000 pieces at a time (if you have the seeds).
- Added [UI]ShowGhostsDuringPlacement config setting.
    - Defaults to true.
    - When enabled, displays grey silhouettes of placement ghosts during bulk planting.
- Added individual grid spacing settings for all plantable pickables.
    - Configuration settings are generated programmatically for each pickable found to have a piece component.

## Configuration Changes:
It isn't necessary to delete your previous configuration file, though it is encouraged.
- Many configuration settings have moved to new setting categories. Too many to mention.
- Added [Performance]BulkPlantingBatchSize config setting.
- Removed [General]StandardizeGridRotations setting. Made it baseline and added alt placement in its place.
- [Difficulty]PreventPartialPlanting now defaults to false.
- [Controls]ToggleAutoReplantKey now has a default binding of F6.
</details>

---

**<details><summary>Pre 2.0 Versions</summary>**

### 1.9.2
- Farming XP when planting in bulk is now batched together and awarded once as opposed to per piece planted.
	- This improves performance significantly upon placement of large grids.

### 1.9.1
- Fixed replant on harvest bug introduced with Bog Witch.

### 1.9.0
- Updated for Valheim v0.219.13 (Bog Witch).
- Change snap detection sphere check to NonAlloc version.
- Prevent NRE if grid extends to unloaded sectors.
- Added bulk harvest hover text keyhints (configurable).
- Added build hud keyhints (configurable).

### 1.8.1
- Removed SFX and VFX for all but the root placement ghost when planting in bulk.
- Added [General]MaxConcurrentPlacements config setting.
	- Sets a maximum number of pieces that the cultivator can place in a single placement.

### 1.8.0
- Updated for Ashlands.
- Added 3 new plant health placement statuses.
	- NoAttachPiece, TooHot, TooCold.
- Added grid snapping support for Smoke Puffs & Fiddlehead.
- Minor code optimizations and major project refactor.

### 1.7.3
- Removed process filters in favor of alternate headless client detection method.
- Minor code improvements.
- Compiled against Valheim v0.217.46.

### 1.7.2
- Fixed piece placement bug introduced with 1.7.1.
- Optimized order of short-circuiting condition checks for early return in harmony patches.

### 1.7.1
- Smoothed visual transition when changing number of rows/columns.
- Removed legacy Unity input references.
- Compiled against Valheim 0.217.38.

### 1.7.0
- Fixed mod for Linux clients.
- Changed the default plant spacing.
	- Removed [General]StandardizeGridSpacing and replaced with MinimizeGridSpacing.
	- With this change, standardized spacing is the new default. Set the new setting to true to restore old behavior.

### 1.6.3
- Updated for Valheim v0.217.22.
- Compiled against BepInEx 5.4.22.
- AutoReplant now correctly places plants for free when noCost cheat is active.
- Misc code improvements.

### 1.6.2
- Added keyboard shortcut support for configurable toggle keys (e.g. Left Ctrl + F8).

### 1.6.1
- Fixed randomized piece / grid rotation introduced in Hildir's Request when crops or saplings are selected in the build menu.
	- Previous grid rotation is once again remembered for subsequent placements.

### 1.6.0
- Updated for Valheim v0.217.14 (Hildir's Request).
- Now allows placement of all valid ghosts even when root ghost is invalid.
- Added visual feedback for toggle keys.
- Set process filter to prevent the mod from running on server.

### 1.5.0
- Added [General]GloballyAlignGridDirections setting.
	- Defaults to true.
	- Aligns row and column directions with the global grid.
- Added [General]StandardizeGridSpacing setting.
	- Defaults to false.
	- Allows for uniformly spaced grids between diverse plant types at the cost of requiring more physical space.
- Added [Controls]ToggleAutoReplantKey setting.
	- Defaults to null, user must set the key to utilize.
	- Toggles [Harvesting]ReplantOnHarvest setting on/off.
- Fixed snapping of pickable resources.
	- Snap points for all pickables now correctly detect position validity and won't attempt to snap on top of themselves or prevent placement in valid locations.
- Compatiblity fix with mods adding custom pickables.

### 1.4.4
- Added the ability to rotate an entire grid of pieces any time during placement instead of only while snapping to existing grids.

### 1.4.3
- Bulk placement no longer applies to viable pieces unless built with the cultivator.

### 1.4.2
- Updated for Valheim v0.216.9.

### 1.4.1
- Fixed [Harvesting]ReplantOnHarvest functionality in multiplayer environments.

### 1.4.0
- Completely overhauled grid snapping!
- By default, new grids will no longer expand to overlap with an existing grid, nor will new grids diagonally snap to old grids.
- Added 2 new config settings: GridSnappingStyle and StandardizeGridRotations.

### 1.3.2
- Fixed functionality of HarvestStyle.LikeResources that was broken with the release of 1.3.0.

### 1.3.1
- Massively reduced system memory use.
- Additional miscellaneous performance and code optimizations.
- Compiled against BepInEx 5.4.2105.

### 1.3.0
- Bulk harvesting now applies to Beehives in addition to all Pickables.
- Added CostDisplayStyle and CostDisplayLocation configuration settings to [UI] category.
- Fixed resource cost of placement when PreventPartialPlanting is disabled.

### 1.2.0
- Added auto crop replanting when [Harvesting]ReplantOnHarvest is enabled (defaults to off).
- Increased sapling spacing from 200% of growth radius to 220%.
- Added [General]UseStamina config setting.
- Replaced ExtraPlantSpacing setting with ExtraCropSpacing and ExtraSaplingSpacing.
- Performance improvements.
- Mod compatibility improvements.

### 1.1.2
- Added support for CraftFromContainers, OdinsCraftyBoxes, and similar mods.
- Cost for planting multiple resources now reflected in build UI.
	- Can be toggled on/off with new [UI]ShowCost config setting.

### 1.1.1
- Fixed plant spacing to ensure crops can grow (saplings may need more tweaking).
- Fixed casting error when using bulk harvest while interacting with a non-applicable object.
- Added [General]ExtraPlantSpacing config option to allow for spacing customization.

### 1.1.0
- Harvest Easily!
	- Added highly requested bulk harvesting features.
	- Added [Harvesting] section and 4 additional configuration settings.
- Organized the order that configuration options are listed in the BepInEx Configuration Manager.

### 1.0.4
- Fixed plant validity check when [Crops]CropsRequireSunlight is set to false in PlantEverything.

### 1.0.3
- Changed default keybind for toggling snapping features.
- Added config option [Pickables]PreventOverlappingPlacements (defaults to true).
	- When enabled, pickable resources can no longer be placed over top of one another or on any other colliding obstructions.

### 1.0.2
- Fixed biome validity check on pieces with Plant components.
- Appropriate and localized error messages now display when placement is prevented by either PreventPartialPlanting or PreventInvalidPlanting.

### 1.0.1
- Bumped version to correct documentation.

### 1.0.0
- Created Mod.
</details>