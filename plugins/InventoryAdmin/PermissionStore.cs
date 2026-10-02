using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;

namespace ValheimModPack.InventoryAdmin
{
    // This store is host-side only. The native adapter obtains identities from
    // authenticated network peers; strings supplied in an RPC are never authority.
    public sealed class PermissionStore : IDisposable
    {
        private const int MaximumGrants = 4096;
        private readonly string path;
        private readonly FileStream processLock;
        private readonly object sync = new object();
        private HashSet<string> grants;
        private bool disposed;

        public PermissionStore(string directory)
        {
            string root = Path.GetFullPath(directory);
            PolicyFiles.RequireRegularPath(root);
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "administrators.dat");
            string lockPath = Path.Combine(root, "administrators.lock");
            PolicyFiles.RequireRegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { grants = Read(); }
            catch { processLock.Dispose(); throw; }
        }

        public bool IsAdministrator(long world, string owner)
        {
            string key = Key(world, owner);
            lock (sync) { CheckOpen(); return grants.Contains(key); }
        }

        public bool SetAdministrator(long world, bool authenticatedCallerIsHost, string owner, bool enabled)
        {
            if (!authenticatedCallerIsHost) throw new UnauthorizedAccessException("Only the host may change administrator permissions.");
            string key = Key(world, owner);
            lock (sync)
            {
                CheckOpen();
                bool existing = grants.Contains(key);
                if (existing == enabled) return false;
                var next = new HashSet<string>(grants, StringComparer.Ordinal);
                if (enabled) next.Add(key); else next.Remove(key);
                if (next.Count > MaximumGrants) throw new InvalidOperationException("Administrator permission limit reached.");
                Save(next); // A failed write must not silently grant permissions in memory.
                grants = next;
                return true;
            }
        }

        public string[] Administrators(long world)
        {
            if (world == 0) throw new ArgumentException("World identity is required.");
            string prefix = world.ToString(CultureInfo.InvariantCulture) + ":";
            lock (sync)
            {
                CheckOpen(); var result = new List<string>();
                foreach (string key in grants) if (key.StartsWith(prefix, StringComparison.Ordinal)) result.Add(key.Substring(prefix.Length));
                result.Sort(StringComparer.Ordinal); return result.ToArray();
            }
        }

        private HashSet<string> Read()
        {
            PolicyFiles.RequireRegularPath(path);
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!File.Exists(path)) return result;
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("Oversized administrator permissions file.");
            using (var stream = new MemoryStream(PolicyBinary.Unseal(File.ReadAllBytes(path), 512 * 1024), false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != 0x49415031 || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported administrator permissions format.");
                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumGrants) throw new InvalidDataException("Invalid administrator count.");
                for (int i = 0; i < count; ++i)
                {
                    long world = reader.ReadInt64(); string owner = PolicyBinary.ReadString(reader, 40);
                    if (!result.Add(Key(world, owner))) throw new InvalidDataException("Duplicate administrator grant.");
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing administrator permission data.");
            }
            return result;
        }

        private void Save(HashSet<string> values)
        {
            var ordered = new List<string>(values); ordered.Sort(StringComparer.Ordinal);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(0x49415031); writer.Write(1); writer.Write(ordered.Count);
                foreach (string key in ordered)
                {
                    int separator = key.IndexOf(':');
                    writer.Write(Int64.Parse(key.Substring(0, separator), CultureInfo.InvariantCulture));
                    PolicyBinary.WriteString(writer, key.Substring(separator + 1), 40);
                }
                writer.Flush(); PolicyFiles.AtomicWrite(path, PolicyBinary.Seal(stream.ToArray()));
            }
        }

        internal static string Key(long world, string owner)
        {
            if (world == 0) throw new InvalidDataException("World identity is required.");
            PermissionPolicy.RequireSteamOwner(owner);
            return world.ToString(CultureInfo.InvariantCulture) + ":" + owner;
        }

        private void CheckOpen() { if (disposed) throw new ObjectDisposedException("PermissionStore"); }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; processLock.Dispose(); } }
    }

    internal static class PolicyFiles
    {
        public static void RequireRegularPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked administration paths are not supported.");
                current = Path.GetDirectoryName(current);
            }
        }

        // Atomic replacement without creating growing automatic backup copies.
        public static void AtomicWrite(string path, byte[] bytes)
        {
            RequireRegularPath(path);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
