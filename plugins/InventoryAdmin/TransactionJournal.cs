using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    public enum InventoryTransactionStage
    {
        Submitted = 1, Prepared = 2, SourceRemoved = 3,
        DestinationApplied = 4, Committed = 5, Aborted = 6, NeedsRecovery = 7
    }

    public sealed class InventoryTransaction
    {
        public string Id = "", ActorOwner = "", SourceOwner = "", DestinationOwner = "", Digest = "", ItemHash = "", Note = "";
        public long World, SourceCharacter, DestinationCharacter, CreatedUtcTicks, UpdatedUtcTicks;
        public MutationRequest Request;
        public InventoryTransactionStage Stage, LastCertainStage;
        public byte[] ItemBytes = new byte[0];
        public InventoryTransaction Copy()
        {
            return new InventoryTransaction { Id = Id, ActorOwner = ActorOwner, SourceOwner = SourceOwner, DestinationOwner = DestinationOwner,
                Digest = Digest, ItemHash = ItemHash, Note = Note, World = World, SourceCharacter = SourceCharacter,
                DestinationCharacter = DestinationCharacter, CreatedUtcTicks = CreatedUtcTicks, UpdatedUtcTicks = UpdatedUtcTicks,
                Request = InventoryCodec.DecodeMutationRequest(InventoryCodec.EncodeMutationRequest(Request)), Stage = Stage,
                LastCertainStage = LastCertainStage, ItemBytes = (byte[])ItemBytes.Clone() };
        }
    }

    // Persisted escrow and receipts complement the native inventory operation
    // locks. This cannot turn a player-controlled client into a trusted server.
    // MarkSourceRemoved / MarkDestinationApplied require World Characters'
    // corresponding durable snapshot ACK, not merely a client's RPC success.
    public sealed class TransactionJournal : IDisposable
    {
        public const int MaximumActive = 128, MaximumReceipts = 8192;
        private readonly string root;
        private readonly FileStream processLock;
        private readonly Dictionary<string, InventoryTransaction> records = new Dictionary<string, InventoryTransaction>(StringComparer.Ordinal);
        private readonly object sync = new object();
        private bool disposed;

        public TransactionJournal(string directory)
        {
            root = Path.GetFullPath(directory); PolicyFiles.RequireRegularPath(root); Directory.CreateDirectory(root);
            string lockPath = Path.Combine(root, "transactions.lock"); PolicyFiles.RequireRegularPath(lockPath);
            processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                string[] files = Directory.GetFiles(root, "*.iatx");
                if (files.Length > MaximumReceipts) throw new InvalidDataException("Too many administration transaction receipts.");
                foreach (string file in files)
                {
                    string id = Path.GetFileNameWithoutExtension(file); PolicyBinary.RequireToken(id);
                    InventoryTransaction record = Read(file);
                    if (record.Id != id || records.ContainsKey(id)) throw new InvalidDataException("Transaction receipt identity mismatch.");
                    records.Add(id, record);
                }
                if (ActiveCount() > MaximumActive) throw new InvalidDataException("Too many unresolved administration transactions.");
                // After process loss even Prepared is ambiguous: the source may
                // have removed an item before its durable ACK reached this host.
                var pending = new List<InventoryTransaction>(records.Values);
                foreach (InventoryTransaction record in pending)
                {
                    if (Terminal(record.Stage) || record.Stage == InventoryTransactionStage.NeedsRecovery) continue;
                    var next = record.Copy(); next.Stage = InventoryTransactionStage.NeedsRecovery;
                    next.Note = "Host restarted before transaction completion; manual review required.";
                    next.UpdatedUtcTicks = DateTime.UtcNow.Ticks; Persist(next); records[next.Id] = next;
                }
            }
            catch { processLock.Dispose(); throw; }
        }

        public InventoryTransaction Begin(long world, string actorOwner, string sourceOwner, long sourceCharacter,
            string destinationOwner, long destinationCharacter, MutationRequest request)
        {
            InventoryCodec.ValidateMutation(request); Owner(actorOwner); Owner(sourceOwner);
            if (world == 0 || sourceCharacter == 0 || sourceCharacter != request.TargetCharacter)
                throw new InvalidDataException("Invalid transaction identity.");
            if (request.Operation == InventoryOperation.Take)
            {
                Owner(destinationOwner);
                if (destinationCharacter == 0 || (sourceOwner == destinationOwner && sourceCharacter == destinationCharacter))
                    throw new InvalidDataException("Invalid transfer destination.");
            }
            else if (!String.IsNullOrEmpty(destinationOwner) || destinationCharacter != 0)
                throw new InvalidDataException("Delete transactions have no destination.");
            long now = DateTime.UtcNow.Ticks;
            var candidate = new InventoryTransaction { Id = request.RequestId, World = world, ActorOwner = actorOwner, SourceOwner = sourceOwner,
                SourceCharacter = sourceCharacter, DestinationOwner = destinationOwner ?? "", DestinationCharacter = destinationCharacter,
                Request = InventoryCodec.DecodeMutationRequest(InventoryCodec.EncodeMutationRequest(request)), Stage = InventoryTransactionStage.Submitted,
                LastCertainStage = InventoryTransactionStage.Submitted, CreatedUtcTicks = now, UpdatedUtcTicks = now };
            candidate.Digest = RequestDigest(candidate);
            lock (sync)
            {
                CheckOpen(); InventoryTransaction existing;
                if (records.TryGetValue(candidate.Id, out existing))
                {
                    if (existing.Digest != candidate.Digest) throw new InvalidOperationException("Transaction identifier was reused with different content.");
                    return existing.Copy();
                }
                if (records.Count >= MaximumReceipts || ActiveCount() >= MaximumActive)
                    throw new InvalidOperationException("Transaction journal limit reached; administrator review is required.");
                Persist(candidate); records.Add(candidate.Id, candidate); return candidate.Copy();
            }
        }

        public InventoryTransaction Get(string id)
        { lock (sync) { CheckOpen(); return Require(id).Copy(); } }

        public InventoryTransaction[] Unresolved()
        {
            lock (sync)
            {
                CheckOpen(); var result = new List<InventoryTransaction>();
                foreach (InventoryTransaction record in records.Values) if (!Terminal(record.Stage)) result.Add(record.Copy());
                result.Sort(delegate(InventoryTransaction left, InventoryTransaction right) { return left.CreatedUtcTicks.CompareTo(right.CreatedUtcTicks); });
                return result.ToArray();
            }
        }

        public InventoryTransaction SetPrepared(string id, byte[] exactNativeItemData)
        {
            string fingerprint = InventoryCodec.Fingerprint(exactNativeItemData);
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Stage == InventoryTransactionStage.Prepared)
                {
                    if (current.ItemHash != fingerprint) throw new InvalidOperationException("Prepared escrow item changed.");
                    return current.Copy();
                }
                RequireStage(current, InventoryTransactionStage.Submitted);
                var next = current.Copy(); next.ItemBytes = (byte[])exactNativeItemData.Clone(); next.ItemHash = fingerprint;
                return SetStage(next, InventoryTransactionStage.Prepared, "");
            }
        }

        public InventoryTransaction MarkSourceRemoved(string id)
        { return Advance(id, InventoryTransactionStage.Prepared, InventoryTransactionStage.SourceRemoved); }
        public InventoryTransaction MarkDestinationApplied(string id)
        {
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Request.Operation != InventoryOperation.Take) throw new InvalidOperationException("Delete transactions cannot apply a destination.");
                return AdvanceLocked(current, InventoryTransactionStage.SourceRemoved, InventoryTransactionStage.DestinationApplied);
            }
        }

        public InventoryTransaction Commit(string id)
        {
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Stage == InventoryTransactionStage.Committed) return current.Copy();
                RequireStage(current, current.Request.Operation == InventoryOperation.Take
                    ? InventoryTransactionStage.DestinationApplied : InventoryTransactionStage.SourceRemoved);
                var next = current.Copy(); next.ItemBytes = new byte[0]; // Keep digest and item hash as replay/audit receipts.
                return SetStage(next, InventoryTransactionStage.Committed, "");
            }
        }

        // Caller can abort only after confirming that no source effect happened.
        // Timeout/disconnect at Prepared must call RequireRecovery instead.
        public InventoryTransaction Abort(string id, string reason)
        {
            PolicyBinary.RequireText(reason, 1024);
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Stage == InventoryTransactionStage.Aborted) return current.Copy();
                if (current.Stage != InventoryTransactionStage.Submitted && current.Stage != InventoryTransactionStage.Prepared)
                    throw new InvalidOperationException("An applied or ambiguous transaction cannot be aborted.");
                var next = current.Copy(); next.ItemBytes = new byte[0]; return SetStage(next, InventoryTransactionStage.Aborted, reason);
            }
        }

        public InventoryTransaction RequireRecovery(string id, string reason)
        {
            PolicyBinary.RequireText(reason, 1024);
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Stage == InventoryTransactionStage.NeedsRecovery) return current.Copy();
                if (Terminal(current.Stage)) throw new InvalidOperationException("Completed transactions cannot require recovery.");
                return SetStage(current.Copy(), InventoryTransactionStage.NeedsRecovery, reason);
            }
        }

        // An explicit host audit can release ONLY confirmed durable source
        // escrow for delivery. Unknown source removal or an acknowledged prior
        // destination effect must never be resolved by replaying a transfer.
        public InventoryTransaction ResumeDeliveryAfterReview(string id, bool authenticatedCallerIsHost,
            bool destinationNotAppliedConfirmed, string auditNote)
        {
            if (!authenticatedCallerIsHost) throw new UnauthorizedAccessException("Only the host may release reviewed escrow.");
            if (!destinationNotAppliedConfirmed) throw new InvalidOperationException("The host must explicitly confirm that delivery did not occur.");
            PolicyBinary.RequireText(auditNote, 1024);
            lock (sync)
            {
                CheckOpen(); InventoryTransaction current = Require(id);
                if (current.Stage != InventoryTransactionStage.NeedsRecovery || current.LastCertainStage != InventoryTransactionStage.SourceRemoved
                    || current.Request.Operation != InventoryOperation.Take)
                    throw new InvalidOperationException("Only escrow with confirmed durable source removal and no confirmed destination effect can resume delivery.");
                return SetStage(current.Copy(), InventoryTransactionStage.SourceRemoved, auditNote);
            }
        }

        private InventoryTransaction Advance(string id, InventoryTransactionStage expected, InventoryTransactionStage target)
        { lock (sync) { CheckOpen(); return AdvanceLocked(Require(id), expected, target); } }
        private InventoryTransaction AdvanceLocked(InventoryTransaction current, InventoryTransactionStage expected, InventoryTransactionStage target)
        {
            if (current.Stage == target) return current.Copy();
            RequireStage(current, expected); return SetStage(current.Copy(), target, "");
        }
        private InventoryTransaction SetStage(InventoryTransaction next, InventoryTransactionStage stage, string note)
        {
            next.Stage = stage; if (stage != InventoryTransactionStage.NeedsRecovery) next.LastCertainStage = stage;
            next.Note = note; next.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            Persist(next); records[next.Id] = next; return next.Copy();
        }
        private InventoryTransaction Require(string id)
        {
            PolicyBinary.RequireToken(id); InventoryTransaction record;
            if (!records.TryGetValue(id, out record)) throw new InvalidOperationException("Unknown transaction.");
            return record;
        }
        private int ActiveCount()
        { int count = 0; foreach (InventoryTransaction record in records.Values) if (!Terminal(record.Stage)) ++count; return count; }
        private static bool Terminal(InventoryTransactionStage stage)
        { return stage == InventoryTransactionStage.Committed || stage == InventoryTransactionStage.Aborted; }
        private static void RequireStage(InventoryTransaction record, InventoryTransactionStage stage)
        { if (record.Stage != stage) throw new InvalidOperationException("Unexpected transaction stage; mutation must not be replayed."); }
        private void CheckOpen() { if (disposed) throw new ObjectDisposedException("TransactionJournal"); }
        private static void Owner(string owner) { if (owner != "local-host") PermissionPolicy.RequireSteamOwner(owner); }

        private static string RequestDigest(InventoryTransaction record)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(record.World); writer.Write(record.SourceCharacter); writer.Write(record.DestinationCharacter);
                PolicyBinary.WriteString(writer, record.ActorOwner, 40); PolicyBinary.WriteString(writer, record.SourceOwner, 40);
                PolicyBinary.WriteString(writer, record.DestinationOwner, 40);
                PolicyBinary.WriteBytes(writer, InventoryCodec.EncodeMutationRequest(record.Request), 512); writer.Flush();
                return PolicyBinary.Hash(stream.ToArray());
            }
        }
        private void Persist(InventoryTransaction record)
        {
            Validate(record);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x49415431); writer.Write(1);
                PolicyBinary.WriteString(writer, record.Id, 32); writer.Write(record.World);
                PolicyBinary.WriteString(writer, record.ActorOwner, 40); PolicyBinary.WriteString(writer, record.SourceOwner, 40);
                PolicyBinary.WriteString(writer, record.DestinationOwner, 40); writer.Write(record.SourceCharacter); writer.Write(record.DestinationCharacter);
                PolicyBinary.WriteBytes(writer, InventoryCodec.EncodeMutationRequest(record.Request), 512);
                PolicyBinary.WriteString(writer, record.Digest, 64); PolicyBinary.WriteString(writer, record.ItemHash, 64);
                writer.Write((int)record.Stage); writer.Write((int)record.LastCertainStage);
                writer.Write(record.CreatedUtcTicks); writer.Write(record.UpdatedUtcTicks); PolicyBinary.WriteString(writer, record.Note, 1024);
                PolicyBinary.WriteBytes(writer, record.ItemBytes, InventoryCodec.MaximumItemBytes); writer.Flush();
                PolicyFiles.AtomicWrite(Path.Combine(root, record.Id + ".iatx"), PolicyBinary.Seal(stream.ToArray()));
            }
        }
        private static InventoryTransaction Read(string path)
        {
            PolicyFiles.RequireRegularPath(path);
            if (new FileInfo(path).Length > InventoryCodec.MaximumItemBytes + 4096) throw new InvalidDataException("Oversized transaction receipt.");
            byte[] body = PolicyBinary.Unseal(File.ReadAllBytes(path), InventoryCodec.MaximumItemBytes + 4096);
            using (var stream = new MemoryStream(body, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadInt32() != 0x49415431 || reader.ReadInt32() != 1) throw new InvalidDataException("Unknown transaction receipt format.");
                var record = new InventoryTransaction { Id = PolicyBinary.ReadString(reader, 32), World = reader.ReadInt64(),
                    ActorOwner = PolicyBinary.ReadString(reader, 40), SourceOwner = PolicyBinary.ReadString(reader, 40),
                    DestinationOwner = PolicyBinary.ReadString(reader, 40), SourceCharacter = reader.ReadInt64(), DestinationCharacter = reader.ReadInt64(),
                    Request = InventoryCodec.DecodeMutationRequest(PolicyBinary.ReadBytes(reader, 512)), Digest = PolicyBinary.ReadString(reader, 64),
                    ItemHash = PolicyBinary.ReadString(reader, 64), Stage = (InventoryTransactionStage)reader.ReadInt32(),
                    LastCertainStage = (InventoryTransactionStage)reader.ReadInt32(), CreatedUtcTicks = reader.ReadInt64(), UpdatedUtcTicks = reader.ReadInt64(),
                    Note = PolicyBinary.ReadString(reader, 1024), ItemBytes = PolicyBinary.ReadBytes(reader, InventoryCodec.MaximumItemBytes) };
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing transaction receipt data.");
                Validate(record); return record;
            }
        }
        private static void Validate(InventoryTransaction record)
        {
            PolicyBinary.RequireToken(record.Id); Owner(record.ActorOwner); Owner(record.SourceOwner); InventoryCodec.ValidateMutation(record.Request);
            PolicyBinary.RequireHash(record.Digest); PolicyBinary.RequireText(record.Note, 1024);
            if (record.Id != record.Request.RequestId || record.World == 0 || record.SourceCharacter == 0
                || record.SourceCharacter != record.Request.TargetCharacter || record.Digest != RequestDigest(record)
                || record.CreatedUtcTicks < 1 || record.UpdatedUtcTicks < record.CreatedUtcTicks || record.UpdatedUtcTicks > DateTime.MaxValue.Ticks
                || record.Stage < InventoryTransactionStage.Submitted || record.Stage > InventoryTransactionStage.NeedsRecovery
                || record.LastCertainStage < InventoryTransactionStage.Submitted || record.LastCertainStage > InventoryTransactionStage.Aborted
                || (record.Stage != InventoryTransactionStage.NeedsRecovery && record.Stage != record.LastCertainStage)
                || (record.Stage == InventoryTransactionStage.NeedsRecovery && Terminal(record.LastCertainStage)))
                throw new InvalidDataException("Invalid transaction receipt.");
            if (record.Request.Operation == InventoryOperation.Take)
            {
                Owner(record.DestinationOwner);
                if (record.DestinationCharacter == 0 || (record.SourceOwner == record.DestinationOwner && record.SourceCharacter == record.DestinationCharacter))
                    throw new InvalidDataException("Invalid transaction destination.");
            }
            else if (record.DestinationOwner.Length != 0 || record.DestinationCharacter != 0
                || record.Stage == InventoryTransactionStage.DestinationApplied || record.LastCertainStage == InventoryTransactionStage.DestinationApplied)
                throw new InvalidDataException("Invalid delete transaction.");
            InventoryTransactionStage certain = record.LastCertainStage;
            bool requiresEscrow = !Terminal(record.Stage) && certain >= InventoryTransactionStage.Prepared;
            if (requiresEscrow)
            {
                PolicyBinary.RequireHash(record.ItemHash);
                if (InventoryCodec.Fingerprint(record.ItemBytes) != record.ItemHash) throw new InvalidDataException("Escrow item checksum mismatch.");
            }
            else if (record.ItemBytes.Length != 0) throw new InvalidDataException("Unexpected escrow data.");
            if (record.ItemHash.Length > 0) PolicyBinary.RequireHash(record.ItemHash);
        }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; processLock.Dispose(); } }
    }
}
