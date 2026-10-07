using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using ValheimModPack.WorldCharacters;

internal static class MapCaptureTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new Exception(message); }
    private static MapCaptureSnapshot Read(Minimap map, ZNet network) { return GameMapCapture.TryReadFields(map, network); }

    private static void NativeStub() { }
    private static void Compatibility()
    {
        MethodInfo method = typeof(MapCaptureTests).GetMethod("NativeStub",BindingFlags.Static|BindingFlags.NonPublic);
        string hash;
        using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", "");
        Check(MapCaptureCompatibility.Matches(method,hash), "Supported exact IL fingerprint is accepted");
        Check(!MapCaptureCompatibility.Matches(method,"unknown"), "Changed native IL uses native save fallback");
        Check(!MapCaptureCompatibility.Matches(null,hash), "Missing native method uses native save fallback");
        Check(!MapCaptureCompatibility.CanSkipNativeCapture(), "Unrecognized native fixture cannot enable optimization");
        Check(GameMapCapture.TryRead(Map(),new ZNet()) == null, "Unknown serializer retains native SaveMapData path");
        Check(!MapCaptureCompatibility.HasIntervention(method), "Unpatched native method is allowed");
        try
        {
            HarmonyLib.Harmony.ForeignOwner = "external.serializer";
            Check(MapCaptureCompatibility.HasIntervention(method), "Foreign Harmony serialization patch uses native fallback");
            HarmonyLib.Harmony.ForeignOwner = null;
            Check(!MapCaptureCompatibility.HasIntervention(method), "Removed Harmony patch is observed on the next map check");
            HarmonyLib.Harmony.ThrowOnRead = true;
            Check(MapCaptureCompatibility.HasIntervention(method), "Unavailable Harmony metadata uses native fallback");
        }
        finally { HarmonyLib.Harmony.ForeignOwner = null; HarmonyLib.Harmony.ThrowOnRead = false; }
        Check(MapCaptureCompatibility.HasIntervention(null), "Missing patch target uses native fallback");
    }

    // Mirrors the supported native GetMapData inner package, independently of
    // the production packed comparison. Its byte expansion is the costly path.
    private static byte[] NativeBody(Minimap map, ZNet network)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(map.m_textureSize);
            for (int i = 0; i < map.Own.Length; ++i) writer.Write(map.Own[i]);
            for (int i = 0; i < map.Own.Length; ++i) writer.Write(map.Shared[i]);
            writer.Write(map.SavedPins.Count(p => p.m_save));
            foreach (Minimap.PinData pin in map.SavedPins)
            {
                if (!pin.m_save) continue;
                writer.Write(pin.m_name); writer.Write(pin.m_pos.x); writer.Write(pin.m_pos.y); writer.Write(pin.m_pos.z);
                writer.Write((int)pin.m_type); writer.Write(pin.m_checked); writer.Write(pin.m_ownerID); writer.Write(pin.m_author.ToString());
            }
            writer.Write(network.IsReferencePositionPublic()); writer.Flush(); return stream.ToArray();
        }
    }
    private static Minimap Map()
    {
        var map = new Minimap();
        map.SavedPins.Add(new Minimap.PinData { m_name = "Home", m_ownerID = 44 });
        map.SavedPins.Add(new Minimap.PinData { m_name = "Portal", m_type = Minimap.PinType.Icon1 });
        return map;
    }
    private static void Change(Action<Minimap, ZNet> mutate, string name, bool persisted = true)
    {
        Minimap map = Map(); var network = new ZNet(); var policy = new MapCapturePolicy();
        byte[] before = NativeBody(map, network); MapCaptureSnapshot captured = Read(map, network);
        Check(captured != null && policy.RequiresCapture(captured, false), "First map requires capture: " + name);
        policy.Admit(captured); mutate(map, network);
        bool changed = !before.SequenceEqual(NativeBody(map, network));
        Check(changed == persisted, "Independent native serialization changes as expected: " + name);
        Check(policy.RequiresCapture(Read(map, network), false) == changed, "Every native input is observed: " + name);
        if (changed)
        {
            // Reading does not consume a change while durable admission is skipped.
            Check(policy.RequiresCapture(Read(map, network), false), "Unadmitted map remains dirty: " + name);
            policy.Admit(Read(map, network));
        }
        Check(!policy.RequiresCapture(Read(map, network), false), "Admitted map becomes clean: " + name);
    }
    private static void AllInputs()
    {
        Change((m,n) => m.Own[0] = true, "own exploration first bit");
        Change((m,n) => m.Own[m.Own.Length - 1] = true, "own exploration partial-word last bit");
        Change((m,n) => m.Shared[72] = true, "shared exploration");
        Change((m,n) => m.m_textureSize++, "texture size");
        Change((m,n) => m.Grids(130), "grid dimensions");
        Change((m,n) => m.SavedPins[0].m_name = "renamed directly", "pin name");
        Change((m,n) => m.SavedPins[0].m_pos.x = 1, "pin x");
        Change((m,n) => m.SavedPins[0].m_pos.y = 1, "pin y");
        Change((m,n) => m.SavedPins[0].m_pos.z = 1, "pin z");
        Change((m,n) => m.SavedPins[0].m_type = Minimap.PinType.Icon2, "pin type");
        Change((m,n) => m.SavedPins[0].m_checked = true, "pin checked");
        Change((m,n) => m.SavedPins[0].m_ownerID++, "pin owner");
        Change((m,n) => m.SavedPins[0].m_author.Value = "Steam_123", "pin author");
        Change((m,n) => m.SavedPins[0].m_save = false, "saved flag disabled");
        Change((m,n) => m.SavedPins.RemoveAt(0), "pin removed");
        Change((m,n) => m.SavedPins.Add(new Minimap.PinData { m_name = "New" }), "saved pin added");
        Change((m,n) => m.SavedPins.Reverse(), "saved pin order");
        Change((m,n) => n.PublicPosition = true, "public player position");
        Change((m,n) => m.SavedPins[0].Icon = new object(), "display-only pin data", false);
        Change((m,n) => m.SavedPins.Add(new Minimap.PinData { m_name = "Visitor", m_save = false }), "unsaved temporary pin", false);
        Minimap map = Map(); var net = new ZNet(); map.SavedPins.Add(new Minimap.PinData { m_save = false });
        var policy = new MapCapturePolicy(); policy.Admit(Read(map,net));
        map.SavedPins[2].m_name = "temporary rename"; map.SavedPins[2].m_pos.x = 123; map.SavedPins[2].m_checked = true;
        Check(!policy.RequiresCapture(Read(map,net),false), "Temporary pin direct mutation does not change saved map");
        map.SavedPins[2].m_save = true;
        Check(policy.RequiresCapture(Read(map,net),false), "A formerly temporary pin becoming saved is captured");
    }
    private static void AdmissionAndFallback()
    {
        Minimap map = Map(); var net = new ZNet(); var policy = new MapCapturePolicy();
        MapCaptureSnapshot first = Read(map,net); policy.Admit(first);
        Check(!policy.RequiresCapture(Read(map,net),false), "Unchanged periodic map is skipped");
        Check(policy.RequiresCapture(Read(map,net),true), "Explicit final/manual save forces unchanged map");
        Check(policy.RequiresCapture(null,false), "Unknown native map layout forces native path");
        Check(Read(null,net) == null && Read(map,null) == null, "Missing native objects use fallback");
        Check(policy.RequiresCapture(Read(Map(),net),false), "A replacement minimap cannot reuse another context");
        policy.Reset(); Check(policy.RequiresCapture(first,false), "New session cannot reuse old admitted map"); policy.Admit(first);
        map.Own[20] = true;
        using (var writer = new SnapshotWriter(1))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            try
            {
                Check(writer.TryEnqueue(1, () => { entered.Set(); release.WaitOne(); return null; }, (a,b,c) => {}), "Occupy real bounded snapshot writer");
                Check(entered.WaitOne(5000), "Writer fixture starts");
                MapCaptureSnapshot candidate = Read(map,net);
                Check(!writer.TryEnqueue(1, () => null, (a,b,c) => {}), "Full writer refuses map admission");
                Check(policy.RequiresCapture(Read(map,net),false), "Capacity-skipped map must not become clean");
                release.Set(); writer.Barrier(); writer.Drain();
                Check(writer.TryEnqueue(1, () => null, (a,b,c) => {}), "Writer admits changed map after prior save");
                policy.Admit(candidate);
                Check(!policy.RequiresCapture(Read(map,net),false), "Only successful admission advances map baseline");
                writer.Barrier(); writer.Drain();
            }
            finally { release.Set(); }
        }
        map.InvalidShared(); Check(Read(map,net) == null, "Unavailable shared grid takes native fallback");
        policy.Admit(null); Check(policy.RequiresCapture(first,false), "Fallback native save invalidates incompatible comparison baseline");
    }
    private static byte[] World(byte[] map, float position)
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(1); for (int i=0;i<4;++i) { if (i<3) writer.Write(false); writer.Write(position); writer.Write(0f); writer.Write(0f); }
            writer.Write(map.Length); writer.Write(map); writer.Flush(); return stream.ToArray();
        }
    }
    private static void RetainedMaps()
    {
        byte[] native = NativeBody(Map(),new ZNet()); byte[] cached = World(native,1);
        byte[] positions = World(new byte[0],2);
        byte[] local = StateCodec.RetainMap(positions,cached);
        Check(local.Skip(59).SequenceEqual(native), "Host position-only capture retains full cached map");
        var initial = new CharacterState { World=11,Character=44,Owner="test",Name="Viking",Revision=1,WorldData=cached };
        var session = new CharacterSession(initial); session.MarkLoaded(session.Token);
        Check(session.RetainAcceptedMap(positions).SequenceEqual(local), "Guest position-only wire capture retains host accepted map");
        byte[] changed = (byte[])native.Clone(); changed[4] ^= 1;
        var complete = initial.Copy(); complete.WorldData = World(changed,3);
        session.Stage(session.Token,1,complete,false);
        Check(session.RetainAcceptedMap(positions).Skip(59).SequenceEqual(changed), "Position-only updates retain an admitted map still awaiting disk ACK");
        Check(BitConverter.ToSingle(local,5)==2, "Retaining map still updates character positions");
    }
    private static void NativeExpansionRegression()
    {
        var map = new Minimap(); map.m_textureSize = 2048; map.Grids(2048*2048);
        var net = new ZNet(); var policy = new MapCapturePolicy(); policy.Admit(Read(map,net));
        var watch = Stopwatch.StartNew(); byte[] expanded = NativeBody(map,net); watch.Stop(); double nativeMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true)) gzip.Write(expanded,0,expanded.Length);
            compressed = stream.ToArray();
        }
        watch.Stop(); double compressionMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        for (int i=0;i<12;++i) Check(!policy.RequiresCapture(Read(map,net),false), "Stationary periodic map avoids native expansion #"+i);
        watch.Stop();
        Check(expanded.Length == 8*1024*1024+9, "Supported native exploration expansion is eight MiB before compression");
        Check(compressed.Length < expanded.Length, "Native-equivalent GZip Fastest compresses the expanded exploration data");
        Console.WriteLine("Map comparison benchmark: native-equivalent expansion+GZip Fastest="+(nativeMs+compressionMs).ToString("F2")+" ms (expansion="+nativeMs.ToString("F2")+", compression="+compressionMs.ToString("F2")+"); packed comparison average="+(watch.Elapsed.TotalMilliseconds/12).ToString("F2")+" ms. Synthetic map, managed runtime only; no timing threshold or FPS assertion.");
    }
    public static int Main()
    {
        try { Compatibility(); AllInputs(); AdmissionAndFallback(); RetainedMaps(); NativeExpansionRegression(); Console.WriteLine("PASS: "+checks+" map-capture regression assertions."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
