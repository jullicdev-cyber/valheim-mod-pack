using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace ValheimModPack.WorldCharacters
{
    // No Unity types: the on-disk format and its validation are testable without the game.
    public sealed class CharacterState
    {
        public long World, Character, Revision;
        public string Owner = "", Name = "", Build = "";
        public byte[] Player = new byte[0], WorldData = new byte[0];
        public CharacterState Copy()
        {
            return new CharacterState { World = World, Character = Character, Revision = Revision,
                Owner = Owner, Name = Name, Build = Build,
                Player = (byte[])Player.Clone(), WorldData = (byte[])WorldData.Clone() };
        }
    }

    public static class StateCodec
    {
        public const int MaximumBytes = 16 * 1024 * 1024, MaximumPlayerBytes = 4 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Encode(CharacterState s)
        {
            Validate(s);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write(0x57434831); writer.Write(1);
                writer.Write(s.World); writer.Write(s.Character); writer.Write(s.Revision);
                WriteString(writer, s.Owner); WriteString(writer, s.Name); WriteString(writer, s.Build);
                WriteBytes(writer, s.Player); WriteBytes(writer, s.WorldData); writer.Flush();
                byte[] body = stream.ToArray();
                using (var hash = SHA256.Create()) writer.Write(hash.ComputeHash(body));
                writer.Flush(); return stream.ToArray();
            }
        }
        public static CharacterState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 72 || bytes.Length > MaximumBytes) throw new InvalidDataException("Invalid state size.");
            int bodyLength = bytes.Length - 32;
            using (var hash = SHA256.Create())
            {
                byte[] expected = hash.ComputeHash(bytes, 0, bodyLength); int difference = 0;
                for (int i = 0; i < 32; ++i) difference |= expected[i] ^ bytes[bodyLength + i];
                if (difference != 0) throw new InvalidDataException("State checksum mismatch; restore requires administrator review.");
            }
            using (var stream = new MemoryStream(bytes, 0, bodyLength, false))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                if (reader.ReadInt32() != 0x57434831 || reader.ReadInt32() != 1) throw new InvalidDataException("Unknown state format.");
                var s = new CharacterState { World = reader.ReadInt64(), Character = reader.ReadInt64(), Revision = reader.ReadInt64(),
                    Owner = ReadString(reader, 100), Name = ReadString(reader, 256), Build = ReadString(reader, 256),
                    Player = ReadBytes(reader, MaximumPlayerBytes), WorldData = ReadBytes(reader, 10 * 1024 * 1024) };
                if (stream.Position != bodyLength) throw new InvalidDataException("Trailing state data.");
                Validate(s); return s;
            }
        }
        public static void Validate(CharacterState s)
        {
            if (s == null || s.World == 0 || s.Character == 0 || s.Revision < 0) throw new InvalidDataException("Invalid state identity.");
            if (String.IsNullOrEmpty(s.Owner) || s.Owner.Length > 100 || s.Name == null || s.Name.Length > 100
                || s.Name.IndexOfAny(new[] {'\r','\n','\0'}) >= 0 || s.Build == null || s.Build.Length > 256)
                throw new InvalidDataException("Invalid state labels.");
            if (s.Player == null || s.Player.Length > MaximumPlayerBytes || s.WorldData == null || s.WorldData.Length > 10 * 1024 * 1024)
                throw new InvalidDataException("State exceeds limits.");
            if (s.Player.Length > 0)
                foreach (var item in NativeInventory.IncludingBackpacks(NativeInventory.ReadPlayer(s.Player))) { }
            ValidateWorld(s.WorldData);
        }
        private static void ValidateWorld(byte[] bytes)
        {
            if (bytes.Length == 0) return;
            using (var stream = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(stream))
            {
                if (r.ReadInt32() != 1) throw new InvalidDataException("Unknown world profile version.");
                for (int i = 0; i < 4; ++i)
                {
                    if (i < 3 && r.ReadByte() > 1) throw new InvalidDataException("Invalid world profile flag.");
                    for (int j = 0; j < 3; ++j)
                    { float value = r.ReadSingle(); if (Single.IsNaN(value) || Single.IsInfinity(value)) throw new InvalidDataException("Invalid world profile position."); }
                }
                ReadBytes(r, 10 * 1024 * 1024);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world profile data.");
            }
        }
        public static byte[] WithoutMap(byte[] world)
        {
            ValidateWorld(world); if (world.Length == 0) return new byte[0];
            // v1: version (4), three flags (3), four Vector3 values (48), map length (4).
            byte[] result = new byte[59]; Buffer.BlockCopy(world, 0, result, 0, 55); return result;
        }
        public static byte[] RetainMap(byte[] positions, byte[] previous)
        {
            ValidateWorld(positions); ValidateWorld(previous);
            if (positions.Length != 59) throw new InvalidDataException("A position-only snapshot must have an empty map field.");
            if (previous.Length == 0) return (byte[])positions.Clone();
            byte[] result = new byte[previous.Length];
            Buffer.BlockCopy(positions, 0, result, 0, 55); Buffer.BlockCopy(previous, 55, result, 55, previous.Length - 55);
            return result;
        }
        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
        public static string Key(long world, string owner, long character)
        {
            return Hash(Utf8.GetBytes(world.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + owner + "\n" + character.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        public static void WriteBytes(BinaryWriter w, byte[] bytes) { w.Write(bytes.Length); w.Write(bytes); }
        public static byte[] ReadBytes(BinaryReader r, int limit)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > limit || count > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid field length.");
            byte[] bytes = r.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return bytes;
        }
        public static void WriteString(BinaryWriter w, string value) { WriteBytes(w, Utf8.GetBytes(value)); }
        public static string ReadString(BinaryReader r, int limit) { return Utf8.GetString(ReadBytes(r, limit)); }
    }
}
