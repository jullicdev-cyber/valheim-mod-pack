using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    internal static class PlayerLocationTests
    {
        private const long World = 12345;
        private const string Session = "abcdef0123456789abcdef0123456789", OtherSession = "0123456789abcdef0123456789abcdef";
        private static int checks;
        private static void Assert(bool condition, string message)
        { if (!condition) throw new Exception(message); ++checks; }
        private static void Invalid(Action action, string message)
        {
            try { action(); } catch (InvalidDataException) { ++checks; return; }
            throw new Exception("Accepted invalid frame: " + message);
        }
        private static PlayerLocation Location(long peer, long character, string name, float x)
        { return new PlayerLocation { PeerId = peer, CharacterId = character, Name = name, X = x, Y = 5000, Z = -10500 }; }
        private static List<PlayerLocation> Frame()
        { return new List<PlayerLocation> { Location(101, 501, "Скрытый игрок", 900), Location(202, 602, "Хост", -900) }; }
        private static PlayerLocationCache Cache()
        { var cache = new PlayerLocationCache(); cache.SetContext(World, Session); cache.SetAccess(true, true); return cache; }
        private static byte[] Raw(IList<PlayerLocation> locations, int count)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(0x49414c31); writer.Write(1); writer.Write(count);
                foreach (PlayerLocation p in locations)
                {
                    writer.Write(p.PeerId); writer.Write(p.CharacterId); byte[] name = Encoding.UTF8.GetBytes(p.Name);
                    writer.Write(name.Length); writer.Write(name); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z);
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        private static byte[] Change(byte[] bytes, int offset, byte value)
        { var changed = (byte[])bytes.Clone(); changed[offset] = value; return changed; }

        public static int Main()
        {
            try
            {
                CodecChecks(); CacheChecks(); Console.WriteLine("Player location checks passed: " + checks); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void CodecChecks()
        {
            List<PlayerLocation> frame = Frame(); byte[] packet = PlayerLocationCodec.Encode(frame);
            List<PlayerLocation> decoded = PlayerLocationCodec.Decode(packet);
            Assert(decoded.Count == 2 && decoded[0].PeerId == 101 && decoded[0].CharacterId == 501
                && decoded[0].Name == "Скрытый игрок" && decoded[0].Y == 5000 && decoded[0].Z == -10500, "Dungeon and Unicode locations round trip.");
            var maximum = new List<PlayerLocation>();
            for (int i = 1; i <= 128; ++i) maximum.Add(Location(i, i + 1000, new string('я', 256), i));
            Assert(PlayerLocationCodec.Decode(PlayerLocationCodec.Encode(maximum)).Count == 128, "Maximum roster and UTF-8 names accepted.");
            maximum.Add(Location(129, 1129, "Extra", 0));
            Invalid(delegate { PlayerLocationCodec.Encode(maximum); }, "oversized encoded roster");
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(new List<PlayerLocation>(), 129)); }, "oversized declared roster");
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(new List<PlayerLocation>(), -1)); }, "negative declared roster");
            Invalid(delegate { PlayerLocationCodec.Decode(Change(packet, 4, 2)); }, "unknown protocol version");
            Invalid(delegate { PlayerLocationCodec.Decode(Change(packet, 0, 0)); }, "wrong magic");
            var trailing = new byte[packet.Length + 1]; Buffer.BlockCopy(packet, 0, trailing, 0, packet.Length);
            Invalid(delegate { PlayerLocationCodec.Decode(trailing); }, "trailing bytes");
            var truncated = new byte[packet.Length - 1]; Buffer.BlockCopy(packet, 0, truncated, 0, truncated.Length);
            Invalid(delegate { PlayerLocationCodec.Decode(truncated); }, "truncated final coordinate");
            foreach (float bad in new[] { Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity, 100001f, -100001f })
            {
                List<PlayerLocation> invalid = Frame(); invalid[0].X = bad;
                Invalid(delegate { PlayerLocationCodec.Encode(invalid); }, "invalid encoded coordinate");
                Invalid(delegate { PlayerLocationCodec.Decode(Raw(invalid, invalid.Count)); }, "invalid decoded coordinate");
            }
            frame = Frame(); frame[0].Y = Single.NaN;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "NaN interior height");
            frame = Frame(); frame[0].Z = Single.MaxValue;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "unbounded map axis");
            frame = Frame(); frame[1].PeerId = frame[0].PeerId;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "duplicate peer IDs");
            frame = Frame(); frame[1].CharacterId = frame[0].CharacterId;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "one character on two peers");
            frame = Frame(); frame[0].PeerId = 0;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "zero peer ID");
            frame = Frame(); frame[0].CharacterId = 0;
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "zero character ID");
            frame = Frame(); frame[0].Name = new string('я', 257);
            Invalid(delegate { PlayerLocationCodec.Encode(frame); }, "UTF-8 byte length exceeds limit");
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "encoded name length exceeds limit");
            frame = Frame(); frame[0].Name = "Bad\0Name";
            Invalid(delegate { PlayerLocationCodec.Decode(Raw(frame, frame.Count)); }, "embedded zero name");
            frame = Frame(); frame[0].Name = "\ud800";
            Invalid(delegate { PlayerLocationCodec.Encode(frame); }, "invalid UTF-16 source name");
            var ascii = new List<PlayerLocation> { Location(10, 20, "AB", 0) };
            packet = Raw(ascii, 1); packet[32] = 0xc0; packet[33] = 0xaf;
            Invalid(delegate { PlayerLocationCodec.Decode(packet); }, "overlong UTF-8 encoding");
            Assert(PlayerLocationCodec.Decode(PlayerLocationCodec.Encode(new List<PlayerLocation>())).Count == 0, "Empty authoritative roster allowed.");
        }

        private static void CacheChecks()
        {
            var denied = new PlayerLocationCache(); denied.SetContext(World, Session);
            Assert(!denied.TryReplace(World, Session, 1, Frame(), 0) && denied.Snapshot(0).Count == 0, "Default-deny cache cannot expose positions.");
            denied.SetAccess(false, true);
            Assert(!denied.TryReplace(World, Session, 1, Frame(), 0), "UI enabled without permission cannot populate cache.");
            PlayerLocationCache cache = Cache(); List<PlayerLocation> frame = Frame();
            Assert(cache.TryReplace(World, Session, 1, frame, 100), "Initial authorized snapshot accepted.");
            frame[0].Name = "Tampered source"; frame[0].X = 1;
            List<PlayerLocation> visible = cache.Snapshot(100); visible[0].Name = "Tampered output"; visible.Clear();
            Assert(cache.Snapshot(100)[0].Name == "Скрытый игрок" && cache.Snapshot(100)[0].X == 900, "Caller and UI cannot mutate cached identities or coordinates.");
            Assert(cache.Snapshot(107.999).Count == 2, "Locations remain visible just before TTL.");
            Assert(!cache.TryReplace(World, Session, 1, Frame(), 107), "Repeated sequence does not renew freshness.");
            Assert(cache.Snapshot(108).Count == 0 && cache.Snapshot(200).Count == 0, "Lost updates expire exactly at TTL and stay cleared.");
            Assert(!cache.TryReplace(World, Session, 1, Frame(), 200), "Expired frame cannot be resurrected by replay.");
            Assert(cache.TryReplace(World, Session, 2, Frame(), 200), "Newer complete snapshot recovers after expiry.");
            var invalid = Frame(); invalid[1].CharacterId = invalid[0].CharacterId;
            Invalid(delegate { cache.TryReplace(World, Session, 3, invalid, 201); }, "duplicate IDs during replacement");
            Assert(cache.Snapshot(201).Count == 2 && cache.Snapshot(201)[1].PeerId == 202, "Malformed replacement cannot partially change existing roster.");
            invalid = Frame(); invalid[0].Y = Single.NaN;
            Invalid(delegate { cache.TryReplace(World, Session, 3, invalid, 201); }, "NaN during replacement");
            var reordered = Frame(); reordered.Reverse(); reordered[0].X = 17;
            Assert(cache.TryReplace(World, Session, 3, reordered, 202), "Reordered roster accepted as one atomic frame.");
            Assert(cache.Snapshot(202)[0].PeerId == 202 && cache.Snapshot(202)[0].CharacterId == 602 && cache.Snapshot(202)[0].X == 17,
                "Reordering cannot bind another player's name or coordinates to an index.");
            Assert(!cache.TryReplace(World, Session, 2, Frame(), 203) && cache.Snapshot(203)[0].X == 17, "Older packets cannot overwrite newer data.");
            var one = new List<PlayerLocation> { reordered[1] };
            Assert(cache.TryReplace(World, Session, 4, one, 203) && cache.Snapshot(203).Count == 1 && cache.Snapshot(203)[0].PeerId == 101,
                "Disconnected peer disappears on next authoritative roster.");
            cache.SetAccess(false, true);
            Assert(cache.Snapshot(203).Count == 0 && !cache.TryReplace(World, Session, 5, Frame(), 204), "Revocation clears immediately and rejects in-flight update.");
            cache.SetAccess(true, true);
            Assert(!cache.TryReplace(World, Session, 4, Frame(), 204), "Permission regain does not resurrect pre-revocation frame.");
            Assert(cache.TryReplace(World, Session, 5, Frame(), 204), "Fresh authorized update accepted after permission regain.");
            cache.SetAccess(true, false);
            Assert(cache.Snapshot(204).Count == 0 && !cache.TryReplace(World, Session, 6, Frame(), 205), "Viewer toggle off clears and blocks delivery.");
            cache.SetAccess(true, true); cache.SetContext(World, OtherSession);
            Assert(cache.Snapshot(205).Count == 0 && !cache.TryReplace(World, Session, 100, Frame(), 205), "New subscription cannot accept an old subscription packet.");
            Assert(cache.TryReplace(World, OtherSession, 1, Frame(), 205), "New subscription has its own sequence.");
            cache.SetContext(World + 1, OtherSession);
            Assert(cache.Snapshot(205).Count == 0 && !cache.TryReplace(World, OtherSession, 2, Frame(), 205), "World change clears positions and rejects earlier world.");
            Assert(cache.TryReplace(World + 1, OtherSession, 1, Frame(), 206), "New world frame accepted.");
            cache.SetContext(0, "");
            Assert(cache.Snapshot(206).Count == 0 && !cache.TryReplace(World + 1, OtherSession, 100, Frame(), 207), "Logout clears and rejects late frames.");
            cache = Cache(); cache.TryReplace(World, Session, 1, Frame(), 10);
            Assert(cache.Snapshot(9).Count == 0, "Monotonic clock regression cannot keep stale locations visible.");
            cache.TryReplace(World, Session, 2, Frame(), 10);
            Assert(cache.Snapshot(Double.NaN).Count == 0, "Invalid freshness clock fails closed.");
            Invalid(delegate { cache.TryReplace(World, Session, 3, Frame(), Double.PositiveInfinity); }, "invalid receive time");
            Invalid(delegate { cache.SetContext(World, "not-a-session"); }, "invalid subscription token");
        }
    }
}
