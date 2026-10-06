using System;
using System.IO;
using System.Linq;

namespace ValheimModPack.PartyPrison
{
    internal static class WithdrawalPolicyTests
    {
        private static int checks;
        private static void Check(bool value, string label) { if (!value) throw new Exception("Withdrawal failed: " + label); ++checks; }
        private static void Refuses<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { ++checks; return; } throw new Exception("Withdrawal accepted: " + label); }
        public static int Main(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root); const long world = 22;
                string token = Guid.NewGuid().ToString("N");
                var custody = new CustodyRecord { World = world, AccountId = "Steam_76561198000000001", SentenceId = token, Stage = CustodyStage.Released };
                byte[][] parts = { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6 }, new byte[] { 0 } };
                string grant;
                using (var store = new CustodyWithdrawal(root, world))
                {
                    Refuses<IOException>(() => { using (var other = new CustodyWithdrawal(root, world)) { } }, "concurrent writers");
                    custody.Stage = CustodyStage.Deposited;
                    Refuses<InvalidOperationException>(() => store.Ensure(custody, parts), "unreleased chest balances"); custody.Stage = CustodyStage.Released;
                    store.Ensure(custody, parts); store.Ensure(custody, parts);
                    Check(store.All(token).Length == 4, "four idempotent balances");
                    var copy = store.Find(token, 0); copy.RemainingPayload[0] = 9; parts[0][0] = 8;
                    Check(store.Find(token, 0).RemainingPayload[0] == 1, "immutable inputs/outputs"); parts[0][0] = 1;
                    byte[][] changed = { new byte[] { 7 }, parts[1], parts[2], parts[3] };
                    Refuses<InvalidDataException>(() => store.Ensure(custody, changed), "original part rebinding");
                    Refuses<InvalidDataException>(() => store.Find(token, 4), "invalid chest index");
                    Refuses<InvalidDataException>(() => store.Begin(token, 0, new byte[] { 1, 2, 3 }, new byte[] { 1 }), "grant without reducing balance");
                    var pending = store.Begin(token, 0, new byte[] { 2, 3 }, new byte[] { 1 }); grant = pending.PendingId;
                    Check(pending.Stage == WithdrawalStage.GrantPending && pending.RemainingPayload.SequenceEqual(new byte[] { 1, 2, 3 }), "begin durable grant keeps balance until ACK");
                    Check(store.Begin(token, 0, new byte[] { 2, 3 }, new byte[] { 1 }).PendingId == grant, "same grant replay keeps receipt ID");
                    Refuses<InvalidDataException>(() => store.Begin(token, 0, new byte[] { 3 }, new byte[] { 2 }), "pending grant cannot mutate");
                    Refuses<InvalidDataException>(() => store.Commit(token, 0, Guid.NewGuid().ToString("N")), "wrong ACK cannot consume balance");
                }
                using (var store = new CustodyWithdrawal(root, world))
                {
                    Check(store.Find(token, 0).PendingId == grant && store.Find(token, 0).PendingPayload[0] == 1, "pending item and receipt survive restart");
                    var committed = store.Commit(token, 0, grant);
                    Check(committed.Stage == WithdrawalStage.Ready && committed.RemainingPayload.SequenceEqual(new byte[] { 2, 3 }), "ACK atomically consumes item and clears pending");
                    Check(committed.CompletedIds.Contains(grant), "completed receipt retained");
                    Check(store.Commit(token, 0, grant).RemainingPayload.SequenceEqual(new byte[] { 2, 3 }), "ACK replay cannot consume twice");
                    var second = store.Begin(token, 0, new byte[] { 3 }, new byte[] { 2 });
                    Check(second.PendingId != grant, "next item has a new receipt");
                    Check(store.Commit(token, 0, grant).PendingId == second.PendingId, "old ACK cannot commit next pending grant");
                    store.Commit(token, 0, second.PendingId);
                }
                using (var store = new CustodyWithdrawal(root, world))
                {
                    var current = store.Find(token, 0);
                    Check(current.RemainingPayload[0] == 3 && current.CompletedIds.Length == 2 && current.OriginalPayload.Length == 3, "consumed balance persists while immutable original remains");
                    store.Ensure(custody, parts);
                    Check(store.Find(token, 0).RemainingPayload[0] == 3, "Ensure never refills consumed native world rollback");
                    store.RequireRecovery(token, 1, "Native payload validation failed");
                    Refuses<InvalidOperationException>(() => store.Begin(token, 1, new byte[] { 5 }, new byte[] { 4 }), "recovery blocks grants");
                }
                using (var store = new CustodyWithdrawal(root, world))
                    Check(store.Find(token, 1).NeedsRecovery && store.Find(token, 1).RemainingPayload[0] == 4, "recovery preserves balance");
                using (var store = new CustodyWithdrawal(root, world + 1)) Check(store.All(token).Length == 0, "worlds isolated");
                string file = Path.Combine(root, "withdrawal-world-" + world.ToString("x16"), token + "-0.withdrawal");
                using (var store = new CustodyWithdrawal(root, world))
                {
                    byte[] bytes = File.ReadAllBytes(file); bytes[0] ^= 1; File.WriteAllBytes(file, bytes);
                    Refuses<IOException>(() => store.Begin(token, 0, new byte[] { 0 }, new byte[] { 3 }), "external edit blocks next grant");
                    Check(store.Faulted && File.ReadAllBytes(file).SequenceEqual(bytes), "external file preserved and ledger faulted");
                }
                Refuses<InvalidDataException>(() => { using (var store = new CustodyWithdrawal(root, world)) { } }, "corruption refused on reopen");
                System.Console.WriteLine("PASS: withdrawal ledger " + checks + " checks; pending grants, durable receipts, and remaining balances survive retries and rollback."); return 0;
            }
            catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
        }
    }
}
