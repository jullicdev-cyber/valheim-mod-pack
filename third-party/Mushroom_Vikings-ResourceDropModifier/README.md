# Resource Drop Modifier

A BepInEx mod for [Valheim](https://www.valheimgame.com/) that gives every item
that drops in the game its own drop multiplier, as an ordinary config setting.
Made for a long-lived dedicated server where some resources run out and others
pile up.

## What it does

- On the first world load the mod adds a setting for every item that drops
  from something to `BepInEx/config/Gonfreecss.ResourceDropModifier.cfg`, in a
  section per biome, each at `1`. The setting's description says what the item
  is called in game and what drops it. Crafted gear has no setting unless a
  chest or loot pile hands it out.
- Change a number and that item drops that many times as much, from every
  source: creatures, trees, rocks, bushes, chests, beehives, sap collectors,
  dungeon loot. `2` doubles it, `0.5` halves it, `0` turns it off.
- Edit it like any other setting: the in-game **Configuration Manager** window
  on a host or in single player, a mod manager's config editor, or the file
  itself on a dedicated server. Changes apply within a second, no restart.
- The server's values are the ones that count. Clients receive them on join and
  never have their own file rewritten. There is no client-side opt-out.
- After a game update new items appear as new settings, keeping the numbers you
  set.

Valheim's own **resource rate** world modifier still applies on top, so this is a
per-item refinement of that setting, not a replacement.

## Details worth knowing

- **Where the item is listed does not limit where it applies.** `Wood = 2` under
  `[Meadows]` doubles wood from a Plains birch too. An item drops in several
  biomes is listed once, under the earliest one in progression order.
- **Fractions are rolled, not rounded.** `0.5` on a creature that drops one hide
  drops it half the time.
- **Some drops cannot be scaled.** Vanilla exempts boss trophies, keys and the
  like from the resource rate, and this mod respects that. The setting's
  description says `Not scalable` when it applies to every drop of an item.
- **Stacked drops saturate at the stack size.** A chest that would give 20
  coins at `5` gives 100, not 999. Creature and rock drops cap at 100 objects.
- **Bushes never give less than one** unless the multiplier is below `1`; at
  `0` they give nothing and are still marked picked.

## Console commands

| Command | Does |
|---|---|
| `rdm_reload` | Re-read the config from disk and apply it. The file is watched, so this is for the cases where the watcher does not fire (some Docker volume drivers, network shares). |
| `rdm_rescan` | Re-run the item scan and add any new settings, for prefabs another mod registered after world load. |
| `rdm_show <prefab>` | Print the multiplier in force for an item, and whether it came from the server or the local file. |

## Requirements

**Everyone needs it.** Drops are decided by whichever player is closest to the
creature or rock, so a client without the mod drops vanilla amounts.

Requires [Mushroom Sync](../MushroomSync/README.md) on every client and on the
server; the release zip ships it alongside.

## Building

```bash
dotnet build ResourceDropModifier/ResourceDropModifier.csproj -c Release
```

Resolves the game path from the Steam registry. For an install in another Steam
library pass it explicitly:

```bash
dotnet build ResourceDropModifier/ResourceDropModifier.csproj -c Release -p:ValheimDir="E:\Games\Steam\steamapps\common\Valheim"
```

The build copies the DLL into `BepInEx/plugins` when that folder exists.

## How it works

[docs/DESIGN.md](docs/DESIGN.md): which two game methods every drop goes
through, why the table is synced over a raw channel rather than config sync,
and how the item scan decides which biome a setting lands under.
