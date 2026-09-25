# Mushroom Sync

Shared server-authoritative sync for the Mushroom mods. The host decides, clients
follow, and nobody's local `.cfg` gets rewritten.

It has no gameplay of its own. It exists so the mods that need server-authoritative
settings share one implementation instead of four copies of the same 300 lines.

## Required by

| Mod | Uses |
|---|---|
| Combat Adjustments | Config sync |
| Craftable Spawners | Config sync |
| Haldor Expansion | Config sync |
| Random Yggdrasil | Raw channel (world rotations) |

**`MushroomSync.dll` must be installed wherever those are** — on every client and on
the dedicated server. The release zip ships it alongside them, so extracting
`MushroomMods-plugins.zip` into `BepInEx/` is enough.

## Using it

Config sync, in a plugin's `Awake`, before binding settings:

```csharp
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(MushroomSyncPlugin.PluginGuid)]
public class MyPlugin : BaseUnityPlugin
{
    internal static ConfigSync Sync;

    private void Awake()
    {
        Sync = ConfigSync.Create(PluginGuid, PluginVersion, Logger)
            .Protecting(Config)
            .OnApplied(ReapplyRuntimeState);

        EnableThing = Config.Bind("General", "EnableThing", true, "...");
        Sync.Register(EnableThing);

        Sync.WatchForChanges(Config).Start();
    }
}
```

After that `EnableThing.Value` returns the host's value on a synced client and the
local one everywhere else — **no call site needs changing**. Where you need to know
which you got, or want the local value alongside, use
`Sync.TryGetSyncedValue(entry, out var v)`.

`OnApplied` matters for any mod that bakes settings into game objects: the config
changing is not enough, the values have to be pushed back in. It fires both when
host values arrive and when they are dropped.

### Settings that must stay local

```csharp
Sync.Exclude(SyncConfigInMultiplayer);   // the opt-out itself
Sync.Exclude(GrantTableVersion);         // server-only bookkeeping
```

An excluded entry is never sent and never overlaid.

### Opting out

Two gates, deliberately separate:

```csharp
Sync.GatedBy(() => LockConfiguration.Value);      // host: publish, or stay quiet
Sync.AcceptedWhen(() => SyncEnabled.Value);       // client: follow a host, or not
```

`GatedBy` is a **host-side switch** — it decides whether this machine publishes when
it is the server. Haldor Expansion uses only this: a client with `LockConfiguration`
off still follows a host that has it on.

`AcceptedWhen` is a **client-side opt-out**. Combat Adjustments passes the same
predicate to both, because its `SyncConfigInMultiplayer` means "do not sync at all".

Conflating the two is a bug worth naming: a host-side switch that also refused
incoming values would stop a client following a server merely because that client
would not have shared its own settings when hosting.

### Pushing your own data

For anything that is not a config entry, use the channel directly — this is what
Random Yggdrasil does for world rotations:

```csharp
var channel = SyncChannel.Create("MyMod.Thing", PluginVersion, Logger);
channel.WritePayload = pkg => { /* server writes */ };
channel.ReadPayload  = pkg => { /* client reads */ };
channel.Cleared      = reason => { /* fall back to local */ };
channel.Start();

channel.Broadcast();   // after the server changes something
```

`SyncChannel.ReadCount(pkg)` reads a length prefix with a sanity bound, so a corrupt
packet cannot make you allocate without limit.

## Design

See [docs/DESIGN.md](docs/DESIGN.md) for why it is built this way — the layering, why
it does not wrap login sockets the way ServerSync did, and why the value overlay is
read-only.
