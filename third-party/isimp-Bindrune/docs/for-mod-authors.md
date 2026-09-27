# Describing your binds to Bindrune

Bindrune reports keybind clashes between mods. It can only rule a clash out when it knows *when* each bind is live, and you are the only one who really knows that about your own mod.

You can tell it, in a few lines, with **no reference to Bindrune, no dependency, and no behaviour change when it is not installed**.

## Nothing to do, if you already use Jotunn key hints

If your buttons have a `KeyHintConfig`, Bindrune already reads it:

- `Item` → that button only applies while that item is held
- `Piece` → that button only applies during build placement

That is the same fact, written for another purpose. If your hints are accurate you can stop here.

## Declaring it directly

Attach an object to your setting's `ConfigDescription` tags. Bindrune finds it by **type name** and reads it by reflection, so your class is your own:

```csharp
internal class BindruneAttributes
{
    public string[] Situations;   // where the bind is live
    public string[] HeldItems;    // what has to be in hand
}
```

```csharp
DigKey = Config.Bind("General", "DigKey", new KeyboardShortcut(KeyCode.G),
    new ConfigDescription("Dig a hole",
        null,
        new BindruneAttributes { HeldItems = new[] { "skill:Pickaxes" } }));
```

That is the whole integration. The tag is an ordinary object in your own assembly; without Bindrune installed it is inert, and nothing about your mod changes.

This is the same trick the `ConfigurationManagerAttributes` ecosystem uses, so if you already tag settings for the config manager, you already know the shape. Both tags can sit on the same setting.

### Rules

- The class must be **named** `BindruneAttributes`. Any namespace and any accessibility will do, since it is matched on `GetType().Name`.
- `Situations` and `HeldItems` may be **fields or properties**, as long as they are public instance members of type `string[]` (anything enumerable as strings works).
- Either may be omitted. An empty declaration is ignored.
- **Your declaration is final.** It beats the user's own tagging, the shared list and everything else, and the panel greys those controls out rather than letting a user argue with your code. Declare what is true, not what you would prefer.

### `Situations`

Use these names exactly. Anything else is ignored. There is no free text, because two binds can only be compared when both use the same vocabulary.

| | |
|---|---|
| `World` | ordinary play |
| `Build placement` | hammer out, placing or removing |
| `Build menu` | the piece selection panel is open |
| `Inventory` | the inventory is open |
| `Container` | a container is open |
| `Crafting` | at a crafting station |
| `Map` | the map is open |
| `Chat` | the chat box has the keyboard |
| `Vehicle` | attached to a ship |
| `Custom` | your own mode, which nothing outside your mod can see |

List every situation the bind is live in. More is safer than fewer: a missing situation can silence a real clash, an extra one only makes a clash more likely to be reported.

`Custom` is a deliberate escape hatch. It means "a state only this mod knows about": two binds both marked `Custom` count as sharing a situation, and the on-screen hints never treat it as a place that can fail to match.

### `HeldItems`

For binds that only mean something with something in hand. Three forms:

| Form | Example | Covers |
|---|---|---|
| `skill:<Skill>` | `skill:Pickaxes` | every item using that skill |
| `type:<ItemType>` | `type:Shield` | every item of that type |
| `item:<prefab>` | `item:ood_remote` | one specific item, by prefab name |

Skill and type names are Valheim's own enum names (`Skills.SkillType`, `ItemDrop.ItemData.ItemType`). Prefab names are the GameObject names in `ObjectDB`, so your own items work exactly like the game's.

Groups and single items compare correctly against each other: a bind needing `skill:Pickaxes` and one needing `item:PickaxeIron` are understood to overlap, because Bindrune expands both to the actual set of prefabs.

Note the axes are independent. `Situations` says *where*, `HeldItems` says *what you are carrying*, and a bind can declare either, both or neither. Declaring only `HeldItems` is normal and correct for an item-driven bind; it does not mean "nowhere".

## What Bindrune does with it

- Pairs that can never be live at the same time stop being reported as clashes.
- Pairs that genuinely meet are marked **confirmed**, with your situation named in the explanation.
- Your bind can be put on the player's HUD and will appear only when it actually applies.

## If you cannot change your mod

Anyone, whether you, a modpack author or a user, can describe binds from the outside in `BepInEx/config/Bindrune/known.txt`, without touching the mod. One line per bind:

```
<bind id><TAB><situations, comma separated><TAB><held items, comma separated>
cfg:com.example.mod:General:DigKey	World	skill:Pickaxes
```

The bind id is the one Bindrune shows in its panel. For a BepInEx setting it is `cfg:<plugin GUID>:<section>:<key>`. Treat it as opaque and copy it from the panel rather than building it by hand.

That file lives in `config`, so it travels with a modpack. That is the point: a pack author can describe the whole pack once for everyone who subscribes. A user's own tagging still wins over it, and a mod's own declaration wins over both.

## Things worth knowing

- **A hardcoded key is invisible.** If your mod reads `Input.GetKeyDown(KeyCode.G)` with no setting behind it, there is nothing for Bindrune, any other tool or the user to find. Put it in a config entry.
- **`KeyboardShortcut` or `KeyCode`: pick by how the key is used.** BepInEx only reports a `KeyboardShortcut` as pressed when the held keys match exactly, so a modifier genuinely protects the bind. The same rule means it does not fire while any other key is held, including movement keys. For a key meant to work while moving, a plain `KeyCode` is the better choice; it fires under any modifier, and Bindrune reports it that way.
- **A keybind stored as a string is read-only in Bindrune.** It will be listed and parsed, but not rewritten, because writing a format we guessed at is not safe. A typed setting is editable.
