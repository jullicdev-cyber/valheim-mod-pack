# EAQS Quick Stack Bridge 1.0.1

Local compatibility plugin for Quick Stack Store Sort Trash Restock **1.4.15** and Equipment and Quick Slots **3.1.3**. Original vendor DLLs are unchanged. The bridge applies two Harmony patches in memory.

- Uses the public EAQS `GetVisibleRows()` and `GetFullHeight()` API to exclude all reserved rows from Quick Stack's item selection and destination selection, including empty reserved cells.
- Keeps ordinary extra rows sortable. Leaves existing favorites and hotbar exclusions to Quick Stack.
- Protects reserved rows from the shared stacking/restocking filter too, but the pack keeps those commands disabled pending gameplay testing.
- Refuses player sorting if inventory geometry is inconsistent. Requires the exact tested vendor versions.
- Enables inventory sorting with **O** and buttons in memory after patches install successfully. The distributed Quick Stack configuration remains in guarded mode (container sorting only). Do not save an unguarded Quick Stack configuration and then remove the bridge.
- Does not repair previously damaged inventories, edit saves, recover lost items, or change EAQS slot counts.

Build on Windows with `./plugins/EAQSQuickStackBridge/Build.ps1 -GameDirectory '<Valheim folder>'`. Run `./plugins/EAQSQuickStackBridge/Test.ps1` for boundary regressions and static checks against the pinned DLLs. Game assemblies are read locally and are not redistributed. The resulting DLL is managed code; the pack's Windows and Linux installers both copy it. Linux runtime operation has not been tested.

The root lock records the local binary hash. `scripts/Build.ps1` includes that binary in a pack rebuild. Recompiling the source requires updating its lock and payload hashes.

Before relying on this in a live world, use a test character: equip armor and quick-slot consumables, fill the extra ordinary row, sort repeatedly with O and the button, and verify positions, quantities and enchantments before and after reconnecting. Also check a full inventory, favorite slots and container sorting. These Unity/gameplay checks have not yet been performed.

Version 1.0.1 waits for both Quick Stack sorting entries before initialization: Quick Stack binds them in Start, after the bridge Awake used to run. The startup regression harness compiles the real plugin source with host doubles; it is not a Unity runtime test.
