using System;
using System.Collections.Generic;
using System.IO;
using XPortal;
using XPortal.Plus;
using UnityEngine;
using History = ValheimModPack.PinRemoval.PinHistoryController;
internal static class MapMarkerTests
{
    private static int checks;
    private static string directory;
    private static void Check(bool passed, string label) { checks++; if (!passed) throw new Exception(label); }
    private static void Throws(Action action, string label)
    { bool threw = false; try { action(); } catch { threw = true; } Check(threw, label); }
    private static MapPinBinding Binding(string id)
    { return new MapPinBinding { PortalId = id, Name = "Горы", Author = "", Icon = 6, X = 1, Y = 5000, Z = 2 }; }
    public static int Main(string[] args)
    {
        directory = args[0]; Directory.CreateDirectory(directory);
        try { Policy(); Persistence(); Native(); System.Console.WriteLine("PASS: " + checks + " AnyPortal+ map marker assertions, real helper source with isolated native doubles; no game run."); return 0; }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        finally { PlusMapMarkers.Reset(); }
    }
    private static void Policy()
    {
        MapPinBinding binding = Binding("1:1"); var bindings = new List<MapPinBinding> { binding }; var pins = new List<MapPinBinding> { binding.Copy() };
        var live = new HashSet<string>();
        Check(MapPinCleanupPolicy.Select(bindings, live, pins, false).Count == 0, "incomplete registry cannot delete");
        Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 1, "missing persistent portal eligible");
        live.Add("1:1"); Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "live distant portal kept"); live.Clear();
        pins[0].Name = "manual rename"; Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "manual rename preserved");
        pins[0] = binding.Copy(); pins[0].Icon = 0; Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "manual icon preserved");
        pins[0] = binding.Copy(); pins[0].X += .001f; Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "manual moved pin preserved");
        pins[0] = binding.Copy(); pins[0].Owner = 123; Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "different owner pin preserved");
        pins[0] = binding.Copy(); pins[0].Author = "remote-author"; Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "different author pin preserved");
        pins[0] = binding.Copy(); pins.Add(binding.Copy()); Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "duplicate fingerprints preserved"); pins.RemoveAt(1);
        MapPinBinding other = binding.Copy(); other.PortalId = "1:2"; bindings.Add(other); live.Add("1:2");
        Check(MapPinCleanupPolicy.Select(bindings, live, pins, true).Count == 0, "same bound pin also belongs to live portal");
        Check(MapPinCleanupPolicy.SameContext(1, 2, 1, 2, true), "same complete context");
        Check(!MapPinCleanupPolicy.SameContext(1, 2, 3, 2, true), "world mismatch denied");
        Check(!MapPinCleanupPolicy.SameContext(1, 2, 1, 3, true), "character mismatch denied");
        Check(!MapPinCleanupPolicy.SameContext(0, 2, 0, 2, true), "zero world denied");
        Check(!MapPinCleanupPolicy.SameContext(1, 2, 1, 2, false), "complete flag revalidated");
    }
    private static void Persistence()
    {
        string file = Path.Combine(directory, "store.dat"); var store = new MapPinBindingsStore(file, 100, 200);
        Check(store.Load().Count == 0, "new character has empty bindings"); store.Save(new List<MapPinBinding>());
        Check(store.Load().Count == 0, "empty file roundtrips");
        var binding = Binding("-2:4294967295"); store.Save(new List<MapPinBinding> { binding });
        Check(binding.Name[0] == '\u0413', "test sources preserve Cyrillic UTF8");
        var loaded = store.Load(); Check(loaded.Count == 1 && loaded[0].SameBinding(binding), "Unicode and dungeon altitude roundtrip");
        loaded[0].Name = "changed"; Check(store.Load()[0].Name == binding.Name, "loaded records do not mutate file");
        Throws(() => new MapPinBindingsStore(file, 101, 200).Load(), "crossworld file denied");
        Throws(() => new MapPinBindingsStore(file, 100, 201).Load(), "crosscharacter file denied");
        byte[] original = File.ReadAllBytes(file);
        Throws(() => store.Save(new List<MapPinBinding> { binding, binding.Copy() }), "duplicate ids rejected");
        Check(Same(original, File.ReadAllBytes(file)), "failed serialization preserves original");
        foreach (Action<MapPinBinding> mutate in new Action<MapPinBinding>[] {
            b => b.X = Single.NaN, b => b.Y = Single.PositiveInfinity, b => b.Z = 100001,
            b => b.PortalId = "01:1", b => b.PortalId = "0:1", b => b.PortalId = "1:0",
            b => b.Icon = 4, b => b.Name = new string('x', 257), b => b.Author = new string('x', 257) })
        { var invalid = binding.Copy(); mutate(invalid); Throws(() => store.Save(new List<MapPinBinding> { invalid }), "invalid binding rejected"); }
        byte[] corrupt = (byte[])original.Clone(); corrupt[28] ^= 1; File.WriteAllBytes(file, corrupt);
        Throws(() => store.Load(), "checksum failure rejected"); Check(Same(corrupt, File.ReadAllBytes(file)), "corrupt original remains intact");
        File.WriteAllBytes(file, new byte[10]); Throws(() => store.Load(), "truncated file rejected");
        File.WriteAllBytes(file, original); store.Save(new List<MapPinBinding>()); Check(store.Load().Count == 0, "atomic replacement supported");
    }
    private static bool Same(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    private static int fixture;
    private static KnownPortal Setup()
    {
        PlusMapMarkers.Reset(); UnifiedPopup.instance = new UnifiedPopup();
        BepInEx.Paths.GameRootPath = Path.Combine(directory, "fixture-" + (++fixture));
        ZNet.instance = new ZNet { Server = false }; ZDOMan.instance = new ZDOMan();
        Player.m_localPlayer = new Player(); Minimap.instance = new Minimap(); Time.unscaledTime++;
        KnownPortalsManager.Instance = new KnownPortalsManager { HasCompleteSnapshot = true, SnapshotRevision = 1 };
        XPortal.RPC.SendToServer.Requests = 0;
        HarmonyLib.AccessTools.HistoryEnabled = false; History.Fail = false; History.Created = History.Renamed = History.Removed = 0;
        var portal = new KnownPortal { Id = new ZDOID(1, 1), Name = "Portal", Location = new Vector3(100, 2, 200) };
        KnownPortalsManager.Instance.Portals.Add(portal); PlusMapMarkers.Tick(); return portal;
    }
    private static YesNoPopup Popup() { return UnifiedPopup.instance.popupStack.Peek() as YesNoPopup; }
    private static void AskCleanup()
    {
        PlusMapMarkers.CleanupWithConfirmation();
        if (!ZNet.instance.Server && KnownPortalsManager.Instance.HasCompleteSnapshot)
        { KnownPortalsManager.Instance.SnapshotRevision++; Time.unscaledTime += 2; PlusMapMarkers.Tick(); }
    }
    private static void Native()
    {
        KnownPortal portal = Setup(); Check(PlusMapMarkers.GetIconSprite(-1) == null, "no default list icon");
        Check(PlusMapMarkers.GetIconSprite(6) != null, "native portal sprite available");
        portal.Icon = -2; Check(!PlusMapMarkers.AddOrUpdate(portal) && Minimap.instance.m_pins.Count == 0, "invalid negative icon rejected"); portal.Icon = -1;
        Check(PlusMapMarkers.AddOrUpdate(portal), "create owned marker");
        Minimap.PinData pin = Minimap.instance.m_pins[0]; Check(pin.m_type == Minimap.PinType.Icon4 && pin.m_name == "Portal", "default map pin uses portal icon");
        Check(PlusMapMarkers.AddOrUpdate(portal) && Minimap.instance.m_pins.Count == 1, "repeated add reuses existing pin");
        pin.m_checked = true; PlusMapMarkers.Reset(); PlusMapMarkers.Tick(); KnownPortalsManager.Instance.Portals.Clear();
        AskCleanup(); Check(Popup() != null, "owned marker reload ignores checked state");
        Popup().No(); Check(Minimap.instance.m_pins.Count == 1 && !UnifiedPopup.IsVisible(), "cancel keeps pin and closes own popup");
        AskCleanup(); Popup().Yes(); Check(Minimap.instance.m_pins.Count == 0, "confirmed absent portal removed");

        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear();
        KnownPortalsManager.Instance.HasCompleteSnapshot = false; AskCleanup();
        Check(!UnifiedPopup.IsVisible() && Minimap.instance.m_pins.Count == 1, "client incomplete snapshot cleanup refused");
        KnownPortalsManager.Instance.HasCompleteSnapshot = true; AskCleanup();
        KnownPortalsManager.Instance.Portals.Add(portal); Popup().Yes(); Check(Minimap.instance.m_pins.Count == 1, "portal recreated before confirmation preserved");
        KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        KnownPortalsManager.Instance.HasCompleteSnapshot = false; Popup().Yes(); Check(Minimap.instance.m_pins.Count == 1, "snapshot invalidation before confirmation preserved");

        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        YesNoPopup old = Popup(); ZNet.instance.World++; old.Yes(); Check(Minimap.instance.m_pins.Count == 1, "world changed during confirmation preserved");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        old = Popup(); Player.m_localPlayer.Character++; old.Yes(); Check(Minimap.instance.m_pins.Count == 1, "character changed during confirmation preserved");

        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        Minimap.instance.m_pins[0].m_name = "Manual"; Popup().Yes(); Check(Minimap.instance.m_pins.Count == 1, "manual rename during confirmation preserved");
        portal = Setup(); Minimap.instance.AddPin(portal.Location, Minimap.PinType.Icon4, portal.Name, true, false, 0, Splatform.PlatformUserID.None);
        KnownPortalsManager.Instance.Portals.Clear(); AskCleanup(); Check(!UnifiedPopup.IsVisible(), "unbound identical manual pin not cleaned");
        KnownPortalsManager.Instance.Portals.Add(portal); PlusMapMarkers.AddOrUpdate(portal); Check(Minimap.instance.m_pins.Count == 1, "explicit exact manual pin adopted");
        KnownPortalsManager.Instance.Portals.Clear(); AskCleanup(); Popup().Yes(); Check(Minimap.instance.m_pins.Count == 0, "explicitly adopted pin cleaned");

        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); ZNet.instance.Server = true; KnownPortalsManager.Instance.Portals.Clear();
        ZDOMan.instance.Portals.Add(new ZDO { m_uid = portal.Id }); AskCleanup();
        Check(!UnifiedPopup.IsVisible(), "host persistent remote portal preserved independently of client registry");
        ZDOMan.instance.Portals.Clear(); AskCleanup(); Popup().Yes(); Check(Minimap.instance.m_pins.Count == 0, "host persistent deletion cleanup");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); ZNet.instance.Server = true; KnownPortalsManager.Instance.Portals.Clear();
        ZDOMan.instance.Portals.Add(new ZDO { m_uid = new ZDOID(2, 99), Position = portal.Location }); AskCleanup();
        Check(!UnifiedPopup.IsVisible() && Minimap.instance.m_pins.Count == 1, "host world-load ID remap retains authoritative same-position portal pin");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); portal.Id = new ZDOID(2, 99);
        Check(PlusMapMarkers.AddOrUpdate(portal) && Minimap.instance.m_pins.Count == 1, "explicit remapped portal pin adoption reuses pin");
        string bindingFile = Path.Combine(BepInEx.Paths.GameRootPath, "ValheimModpack", "AnyPortalPlus", "MapPins", "99-55.dat");
        Check(new MapPinBindingsStore(bindingFile, 99, 55).Load().Count == 1, "remap adoption removes prior fingerprint binding");

        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear();
        PlusMapMarkers.CleanupWithConfirmation(); Check(!UnifiedPopup.IsVisible() && XPortal.RPC.SendToServer.Requests == 1, "guest cleanup waits for fresh full snapshot");
        Time.unscaledTime += 2; PlusMapMarkers.Tick(); Check(XPortal.RPC.SendToServer.Requests == 2 && !UnifiedPopup.IsVisible(), "guest refresh retries once after throttle interval");
        Time.unscaledTime += 10; PlusMapMarkers.Tick(); Check(!UnifiedPopup.IsVisible() && Minimap.instance.m_pins.Count == 1, "guest refresh timeout retains pins");
        PlusMapMarkers.CleanupWithConfirmation(); ZNet.instance.World++; KnownPortalsManager.Instance.SnapshotRevision++; Time.unscaledTime += 2; PlusMapMarkers.Tick();
        Check(!UnifiedPopup.IsVisible() && Minimap.instance.m_pins.Count == 1, "world changed while awaiting snapshot never deletes or opens stale popup");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear();
        PlusMapMarkers.CleanupWithConfirmation(); KnownPortalsManager.Instance.SnapshotRevision++; Time.unscaledTime += 2; PlusMapMarkers.Tick();
        Check(UnifiedPopup.IsVisible(), "new complete guest snapshot opens cleanup confirmation"); Popup().No();

        portal = Setup(); HarmonyLib.AccessTools.HistoryEnabled = true; PlusMapMarkers.AddOrUpdate(portal);
        Check(History.Created == 1, "creation written through history integration"); portal.Name = "New name"; portal.Icon = 0;
        Check(PlusMapMarkers.AddOrUpdate(portal) && History.Renamed == 1 && Minimap.instance.m_pins.Count == 1, "owned marker rename/icon update retains one pin");
        KnownPortalsManager.Instance.Portals.Clear(); AskCleanup(); History.Fail = true; Popup().Yes();
        Check(Minimap.instance.m_pins.Count == 1 && History.Removed == 0, "history persistence failure prevents removal");
        History.Fail = false; AskCleanup(); Popup().Yes(); Check(History.Removed == 1 && Minimap.instance.m_pins.Count == 0, "cleanup deletion recorded by history");

        portal = Setup(); HarmonyLib.AccessTools.HistoryEnabled = true; History.Fail = true;
        Check(!PlusMapMarkers.AddOrUpdate(portal) && Minimap.instance.m_pins.Count == 0, "failed creation archive rolls back native marker");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        old = Popup(); PopupBase foreign = new PopupBase(); UnifiedPopup.Push(foreign); PlusMapMarkers.Reset();
        Check(ReferenceEquals(UnifiedPopup.instance.popupStack.Peek(), foreign), "session reset never closes foreign popup");
        UnifiedPopup.Pop(); old.No(); Check(!UnifiedPopup.IsVisible(), "stale own popup below foreign dialog can still cancel safely");
        portal = Setup(); PlusMapMarkers.AddOrUpdate(portal); KnownPortalsManager.Instance.Portals.Clear(); AskCleanup();
        UnifiedPopup.instance.popupStack.Clear(); Time.unscaledTime += 2; PlusMapMarkers.Tick(); AskCleanup();
        Check(UnifiedPopup.IsVisible(), "externally cleared native popup does not permanently block cleanup"); Popup().No();
    }
}
