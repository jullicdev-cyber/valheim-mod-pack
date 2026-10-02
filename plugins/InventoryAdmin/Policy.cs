using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace ValheimModPack.InventoryAdmin
{
    public static class PermissionPolicy
    {
        // Owner comes exclusively from ZSteamSocket.GetHostName after vanilla's
        // authentication. Match World Characters' canonical owner representation.
        public static void RequireSteamOwner(string owner)
        {
            if (owner == null || owner.Length != 23 || !owner.StartsWith("Steam_", StringComparison.Ordinal))
                throw new InvalidDataException("Expected an authenticated Steam owner identity.");
            ulong id = 0;
            for (int i = 6; i < owner.Length; ++i)
            {
                char c = owner[i]; if (c < '0' || c > '9') throw new InvalidDataException("Invalid Steam identity.");
                id = checked(id * 10 + (ulong)(c - '0'));
            }
            if (id == 0) throw new InvalidDataException("Invalid Steam identity.");
        }

        public static bool CanInspect(long world, bool callerIsHost, string authenticatedCaller, PermissionStore permissions)
        {
            if (world == 0) return false;
            if (callerIsHost) return true;
            if (permissions == null) return false;
            try { RequireSteamOwner(authenticatedCaller); return permissions.IsAdministrator(world, authenticatedCaller); }
            catch (InvalidDataException) { return false; }
        }

        public static void RequireInspect(long world, bool callerIsHost, string authenticatedCaller, PermissionStore permissions)
        {
            if (!CanInspect(world, callerIsHost, authenticatedCaller, permissions))
                throw new UnauthorizedAccessException("Inventory administration is restricted to the host and assigned administrators.");
        }
    }

    internal static class PolicyBinary
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static void WriteString(BinaryWriter writer, string value, int maximum)
        {
            if (value == null) throw new InvalidDataException("Missing text field.");
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > maximum) throw new InvalidDataException("Oversized text field.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        public static string ReadString(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid text field length.");
            return Utf8.GetString(reader.ReadBytes(count));
        }
        public static void WriteBytes(BinaryWriter writer, byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length > maximum) throw new InvalidDataException("Oversized byte field.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        public static byte[] ReadBytes(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid byte field length.");
            return reader.ReadBytes(count);
        }
        public static string Hash(byte[] bytes)
        {
            using (var digest = SHA256.Create())
            {
                byte[] hash = digest.ComputeHash(bytes); var text = new StringBuilder(64);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }
        public static byte[] Seal(byte[] bytes)
        {
            var result = new byte[bytes.Length + 32]; Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            using (var digest = SHA256.Create()) Buffer.BlockCopy(digest.ComputeHash(bytes), 0, result, bytes.Length, 32);
            return result;
        }
        public static byte[] Unseal(byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length < 32 || bytes.Length > maximum) throw new InvalidDataException("Invalid persisted policy data size.");
            int length = bytes.Length - 32;
            using (var digest = SHA256.Create())
            {
                byte[] expected = digest.ComputeHash(bytes, 0, length); int difference = 0;
                for (int i = 0; i < 32; ++i) difference |= expected[i] ^ bytes[length + i];
                if (difference != 0) throw new InvalidDataException("Persisted policy checksum mismatch; manual review required.");
            }
            var result = new byte[length]; Buffer.BlockCopy(bytes, 0, result, 0, length); return result;
        }
        public static void RequireHash(string value)
        {
            if (value == null || value.Length != 64) throw new InvalidDataException("Expected a SHA-256 fingerprint.");
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new InvalidDataException("Invalid fingerprint.");
        }
        public static void RequireToken(string token)
        {
            Guid value;
            if (token == null || token.Length != 32 || !Guid.TryParseExact(token, "N", out value) || value == Guid.Empty || token != value.ToString("N"))
                throw new InvalidDataException("Expected a non-empty transaction or request identifier.");
        }
        public static void RequireText(string value, int maximum)
        {
            if (value == null || Utf8.GetByteCount(value) > maximum || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid text field.");
        }
    }
}
