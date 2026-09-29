using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.WorldCharacters
{
    public sealed class StateStore : IDisposable
    {
        private readonly string root;
        private readonly object sync = new object();
        private readonly FileStream processLock;
        private readonly Dictionary<string, DateTime> historyWritten = new Dictionary<string, DateTime>();
        public StateStore(string directory)
        {
            root = Path.GetFullPath(directory); SafeDirectory(root);
            SafePath(Path.Combine(root, ".lock"));
            processLock = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SafeDirectory(Path.Combine(root, "characters")); SafeDirectory(Path.Combine(root, "pending"));
        }
        public CharacterState Read(long world, string owner, long character)
        {
            lock (sync)
            {
                string path = CharacterPath(world, owner, character);
                if (!File.Exists(path))
                {
                    if (File.Exists(path + ".bak")) throw new InvalidDataException("Primary state missing while a backup exists; manual recovery required.");
                    return null;
                }
                CharacterState s = ReadFile(path);
                if (s.World != world || s.Owner != owner || s.Character != character) throw new InvalidDataException("State identity mismatch.");
                return s;
            }
        }
        // Presence, not validity: corrupt and backup-only records remain protected
        // and are rejected by Read. File.Exists would hide permission/I/O errors.
        public bool HasStoredState(long world, string owner, long character)
        {
            lock (sync)
            {
                string path = CharacterPath(world, owner, character);
                return StoredEntryExists(path) || StoredEntryExists(path + ".bak");
            }
        }
        private static bool StoredEntryExists(string path)
        {
            SafePath(path);
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        public void Save(CharacterState s, long expectedRevision)
        {
            lock (sync)
            {
                CharacterState before = Read(s.World, s.Owner, s.Character);
                long actual = before == null ? 0 : before.Revision;
                if (actual != expectedRevision || s.Revision != actual + 1) throw new InvalidDataException("Stale character revision.");
                if (before != null) KeepHistory(before);
                AtomicWrite(CharacterPath(s.World, s.Owner, s.Character), StateCodec.Encode(s));
            }
        }
        public string Propose(CharacterState s)
        {
            lock (sync)
            {
                // One immutable proposal per identity: reconnects cannot silently replace an inspected import.
                string id = StateCodec.Key(s.World, s.Owner, s.Character);
                string path = PendingPath(id);
                if (!File.Exists(path)) AtomicWrite(path, StateCodec.Encode(s));
                return id;
            }
        }
        public CharacterState Pending(string id) { lock (sync) return ReadFile(PendingPath(id)); }
        public IEnumerable<string> PendingIds()
        {
            lock (sync)
            {
                var ids = new List<string>();
                foreach (string file in Directory.GetFiles(Path.Combine(root, "pending"), "*.wchar")) ids.Add(Path.GetFileNameWithoutExtension(file));
                return ids;
            }
        }
        public void Approve(string id, bool fresh)
        {
            lock (sync)
            {
                CharacterState s = Pending(id);
                if (StateCodec.Key(s.World, s.Owner, s.Character) != id) throw new InvalidDataException("Proposal identity mismatch.");
                if (Read(s.World, s.Owner, s.Character) != null) throw new InvalidOperationException("Character already exists; approval cannot overwrite progress.");
                if (fresh) { s.Player = new byte[0]; s.WorldData = new byte[0]; }
                s.Revision = 1; Save(s, 0);
                // Keep the proposal as the import backup, outside active saves.
            }
        }
        public void RejectProposal(string id)
        {
            lock (sync)
            {
                string path = PendingPath(id); CharacterState s = ReadFile(path);
                if (Read(s.World, s.Owner, s.Character) != null) throw new InvalidOperationException("An approved import is retained as a backup.");
                File.Move(path, path + ".rejected-" + Guid.NewGuid().ToString("N"));
            }
        }
        private void KeepHistory(CharacterState state)
        {
            string key = StateCodec.Key(state.World, state.Owner, state.Character); DateTime last;
            if (historyWritten.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(5)) return;
            string folder = Path.Combine(root, "history", key); SafeDirectory(folder);
            string path = Path.Combine(folder, state.Revision.ToString("D20") + ".wchar");
            if (!File.Exists(path)) AtomicWrite(path, StateCodec.Encode(state));
            string[] history = Directory.GetFiles(folder, "*.wchar"); Array.Sort(history, StringComparer.Ordinal);
            for (int i = 0; i < history.Length - 12; ++i)
            {
                string full = Path.GetFullPath(history[i]);
                if (Path.GetDirectoryName(full) != Path.GetFullPath(folder)) throw new IOException("History path escaped its directory.");
                SafePath(full); File.Delete(full);
            }
            historyWritten[key] = DateTime.UtcNow;
        }
        public byte[] CaptureCheckpoint(long world)
        {
            lock (sync)
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                var states = new List<byte[]>();
                foreach (string file in Directory.GetFiles(Path.Combine(root, "characters"), "*.wchar"))
                { CharacterState s = ReadFile(file); if (s.World == world) states.Add(StateCodec.Encode(s)); }
                writer.Write(0x57434331); writer.Write(world); writer.Write(DateTime.UtcNow.Ticks); writer.Write(states.Count);
                foreach (byte[] state in states) StateCodec.WriteBytes(writer, state);
                writer.Flush(); return stream.ToArray();
            }
        }
        public void CommitCheckpoint(long world, byte[] bytes)
        {
            lock (sync)
            {
                string folder = Path.Combine(root, "checkpoints"); SafeDirectory(folder);
                AtomicWrite(Path.Combine(folder, world.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".wcheckpoint"), bytes);
            }
        }
        public void Archive(string category, CharacterState s)
        {
            lock (sync)
            {
                if (category != "client-recovery" && category != "before-restore") throw new ArgumentException("Unknown archive category.");
                string folder = Path.Combine(root, category); SafeDirectory(folder);
                string path = Path.Combine(folder, StateCodec.Key(s.World, s.Owner, s.Character) + ".wchar");
                AtomicWrite(path, StateCodec.Encode(s));
            }
        }
        private string CharacterPath(long world, string owner, long character) { return Path.Combine(root, "characters", StateCodec.Key(world, owner, character) + ".wchar"); }
        private string PendingPath(string id)
        {
            if (id == null || id.Length != 64) throw new ArgumentException("Expected a full 64-character request ID.");
            foreach (char c in id) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new ArgumentException("Invalid request ID.");
            return Path.Combine(root, "pending", id + ".wchar");
        }
        private static CharacterState ReadFile(string path)
        {
            SafePath(path);
            if (new FileInfo(path).Length > StateCodec.MaximumBytes) throw new InvalidDataException("Oversized state file.");
            return StateCodec.Decode(File.ReadAllBytes(path));
        }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            SafePath(path); SafePath(path + ".bak");
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static void SafeDirectory(string path) { SafePath(path); Directory.CreateDirectory(path); }
        private static void SafePath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked state paths are not supported.");
                current = Path.GetDirectoryName(current);
            }
        }
        public void Dispose() { processLock.Dispose(); }
    }
}
