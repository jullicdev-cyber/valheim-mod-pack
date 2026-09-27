# Changelog

## 0.5.0

Your own keys now survive *Update existing profile* in Thunderstore Mod Manager and r2modman. For their profiles Bindrune offers to keep a spare copy of `bindrune.keys` next to the profiles folder, asking once before it writes anything there, and brings it back the next time the game starts after an update, saying so in the corner. The *Spare copy* button in the panel changes the answer.

A `bindrune.keys` or `situations.txt` held open by another program as Bindrune first read it is no longer written over by the next change, which lost everything it held, and your keys in it are put back once it can be read rather than only the next time the game starts.

Setting a key of your own now remembers the key it replaced as the profile's, so *Whole profile* gives that key back straight away rather than only after the next sync. When the profile has no key there, *Whole profile* now leaves the bind without one instead of keeping yours, which *Just for you* then also took for the profile's.

## 0.4.2

When `bindrune.keys` cannot be written, keybinds taken over from Keepsake now stay in Keepsake's file, to be taken over at the next launch, rather than being lost from both.

## 0.4.1

Keybinds kept in Keepsake become your own keys in Bindrune, which looks after keybinds while both are installed.

Return in the search box at the start menu no longer reaches the menu behind it, where it could still log a character in. The menu now ignores the keyboard while the panel is open.

## 0.4.0

The hotbar keys 1 to 8 can now be moved or cleared, which the game itself does not allow. Bindrune keeps the new key and puts it back whenever the game reloads its controls, and *Default* gives the digit back.

A hotbar key, or its alternate, can now take a modifier such as Alt + 1.

The hotbar shows the keys on its slots, and prompts such as cooking name them, or say hotbar when there are too many to list.

A key a bind cannot take is now refused as soon as you press it.

Binding a game control to a number key now works. It used to leave the control answering to no key, and clashes between the number keys and the hotbar were never reported.

Keys the game reads with a modifier, such as Ctrl + F3, are now reported when a longer combo fires them too.

## 0.3.2

The alternate hotbar keys and the alternate dodge are now listed. The game ships them without a key, and binds in that state were being missed.

A bind the game will not let you change now names the one you can change instead, with a button that goes straight to it. This is how the hotbar digits work.

Keys found only on non-US keyboards, such as the one beside left Shift, can now be set on the game's own controls. Mods cannot see those keys, so a mod bind says so rather than taking one.

A key that could not be set now says why, beside the buttons you set it with.

The panel keeps your search and your place in the list when you close, reopen or resize it.

Typing in the search box at the start menu no longer reaches the menu behind it, where Return pressed whatever was selected.

Problems Bindrune works around are now written to the log once each.

## 0.3.1

Five keys the game reads in its own code, such as F11 for screenshots and Ctrl+F1 to free the mouse, are now listed with its binds and reported when a mod uses them too.

Editing `situations.txt` or `bindrune.keys` by hand while the game runs is no longer undone by the next change in the panel, and opening the panel picks the edit up.

Putting your keys back after a profile sync now scans at most twice a session, at the main menu and once your character is in the world, instead of every 30 seconds for up to four minutes. Keys whose mod only binds them in the world are now restored however long the main menu stays open.

Key suggestions in the rebind preview are much cheaper to work out, and the on-screen hints no longer allocate anything while nothing on screen changes.

The panel now plays the game's own sounds when it opens and closes, when a key you press is set straight away, and when the hints key toggles the hints.

## 0.3.0

When a key you press for a bind clashes, Bindrune now suggests up to three nearby that would not, adding a modifier where the bind can take one. It prefers the modifiers your setup already uses, and never puts one on a key the game reads regardless of modifiers. A key shared with a bind that is never live at the same time is offered too, marked as shared.

A key nothing else uses is now bound as soon as you press it.

Keys the game reads on its own, such as F2 for the network panel or F11 for screenshots, are no longer shown as free.

The keys that open the map, inventory, chat and build menu now count as live in the world, where you press them to open those screens. A world bind on the same key used to be reported as harmless.

## 0.2.1

Muted clashes and pinned hints now live in `BepInEx/bindrune.keys` with your own keys, where a profile sync cannot replace them with the profile owner's. Existing ones are moved there on the first launch.

A mute now covers the clash you looked at rather than the pair for good. It is reported again if it turns into something worse, and dropped once it is gone.

A note about two binds sharing a key now names both of them.

*Cancel* now cancels a rebind instead of binding the left mouse button, and the *Press key* search does the same when clicked again. Left click is still bindable anywhere else.

## 0.2.0

Every clash a bind has now carries a *Go to* button that switches to the other bind in the pair, and scrolls its row into view.

## 0.1.0

First release.
