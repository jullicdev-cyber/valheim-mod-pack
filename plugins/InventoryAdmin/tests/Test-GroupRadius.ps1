[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path $PSScriptRoot -Parent
$packRoot = Split-Path (Split-Path $pluginRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/inventory-admin-group-radius-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $fixtureDirectory 'GroupRadiusTests.exe'
$testSource = Join-Path $fixtureDirectory 'GroupRadiusTests.cs'
@'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using ValheimModPack.InventoryAdmin;

internal static class GroupRadiusTests
{
    private const long World = 741, OtherWorld = -742;
    private const string Alice = "Steam_76561198000000001", Bob = "Steam_76561198000000002";
    private static int checks;
    private static void Check(bool value, string message)
    { ++checks; if (!value) throw new Exception("FAIL: " + message); }
    private static void Near(double value, double expected, string message)
    { Check(Math.Abs(value - expected) < 0.0002, message + ": " + value + " != " + expected); }
    private static void Reject(Action action, string message)
    {
        ++checks;
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (ArgumentException) { return; }
        throw new Exception("FAIL: accepted " + message);
    }
    private static byte[] Change(byte[] bytes, int offset, byte value)
    { var copy = (byte[])bytes.Clone(); copy[offset] = value; return copy; }
    private static byte[] Prefix(byte[] bytes, int length)
    { var copy = new byte[length]; Buffer.BlockCopy(bytes, 0, copy, 0, length); return copy; }
    private static byte[] Append(byte[] bytes)
    { var copy = new byte[bytes.Length + 1]; Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length); return copy; }
    private static bool Equal(byte[] left, byte[] right)
    { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false; return true; }
    private static GroupRadiusSettings Settings()
    { var settings = new GroupRadiusSettings { Enabled = true, LeaderOwner = Alice }; settings.ExemptOwners.Add(Bob); return settings; }
    private static GroupRadiusState State()
    {
        return new GroupRadiusState { World = World, Generation = 4, Enabled = true, Radius = 500,
            LeaderOwner = Alice, LeaderName = "Ведущий", ApprovedConnectedCount = 2,
            LeaderValid = true, LeaderAlive = true, LeaderY = 5000, LeaderEpoch = 1 };
    }
    private static string FilePath(string root, long world)
    { return Path.Combine(root, "group-radius", world.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".dat"); }
    private static byte[] Persisted(long world, byte[] packet)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        { writer.Write(0x49414753); writer.Write(1); writer.Write(world); writer.Write(packet.Length); writer.Write(packet); writer.Flush(); return PolicyBinary.Seal(stream.ToArray()); }
    }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected state and junction directories.");
            Codec(); Activation(); Geometry(); Grace();
            Persistence(Path.Combine(args[0], "persist")); Corruption(Path.Combine(args[0], "corrupt"));
            Failures(Path.Combine(args[0], "failures"), args[1]);
            Console.WriteLine("Group radius policy/codec/persistence checks passed: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Codec()
    {
        var defaults = new GroupRadiusSettings();
        Check(!defaults.Enabled && defaults.Radius == 500 && defaults.LeaderOwner == "local-host" && defaults.ExemptOwners.Count == 0, "feature starts disabled with default leader and radius");
        var settings = Settings(); settings.Generation = 19; settings.ExemptOwners.Add("local-host");
        byte[] packet = GroupRadiusCodec.EncodeSettings(settings); var decoded = GroupRadiusCodec.DecodeSettings(packet);
        Check(decoded.Enabled && decoded.Radius == 500 && decoded.LeaderOwner == Alice && decoded.Generation == 19
            && decoded.ExemptOwners.Contains(Bob) && decoded.ExemptOwners.Contains("local-host"), "settings and stable host exemption round trip");
        var clone = settings.Clone(); clone.ExemptOwners.Clear(); clone.Radius = 50;
        Check(settings.ExemptOwners.Count == 2 && settings.Radius == 500, "settings clone is independent");
        var reordered = settings.Clone(); reordered.ExemptOwners.Clear(); reordered.ExemptOwners.Add("local-host"); reordered.ExemptOwners.Add(Bob);
        Check(Equal(packet, GroupRadiusCodec.EncodeSettings(reordered)), "exemption order has deterministic wire bytes");
        var maximum = new GroupRadiusSettings { Radius = 10000 };
        for (int i = 0; i < 128; ++i) maximum.ExemptOwners.Add("Steam_" + (76561198000000000L + i).ToString());
        Check(GroupRadiusCodec.DecodeSettings(GroupRadiusCodec.EncodeSettings(maximum)).ExemptOwners.Count == 128, "maximum exemption count accepted");
        maximum.ExemptOwners.Add("Steam_76561198000000128"); Reject(delegate { GroupRadiusCodec.EncodeSettings(maximum); }, "oversized exemptions");
        foreach (float value in new[] { Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity, 49.99f, 10000.01f })
        { var invalid = settings.Clone(); invalid.Radius = value; Reject(delegate { GroupRadiusCodec.EncodeSettings(invalid); }, "invalid radius " + value); }
        foreach (string owner in new[] { "Alice", "steam_76561198000000001", "Steam_00000000000000000", "Steam_7656119800000000x", " Steam_76561198000000001", "Steam_765611980000000001", "" })
        {
            var invalid = settings.Clone(); invalid.LeaderOwner = owner; Reject(delegate { GroupRadiusCodec.EncodeSettings(invalid); }, "invalid leader owner");
            invalid = settings.Clone(); invalid.ExemptOwners.Add(owner); Reject(delegate { GroupRadiusCodec.EncodeSettings(invalid); }, "invalid exemption owner");
        }
        var negative = settings.Clone(); negative.Generation = -1; Reject(delegate { GroupRadiusCodec.EncodeSettings(negative); }, "negative generation");
        var missing = settings.Clone(); missing.ExemptOwners = null; Reject(delegate { GroupRadiusCodec.EncodeSettings(missing); }, "missing exemption set");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(Change(packet, 20, 2)); }, "noncanonical settings boolean");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(Change(packet, 0, 0)); }, "wrong settings magic");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(Change(packet, 4, 2)); }, "future settings format");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(Append(packet)); }, "trailing settings bytes");
        for (int i = 0; i < packet.Length; ++i) { int length = i; Reject(delegate { GroupRadiusCodec.DecodeSettings(Prefix(packet, length)); }, "truncated settings " + i); }
        var count = (byte[])packet.Clone(); int countOffset = 25 + 4 + Encoding.UTF8.GetByteCount(settings.LeaderOwner);
        Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, count, countOffset, 4); Reject(delegate { GroupRadiusCodec.DecodeSettings(count); }, "unbounded settings declared count");
        var duplicate = Settings(); duplicate.ExemptOwners.Add(Alice); byte[] duplicatePacket = GroupRadiusCodec.EncodeSettings(duplicate);
        int firstOwnerOffset = 25 + 4 + Encoding.UTF8.GetByteCount(duplicate.LeaderOwner) + 4;
        int secondOwnerOffset = firstOwnerOffset + 4 + 23;
        Buffer.BlockCopy(duplicatePacket, firstOwnerOffset + 4, duplicatePacket, secondOwnerOffset + 4, 23);
        Reject(delegate { GroupRadiusCodec.DecodeSettings(duplicatePacket); }, "duplicate settings exemptions");
        var state = State(); state.LeaderName = new string('я', 256); state.ApprovedConnectedCount = 128;
        byte[] frame = GroupRadiusCodec.EncodeState(state); var restored = GroupRadiusCodec.DecodeState(frame);
        Check(restored.World == World && restored.LeaderY == 5000 && restored.LeaderName == state.LeaderName
            && restored.LeaderEpoch == 1 && restored.ApprovedConnectedCount == 128 && frame.Length < 1024, "dungeon and maximum Unicode leader state bounded round trip");
        state.LeaderName = "Ведущий"; frame = GroupRadiusCodec.EncodeState(state);
        Reject(delegate { GroupRadiusCodec.DecodeState(Change(frame, 28, 2)); }, "noncanonical state boolean");
        Reject(delegate { GroupRadiusCodec.DecodeState(Change(frame, frame.Length - 1, 2)); }, "noncanonical own-leader flag");
        Reject(delegate { GroupRadiusCodec.DecodeState(packet); }, "settings decoded as state");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(frame); }, "state decoded as settings");
        Reject(delegate { GroupRadiusCodec.DecodeState(Append(frame)); }, "trailing state data");
        for (int i = 0; i < frame.Length; ++i) { int length = i; Reject(delegate { GroupRadiusCodec.DecodeState(Prefix(frame, length)); }, "truncated state " + i); }
        foreach (float value in new[] { Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity, 100001f, -100001f, Single.MaxValue })
        { var invalid = State(); invalid.LeaderY = value; Reject(delegate { GroupRadiusCodec.EncodeState(invalid); }, "invalid dungeon height"); }
        foreach (int value in new[] { -1, 129, Int32.MaxValue })
        { var invalid = State(); invalid.ApprovedConnectedCount = value; Reject(delegate { GroupRadiusCodec.EncodeState(invalid); }, "invalid approved count"); }
        foreach (string name in new[] { new string('я', 257), "Bad\0Name", "\ud800" })
        { var invalid = State(); invalid.LeaderName = name; Reject(delegate { GroupRadiusCodec.EncodeState(invalid); }, "invalid leader name"); }
        var invalidState = State(); invalidState.World = 0; Reject(delegate { GroupRadiusCodec.EncodeState(invalidState); }, "missing state world");
        invalidState = State(); invalidState.LeaderEpoch = -1; Reject(delegate { GroupRadiusCodec.EncodeState(invalidState); }, "negative leader epoch");
        invalidState = State(); invalidState.LeaderValid = false; Reject(delegate { GroupRadiusCodec.EncodeState(invalidState); }, "alive leader without valid position");
        invalidState.LeaderAlive = false; Check(!GroupRadiusCodec.DecodeState(GroupRadiusCodec.EncodeState(invalidState)).LeaderValid, "unavailable leader can be represented");
        Reject(delegate { GroupRadiusCodec.DecodeSettings(new byte[16385]); }, "oversized settings packet");
        Reject(delegate { GroupRadiusCodec.DecodeState(new byte[16385]); }, "oversized state packet");
        var utf8 = State(); utf8.LeaderName = "AB"; byte[] badText = GroupRadiusCodec.EncodeState(utf8);
        int nameOffset = 33 + 4 + 23 + 4; badText[nameOffset] = 0xc0; badText[nameOffset + 1] = 0xaf;
        Reject(delegate { GroupRadiusCodec.DecodeState(badText); }, "overlong UTF-8 name");
    }
    private static void Activation()
    {
        var state = State(); Check(GroupRadiusPolicy.Active(state, 10, 11.4999), "fresh enabled group active before TTL");
        Check(!GroupRadiusPolicy.Active(state, 10, 11.5), "coordinates expire exactly at 1.5 seconds");
        Check(!GroupRadiusPolicy.Active(state, 10, 9.99), "clock reversal rejects freshness");
        Check(!GroupRadiusPolicy.Active(state, -1, 0) && !GroupRadiusPolicy.Active(state, 0, Double.NaN), "invalid clock cannot activate");
        var copy = state.Clone(); copy.Enabled = false; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "disabled setting suppresses boundary");
        copy = state.Clone(); copy.ApprovedConnectedCount = 1; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "solo game has no boundary");
        copy = state.Clone(); copy.OwnExempt = true; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "explicit own exemption suppresses boundary");
        copy = state.Clone(); copy.OwnIsLeader = true; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "leader moves freely");
        copy = state.Clone(); copy.LeaderAlive = false; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "dead leader suspends boundary");
        copy = state.Clone(); copy.LeaderValid = false; copy.LeaderAlive = false; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "missing or stale host leader position suspends boundary");
        copy = state.Clone(); copy.LeaderX = Single.NaN; Check(!GroupRadiusPolicy.Active(copy, 10, 10), "malformed center fails inactive");
        Near(GroupRadiusPolicy.WarningDistance(500), 450, "500 metre warning starts at 450");
        Check(!GroupRadiusPolicy.Warning(state, 10, 10, 449.99f, 0) && GroupRadiusPolicy.Warning(state, 10, 10, 450, 0), "warning threshold precise");
        Check(!GroupRadiusPolicy.Warning(state, 10, 12, 490, 0), "stale state cannot show warning");
        Check(GroupRadiusPolicy.Coordinate(100000) && GroupRadiusPolicy.Coordinate(-100000) && !GroupRadiusPolicy.Coordinate(100001), "terrain and interior coordinate bounds inclusive");
    }
    private static void Geometry()
    {
        float x, z; var state = State();
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 500, 0, 5, 3, out x, out z), "outward movement blocked at boundary"); Near(x, 0, "outward removed"); Near(z, 3, "tangent preserved");
        Check(!GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 600, 0, -5, 3, out x, out z), "outside inward movement unrestricted"); Near(x, -5, "inward preserved"); Near(z, 3, "inward tangent preserved");
        Check(!GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 500, 0, 0, 7, out x, out z), "pure tangent unrestricted"); Near(z, 7, "pure tangent unchanged");
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 499, 0, 5, 3, out x, out z), "inside crossing clipped"); Near(z, 3, "inside crossing tangent retained");
        Near(GroupRadiusPolicy.DistanceXZ(499 + x, z, 0, 0), 500, "inside crossing stops on circle");
        Check(!GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 450, 0, 10, 2, out x, out z), "movement in warning zone stays free"); Near(x, 10, "warning zone radial unchanged");
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 300, 400, 3, 4, out x, out z), "diagonal corner outward blocked"); Near(x, 0, "corner x radial removed"); Near(z, 0, "corner z radial removed");
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 300, 400, 7, 1, out x, out z), "diagonal mixed step constrained");
        Near(x * 0.6 + z * 0.8, 0, "diagonal constrained result has no outward component"); Near(-x * 0.8 + z * 0.6, -5, "diagonal tangent retained");
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 0, 0, 1000, 0, out x, out z), "large center crossing bounded"); Near(x, 500, "large center crossing ends at radius");
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 400, 0, 10, 2000, out x, out z), "huge tangent crossing clipped at first intersection");
        Near(GroupRadiusPolicy.DistanceXZ(400 + x, z, 0, 0), 500, "huge tangent result inside circle");
        state.LeaderX = 10500; state.LeaderZ = -10500;
        Check(GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 11000, -10500, 5, 1, out x, out z), "offset center uses relative coordinates"); Near(x, 0, "offset outward blocked");
        Check(!GroupRadiusPolicy.ConstrainMovement(state, 0, 2, 11000, -10500, 5, 1, out x, out z), "stale movement state permits free movement"); Near(x, 5, "stale movement unchanged");
        state.OwnExempt = true; Check(!GroupRadiusPolicy.ConstrainMovement(state, 0, 0, 11000, -10500, 5, 1, out x, out z), "exempt movement unrestricted");
        Near(GroupRadiusPolicy.DistanceXZ(-100000, -100000, 100000, 100000), Math.Sqrt(80000000000d), "coordinate corners use wide distance arithmetic");
        Reject(delegate { GroupRadiusPolicy.DistanceXZ(Single.NaN, 0, 0, 0); }, "invalid distance position");
    }
    private static void Grace()
    {
        var state = State(); var grace = new GroupRadiusRecovery();
        Check(grace.Observe(state, 0, 0, 0, 0) == GroupRadiusRecoveryStatus.Grace && grace.GraceUntil == 10, "initial activation has ten second grace even inside");
        Check(grace.Observe(state, 9.9, 9.9, 600, 0) == GroupRadiusRecoveryStatus.Grace, "initial grace remains before deadline");
        Check(grace.Observe(state, 10, 10, 600, 0) == GroupRadiusRecoveryStatus.Ready, "outside filtering resumes at grace deadline");
        Check(grace.Observe(state, 11, 11, 0, 0) == GroupRadiusRecoveryStatus.None, "inside after grace needs no status");
        Check(grace.Observe(state, 12, 12, 600, 0) == GroupRadiusRecoveryStatus.Ready && grace.GraceUntil == 10, "ordinary boundary crossing never grants fresh grace");
        state.LeaderX = 130; Check(grace.Observe(state, 13, 13, 1000, 0) == GroupRadiusRecoveryStatus.Grace && grace.GraceUntil == 23, "large center jump starts grace");
        state.LeaderX = 140; Check(grace.Observe(state, 14, 14, 1000, 0) == GroupRadiusRecoveryStatus.Grace && grace.GraceUntil == 23, "walking does not extend jump grace");
        Check(grace.Observe(state, 23, 23, 1000, 0) == GroupRadiusRecoveryStatus.Ready, "jump grace expires");
        Check(grace.TryConsumeRecovery() && !grace.TryConsumeRecovery(), "optional completion marker consumed at most once without moving player");
        Check(grace.Observe(state, 24, 24, 1000, 0) == GroupRadiusRecoveryStatus.Attempted, "completion cannot repeat for same epoch");
        state.LeaderEpoch++; Check(grace.Observe(state, 25, 25, 1000, 0) == GroupRadiusRecoveryStatus.Grace && grace.GraceUntil == 35, "explicit portal or respawn epoch starts fresh grace");
        Check(grace.Observe(state, 25, 27, 1000, 0) == GroupRadiusRecoveryStatus.None && !grace.TryConsumeRecovery(), "stale state cancels pending action");
        state.OwnExempt = true; Check(grace.Observe(state, 28, 28, 1000, 0) == GroupRadiusRecoveryStatus.None, "exemption cancels grace");
        grace.Reset(); state.OwnExempt = false; Check(grace.Observe(state, 30, 30, 0, 0) == GroupRadiusRecoveryStatus.Grace, "session reset starts independent grace");
        var another = state.Clone(); another.World = OtherWorld; Check(grace.Observe(another, 31, 31, 0, 0) == GroupRadiusRecoveryStatus.Grace && grace.GraceUntil == 41, "world switch has independent grace");
    }
    private static void Persistence(string root)
    {
        using (var store = new GroupRadiusStore(root))
        {
            Check(!store.Snapshot(World).Enabled && !store.IsCorrupt(World), "missing world settings default disabled");
            Check(!File.Exists(FilePath(root, World)), "reading defaults creates no settings file");
            Reject(delegate { using (var competing = new GroupRadiusStore(root)) { } }, "competing process lock");
            Reject(delegate { store.Snapshot(0); }, "zero world settings access");
            var next = Settings(); GroupRadiusSettings saved;
            Check(store.TryUpdate(World, 0, next, out saved) && saved.Generation == 1, "first update generation persisted");
            next.ExemptOwners.Clear(); next.Radius = 900;
            Check(store.Snapshot(World).ExemptOwners.Contains(Bob) && store.Snapshot(World).Radius == 500, "input mutation cannot change stored snapshot");
            saved.ExemptOwners.Clear(); saved.Enabled = false;
            Check(store.Snapshot(World).Enabled && store.Snapshot(World).ExemptOwners.Contains(Bob), "output mutation cannot change stored snapshot");
            var stale = Settings(); stale.Radius = 750;
            Check(!store.TryUpdate(World, 0, stale, out saved) && saved.Generation == 1 && saved.Radius == 500, "stale administrator edit rejected with current state");
            Check(!store.Snapshot(OtherWorld).Enabled && !store.Snapshot(OtherWorld).ExemptOwners.Contains(Bob), "exemptions isolated to world");
            int wins = 0; Exception failure = null; var start = new ManualResetEvent(false);
            ThreadStart edit = delegate
            {
                try { start.WaitOne(); GroupRadiusSettings current; if (store.TryUpdate(World, 1, Settings(), out current)) Interlocked.Increment(ref wins); }
                catch (Exception error) { failure = error; }
            };
            var left = new Thread(edit); var right = new Thread(edit); left.Start(); right.Start(); start.Set(); left.Join(); right.Join(); start.Dispose();
            if (failure != null) throw failure;
            Check(wins == 1 && store.Snapshot(World).Generation == 2, "simultaneous administrator CAS allows exactly one winner");
            var hostExempt = store.Snapshot(World); hostExempt.ExemptOwners.Add("local-host");
            Check(store.TryUpdate(World, 2, hostExempt, out saved) && saved.ExemptOwners.Contains("local-host"), "host exemption supported with remote leader");
            Check(store.TryUpdate(OtherWorld, 0, new GroupRadiusSettings { Radius = 50 }, out saved), "negative world identity has bounded numeric path");
        }
        using (var store = new GroupRadiusStore(root))
        {
            Check(store.Snapshot(World).Enabled && store.Snapshot(World).Generation == 3
                && store.Snapshot(World).ExemptOwners.Contains(Bob) && store.Snapshot(World).ExemptOwners.Contains("local-host"), "account exemptions survive disconnect and host restart");
            Check(store.Snapshot(OtherWorld).Radius == 50 && !store.Snapshot(OtherWorld).Enabled, "other world state persists independently");
        }
        Check(Directory.GetFiles(Path.Combine(root, "group-radius"), "*.dat").Length == 2
            && Directory.GetFiles(Path.Combine(root, "group-radius"), "*.tmp-*").Length == 0
            && Directory.GetFiles(Path.Combine(root, "group-radius"), "*.bak*").Length == 0, "atomic writes leave no temporary files or backups");
    }
    private static void Corruption(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "group-radius"));
        string path = FilePath(root, World); byte[] clean = Persisted(World, GroupRadiusCodec.EncodeSettings(Settings()));
        foreach (byte[] damaged in new[] { Change(clean, 5, 2), Prefix(clean, 16), Persisted(OtherWorld, GroupRadiusCodec.EncodeSettings(Settings())), new byte[16441] })
        {
            File.WriteAllBytes(path, damaged);
            using (var store = new GroupRadiusStore(root))
            {
                Check(store.IsCorrupt(World) && !store.Snapshot(World).Enabled && store.LastError(World).Length > 0, "corrupt file fails disabled with diagnostic");
                GroupRadiusSettings current = null;
                Reject(delegate { store.TryUpdate(World, 0, Settings(), out current); }, "overwrite corrupt file");
                Check(Equal(File.ReadAllBytes(path), damaged), "corrupt file preserved byte for byte");
            }
        }
        File.WriteAllBytes(path, clean);
        using (var store = new GroupRadiusStore(root))
        {
            Check(store.Snapshot(World).Enabled, "manually restored valid file loads");
            byte[] damaged = Change(clean, 10, 42); File.WriteAllBytes(path, damaged); GroupRadiusSettings current = null;
            Reject(delegate { store.TryUpdate(World, 0, Settings(), out current); }, "external corruption after cached load");
            Check(store.IsCorrupt(World) && !store.Snapshot(World).Enabled && Equal(File.ReadAllBytes(path), damaged), "external damage cannot be hidden by cached overwrite");
        }
    }
    private static void Failures(string root, string junctionRoot)
    {
        Reject(delegate { using (var store = new GroupRadiusStore(junctionRoot)) { } }, "linked administration path");
        using (var store = new GroupRadiusStore(root))
        {
            GroupRadiusSettings current; Check(store.TryUpdate(World, 0, Settings(), out current), "write-failure baseline saved");
            byte[] before = File.ReadAllBytes(FilePath(root, World));
            using (var held = new FileStream(FilePath(root, World), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var replacement = store.Snapshot(World); replacement.Radius = 850;
                Reject(delegate { store.TryUpdate(World, 1, replacement, out current); }, "persisted file sharing violation");
                Check(store.Snapshot(World).Generation == 1 && store.Snapshot(World).Radius == 500, "failed atomic write cannot publish memory edit");
                Check(Equal(File.ReadAllBytes(FilePath(root, World)), before), "failed atomic write preserves original settings");
            }
            Check(Directory.GetFiles(Path.Combine(root, "group-radius"), "*.tmp-*").Length == 0, "failed atomic write cleans staging file");
            Directory.CreateDirectory(FilePath(root, OtherWorld));
            Check(store.IsCorrupt(OtherWorld) && !store.Snapshot(OtherWorld).Enabled, "directory impersonating file fails disabled");
            Reject(delegate { store.TryUpdate(OtherWorld, 0, Settings(), out current); }, "file-path directory overwrite");
        }
        var disposed = new GroupRadiusStore(root); disposed.Dispose();
        bool rejected = false; try { disposed.Snapshot(World); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "disposed store cannot serve settings");
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8
$linkedRoot = Join-Path $fixtureDirectory 'linked-administration'
$linkedTarget = Join-Path $fixtureDirectory 'linked-target'
New-Item -ItemType Directory -Force -Path $linkedRoot,$linkedTarget | Out-Null
New-Item -ItemType Junction -Path (Join-Path $linkedRoot 'group-radius') -Target $linkedTarget | Out-Null
$sources = @('Policy.cs','PermissionStore.cs','GroupRadius.cs','GroupRadiusStore.cs') | ForEach-Object { Join-Path $pluginRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources $testSource
if ($LASTEXITCODE -ne 0) { throw 'Group radius policy test build failed' }
& $runner (Join-Path $fixtureDirectory 'state') $linkedRoot
if ($LASTEXITCODE -ne 0) { throw 'Group radius policy checks failed' }
