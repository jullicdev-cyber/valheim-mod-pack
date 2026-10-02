using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ValheimModPack.InventoryAdmin
{
    internal static class PolicyTests
    {
        private const string Alice = "Steam_76561198000000001", Bob = "Steam_76561198000000002", Carol = "Steam_76561198000000003";
        private static int assertions;
        private static string Token() { return Guid.NewGuid().ToString("N"); }
        private static string Fingerprint() { return InventoryCodec.Fingerprint(new byte[] { 1, 2, 3 }); }
        private static void Check(bool okay, string description)
        { ++assertions; if (!okay) throw new Exception("FAIL: " + description); }
        private static void Reject(Action action, string description)
        {
            ++assertions;
            try { action(); }
            catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; }
            catch (UnauthorizedAccessException) { return; }
            catch (ArgumentException) { return; }
            catch (IOException) { return; }
            throw new Exception("FAIL: accepted " + description);
        }
        private static MutationRequest Request(InventoryOperation operation)
        {
            return new MutationRequest { RequestId = Token(), ViewRequestId = Token(), SlotId = Token(), Fingerprint = Fingerprint(),
                TargetPeerId = 81, TargetCharacter = 11, Revision = 8, Count = 3, Operation = operation };
        }
        private static InventoryView View(MutationRequest request)
        {
            return new InventoryView { RequestId = request.ViewRequestId, TargetPeerId = request.TargetPeerId, TargetCharacter = request.TargetCharacter,
                Revision = request.Revision, TargetOwner = Bob, Items = new List<ItemInfo> {
                    new ItemInfo { SlotId = request.SlotId, Fingerprint = request.Fingerprint, Prefab = "SwordSilver", Name = "$item_swordsilver",
                        Stack = 5, Quality = 4, Variant = 1, Durability = 188.25f, X = 7, Y = 5, Equipped = true, Container = "Equipment" } } };
        }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Expected test state directory.");
                Directory.CreateDirectory(args[0]);
                Permissions(Path.Combine(args[0], "permissions"));
                Protocol();
                Transactions(Path.Combine(args[0], "transactions"));
                CrashRecovery(Path.Combine(args[0], "crash"));
                JournalWriteFailure(Path.Combine(args[0], "writefailure"));
                Limits(Path.Combine(args[0], "limits"));
                Console.WriteLine("InventoryAdmin policy/protocol/transaction checks passed: " + assertions);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void Permissions(string root)
        {
            using (var store = new PermissionStore(root))
            {
                Check(PermissionPolicy.CanInspect(72, true, "local-host", store), "host intrinsic permission");
                Check(!PermissionPolicy.CanInspect(72, false, Alice, store), "remote default deny");
                Check(!PermissionPolicy.CanInspect(0, true, "local-host", store), "no permission before world identity");
                Reject(delegate { store.SetAdministrator(72, false, Alice, true); }, "remote grant attempt");
                Check(!store.IsAdministrator(72, Alice), "failed grant remains absent");
                Check(store.SetAdministrator(72, true, Alice, true), "host grant");
                Check(!store.SetAdministrator(72, true, Alice, true), "duplicate grant is no-op");
                Check(PermissionPolicy.CanInspect(72, false, Alice, store), "granted identity allowed");
                Check(!PermissionPolicy.CanInspect(73, false, Alice, store), "grant limited to world");
                Check(!PermissionPolicy.CanInspect(72, false, Bob, store), "other identity denied");
                Check(!PermissionPolicy.CanInspect(72, false, "Alice", store), "character name is not authority");
                Check(!PermissionPolicy.CanInspect(72, false, "Steam_00000000000000000", store), "zero identity denied");
                Reject(delegate { store.SetAdministrator(0, true, Alice, true); }, "zero-world grant");
                Reject(delegate { store.SetAdministrator(72, true, "local-host", true); }, "local host in delegated grant list");
                Reject(delegate { store.SetAdministrator(72, true, "Steam_7656119800000000x", true); }, "non-numeric Steam identity");
                Reject(delegate { using (var competing = new PermissionStore(root)) { } }, "competing process store");
                string[] admins = store.Administrators(72); Check(admins.Length == 1 && admins[0] == Alice, "administrator listing restricted world");
                admins[0] = Bob; Check(!store.IsAdministrator(72, Bob), "caller cannot mutate stored roles via list");
            }
            using (var store = new PermissionStore(root))
            {
                Check(store.IsAdministrator(72, Alice), "grant persisted on restart");
                Check(store.SetAdministrator(72, true, Alice, false), "host revoke");
                Check(!PermissionPolicy.CanInspect(72, false, Alice, store), "revoke immediate");
                Check(!store.SetAdministrator(72, true, Alice, false), "duplicate revoke no-op");
            }
            using (var store = new PermissionStore(root)) Check(!store.IsAdministrator(72, Alice), "revoke persisted");
            string file = Path.Combine(root, "administrators.dat"); byte[] bytes = File.ReadAllBytes(file); bytes[4] ^= 1; File.WriteAllBytes(file, bytes);
            Reject(delegate { using (var invalid = new PermissionStore(root)) { } }, "corrupt permissions fail closed");
            string broken = root + "-failure";
            using (var store = new PermissionStore(broken))
            {
                Directory.CreateDirectory(Path.Combine(broken, "administrators.dat"));
                Reject(delegate { store.SetAdministrator(72, true, Alice, true); }, "permission persist failure");
                Check(!store.IsAdministrator(72, Alice), "failed disk write cannot grant memory role");
                Check(Directory.GetFiles(broken, "*.tmp-*").Length == 0, "permission failed write cleans staging");
            }
        }
        private static void Protocol()
        {
            var request = Request(InventoryOperation.Take); var view = View(request);
            byte[] encoded = InventoryCodec.EncodeInventoryView(view); InventoryView decoded = InventoryCodec.DecodeInventoryView(encoded);
            Check(decoded.RequestId == view.RequestId && decoded.TargetOwner == Bob && decoded.Revision == 8, "view identity round-trip");
            ItemInfo item = decoded.Items[0]; Check(item.Equipped && item.Quality == 4 && item.Durability == 188.25f && item.X == 7 && item.Y == 5, "view details round-trip");
            Check(InventoryCodec.RequireCurrentSelection(decoded, request).Stack == 5, "matching current selection allowed");
            byte[] mutation = InventoryCodec.EncodeMutationRequest(request); var copied = InventoryCodec.DecodeMutationRequest(mutation);
            Check(copied.RequestId == request.RequestId && copied.Operation == InventoryOperation.Take && copied.Count == 3, "mutation round-trip");
            var players = new List<PlayerInfo> {
                new PlayerInfo { PeerId = 81, CharacterId = 11, OwnerId = Bob, Name = "Игрок", IsAdmin = true, InventoryAvailable = true },
                new PlayerInfo { PeerId = 0, CharacterId = 12, OwnerId = "local-host", Name = "Хост", InventoryAvailable = true } };
            var roster = InventoryCodec.DecodePlayers(InventoryCodec.EncodePlayers(players));
            Check(roster.Count == 2 && roster[0].Name == "Игрок" && roster[0].IsAdmin && roster[1].OwnerId == "local-host", "authenticated roster round-trip");
            byte[] badBoolean = InventoryCodec.EncodePlayers(players); badBoolean[badBoolean.Length - 1] = 2;
            Reject(delegate { InventoryCodec.DecodePlayers(badBoolean); }, "noncanonical wire boolean");
            byte[] badRosterCount = InventoryCodec.EncodePlayers(players); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, badRosterCount, 12, 4);
            Reject(delegate { InventoryCodec.DecodePlayers(badRosterCount); }, "unbounded advertised roster count");
            byte[] badRosterText = InventoryCodec.EncodePlayers(players); Buffer.BlockCopy(BitConverter.GetBytes(Int32.MaxValue), 0, badRosterText, 32, 4);
            Reject(delegate { InventoryCodec.DecodePlayers(badRosterText); }, "unbounded advertised text count");
            players.Add(players[0]); Reject(delegate { InventoryCodec.EncodePlayers(players); }, "duplicate peer roster");
            players.RemoveAt(2); players[0].CharacterId = 0; Reject(delegate { InventoryCodec.EncodePlayers(players); }, "available roster missing character");
            players[0].InventoryAvailable = false; Check(InventoryCodec.DecodePlayers(InventoryCodec.EncodePlayers(players))[0].CharacterId == 0, "joining peer not yet inventory available");
            foreach (int length in new[] { 0, 1, 11, encoded.Length - 1 })
            {
                int captured = length; Reject(delegate { InventoryCodec.DecodeInventoryView(Sub(encoded, captured)); }, "truncated view " + length);
            }
            for (int i = 0; i < mutation.Length; ++i)
            {
                int length = i; Reject(delegate { InventoryCodec.DecodeMutationRequest(Sub(mutation, length)); }, "truncated mutation " + i);
            }
            Reject(delegate { InventoryCodec.DecodeInventoryView(Append(encoded, 0)); }, "trailing view bytes");
            Reject(delegate { InventoryCodec.DecodeMutationRequest(Append(mutation, 0)); }, "trailing mutation bytes");
            byte[] wrongMagic = (byte[])mutation.Clone(); wrongMagic[0] ^= 1;
            Reject(delegate { InventoryCodec.DecodeMutationRequest(wrongMagic); }, "unknown magic");
            byte[] wrongVersion = (byte[])mutation.Clone(); wrongVersion[4] = 2;
            Reject(delegate { InventoryCodec.DecodeMutationRequest(wrongVersion); }, "future protocol version");
            Reject(delegate { InventoryCodec.DecodePlayers(encoded); }, "wrong message type");
            var stale = InventoryCodec.DecodeMutationRequest(mutation); stale.Revision += 1;
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "stale revision");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.ViewRequestId = Token();
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "stale request token");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.TargetPeerId++;
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "another peer");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.TargetCharacter++;
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "another character");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.Fingerprint = new string('a', 64);
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "changed native item fingerprint");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.Count = 6;
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "count exceeds current stack");
            stale = InventoryCodec.DecodeMutationRequest(mutation); stale.SlotId = Token();
            Reject(delegate { InventoryCodec.RequireCurrentSelection(view, stale); }, "missing stable item slot");
            foreach (int invalid in new[] { -1, 0, 65536 })
            {
                var bad = InventoryCodec.DecodeMutationRequest(mutation); bad.Count = invalid;
                Reject(delegate { InventoryCodec.EncodeMutationRequest(bad); }, "invalid stack count " + invalid);
            }
            var unknownOp = InventoryCodec.DecodeMutationRequest(mutation); unknownOp.Operation = (InventoryOperation)3;
            Reject(delegate { InventoryCodec.EncodeMutationRequest(unknownOp); }, "unknown operation");
            var badRevision = InventoryCodec.DecodeMutationRequest(mutation); badRevision.Revision = -1;
            Reject(delegate { InventoryCodec.EncodeMutationRequest(badRevision); }, "negative revision");
            var invalidHash = InventoryCodec.DecodeMutationRequest(mutation); invalidHash.Fingerprint = new string('A', 64);
            Reject(delegate { InventoryCodec.EncodeMutationRequest(invalidHash); }, "noncanonical fingerprint");
            var emptyId = InventoryCodec.DecodeMutationRequest(mutation); emptyId.RequestId = new string('0', 32);
            Reject(delegate { InventoryCodec.EncodeMutationRequest(emptyId); }, "empty identifier");
            view.Items.Add(view.Items[0]); Reject(delegate { InventoryCodec.EncodeInventoryView(view); }, "duplicate item slot"); view.Items.RemoveAt(1);
            view.Items[0].Durability = Single.NaN; Reject(delegate { InventoryCodec.EncodeInventoryView(view); }, "nonfinite durability"); view.Items[0].Durability = 1;
            view.Items[0].Name = new string('Я', 257); Reject(delegate { InventoryCodec.EncodeInventoryView(view); }, "UTF8 byte bound"); view.Items[0].Name = "Wood";
            view.Items[0].Y = 256; Reject(delegate { InventoryCodec.EncodeInventoryView(view); }, "oversized slot position"); view.Items[0].Y = 5;
            Reject(delegate { InventoryCodec.DecodeInventoryView(new byte[InventoryCodec.MaximumViewBytes + 1]); }, "oversized packet");
            byte[] one = Encoding.UTF8.GetBytes("opaque customData backpack recipe quality crafter"); byte[] two = (byte[])one.Clone(); two[two.Length - 1] ^= 1;
            Check(InventoryCodec.Fingerprint(one) != InventoryCodec.Fingerprint(two), "complete opaque mod data participates in item fingerprint");
            Reject(delegate { InventoryCodec.Fingerprint(new byte[0]); }, "empty native item data");
        }
        private static void Transactions(string root)
        {
            var take = Request(InventoryOperation.Take); byte[] parcel = Encoding.UTF8.GetBytes("exact native item quality customData backpack crafter durability");
            using (var journal = new TransactionJournal(root))
            {
                InventoryTransaction current = journal.Begin(72, Alice, Bob, 11, Alice, 12, take);
                Check(current.Stage == InventoryTransactionStage.Submitted && current.ItemBytes.Length == 0, "submit before effect");
                Check(journal.Begin(72, Alice, Bob, 11, Alice, 12, take).Id == current.Id, "duplicate request idempotent");
                Reject(delegate { journal.Begin(73, Alice, Bob, 11, Alice, 12, take); }, "request replay in another world");
                Reject(delegate { journal.Begin(72, Carol, Bob, 11, Alice, 12, take); }, "request replay from another actor");
                var altered = InventoryCodec.DecodeMutationRequest(InventoryCodec.EncodeMutationRequest(take)); altered.Count++;
                Reject(delegate { journal.Begin(72, Alice, Bob, 11, Alice, 12, altered); }, "conflicting reused request id");
                Reject(delegate { journal.MarkSourceRemoved(take.RequestId); }, "remove before prepare");
                Reject(delegate { journal.Commit(take.RequestId); }, "commit before effects");
                current = journal.SetPrepared(take.RequestId, parcel); Check(current.Stage == InventoryTransactionStage.Prepared && Equal(current.ItemBytes, parcel), "persist exact prepared escrow");
                parcel[0] ^= 1; Check(current.ItemBytes[0] != parcel[0], "prepared escrow owns copy"); parcel[0] ^= 1;
                Check(journal.SetPrepared(take.RequestId, parcel).Stage == InventoryTransactionStage.Prepared, "duplicate identical preparation idempotent");
                Reject(delegate { journal.SetPrepared(take.RequestId, new byte[] { 99 }); }, "conflicting duplicate escrow");
                current.ItemBytes[0] ^= 1; current.Request.Count = 99;
                Check(journal.Get(take.RequestId).Request.Count == 3 && Equal(journal.Get(take.RequestId).ItemBytes, parcel), "caller cannot mutate journal record");
                Reject(delegate { journal.MarkDestinationApplied(take.RequestId); }, "destination before durable source");
                Check(journal.MarkSourceRemoved(take.RequestId).Stage == InventoryTransactionStage.SourceRemoved, "source durable effect recorded");
                Check(journal.MarkSourceRemoved(take.RequestId).Stage == InventoryTransactionStage.SourceRemoved, "duplicate source acknowledgement idempotent");
                Reject(delegate { journal.Abort(take.RequestId, "disconnect"); }, "abort after source removed");
                Reject(delegate { journal.Commit(take.RequestId); }, "take commit before durable destination");
                Check(journal.MarkDestinationApplied(take.RequestId).Stage == InventoryTransactionStage.DestinationApplied, "destination durable effect recorded");
                Check(journal.MarkDestinationApplied(take.RequestId).Stage == InventoryTransactionStage.DestinationApplied, "duplicate destination acknowledgement idempotent");
                current = journal.Commit(take.RequestId); Check(current.Stage == InventoryTransactionStage.Committed && current.ItemBytes.Length == 0 && current.ItemHash.Length == 64, "committed receipt discards escrow retains fingerprint");
                Check(journal.Commit(take.RequestId).Stage == InventoryTransactionStage.Committed, "duplicate commit idempotent");
                Check(journal.Begin(72, Alice, Bob, 11, Alice, 12, take).Stage == InventoryTransactionStage.Committed, "completed request replay returns receipt");
                Reject(delegate { journal.RequireRecovery(take.RequestId, "late timeout"); }, "completed receipt cannot become recovery");
                var delete = Request(InventoryOperation.Delete); journal.Begin(72, "local-host", Bob, 11, "", 0, delete); journal.SetPrepared(delete.RequestId, parcel);
                Reject(delegate { journal.MarkDestinationApplied(delete.RequestId); }, "delete has no destination stage");
                journal.MarkSourceRemoved(delete.RequestId); Check(journal.Commit(delete.RequestId).Stage == InventoryTransactionStage.Committed, "delete commits on durable source alone");
                var rejected = Request(InventoryOperation.Delete); journal.Begin(72, Alice, Bob, 11, "", 0, rejected); journal.SetPrepared(rejected.RequestId, parcel);
                Check(journal.Abort(rejected.RequestId, "target refused before mutation").ItemBytes.Length == 0, "safe pre-effect abort clears escrow");
                Check(journal.Abort(rejected.RequestId, "duplicate").Stage == InventoryTransactionStage.Aborted, "abort acknowledgement idempotent");
                var disconnect = Request(InventoryOperation.Take); journal.Begin(72, Alice, Bob, 11, Alice, 12, disconnect); journal.SetPrepared(disconnect.RequestId, parcel);
                current = journal.RequireRecovery(disconnect.RequestId, "disconnect while source result unknown");
                Check(current.Stage == InventoryTransactionStage.NeedsRecovery && current.LastCertainStage == InventoryTransactionStage.Prepared && Equal(current.ItemBytes, parcel), "ambiguous disconnect freezes exact escrow");
                Reject(delegate { journal.Abort(disconnect.RequestId, "timeout"); }, "ambiguous transaction cannot abort");
                Reject(delegate { journal.MarkSourceRemoved(disconnect.RequestId); }, "ambiguous transaction cannot retry source effect");
                Reject(delegate { journal.MarkDestinationApplied(disconnect.RequestId); }, "ambiguous transaction cannot retry destination effect");
                Reject(delegate { journal.ResumeDeliveryAfterReview(disconnect.RequestId, true, true, "review"); }, "unknown source removal cannot be resolved as delivery");
                Check(journal.Unresolved().Length == 1, "unresolved query excludes completed and aborted receipts");
                Reject(delegate { journal.Begin(72, Alice, Alice, 11, Alice, 11, Request(InventoryOperation.Take)); }, "self-transfer same character");
            }
            using (var journal = new TransactionJournal(root))
            {
                Check(journal.Get(take.RequestId).Stage == InventoryTransactionStage.Committed, "committed replay receipt survives restart");
                Check(journal.Begin(72, Alice, Bob, 11, Alice, 12, take).Stage == InventoryTransactionStage.Committed, "restart does not replay committed effect");
                Check(journal.Unresolved()[0].Stage == InventoryTransactionStage.NeedsRecovery, "ambiguous receipt survives restart");
            }
            string file = Path.Combine(root, take.RequestId + ".iatx"); byte[] damaged = File.ReadAllBytes(file); damaged[20] ^= 1; File.WriteAllBytes(file, damaged);
            Reject(delegate { using (var corrupt = new TransactionJournal(root)) { } }, "journal corruption fails closed");
        }
        private static void CrashRecovery(string root)
        {
            var requests = new List<MutationRequest>(); byte[] parcel = { 33, 22, 11 };
            using (var journal = new TransactionJournal(root))
            {
                for (int phase = 0; phase < 4; ++phase)
                {
                    var request = Request(InventoryOperation.Take); requests.Add(request);
                    journal.Begin(72, Alice, Bob, 11, Alice, 12, request);
                    if (phase >= 1) journal.SetPrepared(request.RequestId, parcel);
                    if (phase >= 2) journal.MarkSourceRemoved(request.RequestId);
                    if (phase >= 3) journal.MarkDestinationApplied(request.RequestId);
                }
            }
            using (var journal = new TransactionJournal(root))
            {
                Check(journal.Unresolved().Length == 4, "restart preserves all pending transactions");
                for (int phase = 0; phase < 4; ++phase)
                {
                    InventoryTransaction record = journal.Get(requests[phase].RequestId);
                    Check(record.Stage == InventoryTransactionStage.NeedsRecovery && (int)record.LastCertainStage == phase + 1, "restart freezes phase " + phase);
                    Check(phase == 0 ? record.ItemBytes.Length == 0 : Equal(record.ItemBytes, parcel), "restart preserves escrow in phase " + phase);
                    Reject(delegate { journal.Commit(record.Id); }, "restart pending transaction cannot commit automatically " + phase);
                    if (phase != 2) Reject(delegate { journal.ResumeDeliveryAfterReview(record.Id, true, true, "review"); }, "unsafe phase cannot resume delivery " + phase);
                }
                string resumable = requests[2].RequestId;
                Reject(delegate { journal.ResumeDeliveryAfterReview(resumable, false, true, "review"); }, "delegated administrator cannot release ambiguous escrow");
                Reject(delegate { journal.ResumeDeliveryAfterReview(resumable, true, false, "review"); }, "host must affirm non-delivery after audit");
                Check(journal.Get(resumable).Stage == InventoryTransactionStage.NeedsRecovery, "failed review leaves escrow frozen");
                var resumed = journal.ResumeDeliveryAfterReview(resumable, true, true, "Host inspected receiving inventory and confirmed no delivery.");
                Check(resumed.Stage == InventoryTransactionStage.SourceRemoved && resumed.LastCertainStage == InventoryTransactionStage.SourceRemoved
                    && Equal(resumed.ItemBytes, parcel) && resumed.Note.IndexOf("confirmed no delivery", StringComparison.Ordinal) >= 0, "host review releases only durable source escrow with audit note");
                Reject(delegate { journal.MarkSourceRemoved(resumable + "bad"); }, "review cannot accept malformed identity");
                Reject(delegate { journal.SetPrepared(resumable, parcel); }, "review cannot replay source preparation");
                journal.MarkDestinationApplied(resumable); Check(journal.Commit(resumable).Stage == InventoryTransactionStage.Committed, "reviewed escrow delivers once");
            }
        }
        private static void JournalWriteFailure(string root)
        {
            using (var journal = new TransactionJournal(root))
            {
                var request = Request(InventoryOperation.Delete); string path = Path.Combine(root, request.RequestId + ".iatx");
                Directory.CreateDirectory(path);
                Reject(delegate { journal.Begin(72, Alice, Bob, 11, "", 0, request); }, "journal submit write failure");
                Reject(delegate { journal.Get(request.RequestId); }, "failed disk submission not in memory");
                Directory.Delete(path); journal.Begin(72, Alice, Bob, 11, "", 0, request);
                File.Delete(path); Directory.CreateDirectory(path);
                Reject(delegate { journal.SetPrepared(request.RequestId, new byte[] { 1 }); }, "journal prepare write failure");
                Check(journal.Get(request.RequestId).Stage == InventoryTransactionStage.Submitted, "failed disk stage change leaves prior in-memory stage");
                Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "failed journal stage cleans staging");
            }
        }
        private static void Limits(string root)
        {
            var roster = new List<PlayerInfo>();
            for (int i = 0; i < InventoryCodec.MaximumPlayers; ++i)
                roster.Add(new PlayerInfo { PeerId = i, OwnerId = Bob, Name = "Player" + i });
            Check(InventoryCodec.DecodePlayers(InventoryCodec.EncodePlayers(roster)).Count == InventoryCodec.MaximumPlayers, "maximum bounded roster supported");
            roster.Add(new PlayerInfo { PeerId = 1000, OwnerId = Bob, Name = "Player" });
            Reject(delegate { InventoryCodec.EncodePlayers(roster); }, "roster over bound");
            var view = View(Request(InventoryOperation.Delete)); view.Items.Clear();
            for (int i = 0; i < InventoryCodec.MaximumItems; ++i)
                view.Items.Add(new ItemInfo { SlotId = Token(), Fingerprint = Fingerprint(), Prefab = "Wood", Name = "Wood", Stack = 1, Quality = 1 });
            Check(InventoryCodec.DecodeInventoryView(InventoryCodec.EncodeInventoryView(view)).Items.Count == InventoryCodec.MaximumItems, "maximum bounded view supported");
            view.Items.Add(new ItemInfo { SlotId = Token(), Fingerprint = Fingerprint(), Prefab = "Wood", Name = "Wood", Stack = 1, Quality = 1 });
            Reject(delegate { InventoryCodec.EncodeInventoryView(view); }, "item count over bound");
            Reject(delegate { InventoryCodec.Fingerprint(new byte[InventoryCodec.MaximumItemBytes + 1]); }, "native item blob over bound");
            using (var journal = new TransactionJournal(root))
            {
                MutationRequest first = null;
                for (int i = 0; i < TransactionJournal.MaximumActive; ++i)
                {
                    var request = Request(InventoryOperation.Delete); if (i == 0) first = request;
                    journal.Begin(72, Alice, Bob, 11, "", 0, request);
                }
                Check(journal.Unresolved().Length == TransactionJournal.MaximumActive, "maximum active transactions supported");
                var extra = Request(InventoryOperation.Delete);
                Reject(delegate { journal.Begin(72, Alice, Bob, 11, "", 0, extra); }, "active transaction over bound");
                Check(journal.Begin(72, Alice, Bob, 11, "", 0, first).Id == first.RequestId, "duplicate existing receipt works at capacity");
                journal.Abort(first.RequestId, "confirmed no source effect");
                Check(journal.Begin(72, Alice, Bob, 11, "", 0, extra).Stage == InventoryTransactionStage.Submitted, "safe terminal transaction releases active capacity");
            }
        }
        private static byte[] Sub(byte[] value, int count) { var copy = new byte[count]; Buffer.BlockCopy(value, 0, copy, 0, count); return copy; }
        private static byte[] Append(byte[] value, byte extra) { var copy = new byte[value.Length + 1]; Buffer.BlockCopy(value, 0, copy, 0, value.Length); copy[value.Length] = extra; return copy; }
        private static bool Equal(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; ++i) if (left[i] != right[i]) return false; return true; }
    }
}
