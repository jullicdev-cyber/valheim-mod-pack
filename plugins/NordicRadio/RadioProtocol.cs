using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValheimModPack.NordicRadio
{
    internal sealed class RadioTokenBucket
    {
        public double Rate;
        private readonly double capacity;
        private double tokens;
        private double previous;
        public RadioTokenBucket(double rate, double capacity) { Rate = rate; this.capacity = capacity; tokens = capacity; }
        public bool Take(double cost, double now)
        {
            if (!RadioProtocol.Finite(cost) || !RadioProtocol.Finite(now) || cost < 0) return false;
            tokens = Math.Min(capacity, tokens + Math.Max(0, now - previous) * Rate); previous = now;
            if (cost > tokens) return false;
            tokens -= cost; return true;
        }
    }

    public sealed class TrackInfo
    {
        public string Id;
        public string Title;
        public long Size;
        public double Duration;
    }

    public sealed class RadioSnapshot
    {
        public string TrackId = "";
        public bool Playing;
        public double StartedAt;
        public float Offset;
        public float Volume = 0.7f;
        public bool Repeat = true;
        public bool Shuffle;
        public int Revision;

        public double Position(double now) { return Math.Max(0, Offset + (Playing ? Math.Max(0, now - StartedAt) : 0)); }
        public RadioSnapshot Copy() { return (RadioSnapshot)MemberwiseClone(); }
    }

    internal enum RadioMessageKind : byte { Hello = 1, Library = 2, Watch = 3, State = 4, Command = 5, ChunkRequest = 6, Chunk = 7, Error = 8, PortablePresence = 9 }

    internal sealed class RadioMessage
    {
        public RadioMessageKind Kind;
        public long Owner;
        public uint Object;
        public string Id = "";
        public string Command = "";
        public float Value;
        public long Offset;
        public byte[] Data;
        public List<TrackInfo> Tracks;
        public RadioSnapshot State;
    }

    // The wire format is independent of Unity. Every count is bounded before allocation.
    internal static class RadioProtocol
    {
        public const string RpcName = "VMP_NordicRadio_2";
        public const float MaxAudioDistance = 150f;
        // Keep a margin around audible sources for playlist/state prefetch while approaching.
        public const float WatchDistance = 200f;
        public const int MaxTracks = 256;
        public const int ChunkSize = 24 * 1024;
        public const int MaxPacket = 128 * 1024;
        public const long MaxFile = 64L * 1024 * 1024;
        public const long MaxCache = 2L * 1024 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static bool ValidId(string id)
        {
            if (id == null || id.Length != 64) return false;
            foreach (char c in id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public static bool ValidPortableToken(string token)
        {
            if (token == null || token.Length != 32) return false;
            foreach (char c in token) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        public static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        public static bool ValidCommand(string value)
        {
            return value == "play" || value == "pause" || value == "next" || value == "previous" || value == "select" || value == "volume" || value == "repeat" || value == "shuffle";
        }

        public static byte[] Encode(RadioMessage message)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write((byte)2);
                writer.Write((byte)message.Kind);
                switch (message.Kind)
                {
                    case RadioMessageKind.Hello: break;
                    case RadioMessageKind.Library:
                        if (message.Tracks == null || message.Tracks.Count > MaxTracks) throw new InvalidDataException("Playlist count");
                        writer.Write(message.Tracks.Count);
                        foreach (TrackInfo track in message.Tracks)
                        {
                            WriteString(writer, track.Id, 64); WriteString(writer, track.Title, 384);
                            writer.Write(track.Size); writer.Write(track.Duration);
                        }
                        break;
                    case RadioMessageKind.Watch: WriteKey(writer, message); break;
                    case RadioMessageKind.PortablePresence:
                        WriteKey(writer, message); WriteString(writer, message.Id, 32); break;
                    case RadioMessageKind.State:
                        WriteKey(writer, message);
                        RadioSnapshot state = message.State;
                        WriteString(writer, state.TrackId, 64);
                        writer.Write(state.Playing); writer.Write(state.StartedAt); writer.Write(state.Offset); writer.Write(state.Volume);
                        writer.Write(state.Repeat); writer.Write(state.Shuffle); writer.Write(state.Revision);
                        break;
                    case RadioMessageKind.Command:
                        WriteKey(writer, message); WriteString(writer, message.Command, 16); WriteString(writer, message.Id, 64); writer.Write(message.Value);
                        break;
                    case RadioMessageKind.ChunkRequest:
                        WriteString(writer, message.Id, 64); writer.Write(message.Offset);
                        break;
                    case RadioMessageKind.Chunk:
                        WriteString(writer, message.Id, 64); writer.Write(message.Offset);
                        if (message.Data == null || message.Data.Length > ChunkSize) throw new InvalidDataException("Chunk length");
                        writer.Write(message.Data.Length); writer.Write(message.Data);
                        break;
                    case RadioMessageKind.Error:
                        WriteString(writer, message.Id, 64); WriteString(writer, message.Command, 256); break;
                    default: throw new InvalidDataException("Message kind");
                }
                writer.Flush();
                if (stream.Length > MaxPacket) throw new InvalidDataException("Packet length");
                return stream.ToArray();
            }
        }

        public static RadioMessage Decode(byte[] data)
        {
            if (data == null || data.Length < 2 || data.Length > MaxPacket) throw new InvalidDataException("Packet length");
            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                if (reader.ReadByte() != 2) throw new InvalidDataException("Protocol version");
                var result = new RadioMessage { Kind = (RadioMessageKind)reader.ReadByte() };
                switch (result.Kind)
                {
                    case RadioMessageKind.Hello: break;
                    case RadioMessageKind.Library:
                        int count = reader.ReadInt32();
                        if (count < 0 || count > MaxTracks) throw new InvalidDataException("Playlist count");
                        result.Tracks = new List<TrackInfo>(count);
                        var ids = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < count; i++)
                        {
                            var track = new TrackInfo { Id = ReadId(reader, false), Title = ReadString(reader, 384), Size = reader.ReadInt64(), Duration = reader.ReadDouble() };
                            if (track.Title.Length > 128 || !ids.Add(track.Id) || track.Size < 1 || track.Size > MaxFile || !Finite(track.Duration) || track.Duration < 0 || track.Duration > 86400) throw new InvalidDataException("Track metadata");
                            result.Tracks.Add(track);
                        }
                        break;
                    case RadioMessageKind.Watch: ReadKey(reader, result); break;
                    case RadioMessageKind.PortablePresence:
                        ReadKey(reader, result); result.Id = ReadString(reader, 32);
                        if (result.Id.Length != 0 && !ValidPortableToken(result.Id)) throw new InvalidDataException("Portable item identity");
                        break;
                    case RadioMessageKind.State:
                        ReadKey(reader, result);
                        result.State = new RadioSnapshot { TrackId = ReadId(reader, true), Playing = reader.ReadBoolean(), StartedAt = reader.ReadDouble(), Offset = reader.ReadSingle(), Volume = reader.ReadSingle(), Repeat = reader.ReadBoolean(), Shuffle = reader.ReadBoolean(), Revision = reader.ReadInt32() };
                        if (!Finite(result.State.StartedAt) || result.State.StartedAt < 0 || !Finite(result.State.Offset) || result.State.Offset < 0 || result.State.Offset > 86400 || !Finite(result.State.Volume) || result.State.Volume < 0 || result.State.Volume > 1 || result.State.Revision < 0 || (result.State.Playing && result.State.TrackId.Length == 0)) throw new InvalidDataException("State values");
                        break;
                    case RadioMessageKind.Command:
                        ReadKey(reader, result); result.Command = ReadString(reader, 16); result.Id = ReadId(reader, true); result.Value = reader.ReadSingle();
                        if (!ValidCommand(result.Command) || !Finite(result.Value) || result.Value < 0 || result.Value > 1 || (result.Command == "select" && result.Id.Length == 0)) throw new InvalidDataException("Command values");
                        break;
                    case RadioMessageKind.ChunkRequest:
                        result.Id = ReadId(reader, false); result.Offset = reader.ReadInt64();
                        if (result.Offset < 0 || result.Offset >= MaxFile || result.Offset % ChunkSize != 0) throw new InvalidDataException("Chunk offset");
                        break;
                    case RadioMessageKind.Chunk:
                        result.Id = ReadId(reader, false); result.Offset = reader.ReadInt64(); int size = reader.ReadInt32();
                        if (result.Offset < 0 || result.Offset >= MaxFile || result.Offset % ChunkSize != 0 || size < 1 || size > ChunkSize || size > stream.Length - stream.Position) throw new InvalidDataException("Chunk values");
                        result.Data = reader.ReadBytes(size); break;
                    case RadioMessageKind.Error:
                        result.Id = ReadId(reader, true); result.Command = ReadString(reader, 256); break;
                    default: throw new InvalidDataException("Message kind");
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing packet data");
                return result;
            }
        }

        private static void WriteKey(BinaryWriter writer, RadioMessage message) { writer.Write(message.Owner); writer.Write(message.Object); }
        private static void ReadKey(BinaryReader reader, RadioMessage message)
        {
            message.Owner = reader.ReadInt64(); message.Object = reader.ReadUInt32();
            if (message.Owner == 0 || message.Object == 0) throw new InvalidDataException("Radio identity");
        }
        private static string ReadId(BinaryReader reader, bool allowEmpty)
        {
            string value = ReadString(reader, 64);
            if (!(allowEmpty && value.Length == 0) && !ValidId(value)) throw new InvalidDataException("Content id");
            return value;
        }
        private static void WriteString(BinaryWriter writer, string value, int limit)
        {
            byte[] bytes = Utf8.GetBytes(value ?? "");
            if (bytes.Length > limit) throw new InvalidDataException("String length");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        private static string ReadString(BinaryReader reader, int limit)
        {
            int size = reader.ReadInt32();
            if (size < 0 || size > limit || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("String length");
            return Utf8.GetString(reader.ReadBytes(size));
        }

        public static bool Apply(RadioSnapshot state, IList<TrackInfo> tracks, string command, string id, float value, double now, Random random)
        {
            if (!ValidCommand(command) || !Finite(now) || !Finite(value) || value < 0 || value > 1) return false;
            int index = -1;
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Id == state.TrackId) { index = i; break; }
            if (command == "volume") state.Volume = value;
            else if (command == "repeat") state.Repeat = value >= 0.5f;
            else if (command == "shuffle") state.Shuffle = value >= 0.5f;
            else if (command == "pause") { state.Offset = (float)Math.Min(86400, state.Position(now)); state.Playing = false; }
            else if (tracks.Count == 0) return false;
            else if (command == "play")
            {
                if (index < 0) { state.TrackId = tracks[0].Id; state.Offset = 0; }
                else if (tracks[index].Duration > 0 && state.Offset >= tracks[index].Duration) state.Offset = 0;
                if (!state.Playing) state.StartedAt = now;
                state.Playing = true;
            }
            else
            {
                if (command == "select")
                {
                    index = -1;
                    for (int i = 0; i < tracks.Count; i++) if (tracks[i].Id == id) { index = i; break; }
                    if (index < 0) return false;
                }
                else if (index < 0) index = command == "previous" ? tracks.Count - 1 : 0;
                else if (state.Shuffle && tracks.Count > 1)
                {
                    int candidate = random.Next(tracks.Count - 1);
                    index = candidate >= index ? candidate + 1 : candidate;
                    if (index >= tracks.Count) index = 0;
                }
                else if (command == "previous") index = (index - 1 + tracks.Count) % tracks.Count;
                else index = (index + 1) % tracks.Count;
                state.TrackId = tracks[index].Id; state.Offset = 0; state.StartedAt = now; state.Playing = true;
            }
            state.Revision = state.Revision == Int32.MaxValue ? 0 : state.Revision + 1;
            return true;
        }
    }
}
