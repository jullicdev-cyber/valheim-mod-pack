using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ValheimModPack.PartyPrison
{
    internal static class CustodyPolicyTests
    {
        private static int checks;
        private const string A = "Steam_76561198000000001", B = "Steam_76561198000000002";
        private static readonly string[] Chests = { "1:1", "1:2", "1:3", "1:4" };
        private static string Token() { return Guid.NewGuid().ToString("N"); }
        private static void Check(bool value, string reason)
        { if (!value) throw new Exception("Custody test failed: " + reason); ++checks; }
        private static void Refuses<T>(Action action, string reason) where T : Exception
        {
            try { action(); } catch (T) { ++checks; return; }
            throw new Exception("Custody accepted invalid action: " + reason);
        }
        private static string Journal(string root, long world, string token)
        { return Path.Combine(root, "custody-world-" + world.ToString("x16"), token + ".custody"); }
        public static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                Basic(Path.Combine(root, "basic")); Recovery(Path.Combine(root, "recovery"));
                Tampering(Path.Combine(root, "tamper")); Corruption(Path.Combine(root, "corrupt"));
                PublicHandoff(Path.Combine(root, "public")); LegacyFormat(Path.Combine(root, "legacy"));
                Check(true, "all fixtures finished");
                System.Console.WriteLine("PASS: custody journal " + checks + " checks; originals durable, immutable, stage-safe, corruption preserved."); return 0;
            }
            catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
        }
        private static void Basic(string root)
        {
            const long world = 123; string token = Token(), next = Token(); byte[] payload = { 1, 2, 3, 4 };
            Refuses<InvalidDataException>(() => new CustodyStore(root, 0), "zero world");
            using (var store = new CustodyStore(root, world))
            {
                Check(!store.HasOutstanding && store.CanAdmit(A), "empty custody");
                Refuses<IOException>(() => { using (var other = new CustodyStore(root, world)) { } }, "concurrent journal owner");
                Refuses<InvalidDataException>(() => store.Prepare("attacker", token, payload), "unauthenticated account");
                Refuses<InvalidDataException>(() => store.Prepare(A, "../receipt", payload), "unsafe token");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, new byte[0]), "empty native blob");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, new byte[CustodyStore.MaximumPayloadBytes + 1]), "oversized native blob");
                CustodyRecord prepared = store.Prepare(A, token, payload);
                Check(File.Exists(Journal(root, world, token)) && prepared.Stage == CustodyStage.Prepared, "original persisted before clearing");
                prepared.OriginalPayload[0] = 99; payload[1] = 99;
                Check(store.Find(A, token).OriginalPayload.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "input and output buffers isolated");
                Check(store.HasOutstanding && !store.CanAdmit(A) && store.CanAdmit(B), "owner admission policy");
                Check(store.Prepare(A, token, new byte[] { 1, 2, 3, 4 }).Stage == CustodyStage.Prepared, "identical offer idempotent");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, new byte[] { 1 }), "immutable bytes");
                Refuses<InvalidDataException>(() => store.Prepare(B, token, new byte[] { 1, 2, 3, 4 }), "immutable custody owner");
                Refuses<InvalidOperationException>(() => store.Prepare(B, next, new byte[] { 7 }), "four shared chests reserved globally");
                Refuses<InvalidOperationException>(() => store.Release(A, token), "unlock before clear or deposit");
                Refuses<InvalidOperationException>(() => store.MarkDeposited(A, token, Chests), "deposit before WC save ACK");
                Refuses<InvalidDataException>(() => store.MarkCleared(A, token, 0), "undurable ACK");
                Refuses<InvalidDataException>(() => store.MarkCleared(B, token, 10), "different ACK account");
                Refuses<InvalidDataException>(() => store.MarkCleared(A, next, 10), "stale ACK token");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A).Stage == CustodyStage.Prepared, "prepared stage survives crash/reopen");
                Check(store.MarkCleared(A, token, 10).ClearSequence == 10, "durable clear sequence retained");
                Check(store.MarkCleared(A, token, 10).Stage == CustodyStage.Cleared, "clear ACK replay idempotent");
                Refuses<InvalidDataException>(() => store.MarkCleared(A, token, 11), "changed ACK refused");
                Refuses<InvalidDataException>(() => store.MarkDeposited(A, token, new[] { "1", "2", "3" }), "missing fourth chest");
                Refuses<InvalidDataException>(() => store.MarkDeposited(A, token, new[] { "1", "2", "3", "3" }), "duplicate chest");
                Refuses<InvalidOperationException>(() => store.MarkCollected(A, token), "collection before release");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A, token).Stage == CustodyStage.Cleared, "cleared stage survives crash/reopen");
                CustodyRecord deposited = store.MarkDeposited(A, token, Chests); deposited.ChestIds[0] = "changed";
                Check(store.Find(A, token).ChestIds[0] == Chests[0], "chest identities isolated");
                Check(store.MarkDeposited(A, token, Chests).Stage == CustodyStage.Deposited, "deposit ACK idempotent");
                Refuses<InvalidDataException>(() => store.MarkDeposited(A, token, new[] { "2:1", "2:2", "2:3", "2:4" }), "chest identity rebind");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A).Stage == CustodyStage.Deposited, "deposited stage survives restart");
                Check(store.Release(A, token).Stage == CustodyStage.Released, "release only after deposit");
                Check(store.Release(A, token).Stage == CustodyStage.Released, "release replay idempotent");
                Refuses<InvalidOperationException>(() => store.Prepare(A, next, new byte[] { 9 }), "next sentence blocked pending retrieval");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A).Stage == CustodyStage.Released, "released stage survives restart without reissuing inventory");
                Check(store.MarkCollected(A, token).Stage == CustodyStage.Collected, "collection receipt durable");
                Check(store.MarkCollected(A, token).Stage == CustodyStage.Collected, "collection replay idempotent");
                Check(store.Find(A) == null && store.CanAdmit(A) && !store.HasOutstanding, "collection unlocks admission");
                store.Prepare(B, next, new byte[] { 9 });
                Check(store.All().Length == 2 && store.Find(A, token).OriginalPayload.Length == 4, "immutable history retained after collection");
            }
            using (var store = new CustodyStore(root, world + 1))
                Check(store.All().Length == 0 && !store.HasOutstanding, "worlds isolated");
        }
        private static void Recovery(string root)
        {
            string token = Token();
            using (var store = new CustodyStore(root, 8))
            {
                store.Prepare(A, token, new byte[] { 1 }); store.MarkCleared(A, token, 5);
                CustodyRecord recovery = store.RequireRecovery(A, token, "Ambiguous world chest rollback");
                Check(recovery.Stage == CustodyStage.Cleared && recovery.NeedsRecovery, "recovery preserves last certain stage");
                Refuses<InvalidOperationException>(() => store.MarkDeposited(A, token, Chests), "ambiguous replay blocked");
                Refuses<InvalidOperationException>(() => store.Prepare(A, Token(), new byte[] { 2 }), "recovery blocks new admission");
            }
            using (var store = new CustodyStore(root, 8))
            {
                Check(store.Find(A, token).NeedsRecovery && store.Find(A, token).OriginalPayload[0] == 1, "recovery and original retained on disk");
                Refuses<InvalidOperationException>(() => store.MarkDeposited(A, token, Chests), "recovery remains blocked after restart");
            }
            using (var store = new CustodyStore(root, 18))
            {
                string closed = Token(); store.Prepare(A, closed, new byte[] { 2 }); store.MarkCleared(A, closed, 1);
                store.MarkDeposited(A, closed, Chests); store.Release(A, closed); store.MarkCollected(A, closed);
                store.RequireRecovery(A, closed, "Collected chest reappeared after stale world restoration");
                Check(store.HasOutstanding && !store.CanAdmit(A), "rollback of finished custody blocks readmission");
                Refuses<InvalidOperationException>(() => store.MarkCollected(A, closed), "finished receipt replay cannot dismiss recovery");
                Refuses<InvalidOperationException>(() => store.Release(A, closed), "release replay cannot unlock recovered chest");
            }
        }
        private static void PublicHandoff(string root)
        {
            const long world = 19; string first = Token(), second = Token(); byte[] original = { 7, 8, 9 };
            using (var store = new CustodyStore(root, world))
            {
                store.Prepare(A, first, original);
                Refuses<InvalidOperationException>(() => store.MarkPublicAccess(A, first), "public before clearing");
                store.MarkCleared(A, first, 1);
                Refuses<InvalidOperationException>(() => store.MarkPublicAccess(A, first), "public before depositing");
                store.MarkDeposited(A, first, Chests);
                CustodyRecord exposed = store.MarkPublicAccess(A, first);
                Check(exposed.PublicAccess && exposed.Stage == CustodyStage.Deposited, "native access does not release the prisoner");
                Check(store.MarkPublicAccess(A, first).PublicAccess, "public handoff idempotent");
                CustodyRecord state = store.FindState(A, first);
                Check(state.PublicAccess && state.OriginalPayload.Length == 0 && state.PayloadHash == CustodyStore.Fingerprint(original), "metadata reads omit large inventory blobs");
                state.ChestIds[0] = "changed";
                Check(store.FindState(A).ChestIds[0] == Chests[0], "metadata chest identity isolated");
                Refuses<InvalidOperationException>(() => store.Prepare(A, second, new byte[] { 10 }), "native public access alone does not permit concurrent imprisonment");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A, first).PublicAccess, "native handoff survives restart");
                Check(store.Find(A, first).OriginalPayload.SequenceEqual(original), "handoff retains the immutable backup");
                store.Release(A, first); store.MarkCollected(A, first);
                Check(!store.HasOutstanding && store.FindState(A) == null, "ordinary chest handoff retires escrow on release");
                store.Prepare(A, second, new byte[] { 10 }); store.MarkCleared(A, second, 2); store.MarkDeposited(A, second, Chests); store.MarkPublicAccess(A, second);
                Check(store.FindState(A).SentenceId == second && store.Find(A, first).Stage == CustodyStage.Collected, "second sentence has independent custody state");
                store.Release(A, second); store.MarkCollected(A, second);
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.All().All(record => record.PublicAccess && record.Stage == CustodyStage.Collected) && !store.HasOutstanding, "two public custody handoffs persist without unfinished withdrawal ledgers");
                Check(store.AllStates().Length == 2 && store.AllStates().All(record => record.OriginalPayload.Length == 0), "history migration omits every original payload buffer");
            }
        }
        private static void LegacyFormat(string root)
        {
            const long world = 29; string token = Token();
            using (var store = new CustodyStore(root, world))
            { store.Prepare(A, token, new byte[] { 5 }); store.MarkCleared(A, token, 1); store.MarkDeposited(A, token, Chests); }
            string path = Journal(root, world, token); byte[] current = File.ReadAllBytes(path);
            byte[] body = new byte[current.Length - 33]; Buffer.BlockCopy(current, 32, body, 0, body.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(1), 0, body, 4, 4);
            byte[] legacy = new byte[body.Length + 32]; using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(body), 0, legacy, 0, 32);
            Buffer.BlockCopy(body, 0, legacy, 32, body.Length); File.WriteAllBytes(path, legacy);
            using (var store = new CustodyStore(root, world))
            {
                Check(!store.FindState(A).PublicAccess && store.FindState(A).Stage == CustodyStage.Deposited, "v1 journal loads with transient locked native access");
                store.MarkPublicAccess(A, token);
            }
            using (var store = new CustodyStore(root, world))
                Check(store.FindState(A).PublicAccess && store.Find(A, token).OriginalPayload[0] == 5, "legacy journal upgrades once without changing original bytes");
        }
        private static void Tampering(string root)
        {
            string token = Token();
            using (var store = new CustodyStore(root, 9))
            {
                store.Prepare(A, token, new byte[] { 1 });
                string path = Journal(root, 9, token); byte[] external = File.ReadAllBytes(path); external[external.Length - 1] ^= 1; File.WriteAllBytes(path, external);
                Refuses<IOException>(() => store.MarkCleared(A, token, 2), "external mutation fails closed");
                Check(store.Faulted && File.ReadAllBytes(path).SequenceEqual(external), "external file preserved and store faulted");
                Refuses<IOException>(() => store.Find(A), "faulted journal cannot keep admitting");
            }
        }
        private static void Corruption(string root)
        {
            string token = Token();
            using (var store = new CustodyStore(root, 10)) store.Prepare(A, token, new byte[] { 1, 2 });
            string path = Journal(root, 10, token); byte[] bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes);
            Refuses<InvalidDataException>(() => { using (var store = new CustodyStore(root, 10)) { } }, "corrupt journal refused");
            Check(File.ReadAllBytes(path).SequenceEqual(bytes), "corruption not overwritten with empty custody");
            File.WriteAllBytes(path, new byte[CustodyStore.MaximumPayloadBytes + 8193]);
            Refuses<InvalidDataException>(() => { using (var store = new CustodyStore(root, 10)) { } }, "oversized disk journal refused before allocation");
        }
    }
}
