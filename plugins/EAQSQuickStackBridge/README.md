# EAQS Quick Stack Bridge 1.1.0

Local compatibility plugin for Quick Stack Store Sort Trash Restock **1.4.15**, Equipment and Quick Slots **3.1.3**, and optional AzuAutoStore **3.1.6**. Original vendor DLLs are unchanged. The bridge applies Harmony patches in memory.

- Uses the public EAQS `GetVisibleRows()` and `GetFullHeight()` API to exclude all reserved rows from Quick Stack's item selection and destination selection, including empty reserved cells.
- Keeps ordinary extra rows sortable. Leaves existing favorites and hotbar exclusions to Quick Stack.
- Protects reserved rows from the shared stacking/restocking filter too, but the pack keeps those commands disabled pending gameplay testing.
- Refuses player sorting if inventory geometry is inconsistent. Requires the exact tested vendor versions.
- Enables inventory sorting with **O** and buttons in memory after patches install successfully. The distributed Quick Stack configuration remains in guarded mode (container sorting only). Do not save an unguarded Quick Stack configuration and then remove the bridge.
- Does not repair previously damaged inventories, edit saves, recover lost items, or change EAQS slot counts.

Version 1.1.0 also protects the **K** quick-unload action and Azu's single-item inventory-store action. **Alt + left click** marks/unmarks an item type in Quick Stack; **Alt + right click** marks/unmarks an inventory slot. A favorite type protects every matching stack; a favorite slot protects whatever currently occupies that slot. K respects both immediately, including removal of a favorite without restarting. Equipped items and every hidden EAQS row remain protected. Ordinary visible cells, including the pack's extra EAQS row, remain available. Azu's configured hotbar exclusion, container access checks, range and storage rules remain active.

The implementation patches Azu's existing `CantStoreFavorite` filters in VanillaContainers, BackpackContainer, kgDrawer and mkzDrawer. It only adds item protection for exact references in the local player's main inventory: matching grid coordinates on a ground drop or chest item do not trigger favorite protection. It does not globally filter `Inventory.GetAllItems`, change manual drag-and-drop or change ground auto-pull.

Azu 3.1.6 and Quick Stack originally used separate live favorite caches while Azu could write the same `QuickStackStore_player_<id>.dat` file with only two lists. Quick Stack's file contains a third list of trash flags. The bridge makes both mods share the same live favorite slot/type sets and routes Azu saves through Quick Stack's native three-list writer. Azu's **Z + click** favorite controls can still be used; Z and Alt now update that same state instead of diverging. Resetting Quick Stack favorites also reconnects Azu to the replacement sets.

The existing Quick Stack preference file is authoritative, including an empty file or empty favorite sets. Legacy `AzuAutoStore_player_<id>.dat` and `AzuExtendedPlayerInventory_player_<id>.dat` marks are imported only when no Quick Stack primary existed before either getter first ran; the bridge observes this before Quick Stack's `OpenOrCreate` can create it. It then writes the canonical Quick Stack file. Old mirror files are preserved unchanged and are not merged again on each restart, so removed favorites stay removed. The updater must preserve all three preference-file families. The bridge never deletes those files.

Manual Azu dispatch guards install in **Awake**, before its shortcut Update can run. K and single-item store remain blocked until exact versions, filter APIs, collection fields and state hooks validate. A runtime favorite-data failure blocks further manual storing and displays one warning instead of treating missing data as an empty favorite set. Ground auto-pull is outside these dispatch hooks. Updating any of the three vendor versions requires revalidation.

Build on Windows with `./plugins/EAQSQuickStackBridge/Build.ps1 -GameDirectory '<Valheim folder>'`. Run `./plugins/EAQSQuickStackBridge/Test.ps1` for boundary regressions and static checks against the pinned DLLs. Game assemblies are read locally and are not redistributed. The resulting DLL is managed code; the pack's Windows and Linux installers both copy it. Linux runtime operation has not been tested.

The root lock records the local binary hash. `scripts/Build.ps1` includes that binary in a pack rebuild. Recompiling the source requires updating its lock and payload hashes.

Before relying on this in a live world, use a test character: equip armor and quick-slot consumables, fill the extra ordinary row, sort repeatedly with O and the button, and verify positions, quantities and enchantments before and after reconnecting. Also check a full inventory, favorite slots and container sorting. These Unity/gameplay checks have not yet been performed.

Version 1.0.1 waits for both Quick Stack sorting entries before initialization: Quick Stack binds them in Start, after the bridge Awake used to run. The startup regression harness compiles the real plugin source with host doubles; it is not a Unity runtime test.

Tests cover the original 1,095 inventory-boundary assertions and eight startup assertions, 36 production Azu filter/lifecycle assertions, 27 production shared-state/migration assertions, and actual vendor APIs and call sites via Mono.Cecil. State tests cover either mod reading first, an existing empty primary, immediate removal of a favorite, Z saving without losing the third list, two complete reloads without reviving stale mirror marks, replacement sets after reset, and refusing detached caches.

`Build-NativeChecks.ps1 -GameDirectory '<Valheim folder>' [-PluginAssembly '<already built DLL>']` builds the optional `EAQSAzuNativeChecks.dll`. Its public `ValheimModPack.BridgeSmoke.NativeChecks.Run()` is intended for the isolated QoL native probe after the mods' Start methods complete. It verifies real Harmony ownership and uses actual vendor getters, toggles and serializers with fixture files under the isolated smoke root; it refuses the user's normal configuration directory and never runs with a live player. Live movement of items with K, multiplayer chest access, UI border appearance and physical click interaction still require an in-game acceptance check.

Acceptance: mark a type with Alt+left click and a slot with Alt+right click, press K near stocked chests, and verify both stay in inventory while ordinary resources unload. Remove the marks and repeat, including after reconnecting. Check Z changes appear consistently, equipment/quick slots and the hotbar remain intact, and ground pickup into chests still works. Sorting with O must retain its previous equipment-slot protection.
