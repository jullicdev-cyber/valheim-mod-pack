using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ValheimModPack.PartyPrison
{
    // Host-only store. Every mutation is durable before it becomes visible.
    // Remaining time has no wall-clock timestamp: disconnects and server downtime
    // do not reduce a sentence. The native host confirms confinement each tick.
    public sealed class SentenceStore : IDisposable
    {
        private const int MaximumBytes = 1024 * 1024;
        private const int Magic = 0x50505331;
        private readonly object sync = new object();
        private readonly object timerGate = new object();
        private readonly Action beforeTimerWrite;
        private Task timerWrite;
        private readonly Dictionary<string, TimerDebit> queuedTimer = new Dictionary<string, TimerDebit>(StringComparer.Ordinal);
        private sealed class TimerDebit { internal string Token; internal double Seconds; }
        private readonly FileStream processLock;
        private readonly string path;
        private readonly long world;
        private Dictionary<string, SentenceState> sentences = new Dictionary<string, SentenceState>(StringComparer.Ordinal);
        private PrisonRegion region;
        private byte[] diskHash;
        private bool disposed;
        private volatile bool faulted;
        private volatile string faultReason;
        public bool Faulted { get { return faulted; } }
        public string FaultReason { get { return faultReason ?? ""; } }
        private void MarkFault(string reason)
        { lock (sync) { if (!faulted) { faultReason = reason; faulted = true; } } }

        public SentenceStore(string directory, long world)
            : this(directory, world, null) { }

        internal SentenceStore(string directory, long world, Action beforeTimerWrite)
        {
            if (world == 0) throw new InvalidDataException("World identity is required.");
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("State directory is required.");
            this.world = world;
            this.beforeTimerWrite = beforeTimerWrite;
            string root = Path.GetFullPath(directory);
            RequireRegularPath(root);
            Directory.CreateDirectory(root);
            string stem = "world-" + world.ToString("x16", CultureInfo.InvariantCulture);
            path = Path.Combine(root, stem + ".dat");
            string lockPath = Path.Combine(root, stem + ".lock");
            RequireRegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { Read(); }
            catch { processLock.Dispose(); throw; }
        }

        public long World { get { return world; } }
        public string StatePath { get { return path; } }
        public PrisonRegion Region { get { lock (sync) { CheckOpen(); return region == null ? null : region.Copy(); } } }

        public SentenceState Find(string authenticatedAccountId)
        {
            SentencePolicy.RequireAccountId(authenticatedAccountId);
            lock (sync)
            {
                CheckOpen();
                SentenceState value;
                return sentences.TryGetValue(authenticatedAccountId, out value) ? value.Copy() : null;
            }
        }

        public SentenceState[] All()
        {
            lock (sync)
            {
                CheckOpen();
                var ordered = new List<string>(sentences.Keys);
                ordered.Sort(StringComparer.Ordinal);
                var result = new SentenceState[ordered.Count];
                for (int i = 0; i < ordered.Count; ++i) result[i] = sentences[ordered[i]].Copy();
                return result;
            }
        }

        public void SetRegion(bool authenticatedCallerIsHost, PrisonRegion value)
        { RequireHost(authenticatedCallerIsHost); lock (timerGate) { FlushTimerLocked(); SetRegionCore(authenticatedCallerIsHost, value); } }
        public SentenceState Impose(bool authenticatedCallerIsHost, string authenticatedAccountId, string playerName,
            string reason, double seconds, PrisonPoint returnPosition)
        { RequireHost(authenticatedCallerIsHost); lock (timerGate) { FlushTimerLocked(); return ImposeCore(authenticatedCallerIsHost, authenticatedAccountId, playerName, reason, seconds, returnPosition); } }
        public SentenceState[] TickOnline(IEnumerable<string> verifiedOnlineAccounts, double seconds)
        { lock (timerGate) { FlushTimerLocked(); return TickOnlineCore(verifiedOnlineAccounts, seconds); } }
        public SentenceState RequestRelease(bool authenticatedCallerIsHost, string authenticatedAccountId)
        { RequireHost(authenticatedCallerIsHost); lock (timerGate) { FlushTimerLocked(); return RequestReleaseCore(authenticatedCallerIsHost, authenticatedAccountId); } }
        public SentenceState RequestEmergencyRelease(bool authenticatedCallerIsHost, string authenticatedAccountId)
        { RequireHost(authenticatedCallerIsHost); lock (timerGate) { FlushTimerLocked(); return RequestEmergencyReleaseCore(authenticatedCallerIsHost, authenticatedAccountId); } }
        public bool AcknowledgeRelease(string authenticatedAccountId, string sentenceId)
        { lock (timerGate) { FlushTimerLocked(); return AcknowledgeReleaseCore(authenticatedAccountId, sentenceId); } }

        // Timer commits are bounded to one background write. Queries expose only
        // durable time, never an unsaved expiry. Commands and shutdown drain this
        // queue before mutating the same store; there are no Unity APIs here.
        public bool QueueTickOnline(IEnumerable<string> verifiedOnlineAccounts, double seconds)
        {
            SentencePolicy.RequireTick(seconds);
            if (verifiedOnlineAccounts == null) throw new ArgumentNullException("verifiedOnlineAccounts");
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string account in verifiedOnlineAccounts) {
                SentencePolicy.RequireAccountId(account); accounts.Add(account);
                if (accounts.Count > SentencePolicy.MaximumSentences) throw new InvalidDataException("Oversized active prisoner roster.");
            }
            lock (timerGate) {
                if (timerWrite != null && timerWrite.IsCompleted) FinishTimerLocked();
                lock (sync) {
                    CheckOpen();
                    foreach (string account in accounts) {
                        SentenceState value;
                        if (seconds == 0 || !sentences.TryGetValue(account, out value) || value.PendingRelease) continue;
                        TimerDebit debit;
                        if (!queuedTimer.TryGetValue(account, out debit) || debit.Token != value.SentenceId)
                            queuedTimer[account] = debit = new TimerDebit { Token = value.SentenceId };
                        debit.Seconds = Math.Min(value.RemainingSeconds, debit.Seconds + seconds);
                    }
                }
                if (timerWrite != null) return false;
                PrisonRegion copyRegion; Dictionary<string, SentenceState> next = TakeQueuedTimer(out copyRegion);
                if (next == null) return false;
                timerWrite = Task.Run(() => {
                    try {
                        if (beforeTimerWrite != null) beforeTimerWrite();
                        Persist(copyRegion, next);
                        lock (sync) { CheckOpen(); sentences = next; }
                    }
                    catch { MarkFault("Background sentence timer could not be saved."); throw; }
                });
                return true;
            }
        }

        private Dictionary<string, SentenceState> TakeQueuedTimer(out PrisonRegion copyRegion)
        {
            lock (sync) {
                CheckOpen(); copyRegion = region == null ? null : region.Copy();
                if (queuedTimer.Count == 0) return null;
                Dictionary<string, SentenceState> next = CopySentences(); bool changed = false;
                foreach (var pair in queuedTimer) {
                    SentenceState value;
                    if (!next.TryGetValue(pair.Key, out value) || value.PendingRelease || value.SentenceId != pair.Value.Token || pair.Value.Seconds <= 0) continue;
                    value.RemainingSeconds = Math.Max(0, value.RemainingSeconds - pair.Value.Seconds);
                    value.PendingRelease = value.RemainingSeconds == 0;
                    value.Revision = checked(value.Revision + 1); changed = true;
                }
                queuedTimer.Clear(); return changed ? next : null;
            }
        }
        private void FinishTimerLocked()
        {
            Task pending = timerWrite;
            if (pending == null) return;
            try { pending.GetAwaiter().GetResult(); }
            catch { MarkFault("Background sentence timer could not be saved."); throw; }
            finally { timerWrite = null; }
        }
        private void FlushTimerLocked()
        {
            FinishTimerLocked();
            if (queuedTimer.Count == 0) return;
            PrisonRegion copyRegion; Dictionary<string, SentenceState> next = TakeQueuedTimer(out copyRegion);
            if (next == null) return;
            Persist(copyRegion, next); lock (sync) { sentences = next; }
        }

        private void SetRegionCore(bool authenticatedCallerIsHost, PrisonRegion value)
        {
            RequireHost(authenticatedCallerIsHost);
            SentencePolicy.RequireRegion(value);
            lock (sync)
            {
                CheckOpen();
                if (sentences.Count != 0) throw new InvalidOperationException("Release all prisoners before changing the prison.");
                Persist(value, sentences);
                region = value.Copy();
            }
        }

        private SentenceState ImposeCore(bool authenticatedCallerIsHost, string authenticatedAccountId, string playerName,
            string reason, double seconds, PrisonPoint returnPosition)
        {
            RequireHost(authenticatedCallerIsHost);
            SentencePolicy.RequireDuration(seconds);
            var value = new SentenceState { AccountId = authenticatedAccountId, SentenceId = Guid.NewGuid().ToString("N"),
                PlayerName = playerName, Reason = reason, Revision = 1, RemainingSeconds = seconds, ReturnPosition = returnPosition };
            SentencePolicy.RequireSentence(value);
            lock (sync)
            {
                CheckOpen();
                if (region == null) throw new InvalidOperationException("Configure the prison first.");
                if (sentences.ContainsKey(authenticatedAccountId))
                    throw new InvalidOperationException("This account already has a sentence or an unconfirmed release.");
                if (sentences.Count >= SentencePolicy.MaximumSentences) throw new InvalidOperationException("Prisoner limit reached.");
                var next = CopySentences(); next.Add(authenticatedAccountId, value);
                Persist(region, next); sentences = next;
                return value.Copy();
            }
        }

        private SentenceState[] TickOnlineCore(IEnumerable<string> verifiedOnlineAccounts, double seconds)
        {
            SentencePolicy.RequireTick(seconds);
            if (verifiedOnlineAccounts == null) throw new ArgumentNullException("verifiedOnlineAccounts");
            // Materialize and validate the complete host roster before any mutation.
            var accounts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string account in verifiedOnlineAccounts)
            {
                SentencePolicy.RequireAccountId(account); accounts.Add(account);
                if (accounts.Count > SentencePolicy.MaximumSentences) throw new InvalidDataException("Oversized active prisoner roster.");
            }
            lock (sync)
            {
                CheckOpen();
                var changed = new List<SentenceState>();
                if (seconds == 0) return changed.ToArray();
                var next = CopySentences();
                foreach (string account in accounts)
                {
                    SentenceState value;
                    if (!next.TryGetValue(account, out value) || value.PendingRelease) continue;
                    value.RemainingSeconds = Math.Max(0, value.RemainingSeconds - seconds);
                    value.PendingRelease = value.RemainingSeconds == 0;
                    value.Revision = checked(value.Revision + 1);
                    changed.Add(value.Copy());
                }
                if (changed.Count == 0) return changed.ToArray();
                Persist(region, next); sentences = next;
                changed.Sort(delegate(SentenceState left, SentenceState right) { return StringComparer.Ordinal.Compare(left.AccountId, right.AccountId); });
                return changed.ToArray();
            }
        }

        private SentenceState RequestReleaseCore(bool authenticatedCallerIsHost, string authenticatedAccountId)
        {
            RequireHost(authenticatedCallerIsHost);
            SentencePolicy.RequireAccountId(authenticatedAccountId);
            lock (sync)
            {
                CheckOpen();
                SentenceState old;
                if (!sentences.TryGetValue(authenticatedAccountId, out old)) return null;
                if (old.PendingRelease) return old.Copy();
                var next = CopySentences(); var value = next[authenticatedAccountId];
                value.RemainingSeconds = 0; value.PendingRelease = true; value.Revision = checked(value.Revision + 1);
                Persist(region, next); sentences = next;
                return value.Copy();
            }
        }

        // Keep the release in the durable sentence until the recipient saves
        // its cleanup. This works before confiscation as well as after it, and
        // reconnecting clients receive the same explicit cancellation decision.
        private SentenceState RequestEmergencyReleaseCore(bool authenticatedCallerIsHost, string authenticatedAccountId)
        {
            RequireHost(authenticatedCallerIsHost);
            SentencePolicy.RequireAccountId(authenticatedAccountId);
            lock (sync)
            {
                CheckOpen(); SentenceState old;
                if (!sentences.TryGetValue(authenticatedAccountId, out old)) return null;
                if (old.EmergencyRelease) return old.Copy();
                var next = CopySentences(); var value = next[authenticatedAccountId];
                value.RemainingSeconds = 0; value.PendingRelease = value.EmergencyRelease = true;
                value.Revision = checked(value.Revision + 1);
                Persist(region, next); sentences = next;
                return value.Copy();
            }
        }

        // Call only after the host authenticates the ACK sender and verifies that
        // the client actually applied this release. Old tokens cannot erase a new sentence.
        private bool AcknowledgeReleaseCore(string authenticatedAccountId, string sentenceId)
        {
            SentencePolicy.RequireAccountId(authenticatedAccountId);
            lock (sync)
            {
                CheckOpen();
                SentenceState value;
                if (!sentences.TryGetValue(authenticatedAccountId, out value) || !value.PendingRelease
                    || !String.Equals(sentenceId, value.SentenceId, StringComparison.Ordinal)) return false;
                var next = CopySentences(); next.Remove(authenticatedAccountId);
                Persist(region, next); sentences = next;
                return true;
            }
        }

        private Dictionary<string, SentenceState> CopySentences()
        {
            var result = new Dictionary<string, SentenceState>(StringComparer.Ordinal);
            foreach (var pair in sentences) result.Add(pair.Key, pair.Value.Copy());
            return result;
        }

        private void Read()
        {
            RequireRegularPath(path);
            if (Directory.Exists(path)) throw new IOException("Prison state path is a directory.");
            if (!File.Exists(path)) return;
            byte[] bytes = ReadBounded(path);
            byte[] body = Unseal(bytes);
            using (var stream = new MemoryStream(body, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != Magic)
                    throw new InvalidDataException("Unsupported prison state format; file preserved.");
                int version = reader.ReadInt32();
                if (version < 1 || version > 2) throw new InvalidDataException("Unsupported prison state format; file preserved.");
                if (reader.ReadInt64() != world) throw new InvalidDataException("Prison state belongs to another world.");
                byte present = reader.ReadByte();
                if (present > 1) throw new InvalidDataException("Invalid prison region flag.");
                if (present == 1)
                {
                    region = new PrisonRegion { Center = ReadPoint(reader), Radius = reader.ReadDouble(), HalfHeight = reader.ReadDouble(),
                        CellSpawn = ReadPoint(reader), ArenaSpawn = ReadPoint(reader) };
                    SentencePolicy.RequireRegion(region);
                }
                int count = reader.ReadInt32();
                if (count < 0 || count > SentencePolicy.MaximumSentences || (count > 0 && region == null))
                    throw new InvalidDataException("Invalid prison sentence count or missing region.");
                var tokens = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; ++i)
                {
                    var value = new SentenceState { AccountId = ReadText(reader, SentencePolicy.MaximumAccountCharacters),
                        SentenceId = ReadText(reader, 32), PlayerName = ReadText(reader, SentencePolicy.MaximumNameCharacters),
                        Reason = ReadText(reader, SentencePolicy.MaximumReasonCharacters), Revision = reader.ReadInt64(),
                        RemainingSeconds = reader.ReadDouble(), ReturnPosition = ReadPoint(reader) };
                    byte pending = reader.ReadByte();
                    if (pending > 1) throw new InvalidDataException("Invalid pending release flag.");
                    value.PendingRelease = pending == 1;
                    if (version >= 2)
                    {
                        byte emergency = reader.ReadByte();
                        if (emergency > 1) throw new InvalidDataException("Invalid emergency release flag.");
                        value.EmergencyRelease = emergency == 1;
                    }
                    SentencePolicy.RequireSentence(value);
                    if (sentences.ContainsKey(value.AccountId) || !tokens.Add(value.SentenceId))
                        throw new InvalidDataException("Duplicate prison account or sentence token.");
                    sentences.Add(value.AccountId, value);
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing prison state data.");
            }
            diskHash = Hash(bytes);
        }

        private void Persist(PrisonRegion newRegion, Dictionary<string, SentenceState> values)
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(Magic); writer.Write(2); writer.Write(world);
                writer.Write((byte)(newRegion == null ? 0 : 1));
                if (newRegion != null)
                {
                    SentencePolicy.RequireRegion(newRegion);
                    WritePoint(writer, newRegion.Center); writer.Write(newRegion.Radius); writer.Write(newRegion.HalfHeight);
                    WritePoint(writer, newRegion.CellSpawn); WritePoint(writer, newRegion.ArenaSpawn);
                }
                var ordered = new List<string>(values.Keys); ordered.Sort(StringComparer.Ordinal);
                writer.Write(ordered.Count);
                foreach (string account in ordered)
                {
                    SentenceState value = values[account]; SentencePolicy.RequireSentence(value);
                    WriteText(writer, value.AccountId); WriteText(writer, value.SentenceId);
                    WriteText(writer, value.PlayerName); WriteText(writer, value.Reason);
                    writer.Write(value.Revision); writer.Write(value.RemainingSeconds); WritePoint(writer, value.ReturnPosition);
                    writer.Write((byte)(value.PendingRelease ? 1 : 0));
                    writer.Write((byte)(value.EmergencyRelease ? 1 : 0));
                }
                writer.Flush(); bytes = Seal(stream.ToArray());
            }
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Prison state exceeds its size limit.");
            AtomicWrite(bytes);
            diskHash = Hash(bytes);
        }

        private void AtomicWrite(byte[] bytes)
        {
            RequireRegularPath(path);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                EnsureUnchangedOnDisk();
                if (diskHash != null) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private void EnsureUnchangedOnDisk()
        {
            bool same;
            try
            {
                RequireRegularPath(path);
                if (Directory.Exists(path)) throw new IOException("Prison state path is a directory.");
                same = diskHash == null ? !File.Exists(path) : File.Exists(path) && Equal(diskHash, Hash(ReadBounded(path)));
            }
            catch (Exception error)
            {
                MarkFault("Prison state could not be verified; host must stop admitting players and repair the file.");
                throw new IOException(FaultReason, error);
            }
            if (same) return;
            MarkFault("Prison state changed outside this host; file preserved and host must stop admitting players.");
            throw new IOException(FaultReason);
        }

        private static byte[] ReadBounded(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 53 || stream.Length > MaximumBytes) throw new InvalidDataException("Invalid prison state size.");
                byte[] bytes = new byte[(int)stream.Length]; int position = 0;
                while (position < bytes.Length)
                {
                    int read = stream.Read(bytes, position, bytes.Length - position);
                    if (read == 0) throw new EndOfStreamException("Incomplete prison state.");
                    position += read;
                }
                return bytes;
            }
        }

        private static byte[] Seal(byte[] body)
        {
            byte[] result = new byte[body.Length + 32];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            Buffer.BlockCopy(Hash(body), 0, result, body.Length, 32);
            return result;
        }

        private static byte[] Unseal(byte[] bytes)
        {
            int length = bytes.Length - 32;
            byte[] body = new byte[length]; Buffer.BlockCopy(bytes, 0, body, 0, length);
            byte[] expected = Hash(body); int difference = 0;
            for (int i = 0; i < 32; ++i) difference |= expected[i] ^ bytes[length + i];
            if (difference != 0) throw new InvalidDataException("Prison checksum mismatch; file preserved and admission must fail closed.");
            return body;
        }

        private static byte[] Hash(byte[] value) { using (var hash = SHA256.Create()) return hash.ComputeHash(value); }
        private static bool Equal(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; int diff = 0; for (int i = 0; i < left.Length; ++i) diff |= left[i] ^ right[i]; return diff == 0; }
        private static void WritePoint(BinaryWriter writer, PrisonPoint point) { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); }
        private static PrisonPoint ReadPoint(BinaryReader reader) { return new PrisonPoint(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()); }
        private static void WriteText(BinaryWriter writer, string value)
        { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader, int maximumCharacters)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximumCharacters * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid prison text length.");
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("Incomplete prison text.");
            string value = new UTF8Encoding(false, true).GetString(bytes);
            if (value.Length > maximumCharacters) throw new InvalidDataException("Oversized prison text.");
            return value;
        }

        private static void RequireHost(bool authenticatedCallerIsHost)
        { if (!authenticatedCallerIsHost) throw new UnauthorizedAccessException("Only the authenticated host may manage prison sentences."); }
        private void CheckOpen()
        { if (disposed) throw new ObjectDisposedException("SentenceStore"); if (Faulted) throw new IOException(FaultReason); }
        private static void RequireRegularPath(string file)
        {
            string current = Path.GetFullPath(file);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked prison state paths are not supported.");
                current = Path.GetDirectoryName(current);
            }
        }
        public void Dispose() {
            lock (timerGate) {
                lock (sync) { if (disposed) return; }
                try { FlushTimerLocked(); }
                finally { lock (sync) { disposed = true; processLock.Dispose(); } }
            }
        }
    }
}
