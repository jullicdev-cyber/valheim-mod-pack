# Radio window review

`RadioWindow.cs` uses the installed Jotunn wood panel, Averia fonts and Valheim button styles. It creates one 640 × 680 panel, scales it to the available canvas, and repaints labels in place instead of rebuilding it when network state changes. Six tracks fit on each playlist page.

The interface has Russian and English captions. Filenames are rendered with rich text disabled, control characters removed and Unicode text-element truncation. Known transfer and library-scan statuses have Russian captions; unknown technical details remain readable in their original language with a localized error prefix. A failed or in-progress host scan is shown instead of misleadingly telling the host to add music to an empty library.

The window owns exactly one Jotunn input-block request and releases that request when closed, invalidated, disabled or when a UI callback fails. It does not reset other mods' input blocks. Every button callback carries its window generation, so destroyed-window callbacks cannot control a later window. Escape and gamepad B close it; loss of the original player/world, death, teleporting, sleeping, distance over five metres, a native popup, the inventory, or the large map invalidate the context. Focus navigation stays inside enabled radio buttons. Selection sound is disabled on these buttons so pointer focus plus click does not play two button sounds.

The 1.1.2 review found that a portable's deferred opening could steal focus from text input opened during the inventory transition. Opening now checks Jotunn's combined text-input guard before acquiring its own lease; an already-open window checks the actual native text panel, chat and console instead, so its own Jotunn lock cannot make it close itself. Escape/B also discard a pending opening. Six regressions execute the production portable controller with boundary doubles; they do not simulate native Unity event dispatch.

Validation performed during development:

- C# 5 compilation against the installed game's managed assemblies and the pack's Jotunn 2.30.2, with small stubs for the other new radio classes.
- Twenty-eight checks of the actual compiled `SafeTitle`/`Clock`/`LocalStatus` helpers: Cyrillic, combining characters, supplementary Unicode, control/bidi characters, markup text, truncation boundaries, negative/non-finite/large times, localization of known status/error prefixes and preservation of unknown technical details.
- Read-only inspection of the installed Jotunn input-block implementation and Valheim UI method signatures.

Reproduce the compilation and twenty-eight helper checks with `Test-UI.ps1 -GameDirectory <Valheim directory>`. The script builds the real `RadioWindow.cs` against installed managed game references and `RadioWindowTestStubs.cs`, then invokes only its pure formatting helpers from `UIHelperTests.cs`; it does not call Unity's native engine.

These checks do not establish that the panel renders correctly in a running game. Manual acceptance should cover 1440 × 900 and a smaller window, long Cyrillic filenames, an empty and multi-page library, keyboard/mouse and gamepad navigation, volume and playback synchronization between two players, close/reopen while a download runs, disconnect while open, and a native popup opening over the radio window. Confirm that closing the radio restores movement and does not interfere with another mod's active modal.
