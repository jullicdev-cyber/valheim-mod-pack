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
                DepositPlan(Path.Combine(root, "plan"));
                EmergencyArchive(Path.Combine(root, "emergency"));
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
                Check(store.FindState(A, first).ChestIds[0] == Chests[0], "metadata chest identity isolated");
                Check(!store.HasOutstanding && store.CanAdmit(A) && store.FindState(A) == null && store.FindState(A, first).Stage == CustodyStage.Deposited,
                    "public handoff ends inventory debt while token-specific state retains the active sentence stage");
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.Find(A, first).PublicAccess, "native handoff survives restart");
                Check(store.Find(A, first).OriginalPayload.SequenceEqual(original), "handoff retains the immutable backup");
                store.Release(A, first);
                Check(!store.HasOutstanding && store.FindState(A) == null, "ordinary chest handoff retires escrow on release");
                store.Prepare(A, second, new byte[] { 10 }); store.MarkCleared(A, second, 2); store.MarkDeposited(A, second, Chests); store.MarkPublicAccess(A, second);
                Check(store.FindState(A, second).SentenceId == second && store.Find(A, first).Stage == CustodyStage.Released, "second sentence needs no retrieval receipt from the first");
                store.Release(A, second);
            }
            using (var store = new CustodyStore(root, world))
            {
                Check(store.All().All(record => record.PublicAccess && record.Stage == CustodyStage.Released) && !store.HasOutstanding, "two public custody handoffs persist without retrieval or withdrawal ledgers");
                Check(store.AllStates().Length == 2 && store.AllStates().All(record => record.OriginalPayload.Length == 0), "history migration omits every original payload buffer");
            }
        }
        private static void LegacyFormat(string root)
        {
            for (int version = 1; version <= 3; ++version) {
                const long world = 29; string token = Token(), directory = Path.Combine(root, "version-" + version);
                using (var store = new CustodyStore(directory, world))
                { store.Prepare(A, token, new byte[] { 5 }); store.MarkCleared(A, token, 1); store.MarkDeposited(A, token, Chests); }
                string path = Journal(directory, world, token); byte[] current = File.ReadAllBytes(path);
                int removed = version == 1 ? 10 : version == 2 ? 9 : 8;
                byte[] body = new byte[current.Length - 32 - removed]; Buffer.BlockCopy(current, 32, body, 0, body.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(version), 0, body, 4, 4);
                byte[] legacy = new byte[body.Length + 32]; using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(body), 0, legacy, 0, 32);
                Buffer.BlockCopy(body, 0, legacy, 32, body.Length); File.WriteAllBytes(path, legacy);
                using (var store = new CustodyStore(directory, world)) {
                    CustodyRecord loaded = store.Find(A, token);
                    Check(!loaded.PublicAccess && loaded.Stage == CustodyStage.Deposited && loaded.DepositPayloads.Length == 0 && loaded.DepositBaselineHashes.Length == 0,
                        "v" + version + " journal loads without an invented deposit plan");
                    store.MarkPublicAccess(A, token);
                }
                using (var store = new CustodyStore(directory, world))
                    Check(store.Find(A, token).PublicAccess && store.Find(A, token).OriginalPayload[0] == 5,
                        "v" + version + " journal upgrades without changing original bytes");
            }
        }
        private static void DepositPlan(string root)
        {
            const long world = 59; string token = Token(), next = Token(); byte[] original = { 1, 2 };
            byte[][] targets = { new byte[] { 10, 1 }, new byte[] { 11, 2 }, new byte[] { 12 }, new byte[] { 13 } };
            string[] baselines = targets.Select(CustodyStore.Fingerprint).ToArray();
            string firstBaseline = baselines[0], secondBaseline = baselines[1];
            using (var store = new CustodyStore(root, world)) {
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, null), "missing deposit plan rejected");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, new[] { new byte[] { 1 } }), "incomplete deposit plan rejected");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, targets, new[] { "bad" }), "incomplete baseline cannot authorize replacing old contents");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, targets, new[] { "bad", "bad", "bad", "bad" }), "malformed baseline cannot authorize replacing old contents");
                var prepared = store.Prepare(A, token, original, targets, baselines);
                Check(prepared.Stage == CustodyStage.Prepared && prepared.DepositPayloads.Length == 4, "combined target is durable before the player is cleared");
                targets[0][0] = 99; prepared.DepositPayloads[1][0] = 99;
                baselines[0] = new string('0', 64); prepared.DepositBaselineHashes[1] = new string('0', 64);
                Check(store.Find(A, token).DepositPayloads[0][0] == 10 && store.Find(A, token).DepositPayloads[1][0] == 11,
                    "deposit target input and output buffers are isolated");
                Check(store.Find(A, token).DepositBaselineHashes[0] == firstBaseline && store.Find(A, token).DepositBaselineHashes[1] == secondBaseline,
                    "pre-publication baseline input and output buffers are isolated");
                CustodyRecord state = store.FindState(A, token);
                Check(state.DepositPayloads.Length == 0 && state.DepositBaselineHashes.Length == 0 && state.OriginalPayload.Length == 0,
                    "wire metadata omits original, combined inventories and private transfer baselines");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, targets), "a retry cannot replan against changed public contents");
                Refuses<InvalidDataException>(() => store.Prepare(A, token, original, store.Find(A, token).DepositPayloads, baselines),
                    "a retry cannot rebind the baseline after items are removed from old public chests");
                Check(store.Prepare(A, token, original).DepositPayloads[0][0] == 10, "legacy offer replay retains the original combined plan");
                store.MarkCleared(A, token, 10);
                Refuses<InvalidOperationException>(() => store.Prepare(B, next, new byte[] { 3 }), "unpublished durable transfer still blocks concurrent admission");
            }
            using (var store = new CustodyStore(root, world)) {
                CustodyRecord cleared = store.Find(A, token);
                Check(cleared.Stage == CustodyStage.Cleared && cleared.DepositPayloads[0].SequenceEqual(new byte[] { 10, 1 })
                    && cleared.DepositPayloads[1].SequenceEqual(new byte[] { 11, 2 }) && cleared.OriginalPayload.SequenceEqual(original)
                    && cleared.DepositBaselineHashes[0] == firstBaseline && cleared.DepositBaselineHashes[1] == secondBaseline,
                    "restart preserves exact combined targets and separate player original for partial-deposit retry");
                store.MarkDeposited(A, token, Chests); store.MarkPublicAccess(A, token); store.Release(A, token);
                store.RequireRecovery(A, token, "Previous public world inventory changed");
                Check(!store.HasOutstanding && store.CanAdmit(A) && store.FindState(A) == null,
                    "old public chest changes create no retrieval debt or new-admission blockade");
                store.Prepare(A, next, new byte[] { 3 }, new[] { new byte[] { 3 }, new byte[] { 4 }, new byte[] { 5 }, new byte[] { 6 } });
                Check(store.FindState(A).SentenceId == next && store.Find(A, token).OriginalPayload.SequenceEqual(original),
                    "new imprisonment selects current snapshot without replaying or deleting old public backups");
            }
        }
        private static void EmergencyArchive(string root)
        {
            for (int stage = 1; stage <= 5; ++stage)
            {
                string path = Path.Combine(root, "stage-" + stage); string token = Token(), next = Token();
                byte[] original = { 5, 6, 7 };
                using (var store = new CustodyStore(path, 49))
                {
                    store.Prepare(A, token, original);
                    if (stage >= 2) store.MarkCleared(A, token, 50);
                    if (stage >= 3) { store.MarkDeposited(A, token, Chests); store.MarkPublicAccess(A, token); }
                    if (stage >= 4) store.Release(A, token);
                    if (stage >= 5) store.MarkCollected(A, token);
                    store.RequireRecovery(A, token, "Broken or missing prison pieces");
                    CustodyRecord archived = store.CloseEmergency(A, token, "");
                    Check(archived.Closed && archived.Stage == (CustodyStage)stage && archived.NeedsRecovery, "emergency archives the last certain stage " + stage);
                    Check(archived.OriginalPayload.SequenceEqual(original) && archived.ClearSequence == (stage >= 2 ? 50 : 0), "archive retains originals and never fabricates a confiscation ACK " + stage);
                    Check(archived.PublicAccess == (stage >= 3), "archive retains the native handoff decision " + stage);
                    Check(!store.HasOutstanding && store.Find(A) == null && store.FindState(A) == null && store.CanAdmit(A), "archived recovery cannot keep the player locked or block admission " + stage);
                    Check(store.CloseEmergency(A, token, "Repeated cancellation").RecoveryReason == "Broken or missing prison pieces", "emergency archive replay is idempotent " + stage);
                    Refuses<InvalidOperationException>(() => store.Prepare(A, token, original), "archived token cannot start confiscation again " + stage);
                    Refuses<InvalidOperationException>(() => store.Release(A, token), "archived backup cannot authorize native replay " + stage);
                    store.Prepare(A, next, new byte[] { 9 });
                    Check(store.FindState(A).SentenceId == next && store.FindState(A, token).Closed, "next admission is independent from archived escrow " + stage);
                }
                using (var store = new CustodyStore(path, 49))
                {
                    CustodyRecord archived = store.Find(A, token);
                    Check(archived.Closed && archived.NeedsRecovery && archived.OriginalPayload.SequenceEqual(original), "archive and immutable personal backup survive restart " + stage);
                    Check(store.FindState(A).SentenceId == next && store.AllStates().All(r => r.OriginalPayload.Length == 0), "restart selects the active sentence without copying archived payloads " + stage);
                }
            }
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
            File.WriteAllBytes(path, new byte[CustodyStore.MaximumJournalBytes + 1]);
            Refuses<InvalidDataException>(() => { using (var store = new CustodyStore(root, 10)) { } }, "oversized disk journal refused before allocation");
        }
    }
}
