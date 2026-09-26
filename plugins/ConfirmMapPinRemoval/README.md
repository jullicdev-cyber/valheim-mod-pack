# Confirm Map Pin Removal 1.0.0

Client-side BepInEx/Harmony mod for the pack's Valheim 1.0.16. Right-clicking a saved map pin opens the game's native Yes/No popup with the pin name. With Russian selected in Valheim, the prompt is Russian; other languages use English prompt text and the game's localized buttons.

Only `Minimap.RemovePinUnderPointer` is intercepted. Automatic pin cleanup and programmatic `RemovePin` calls remain unchanged. The exact pin selected on click is retained, and confirmation rechecks that it still belongs to the same map. A moved pointer cannot redirect deletion to another pin. Cancel and repeated confirmations cannot delete items. Errors opening the dialog block deletion.

The patch also covers touch deletion routed through the same method. Other mods' separate removal commands and controller paths that bypass this method are not covered. Existing map data is not rewritten or migrated. No server installation is required for other players to connect; install the plugin on each client that wants confirmation.

Build: `./plugins/ConfirmMapPinRemoval/Build.ps1 -GameDirectory '<Valheim folder>'` on Windows. Tests: `./plugins/ConfirmMapPinRemoval/Test.ps1 -GameDirectory '<Valheim folder>'`. Build reads local game assemblies without redistributing them. The managed DLL is included in both Windows and Linux packs; Linux runtime has not been tested.

Before use, check a disposable map pin: right-click → No retains it; right-click → Yes removes it; blank-map clicks do nothing; automatic markers continue updating. Static API and confirmation-state tests do not replace this gameplay test.
