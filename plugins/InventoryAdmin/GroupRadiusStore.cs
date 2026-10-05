using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    // Caller authorization belongs to the host adapter. This store provides
    // durable world scoping and generation comparison for simultaneous editors.
    public sealed class GroupRadiusStore : IDisposable
    {
        private const int Magic = 0x49414753, Version = 1, MaximumFileBytes = GroupRadiusCodec.MaximumPacketBytes + 56;
        private readonly string directory;
        private readonly FileStream processLock;
        private readonly object sync = new object();
        private readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        private bool disposed;
        private sealed class Entry
        {
            internal GroupRadiusSettings Settings = new GroupRadiusSettings();
            internal string Error = "";
        }

        public GroupRadiusStore(string inventoryAdminDirectory)
        {
            if (String.IsNullOrEmpty(inventoryAdminDirectory)) throw new ArgumentException("Inventory administration directory is required.");
            directory = Path.Combine(Path.GetFullPath(inventoryAdminDirectory), "group-radius");
            PolicyFiles.RequireRegularPath(directory); Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, "settings.lock"); PolicyFiles.RequireRegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public GroupRadiusSettings Snapshot(long world)
        { lock (sync) { CheckOpen(); return Get(world).Settings.Clone(); } }

        public bool IsCorrupt(long world)
        { lock (sync) { CheckOpen(); return Get(world).Error.Length != 0; } }

        public string LastError(long world)
        { lock (sync) { CheckOpen(); return Get(world).Error; } }

        public bool TryUpdate(long world, long expectedGeneration, GroupRadiusSettings next, out GroupRadiusSettings current)
        {
            RequireWorld(world);
            if (expectedGeneration < 0) throw new InvalidDataException("Invalid expected group radius settings generation.");
            GroupRadiusPolicy.ValidateSettings(next);
            GroupRadiusSettings replacement = next.Clone();
            lock (sync)
            {
                CheckOpen(); Entry entry = Get(world);
                if (entry.Error.Length != 0) throw new InvalidOperationException("Group radius settings are corrupt; the original file requires manual review. " + entry.Error);
                // Recheck disk before replacement: an external damaged or stale
                // file must not be silently overwritten by an in-memory editor.
                Entry persisted = Read(world);
                if (persisted.Error.Length != 0) { entries[world] = persisted; throw new InvalidOperationException("Group radius settings require manual review. " + persisted.Error); }
                entries[world] = persisted; entry = persisted;
                current = entry.Settings.Clone();
                if (expectedGeneration != entry.Settings.Generation) return false;
                if (entry.Settings.Generation == Int64.MaxValue) throw new InvalidOperationException("Group radius settings generation is exhausted.");
                replacement.Generation = entry.Settings.Generation + 1;
                Save(world, replacement); // Publish in memory only after atomic durable replacement.
                entry.Settings = replacement; current = replacement.Clone(); return true;
            }
        }

        private Entry Get(long world)
        {
            RequireWorld(world); Entry entry;
            if (!entries.TryGetValue(world, out entry)) { entry = Read(world); entries.Add(world, entry); }
            return entry;
        }

        private Entry Read(long world)
        {
            var entry = new Entry(); string path = FilePath(world);
            try
            {
                PolicyFiles.RequireRegularPath(path);
                if (Directory.Exists(path)) throw new IOException("The settings file path is a directory.");
                if (!File.Exists(path)) return entry;
                if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("Oversized group radius settings file.");
                byte[] bytes = PolicyBinary.Unseal(File.ReadAllBytes(path), MaximumFileBytes);
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version || reader.ReadInt64() != world)
                        throw new InvalidDataException("Group radius settings format or world mismatch.");
                    byte[] packet = PolicyBinary.ReadBytes(reader, GroupRadiusCodec.MaximumPacketBytes);
                    if (stream.Position != stream.Length) throw new InvalidDataException("Trailing group radius settings file data.");
                    entry.Settings = GroupRadiusCodec.DecodeSettings(packet); return entry;
                }
            }
            catch (InvalidDataException error) { entry.Error = error.Message; return entry; }
            catch (IOException error) { entry.Error = error.Message; return entry; }
            catch (UnauthorizedAccessException error) { entry.Error = error.Message; return entry; }
            catch (DecoderFallbackException error) { entry.Error = error.Message; return entry; }
        }

        private void Save(long world, GroupRadiusSettings settings)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(Magic); writer.Write(Version); writer.Write(world);
                PolicyBinary.WriteBytes(writer, GroupRadiusCodec.EncodeSettings(settings), GroupRadiusCodec.MaximumPacketBytes);
                writer.Flush(); PolicyFiles.AtomicWrite(FilePath(world), PolicyBinary.Seal(stream.ToArray()));
            }
        }

        private string FilePath(long world) { return Path.Combine(directory, world.ToString(CultureInfo.InvariantCulture) + ".dat"); }
        private static void RequireWorld(long world) { if (world == 0) throw new InvalidDataException("World identity is required."); }
        private void CheckOpen() { if (disposed) throw new ObjectDisposedException("GroupRadiusStore"); }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; processLock.Dispose(); } }
    }
}
