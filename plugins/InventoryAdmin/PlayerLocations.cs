using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    // This frame carries display data only. Authorization and the authenticated
    // world/subscription context are checked by the native network adapter.
    public sealed class PlayerLocation
    {
        public long PeerId, CharacterId;
        public string Name = "";
        public float X, Y, Z;
    }

    public static class PlayerLocationCodec
    {
        public const int MaximumPlayers = 128, MaximumNameBytes = 512, MaximumPacketBytes = 96 * 1024;
        // Valheim dungeon interiors live far above the surface. Keep all three
        // axes bounded without rejecting those legitimate interior positions.
        public const float MaximumCoordinate = 100000f;
        private const int Magic = 0x49414c31, Version = 1;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(IList<PlayerLocation> locations)
        {
            Validate(locations);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(locations.Count);
                foreach (PlayerLocation location in locations)
                {
                    writer.Write(location.PeerId); writer.Write(location.CharacterId);
                    byte[] name = Utf8.GetBytes(location.Name); writer.Write(name.Length); writer.Write(name);
                    writer.Write(location.X); writer.Write(location.Y); writer.Write(location.Z);
                }
                writer.Flush();
                if (stream.Length > MaximumPacketBytes) throw new InvalidDataException("Player location frame exceeds maximum size.");
                return stream.ToArray();
            }
        }

        public static List<PlayerLocation> Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaximumPacketBytes)
                throw new InvalidDataException("Invalid player location packet size.");
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Utf8))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                        throw new InvalidDataException("Unknown player location wire format.");
                    int count = reader.ReadInt32();
                    if (count < 0 || count > MaximumPlayers) throw new InvalidDataException("Invalid player location count.");
                    var result = new List<PlayerLocation>(count);
                    for (int i = 0; i < count; ++i)
                    {
                        long peer = reader.ReadInt64(), character = reader.ReadInt64(); int nameLength = reader.ReadInt32();
                        if (nameLength < 0 || nameLength > MaximumNameBytes || nameLength > stream.Length - stream.Position)
                            throw new InvalidDataException("Invalid player location name length.");
                        var location = new PlayerLocation { PeerId = peer, CharacterId = character,
                            Name = Utf8.GetString(reader.ReadBytes(nameLength)), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                        result.Add(location);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing player location packet data.");
                    Validate(result); return result;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated player location packet.", error); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 player location name.", error); }
        }

        public static void Validate(IList<PlayerLocation> locations)
        {
            if (locations == null || locations.Count > MaximumPlayers) throw new InvalidDataException("Too many or missing player locations.");
            var peers = new HashSet<long>(); var characters = new HashSet<long>();
            foreach (PlayerLocation location in locations)
            {
                if (location == null || location.PeerId == 0 || location.CharacterId == 0)
                    throw new InvalidDataException("Missing player location identity.");
                if (!peers.Add(location.PeerId) || !characters.Add(location.CharacterId))
                    throw new InvalidDataException("Duplicate player location identity.");
                if (location.Name == null || location.Name.IndexOf('\0') >= 0)
                    throw new InvalidDataException("Invalid player location name.");
                try
                {
                    if (Utf8.GetByteCount(location.Name) > MaximumNameBytes) throw new InvalidDataException("Oversized player location name.");
                }
                catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid player location name text.", error); }
                if (!Coordinate(location.X) || !Coordinate(location.Y) || !Coordinate(location.Z))
                    throw new InvalidDataException("Invalid player location coordinates.");
            }
        }

        private static bool Coordinate(float value)
        { return !Single.IsNaN(value) && !Single.IsInfinity(value) && value >= -MaximumCoordinate && value <= MaximumCoordinate; }

        internal static PlayerLocation Copy(PlayerLocation location)
        { return new PlayerLocation { PeerId = location.PeerId, CharacterId = location.CharacterId, Name = location.Name, X = location.X, Y = location.Y, Z = location.Z }; }
    }

    // Replacements are complete rosters, so disconnects disappear immediately.
    // The caller uses a monotonic clock (Unity's realtimeSinceStartup), never UTC.
    public sealed class PlayerLocationCache
    {
        public const double FreshnessSeconds = 8;
        private long world, sequence;
        private string session = "";
        private bool authorized, enabled;
        private double receivedAt = -1;
        private List<PlayerLocation> locations = new List<PlayerLocation>();

        public void SetContext(long newWorld, string newSession)
        {
            if (newWorld == 0)
            {
                if (newSession != "") throw new InvalidDataException("A cleared location context has no subscription token.");
            }
            else
            {
                Guid token;
                if (newSession == null || newSession.Length != 32 || !Guid.TryParseExact(newSession, "N", out token)
                    || token == Guid.Empty || newSession != token.ToString("N"))
                    throw new InvalidDataException("Invalid player location subscription token.");
            }
            if (world == newWorld && session == newSession) return;
            world = newWorld; session = newSession; sequence = 0; Clear();
        }

        public void SetAccess(bool isAuthorized, bool isEnabled)
        {
            authorized = isAuthorized; enabled = isEnabled;
            if (!authorized || !enabled) Clear();
        }

        public bool TryReplace(long frameWorld, string frameSession, long frameSequence, IList<PlayerLocation> frame, double now)
        {
            if (!authorized || !enabled || world == 0 || frameWorld != world || frameSession != session
                || frameSequence <= sequence || frameSequence < 1) return false;
            if (!Time(now)) throw new InvalidDataException("Invalid player location receive time.");
            PlayerLocationCodec.Validate(frame);
            var replacement = new List<PlayerLocation>(frame.Count);
            foreach (PlayerLocation location in frame) replacement.Add(PlayerLocationCodec.Copy(location));
            // A reset clock cannot prolong old data within the same context.
            if (receivedAt >= 0 && now < receivedAt) { Clear(); return false; }
            locations = replacement; sequence = frameSequence; receivedAt = now; return true;
        }

        public List<PlayerLocation> Snapshot(double now)
        {
            if (!authorized || !enabled || !Time(now) || receivedAt < 0 || now < receivedAt || now - receivedAt >= FreshnessSeconds)
            { Clear(); return new List<PlayerLocation>(); }
            var result = new List<PlayerLocation>(locations.Count);
            foreach (PlayerLocation location in locations) result.Add(PlayerLocationCodec.Copy(location));
            return result;
        }

        public void Clear()
        { locations.Clear(); receivedAt = -1; }

        private static bool Time(double now)
        { return !Double.IsNaN(now) && !Double.IsInfinity(now) && now >= 0; }
    }
}
