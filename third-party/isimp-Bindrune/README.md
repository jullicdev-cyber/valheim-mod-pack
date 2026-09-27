# Bindrune

Bindrune shows every keybind in your game in one panel: the game's own controls and the keybinds of every installed mod. When two binds share a key, it tells you whether they will actually get in each other's way, and why. It never blocks a binding. It explains what will happen and leaves the choice to you. It can also take the hotbar off the number keys, which the game itself does not allow.

![The Bindrune keybind panel](https://raw.githubusercontent.com/isimp/Bindrune/main/docs/images/screenshot.webp)

## AI notice

Most of Bindrune was written by Claude Code (Anthropic), which did the heavy lifting on implementation and design. Heads-up so you can judge for yourself.

## Using it

Press Insert in game to open the panel. Select a bind to see what it clashes with, change its key from the same place, or *Mute* a clash you are happy with. A new key nothing else uses is saved straight away. One that clashes is shown with what it would run into, and a few nearby keys that would not, before anything is saved.

You can tell Bindrune where a bind is used, for example only in the build menu or only with a pickaxe in hand. Binds that can never be active at the same time are then no longer reported as clashes.

Any bind can also be pinned with *Show on screen* to a small list of hints, which only shows a bind while it applies. Alt+H shows and hides that list.

## The hotbar keys

The game ties its hotbar to the number keys 1 to 8. Its controls let you add a second key to a slot, but never take the number away. Bindrune can put each slot on any key you like, with a modifier such as Alt if you want one, or on none, which leaves the number keys free for other binds. The hotbar shows the keys you chose, and so do the game's prompts that name them. They stay in place when the game reloads its controls, and they are yours alone, so a synced profile does not change them. *Default* on the bind gives the number back, and without Bindrune the game has its own keys again.

![The hotbar with its slots on the keys they were moved to](https://raw.githubusercontent.com/isimp/Bindrune/main/docs/images/hotbar.webp)

## Your own keys

When a shared modpack profile is synced, it overwrites your config files and your rebinds with them. Bindrune can keep the keys you mark as your own, with *Just for you*, outside the synced config and puts them back after every sync. *Whole profile* hands a bind back to the profile's key. The clashes you have waved through and the hints you have pinned are kept there too.

Keepsake does the same for any other setting: you keep your own value for it, and a sync no longer takes it away. With both installed, keybinds are Bindrune's, and a keybind you kept in Keepsake becomes one of your own keys here. Keepsake is on Thunderstore at https://thunderstore.io/c/valheim/p/isimp/Keepsake/ and on Hexium at https://valheim.hexium.gg/mods/isimp/Keepsake

## Profile updates

| Update | Your own keys |
|---|---|
| Gale profile sync, or a Gale import into the same profile | Yes |
| Installing or updating a modpack, in any mod manager | Yes |
| Thunderstore Mod Manager or r2modman, *Update existing profile* | Yes, once you allow a spare copy |
| Importing as a new profile, in any mod manager | Carried over by hand |

Your moved hotbar keys, the clashes you have waved through and the hints you have pinned are kept with them and go the same way. *Update existing profile* in Thunderstore Mod Manager and r2modman replaces the whole profile folder. For their profiles Bindrune asks, once you have something of your own, whether it may keep a spare copy of `bindrune.keys` next to the profiles folder, in `Bindrune` and the profile's name; it then brings the copy back the next time the game starts after such an update. Nothing is written there before you answer, and the *Spare copy* button at the top of the panel changes the answer. A profile imported as new starts without them; to bring them along, copy `bindrune.keys` from the old profile's `BepInEx` folder into the new one's.

## Requirements

Bindrune needs BepInEx 5 and Jotunn. Mod managers install Jotunn along with it.

## More

Settings, the files Bindrune keeps, how it reads the game's binds, notes for mod authors and build instructions are on GitHub at https://github.com/isimp/Bindrune
