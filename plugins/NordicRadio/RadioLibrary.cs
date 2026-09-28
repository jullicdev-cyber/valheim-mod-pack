using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimModPack.NordicRadio
{
    internal sealed class LibraryEntry
    {
        public TrackInfo Track;
        public string Path;
        public long Modified;
    }

    internal sealed class RadioLibrary
    {
        public readonly string MusicDirectory;
        public readonly string CacheDirectory;
        private readonly Dictionary<string, LibraryEntry> verified = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);

        public RadioLibrary(string root)
        {
            string full = System.IO.Path.GetFullPath(root);
            MusicDirectory = System.IO.Path.Combine(full, "Music");
            CacheDirectory = System.IO.Path.Combine(full, "Cache");
            Directory.CreateDirectory(MusicDirectory); Directory.CreateDirectory(CacheDirectory);
            AssertDirectory(full); AssertDirectory(MusicDirectory); AssertDirectory(CacheDirectory);
        }

        private static void AssertDirectory(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Radio directories must not be symbolic links: " + path);
        }

        public List<LibraryEntry> Scan(Action<string> log, Func<bool> cancelled = null)
        {
            AssertDirectory(MusicDirectory);
            var result = new List<LibraryEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new List<string>();
            foreach (string path in Directory.EnumerateFiles(MusicDirectory))
            {
                if (String.Equals(System.IO.Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                // A deliberately huge folder must not consume unbounded sorting memory.
                if (paths.Count >= 4096) break;
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (cancelled != null && cancelled()) break;
                if (result.Count >= RadioProtocol.MaxTracks) { log("Music library limited to 256 tracks."); break; }
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length < 4 || info.Length > RadioProtocol.MaxFile) { log("Skipped unsupported MP3 size or linked file: " + info.Name); continue; }
                    long size = info.Length; long modified = info.LastWriteTimeUtc.Ticks;
                    string id = HashFile(path);
                    double duration = Mp3Duration(path);
                    info.Refresh();
                    if (info.Length != size || info.LastWriteTimeUtc.Ticks != modified || duration <= 0 || duration > 86400) { log("Skipped changing or invalid MPEG Layer III file: " + info.Name); continue; }
                    if (!ids.Add(id)) continue;
                    result.Add(new LibraryEntry { Track = new TrackInfo { Id = id, Title = SafeTitle(System.IO.Path.GetFileNameWithoutExtension(path)), Size = size, Duration = duration }, Path = path, Modified = modified });
                }
                catch (Exception exception) { log("Skipped MP3: " + System.IO.Path.GetFileName(path) + " (" + exception.Message + ")"); }
            }
            return result;
        }

        public static string SafeTitle(string value)
        {
            var result = new StringBuilder();
            foreach (char c in value)
            {
                if (result.Length >= 96) break;
                if (c != '<' && c != '>' && !Char.IsControl(c)) result.Append(c);
            }
            if (result.Length > 0 && Char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
            return result.Length == 0 ? "Untitled" : result.ToString();
        }

        public static string HashFile(string path)
        {
            using (var hash = SHA256.Create())
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return Hex(hash.ComputeHash(file));
        }

        public static string Hex(byte[] value)
        {
            var result = new StringBuilder(value.Length * 2);
            foreach (byte b in value) result.Append(b.ToString("x2"));
            return result.ToString();
        }

        public string CachePath(string id)
        {
            if (!RadioProtocol.ValidId(id)) throw new InvalidDataException("Invalid cache id");
            AssertDirectory(CacheDirectory);
            return System.IO.Path.Combine(CacheDirectory, id + ".mp3");
        }

        public string GetVerified(string id)
        {
            LibraryEntry entry;
            lock (verified) { if (!verified.TryGetValue(id, out entry)) return null; }
            var info = new FileInfo(entry.Path);
            if (!info.Exists || info.Length != entry.Track.Size || info.LastWriteTimeUtc.Ticks != entry.Modified || (info.Attributes & FileAttributes.ReparsePoint) != 0) { lock (verified) verified.Remove(id); return null; }
            return entry.Path;
        }

        // Run on the library worker, never read an existing untrusted file into a clip.
        public string ValidateCache(TrackInfo track)
        {
            string path = CachePath(track.Id);
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != track.Size || HashFile(path) != track.Id) return null;
            MarkVerified(track.Id, path);
            return path;
        }

        public void MarkVerified(string id, string path)
        {
            if (!String.Equals(path, CachePath(id), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected cache path");
            var info = new FileInfo(path);
            lock (verified) verified[id] = new LibraryEntry { Path = path, Modified = info.LastWriteTimeUtc.Ticks, Track = new TrackInfo { Id = id, Size = info.Length } };
        }

        // Optional pre-shared music avoids transmitting it through the game at all.
        // Names do not establish identity: use the host's exact length and SHA-256.
        public string FindLocalTrack(TrackInfo track, Func<bool> cancelled)
        {
            AssertDirectory(MusicDirectory); int examined = 0;
            foreach (string path in Directory.EnumerateFiles(MusicDirectory))
            {
                if (++examined > 4096 || (cancelled != null && cancelled())) return null;
                if (!String.Equals(System.IO.Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != track.Size) continue;
                    long modified = info.LastWriteTimeUtc.Ticks;
                    if (HashFile(path) != track.Id) continue;
                    info.Refresh();
                    if (info.Length != track.Size || info.LastWriteTimeUtc.Ticks != modified
                        || (cancelled != null && cancelled())) continue;
                    lock (verified) verified[track.Id] = new LibraryEntry { Path = path, Modified = modified, Track = track };
                    return path;
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return null;
        }

        public string BeginDownload(TrackInfo track, ICollection<string> protectedIds)
        {
            Prune(track.Size, protectedIds);
            return System.IO.Path.Combine(CacheDirectory, track.Id + "." + Guid.NewGuid().ToString("N") + ".part");
        }

        public void CommitDownload(string id, string temporary)
        {
            string destination = CachePath(id);
            if (!String.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(temporary)), CacheDirectory, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected temporary path");
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked cache file");
                File.Delete(destination);
            }
            File.Move(temporary, destination);
            MarkVerified(id, destination);
        }

        private void Prune(long incoming, ICollection<string> protectedIds)
        {
            AssertDirectory(CacheDirectory);
            var files = new List<FileInfo>();
            long total = 0;
            foreach (string path in Directory.EnumerateFiles(CacheDirectory, "*.part"))
            {
                var info = new FileInfo(path);
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                Guid suffix;
                if (name.Length != 97 || name[64] != '.' || !RadioProtocol.ValidId(name.Substring(0, 64)) || !Guid.TryParseExact(name.Substring(65), "N", out suffix) || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                // Only our exact temporary-file pattern is eligible for cleanup.
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) info.Delete();
                else total += info.Length;
            }
            foreach (string path in Directory.EnumerateFiles(CacheDirectory, "*.mp3"))
            {
                var info = new FileInfo(path);
                if (!RadioProtocol.ValidId(System.IO.Path.GetFileNameWithoutExtension(path)) || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total += info.Length; files.Add(info);
            }
            files.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
            foreach (FileInfo file in files)
            {
                if (total + incoming <= RadioProtocol.MaxCache) break;
                string id = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                if (protectedIds.Contains(id)) continue;
                long size = file.Length; file.Delete(); total -= size;
                lock (verified) verified.Remove(id);
            }
            if (total + incoming > RadioProtocol.MaxCache) throw new IOException("Radio cache is full (2 GiB limit)");
        }

        // Sum frame sample counts instead of estimating from file size. This also handles VBR.
        internal static double Mp3Duration(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536))
            using (var reader = new BinaryReader(stream))
            {
                long start = 0;
                if (stream.Length >= 10)
                {
                    byte[] id3 = reader.ReadBytes(10);
                    if (id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
                    {
                        if ((id3[6] | id3[7] | id3[8] | id3[9]) >= 128) return 0;
                        start = 10L + ((long)id3[6] << 21) + ((long)id3[7] << 14) + ((long)id3[8] << 7) + id3[9];
                        if ((id3[5] & 16) != 0) start += 10;
                    }
                }
                long position = start; int frames = 0; double seconds = 0;
                int[] bitrate1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
                int[] bitrate2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
                int[] rates = { 44100, 48000, 32000 };
                while (position + 4 <= stream.Length)
                {
                    stream.Position = position;
                    byte a = reader.ReadByte(), b = reader.ReadByte(), c = reader.ReadByte(), d = reader.ReadByte();
                    int version = (b >> 3) & 3, layer = (b >> 1) & 3, bitrateIndex = c >> 4, rateIndex = (c >> 2) & 3;
                    if (a != 255 || (b & 224) != 224 || version == 1 || layer != 1 || bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3)
                    {
                        if (frames > 0) break;
                        if (++position - start > 65536) return 0;
                        continue;
                    }
                    int rate = rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
                    int bitrate = (version == 3 ? bitrate1[bitrateIndex] : bitrate2[bitrateIndex]) * 1000;
                    int length = (version == 3 ? 144 : 72) * bitrate / rate + ((c >> 1) & 1);
                    if (position + length > stream.Length) break;
                    seconds += (version == 3 ? 1152.0 : 576.0) / rate;
                    frames++; position += length;
                }
                return frames >= 2 ? seconds : 0;
            }
        }
    }
}
