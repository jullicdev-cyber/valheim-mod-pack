using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ValheimModPack.PartyPrison
{
    public enum CustodyStage { Prepared = 1, Cleared = 2, Deposited = 3, Released = 4, Collected = 5 }

    public sealed class CustodyRecord
    {
        public string AccountId = "", SentenceId = "", PayloadHash = "", RecoveryReason = "";
        public long World, ClearSequence;
        public CustodyStage Stage;
        public byte[] OriginalPayload = new byte[0];
        public string[] ChestIds = new string[0];
        public bool NeedsRecovery;
        public CustodyRecord Copy()
        {
            return new CustodyRecord { AccountId = AccountId, SentenceId = SentenceId, PayloadHash = PayloadHash,
                RecoveryReason = RecoveryReason, World = World, ClearSequence = ClearSequence, Stage = Stage,
                OriginalPayload = (byte[])OriginalPayload.Clone(), ChestIds = (string[])ChestIds.Clone(), NeedsRecovery = NeedsRecovery };
        }
    }

    // Original item bytes remain immutable, including after collection. A crash
    // never licenses replay of a released chest. Ambiguous world recovery closes
    // admission/access and leaves the journal intact for explicit host recovery.
    public sealed class CustodyStore : IDisposable
    {
        public const int MaximumPayloadBytes = 4 * 1024 * 1024;
        private const int Magic = 0x50504332, MaximumRecords = 4096;
        private readonly string directory;
        private readonly long world;
        private readonly FileStream processLock;
        private readonly Dictionary<string, CustodyRecord> records = new Dictionary<string, CustodyRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool disposed;
        public bool Faulted { get; private set; }
        public string FaultReason { get; private set; }

        public CustodyStore(string root, long world)
        {
            if (world == 0 || String.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Custody requires world identity and a journal directory.");
            this.world = world;
            directory = Path.Combine(Path.GetFullPath(root), "custody-world-" + world.ToString("x16", CultureInfo.InvariantCulture));
            RegularPath(directory); Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, "journal.lock"); RegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*.custody"))
                {
                    if (records.Count >= MaximumRecords) throw new InvalidDataException("Too many custody journal records.");
                    RegularPath(path);
                    byte[] bytes = BoundedRead(path);
                    CustodyRecord record = Decode(bytes);
                    if (record.World != world || Path.GetFileNameWithoutExtension(path) != record.SentenceId)
                        throw new InvalidDataException("Custody journal identity mismatch.");
                    records.Add(record.SentenceId, record); hashes.Add(record.SentenceId, Fingerprint(bytes));
                }
            }
            catch { processLock.Dispose(); throw; }
        }

        public CustodyRecord Find(string account, string token)
        {
            CheckOpen(); SentencePolicy.RequireAccountId(account); Token(token);
            CustodyRecord value;
            return records.TryGetValue(token, out value) && value.AccountId == account ? value.Copy() : null;
        }
        public CustodyRecord Find(string account)
        {
            CheckOpen(); SentencePolicy.RequireAccountId(account);
            foreach (CustodyRecord record in records.Values)
                if (record.AccountId == account && (record.Stage != CustodyStage.Collected || record.NeedsRecovery)) return record.Copy();
            return null;
        }
        public CustodyRecord[] All()
        {
            CheckOpen(); var keys = new List<string>(records.Keys); keys.Sort(StringComparer.Ordinal);
            var result = new CustodyRecord[keys.Count];
            for (int i = 0; i < keys.Count; ++i) result[i] = records[keys[i]].Copy();
            return result;
        }
        public bool CanAdmit(string account)
        { return Find(account) == null; }
        public bool HasOutstanding
        {
            get { CheckOpen(); foreach (CustodyRecord r in records.Values) if (r.Stage != CustodyStage.Collected || r.NeedsRecovery) return true; return false; }
        }

        public CustodyRecord Prepare(string account, string token, byte[] originalPayload)
        {
            CheckOpen(); SentencePolicy.RequireAccountId(account); Token(token); Payload(originalPayload);
            string hash = Fingerprint(originalPayload); CustodyRecord old;
            if (records.TryGetValue(token, out old))
            {
                if (old.AccountId != account || old.PayloadHash != hash)
                    throw new InvalidDataException("Custody token cannot be rebound or its original items changed.");
                return old.Copy();
            }
            // There are exactly four physical chests, so another unresolved
            // custody must finish before these chests can receive another owner.
            if (HasOutstanding) throw new InvalidOperationException("Collect the previous prison belongings before admitting another prisoner.");
            if (records.Count >= MaximumRecords) throw new InvalidOperationException("Custody journal history limit reached.");
            var value = new CustodyRecord { AccountId = account, SentenceId = token, World = world,
                Stage = CustodyStage.Prepared, OriginalPayload = (byte[])originalPayload.Clone(), PayloadHash = hash };
            Persist(value); records.Add(token, value); return value.Copy();
        }

        public CustodyRecord MarkCleared(string account, string token, long durableSequence)
        {
            CustodyRecord value = Require(account, token);
            if (durableSequence <= 0) throw new InvalidDataException("A durable character save must confirm confiscation.");
            if (value.Stage >= CustodyStage.Cleared)
            {
                if (value.ClearSequence != durableSequence) throw new InvalidDataException("Confiscation ACK sequence changed.");
                return value.Copy();
            }
            RequireStage(value, CustodyStage.Prepared); value.ClearSequence = durableSequence;
            return Advance(value, CustodyStage.Cleared);
        }
        public CustodyRecord MarkDeposited(string account, string token, string[] chestIds)
        {
            CustodyRecord value = Require(account, token); Chests(chestIds);
            if (value.Stage >= CustodyStage.Deposited)
            {
                for (int i = 0; i < 4; ++i) if (value.ChestIds[i] != chestIds[i]) throw new InvalidDataException("Custody chest identity changed.");
                return value.Copy();
            }
            RequireStage(value, CustodyStage.Cleared); value.ChestIds = (string[])chestIds.Clone();
            return Advance(value, CustodyStage.Deposited);
        }
        public CustodyRecord Release(string account, string token)
        {
            CustodyRecord value = Require(account, token);
            if (value.Stage >= CustodyStage.Released) return value.Copy();
            RequireStage(value, CustodyStage.Deposited); return Advance(value, CustodyStage.Released);
        }
        public CustodyRecord MarkCollected(string account, string token)
        {
            CustodyRecord value = Require(account, token);
            if (value.Stage == CustodyStage.Collected) return value.Copy();
            RequireStage(value, CustodyStage.Released); return Advance(value, CustodyStage.Collected);
        }
        public CustodyRecord RequireRecovery(string account, string token, string reason)
        {
            CustodyRecord value = Find(account, token);
            if (value == null) throw new InvalidDataException("Unknown custody account or token.");
            SentencePolicy.RequireText(reason, 512, "custody recovery reason");
            if (reason.Length == 0) throw new InvalidDataException("Custody recovery requires a reason.");
            value.NeedsRecovery = true; value.RecoveryReason = reason; Persist(value); records[token] = value; return value.Copy();
        }

        private CustodyRecord Advance(CustodyRecord value, CustodyStage next)
        {
            if (value.NeedsRecovery) throw new InvalidOperationException("Custody needs host recovery: " + value.RecoveryReason);
            value.Stage = next; Persist(value); records[value.SentenceId] = value; return value.Copy();
        }
        private CustodyRecord Require(string account, string token)
        {
            CustodyRecord value = Find(account, token);
            if (value == null) throw new InvalidDataException("Unknown custody account or token.");
            if (value.NeedsRecovery) throw new InvalidOperationException("Custody needs host recovery: " + value.RecoveryReason);
            return value;
        }
        private static void RequireStage(CustodyRecord value, CustodyStage expected)
        { if (value.Stage != expected) throw new InvalidOperationException("Invalid custody transition."); }

        private void Persist(CustodyRecord value)
        {
            Validate(value); byte[] bytes = Encode(value); string path = Path.Combine(directory, value.SentenceId + ".custody");
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                RegularPath(path); RegularPath(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                string known;
                if (hashes.TryGetValue(value.SentenceId, out known))
                {
                    if (!File.Exists(path) || Fingerprint(BoundedRead(path)) != known)
                        throw new IOException("Custody journal changed externally; file preserved.");
                    string backup = path + ".previous"; RegularPath(backup);
                    File.Replace(temporary, path, backup, true);
                }
                else
                {
                    if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Custody journal target already exists.");
                    File.Move(temporary, path);
                }
                hashes[value.SentenceId] = Fingerprint(bytes);
            }
            catch (Exception e) { Faulted = true; FaultReason = "Custody journal save failed: " + e.Message; throw; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static byte[] Encode(CustodyRecord value)
        {
            byte[] body;
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                w.Write(Magic); w.Write(1); w.Write(value.World); Text(w, value.AccountId); Text(w, value.SentenceId);
                w.Write((int)value.Stage); w.Write(value.ClearSequence); w.Write(value.NeedsRecovery); Text(w, value.RecoveryReason);
                Text(w, value.PayloadHash); w.Write(value.OriginalPayload.Length); w.Write(value.OriginalPayload);
                w.Write(value.ChestIds.Length); foreach (string id in value.ChestIds) Text(w, id);
                w.Flush(); body = stream.ToArray();
            }
            using (var stream = new MemoryStream()) { byte[] hash = Hash(body); stream.Write(hash, 0, hash.Length); stream.Write(body, 0, body.Length); return stream.ToArray(); }
        }
        private static CustodyRecord Decode(byte[] bytes)
        {
            if (bytes.Length < 64 || bytes.Length > MaximumPayloadBytes + 8192) throw new InvalidDataException("Invalid custody journal size.");
            byte[] body = new byte[bytes.Length - 32]; Buffer.BlockCopy(bytes, 32, body, 0, body.Length);
            byte[] hash = Hash(body); for (int i = 0; i < 32; ++i) if (hash[i] != bytes[i]) throw new InvalidDataException("Custody journal checksum mismatch; file preserved.");
            using (var stream = new MemoryStream(body, false)) using (var r = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (r.ReadInt32() != Magic || r.ReadInt32() != 1) throw new InvalidDataException("Unsupported custody journal format.");
                var value = new CustodyRecord { World = r.ReadInt64(), AccountId = Text(r, 64), SentenceId = Text(r, 32),
                    Stage = (CustodyStage)r.ReadInt32(), ClearSequence = r.ReadInt64() };
                byte flag = r.ReadByte(); if (flag > 1) throw new InvalidDataException("Invalid custody recovery flag.");
                value.NeedsRecovery = flag == 1; value.RecoveryReason = Text(r, 512); value.PayloadHash = Text(r, 64);
                int size = r.ReadInt32(); if (size <= 0 || size > MaximumPayloadBytes || size > stream.Length - stream.Position) throw new InvalidDataException("Invalid custody payload size.");
                value.OriginalPayload = r.ReadBytes(size); int count = r.ReadInt32();
                if (count != 0 && count != 4) throw new InvalidDataException("Custody needs exactly four chests.");
                value.ChestIds = new string[count]; for (int i = 0; i < count; ++i) value.ChestIds[i] = Text(r, 128);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing custody journal data.");
                Validate(value); return value;
            }
        }
        private static void Validate(CustodyRecord value)
        {
            SentencePolicy.RequireAccountId(value.AccountId); Token(value.SentenceId); Payload(value.OriginalPayload);
            SentencePolicy.RequireText(value.RecoveryReason, 512, "custody recovery reason");
            if (value.World == 0 || value.PayloadHash != Fingerprint(value.OriginalPayload)
                || value.Stage < CustodyStage.Prepared || value.Stage > CustodyStage.Collected
                || (value.Stage == CustodyStage.Prepared ? value.ClearSequence != 0 : value.ClearSequence <= 0)
                || value.NeedsRecovery != (value.RecoveryReason.Length != 0)) throw new InvalidDataException("Invalid custody state.");
            if (value.Stage >= CustodyStage.Deposited) Chests(value.ChestIds);
            else if (value.ChestIds.Length != 0) throw new InvalidDataException("Unexpected custody chest identities.");
        }
        private static void Chests(string[] ids)
        {
            if (ids == null || ids.Length != 4) throw new InvalidDataException("Custody requires four chest identities.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids) { SentencePolicy.RequireText(id, 128, "custody chest identity"); if (id.Length == 0 || !seen.Add(id)) throw new InvalidDataException("Duplicate or missing custody chest identity."); }
        }
        internal static void Token(string token)
        { Guid id; if (token == null || !Guid.TryParseExact(token, "N", out id) || id == Guid.Empty || token != id.ToString("N")) throw new InvalidDataException("Invalid custody token."); }
        internal static void Payload(byte[] bytes)
        { if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("Custody payload exceeds its size limit."); }
        public static string Fingerprint(byte[] bytes)
        { if (bytes == null) throw new ArgumentNullException("bytes"); return BitConverter.ToString(Hash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static byte[] Hash(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        private static void Text(BinaryWriter w, string value) { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); w.Write(bytes.Length); w.Write(bytes); }
        private static string Text(BinaryReader r, int limit)
        {
            int size = r.ReadInt32(); if (size < 0 || size > limit * 4 || size > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid custody text size.");
            string value = new UTF8Encoding(false, true).GetString(r.ReadBytes(size)); if (value.Length > limit) throw new InvalidDataException("Oversized custody text."); return value;
        }
        private static byte[] BoundedRead(string path)
        { RegularPath(path); if (new FileInfo(path).Length > MaximumPayloadBytes + 8192) throw new InvalidDataException("Oversized custody journal."); return File.ReadAllBytes(path); }
        private static void RegularPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked custody journal paths are unsupported."); current = Path.GetDirectoryName(current); }
        }
        private void CheckOpen() { if (disposed) throw new ObjectDisposedException("CustodyStore"); if (Faulted) throw new IOException(FaultReason); }
        public void Dispose() { if (disposed) return; disposed = true; processLock.Dispose(); }
    }
}
