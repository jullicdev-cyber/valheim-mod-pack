using System;
using System.IO;
using System.Security.Cryptography;
using ValheimModPack.PinRemoval;
public static class ArchiveTests
{
    private static int count;
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static void Fails(Action action, string message)
    { bool failed = false; try { action(); } catch { failed = true; } Check(failed, message); }
    private static PinRecord Pin() { return new PinRecord { Name = "Медь <не разметка>", Type = 2, X = 12.5f, Y = 1.25f, Z = -34.75f, Owner = 777, Author = "Steam_12345", Checked = true, DoubleSize = true, WorldSize = 3 }; }
    private static void HashFile(string file, byte[] bytes)
    {
        byte[] digest; using (var hash = SHA256.Create()) digest = hash.ComputeHash(bytes, 0, bytes.Length - 32);
        Array.Copy(digest, 0, bytes, bytes.Length - 32, 32); File.WriteAllBytes(file, bytes);
    }
    public static void Main()
    {
        string folder = Path.Combine(Path.GetTempPath(), "vmp-pin-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "world-character.bin"); long now = DateTime.UtcNow.Ticks;
            var archive = new PinArchive(file, 100, 200);
            Check(archive.Deleted.Count == 0, "Empty archive starts without phantom history");
            Fails(() => new PinArchive(file, 0, 200), "World must be known");
            Fails(() => new PinArchive(file, 100, 0), "Character must be known");
            var pin = Pin(); var renamed = pin.Copy(); renamed.Name = "Renamed"; renamed.Checked = false;
            Check(pin.Key == renamed.Key, "Rename and checkmark preserve metadata identity");
            renamed.Owner++; Check(pin.Key != renamed.Key, "Different owner cannot inherit creation metadata");
            renamed = pin.Copy(); renamed.X += .01f; Check(pin.Key != renamed.Key, "Distinct location has distinct identity");
            archive.Enrich(pin); Check(pin.CreatedUtc == 0 && pin.CreatorName == "", "Old pin never receives invented date or creator");
            archive.RememberCreation(pin, "Скальд", now - 50);
            archive.Enrich(pin); Check(pin.CreatedUtc == now - 50 && pin.CreatorName == "Скальд", "Observed creation metadata is attached");
            var deleted = archive.RecordBeforeDelete(pin, now);
            Check(File.Exists(file) && File.Exists(file + ".bak"), "Journal committed to disk with previous version backup");
            Check(deleted.Id.Length == 32 && deleted.DeletedUtc == now && deleted.RestoredUtc == 0, "Deletion has independent ID and real deletion time");
            pin.Name = "Mutated live pin"; Check(deleted.Name != pin.Name, "Deletion stores a snapshot, not mutable live data");
            archive = new PinArchive(file, 100, 200);
            Check(archive.Deleted.Count == 1 && archive.Deleted[0].Name == "Медь <не разметка>", "UTF-8 history survives restart");
            Check(archive.Deleted[0].X == 12.5f && archive.Deleted[0].Checked && archive.Deleted[0].DoubleSize && archive.Deleted[0].WorldSize == 3, "Coordinates and pin flags survive restart");
            var metadata = Pin(); archive.Enrich(metadata);
            Check(metadata.CreatedUtc == now - 50 && metadata.CreatorName == "Скальд", "Creation metadata survives restart");
            Fails(() => new PinArchive(file, 101, 200), "Wrong world rejected even if file copied");
            Fails(() => new PinArchive(file, 100, 201), "Wrong character rejected even if file copied");
            int added = 0;
            Check(archive.Restore("missing", p => false, p => added++, now) == RestoreResult.Missing && added == 0, "Unknown recovery ID cannot add a pin");
            Check(archive.Restore(deleted.Id, p => false, p => { added++; Check(p.Author == "Steam_12345" && p.Owner == 777, "Restore retains shared ownership"); }, now + 1) == RestoreResult.Restored && added == 1, "One restore adds one exact pin");
            Check(archive.Restore(deleted.Id, p => false, p => added++, now + 2) == RestoreResult.AlreadyRestored && added == 1, "Double-click cannot duplicate a pin");
            archive = new PinArchive(file, 100, 200);
            Check(archive.Restore(deleted.Id, p => false, p => added++, now + 3) == RestoreResult.AlreadyRestored && added == 1, "Recovery state survives restart");
            deleted = archive.RecordBeforeDelete(Pin(), now + 4);
            Check(archive.Restore(deleted.Id, p => true, p => added++, now + 5) == RestoreResult.AlreadyPresent && added == 1, "Already present/reimported/renamed pin is not duplicated");
            deleted = archive.RecordBeforeDelete(Pin(), now + 6);
            Fails(() => archive.Restore(deleted.Id, p => false, p => { throw new IOException("game refused"); }, now + 7), "Game add failure reported");
            Check(deleted.RestoredUtc == 0, "Failed game add remains recoverable");
            bool present = false;
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Fails(() => archive.Restore(deleted.Id, p => present, p => { added++; present = true; }, now + 8), "Failed disk commit after game add reported");
            Check(deleted.RestoredUtc == 0 && present, "Disk failure preserves recoverability");
            int before = added;
            Check(archive.Restore(deleted.Id, p => present, p => added++, now + 9) == RestoreResult.AlreadyPresent && added == before, "Retry after disk failure does not duplicate live pin");
            byte[] valid = File.ReadAllBytes(file); byte[] corrupt = (byte[])valid.Clone(); corrupt[40] ^= 64; File.WriteAllBytes(file, corrupt);
            Fails(() => new PinArchive(file, 100, 200), "Corruption detected");
            Check(File.ReadAllBytes(file)[40] == corrupt[40], "Corrupt original remains untouched for recovery");
            File.WriteAllBytes(file, new byte[12]); Fails(() => new PinArchive(file, 100, 200), "Truncated file rejected");
            File.WriteAllBytes(file, new byte[PinArchive.MaximumFileBytes + 1]); Fails(() => new PinArchive(file, 100, 200), "Oversized file rejected before parsing");
            corrupt = (byte[])valid.Clone(); Array.Copy(BitConverter.GetBytes(Int32.MaxValue), 0, corrupt, 24, 4); HashFile(file, corrupt);
            Fails(() => new PinArchive(file, 100, 200), "Huge entry count rejected despite valid checksum");
            File.WriteAllBytes(file, valid);
            var invalid = Pin(); invalid.X = Single.NaN; Fails(() => archive.RecordBeforeDelete(invalid, now), "NaN coordinates never persisted");
            invalid = Pin(); invalid.Name = new string('a', 257); Fails(() => archive.RecordBeforeDelete(invalid, now), "Oversized names do not corrupt history");
            invalid = Pin(); invalid.WorldSize = Single.PositiveInfinity; Fails(() => archive.RecordBeforeDelete(invalid, now), "Infinite pin size rejected");
            string blocker = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blocker, "x");
            var blocked = new PinArchive(Path.Combine(blocker, "history.bin"), 100, 200);
            Fails(() => blocked.RecordBeforeDelete(Pin(), now), "Unwritable journal fails before deletion can happen");
            Check(blocked.Deleted.Count == 0, "Failed journal write rolls back in-memory entry");
            Fails(() => blocked.RememberCreation(Pin(), "Test", now), "Creation write error reported");
            metadata = Pin(); blocked.Enrich(metadata); Check(metadata.CreatedUtc == 0, "Creation metadata rollback avoids unsaved false history");
            var bounded = new PinArchive(Path.Combine(folder, "bounded.bin"), 9, 8);
            for (int i = 0; i <= PinArchive.MaximumDeleted; i++) { var entry = Pin(); entry.Name = i.ToString(); bounded.RecordBeforeDelete(entry, now + i); }
            Check(bounded.Deleted.Count == PinArchive.MaximumDeleted && bounded.Deleted[0].Name == "1", "Bounded archive retains latest 500 deletions");
            bounded = new PinArchive(Path.Combine(folder, "bounded.bin"), 9, 8);
            Check(bounded.Deleted.Count == PinArchive.MaximumDeleted && bounded.Deleted[499].Name == "500", "Bounded history is durable");
            Console.WriteLine("OK: " + count + " production pin-history persistence/recovery assertions.");
        }
        finally
        {
            string resolved = Path.GetFullPath(folder), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("vmp-pin-history-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
}
