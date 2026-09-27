# Renewable Resource Timers

Small supplement for PlantEverything 1.21.3, Valheim 1.0.16 and this pack's harvest intervals.

| Resource | Interval | Implementation |
| --- | --- | --- |
| Red, yellow, blue and smokepuff mushrooms | 30 minutes after harvest | PlantEverything `[Mushrooms]` |
| Raspberry, blueberry, cloudberry and lingonberry bushes | 45 minutes after harvest | PlantEverything `[Berries]` |
| Thistle, dandelion, fiddlehead | 50 minutes after harvest | PlantEverything `[Flowers]` |
| Ashvine berries (`VineAsh`) | 45 minutes after harvest | This plugin |
| Planted Jotun puffs and magecap | 30 minutes from planting | This plugin |

Jotun puffs and magecap are **one-time harvests** in the installed game's native assets, including their wild pickables. Harvesting does not start a respawn timer for them. They must be planted again; this plugin reduces their next growth to 30 minutes. It does not make new permanent mushroom spawners. `MushroomBzerker` has no corresponding pickable or sapling in the installed asset manifest and receives no artificial timer.

All intervals use the game's world clock. Native respawn checks run approximately every 60 seconds, and plant growth checks approximately every 10 seconds while loaded, so visible maturity can occur on the next native check. Unloaded objects become available when the game loads and checks them. Original planting/picking timestamps remain untouched.

The plugin changes only `VineAsh.m_respawnTimeMinutes` and the two Mistlands saplings' `m_growTime`/`m_growTimeMax`. It preserves vine initial random maturity, growth, spacing, harvest quantity and visuals; initial random maturity is used only before the first recorded harvest. Other farm crops, trees, stone, flint and branches retain their existing settings.

PlantEverything's `EnableCropOverrides` and `EnableVineOverrides` stay disabled. Its vine switch would additionally change the vine sapling's native 200–300 second growth range to a fixed value and remove initial random maturity, so it cannot express this narrow change alone.

Install the plugin and the same patch version of Jotunn-dependent pack on **the host/server and all clients**. The two settings in `valheimmodpack.renewableresourcetimers.cfg` are marked admin-only and synchronized by Jotunn from the server. Loaded instances refresh after sync, future instances are adjusted at `Awake`, and native calculation hooks keep the parameters current. No custom RPC, ZDO write, ownership transfer, prefab mutation, harvest replacement or changes to another mod's DLL are used.

Build: `./Build.ps1 -GameDirectory <Valheim folder>`; checks: `./Test.ps1 -GameDirectory <Valheim folder>`. `NativeChecks.cs` is a separate optional smoke-test helper and is excluded from the production build.
