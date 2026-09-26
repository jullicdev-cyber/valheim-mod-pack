# Confirm Map Pin Removal 1.1.0

Client-side BepInEx/Harmony mod for the pack's Valheim 1.0.16 and Jotunn 2.30.2. Right-clicking a saved map pin opens a confirmation with the pin name, Cancel and Delete buttons. Russian game language uses «Удаление метки», «Отмена» and «Удалить»; other languages use English.

The window uses the same Jotunn components as [XPortal's portal configuration panel](https://github.com/SpikeHimself/XPortal/blob/main/XPortal/UI/PortalConfigurationPanel.cs): `CreateWoodpanel`, AveriaSerif fonts, orange heading and `CreateButton`. It is a dedicated compact dialog, with a dimmed backdrop that catches pointer input. Jotunn's counted input lock blocks gameplay while it is open. Initial selection is Cancel; explicit left/right button navigation stays inside the dialog. Escape or controller B cancels.

Only manual `Minimap.RemovePinUnderPointer` deletion is intercepted. Automatic pin cleanup and programmatic `RemovePin` calls remain unchanged. The exact pin selected on click is retained; confirmation rechecks the same map, local player, saved pin membership, open map and living player. Moving the pointer or clicking again cannot redirect deletion. Cancel, stale callbacks and repeated confirmations cannot delete a different pin.

Closing the map, changing worlds, dying, disabling the plugin, losing the window or opening a native popup cancels the pending request. Local player destruction is intercepted to release the window before logout completes. The view owns only its own overlay and one counted input lock; it never pops the game's global popup stack. UI failures are logged and block deletion. Selection API failures leave the already-installed deletion guard in place and show a startup warning once; an incompatible game where the deletion hook itself cannot be installed cannot be protected.

Rich-text tags are stripped from the displayed name, text markup is disabled, whitespace is folded and names are limited to 80 Unicode text elements. The saved pin name is never modified. Long names wrap and shrink within the body area.

The patch also covers touch deletion routed through the same method. Other mods' separate removal commands and controller paths that bypass this method are not covered. Existing map data is not rewritten or migrated. No server installation is required for other players to connect; install the plugin on each client that wants confirmation.

Build: `./plugins/ConfirmMapPinRemoval/Build.ps1 -GameDirectory '<Valheim folder>'` on Windows. Tests: `./plugins/ConfirmMapPinRemoval/Test.ps1 -GameDirectory '<Valheim folder>'`. Build reads local game assemblies without redistributing them. Jotunn is already included in the pack; XPortal is not a runtime dependency. The managed DLL is included in both Windows and Linux packs; Linux runtime has not been tested.

Automated checks cover confirmation state and label handling (31 assertions), the real Plugin.cs with host doubles (42 assertions), and the installed Valheim/Jotunn APIs via Mono.Cecil. The host doubles do not run Unity, Harmony detours or UI rendering. No gameplay or visual verification is claimed.

In-game acceptance check on a disposable pin: right-click opens the wood panel above the map; Cancel and Escape retain the pin; Delete removes only the selected pin once; moving the pointer does not change the target; blank-map clicks do nothing. Also check a long Cyrillic name, repeated opening, logout with the dialog open, and input after closing. Check that XPortal still opens and closes normally. These require a live game session.
