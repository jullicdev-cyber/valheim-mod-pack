using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;

namespace ValheimModPack.PartyPrison
{
    public enum WithdrawalStage { Ready = 0, GrantPending = 1 }
    public sealed class WithdrawalRecord
    {
        public long World;
        public int ChestIndex;
        public string SentenceId = "", OriginalHash = "", PendingId = "", RecoveryReason = "";
        public byte[] OriginalPayload = new byte[0], RemainingPayload = new byte[0], PendingPayload = new byte[0], PendingRemainingPayload = new byte[0];
        public string[] CompletedIds = new string[0];
        public bool NeedsRecovery;
        public WithdrawalStage Stage { get { return PendingId.Length == 0 ? WithdrawalStage.Ready : WithdrawalStage.GrantPending; } }
        public WithdrawalRecord Copy()
        {
            return new WithdrawalRecord { World = World, ChestIndex = ChestIndex, SentenceId = SentenceId, OriginalHash = OriginalHash,
                PendingId = PendingId, RecoveryReason = RecoveryReason, OriginalPayload = (byte[])OriginalPayload.Clone(),
                RemainingPayload = (byte[])RemainingPayload.Clone(), PendingPayload = (byte[])PendingPayload.Clone(),
                PendingRemainingPayload = (byte[])PendingRemainingPayload.Clone(), CompletedIds = (string[])CompletedIds.Clone(), NeedsRecovery = NeedsRecovery };
        }
    }

    // Chest ZDOs display this durable ledger but never author its balance. The
    // host prepares a single item grant, the recipient durably saves that grant
    // with its receipt, and only then may the host remove it from this ledger.
    // A reconnect replays the same grant ID, never a fresh copy of that item.
    public sealed class CustodyWithdrawal : IDisposable
    {
        private const int Magic = 0x50505731, MaximumRecords = 16384, MaximumReceipts = 2048;
        private const int MaximumFileBytes = 4 * CustodyStore.MaximumPayloadBytes + 262144;
        private readonly string directory;
        private readonly long world;
        private readonly FileStream processLock;
        private readonly Dictionary<string, WithdrawalRecord> records = new Dictionary<string, WithdrawalRecord>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> diskHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool disposed;
        public bool Faulted { get; private set; }
        public string FaultReason { get; private set; }

        public CustodyWithdrawal(string root, long world)
        {
            if (world == 0 || String.IsNullOrWhiteSpace(root)) throw new InvalidDataException("Withdrawal ledger requires a world and directory.");
            this.world = world; directory = Path.Combine(Path.GetFullPath(root), "withdrawal-world-" + world.ToString("x16", CultureInfo.InvariantCulture));
            RegularPath(directory); Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, "withdrawal.lock"); RegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*.withdrawal"))
                {
                    if (records.Count >= MaximumRecords) throw new InvalidDataException("Too many withdrawal ledger records.");
                    byte[] bytes = BoundedRead(path); WithdrawalRecord value = Decode(bytes); string key = Key(value.SentenceId, value.ChestIndex);
                    if (value.World != world || Path.GetFileNameWithoutExtension(path) != key) throw new InvalidDataException("Withdrawal ledger identity mismatch.");
                    records.Add(key, value); diskHashes.Add(key, CustodyStore.Fingerprint(bytes));
                }
            }
            catch { processLock.Dispose(); throw; }
        }
        public void Ensure(CustodyRecord custody, byte[][] originalParts)
        {
            CheckOpen();
            if (custody == null || custody.World != world || custody.NeedsRecovery || custody.Stage != CustodyStage.Released)
                throw new InvalidOperationException("Only released, verified custody may create withdrawal balances.");
            CustodyStore.Token(custody.SentenceId); SentencePolicy.RequireAccountId(custody.AccountId);
            if (originalParts == null || originalParts.Length != 4) throw new InvalidDataException("Four immutable native chest payloads are required.");
            for (int i = 0; i < 4; ++i)
            {
                CustodyStore.Payload(originalParts[i]); WithdrawalRecord prior = Find(custody.SentenceId, i);
                if (prior != null && prior.OriginalHash != CustodyStore.Fingerprint(originalParts[i]))
                    throw new InvalidDataException("Withdrawal original chest contents changed.");
            }
            for (int i = 0; i < 4; ++i)
            {
                string key = Key(custody.SentenceId, i); if (records.ContainsKey(key)) continue;
                if (records.Count >= MaximumRecords) throw new InvalidOperationException("Withdrawal ledger history limit reached.");
                var value = new WithdrawalRecord { World = world, SentenceId = custody.SentenceId, ChestIndex = i,
                    OriginalHash = CustodyStore.Fingerprint(originalParts[i]), OriginalPayload = (byte[])originalParts[i].Clone(),
                    RemainingPayload = (byte[])originalParts[i].Clone() };
                Persist(value); records.Add(key, value);
            }
        }
        public WithdrawalRecord Find(string token, int index)
        { CheckOpen(); string key = Key(token, index); WithdrawalRecord value; return records.TryGetValue(key, out value) ? value.Copy() : null; }
        public WithdrawalRecord[] All(string token)
        {
            CheckOpen(); CustodyStore.Token(token); var result = new List<WithdrawalRecord>();
            for (int i = 0; i < 4; ++i) { WithdrawalRecord value = Find(token, i); if (value != null) result.Add(value); }
            return result.ToArray();
        }
        public WithdrawalRecord Begin(string token, int index, byte[] remainingAfter, byte[] itemPayload)
        {
            WithdrawalRecord value = Require(token, index); RequireHealthy(value);
            CustodyStore.Payload(remainingAfter); CustodyStore.Payload(itemPayload);
            if (value.PendingId.Length != 0)
            {
                if (CustodyStore.Fingerprint(value.PendingRemainingPayload) != CustodyStore.Fingerprint(remainingAfter)
                    || CustodyStore.Fingerprint(value.PendingPayload) != CustodyStore.Fingerprint(itemPayload))
                    throw new InvalidDataException("A pending withdrawal grant is immutable until its durable receipt is acknowledged.");
                return value.Copy();
            }
            if (value.CompletedIds.Length >= MaximumReceipts) throw new InvalidOperationException("Withdrawal receipt history limit reached.");
            if (CustodyStore.Fingerprint(value.RemainingPayload) == CustodyStore.Fingerprint(remainingAfter))
                throw new InvalidDataException("A withdrawal must change the remaining chest balance.");
            value.PendingId = Guid.NewGuid().ToString("N"); value.PendingRemainingPayload = (byte[])remainingAfter.Clone();
            value.PendingPayload = (byte[])itemPayload.Clone(); Persist(value); records[Key(token, index)] = value; return value.Copy();
        }
        public WithdrawalRecord Commit(string token, int index, string pendingId)
        {
            WithdrawalRecord value = Require(token, index); RequireHealthy(value); CustodyStore.Token(pendingId);
            foreach (string completed in value.CompletedIds) if (completed == pendingId) return value.Copy();
            if (value.PendingId != pendingId || value.PendingId.Length == 0) throw new InvalidDataException("Withdrawal ACK does not match the pending grant.");
            value.RemainingPayload = (byte[])value.PendingRemainingPayload.Clone();
            var completedIds = new List<string>(value.CompletedIds); completedIds.Add(pendingId); value.CompletedIds = completedIds.ToArray();
            value.PendingId = ""; value.PendingPayload = value.PendingRemainingPayload = new byte[0];
            Persist(value); records[Key(token, index)] = value; return value.Copy();
        }
        public WithdrawalRecord RequireRecovery(string token, int index, string reason)
        {
            WithdrawalRecord value = Require(token, index); SentencePolicy.RequireText(reason, 512, "withdrawal recovery reason");
            if (reason.Length == 0) throw new InvalidDataException("Withdrawal recovery requires a reason.");
            value.NeedsRecovery = true; value.RecoveryReason = reason; Persist(value); records[Key(token, index)] = value; return value.Copy();
        }
        private WithdrawalRecord Require(string token, int index)
        { WithdrawalRecord value = Find(token, index); if (value == null) throw new InvalidDataException("Unknown withdrawal chest ledger."); return value; }
        private static void RequireHealthy(WithdrawalRecord value)
        { if (value.NeedsRecovery) throw new InvalidOperationException("Withdrawal requires host recovery: " + value.RecoveryReason); }
        private static string Key(string token, int index)
        { CustodyStore.Token(token); if (index < 0 || index > 3) throw new InvalidDataException("Invalid withdrawal chest index."); return token + "-" + index.ToString(CultureInfo.InvariantCulture); }

        private void Persist(WithdrawalRecord value)
        {
            Validate(value); string key = Key(value.SentenceId, value.ChestIndex), path = Path.Combine(directory, key + ".withdrawal");
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); byte[] bytes = Encode(value);
            try
            {
                RegularPath(path); RegularPath(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                string known;
                if (diskHashes.TryGetValue(key, out known))
                {
                    if (!File.Exists(path) || CustodyStore.Fingerprint(BoundedRead(path)) != known)
                        throw new IOException("Withdrawal ledger changed externally; file preserved.");
                    string backup = path + ".previous"; RegularPath(backup); File.Replace(temporary, path, backup, true);
                }
                else
                {
                    if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Withdrawal ledger target already exists.");
                    File.Move(temporary, path);
                }
                diskHashes[key] = CustodyStore.Fingerprint(bytes);
            }
            catch (Exception e) { Faulted = true; FaultReason = "Withdrawal ledger save failed: " + e.Message; throw; }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static byte[] Encode(WithdrawalRecord value)
        {
            byte[] body;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true)))
            {
                writer.Write(Magic); writer.Write(1); writer.Write(value.World); Text(writer, value.SentenceId); writer.Write(value.ChestIndex);
                Text(writer, value.OriginalHash); Bytes(writer, value.OriginalPayload); Bytes(writer, value.RemainingPayload);
                Text(writer, value.PendingId); Bytes(writer, value.PendingPayload); Bytes(writer, value.PendingRemainingPayload);
                writer.Write(value.CompletedIds.Length); foreach (string id in value.CompletedIds) Text(writer, id);
                writer.Write(value.NeedsRecovery); Text(writer, value.RecoveryReason); writer.Flush(); body = stream.ToArray();
            }
            using (var stream = new MemoryStream())
            {
                byte[] hash = Hash(body); stream.Write(hash, 0, hash.Length); stream.Write(body, 0, body.Length);
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Withdrawal ledger exceeds its size limit."); return stream.ToArray();
            }
        }
        private static WithdrawalRecord Decode(byte[] bytes)
        {
            if (bytes.Length < 64 || bytes.Length > MaximumFileBytes) throw new InvalidDataException("Invalid withdrawal ledger size.");
            byte[] body = new byte[bytes.Length - 32]; Buffer.BlockCopy(bytes, 32, body, 0, body.Length);
            byte[] checksum = Hash(body); for (int i = 0; i < 32; ++i) if (checksum[i] != bytes[i]) throw new InvalidDataException("Withdrawal ledger checksum mismatch; file preserved.");
            using (var stream = new MemoryStream(body, false)) using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported withdrawal ledger format.");
                var value = new WithdrawalRecord { World = reader.ReadInt64(), SentenceId = Text(reader, 32), ChestIndex = reader.ReadInt32(),
                    OriginalHash = Text(reader, 64), OriginalPayload = Bytes(reader), RemainingPayload = Bytes(reader),
                    PendingId = Text(reader, 32), PendingPayload = Bytes(reader), PendingRemainingPayload = Bytes(reader) };
                int count = reader.ReadInt32(); if (count < 0 || count > MaximumReceipts) throw new InvalidDataException("Invalid withdrawal receipt count.");
                value.CompletedIds = new string[count]; for (int i = 0; i < count; ++i) value.CompletedIds[i] = Text(reader, 32);
                byte flag = reader.ReadByte(); if (flag > 1) throw new InvalidDataException("Invalid withdrawal recovery flag.");
                value.NeedsRecovery = flag == 1; value.RecoveryReason = Text(reader, 512);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing withdrawal ledger data."); Validate(value); return value;
            }
        }
        private static void Validate(WithdrawalRecord value)
        {
            Key(value.SentenceId, value.ChestIndex); CustodyStore.Payload(value.OriginalPayload); CustodyStore.Payload(value.RemainingPayload);
            SentencePolicy.RequireText(value.RecoveryReason, 512, "withdrawal recovery reason");
            if (value.World == 0 || value.OriginalHash != CustodyStore.Fingerprint(value.OriginalPayload)
                || value.CompletedIds == null || value.CompletedIds.Length > MaximumReceipts
                || value.NeedsRecovery != (value.RecoveryReason.Length != 0)) throw new InvalidDataException("Invalid withdrawal ledger state.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in value.CompletedIds) { CustodyStore.Token(id); if (!ids.Add(id)) throw new InvalidDataException("Duplicate withdrawal receipt."); }
            if (value.PendingId.Length == 0)
            { if (value.PendingPayload.Length != 0 || value.PendingRemainingPayload.Length != 0) throw new InvalidDataException("Unidentified withdrawal grant."); }
            else
            {
                CustodyStore.Token(value.PendingId); CustodyStore.Payload(value.PendingPayload); CustodyStore.Payload(value.PendingRemainingPayload);
                if (ids.Contains(value.PendingId) || CustodyStore.Fingerprint(value.RemainingPayload) == CustodyStore.Fingerprint(value.PendingRemainingPayload))
                    throw new InvalidDataException("Invalid pending withdrawal grant.");
            }
        }
        private static byte[] Hash(byte[] bytes) { using (var sha = System.Security.Cryptography.SHA256.Create()) return sha.ComputeHash(bytes); }
        private static void Bytes(BinaryWriter writer, byte[] value) { writer.Write(value.Length); writer.Write(value); }
        private static byte[] Bytes(BinaryReader reader)
        {
            int size = reader.ReadInt32(); if (size < 0 || size > CustodyStore.MaximumPayloadBytes || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid withdrawal payload size.");
            return reader.ReadBytes(size);
        }
        private static void Text(BinaryWriter writer, string value) { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
        private static string Text(BinaryReader reader, int limit)
        {
            int size = reader.ReadInt32(); if (size < 0 || size > limit * 4 || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid withdrawal text size.");
            string value = new UTF8Encoding(false, true).GetString(reader.ReadBytes(size)); if (value.Length > limit) throw new InvalidDataException("Oversized withdrawal text."); return value;
        }
        private static byte[] BoundedRead(string path)
        { RegularPath(path); if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("Oversized withdrawal ledger."); return File.ReadAllBytes(path); }
        private static void RegularPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked withdrawal ledger paths are unsupported."); current = Path.GetDirectoryName(current); }
        }
        private void CheckOpen() { if (disposed) throw new ObjectDisposedException("CustodyWithdrawal"); if (Faulted) throw new IOException(FaultReason); }
        public void Dispose() { if (disposed) return; disposed = true; processLock.Dispose(); }
    }
}
