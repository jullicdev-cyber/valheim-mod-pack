using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimModPack.PartyPrison
{
    // Production SentenceStore, real atomic files, and a deterministic gate at
    // the worker boundary. The timeouts below only detect a deadlocked fixture;
    // none of these assertions measure frame time or infer gameplay FPS.
    internal static class TimerTests
    {
        private const string Alice = "Steam_76561198000000001", Bob = "Steam_76561198000000002", Carol = "Steam_76561198000000003";
        private static int checks;
        private sealed class WriteGate : IDisposable
        {
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
            internal readonly ManualResetEvent Continue = new ManualResetEvent(false);
            internal Exception Failure;
            internal int Calls;
            internal void Before()
            {
                if (Interlocked.Increment(ref Calls) != 1) return;
                Entered.Set();
                if (!Continue.WaitOne(10000)) throw new TimeoutException("Test worker was not released.");
                if (Failure != null) throw Failure;
            }
            public void Dispose() { Continue.Set(); Entered.Dispose(); Continue.Dispose(); }
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); ++checks; }
        private static void Refuse<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { ++checks; return; }
            throw new Exception("Did not refuse: " + message);
        }
        private static void Reached(WaitHandle handle, string message)
        { Check(handle.WaitOne(5000), message); }
        private static void Finished(Task task, string message)
        {
            Reached(((IAsyncResult)task).AsyncWaitHandle, message);
            task.GetAwaiter().GetResult();
        }
        private static void FaultPublished(SentenceStore store)
        {
            // Observe the public volatile fault flag without pumping the queue,
            // invoking a command, or waiting through Dispose.
            Check(SpinWait.SpinUntil(() => store.Faulted, 5000), "Worker failure must publish its fault without another timer admission.");
            Check(!String.IsNullOrEmpty(store.FaultReason), "Fault publication includes its diagnostic before the flag.");
        }
        private static PrisonRegion Region()
        {
            return new PrisonRegion { Center = new PrisonPoint(0, 0, 0), Radius = 20, HalfHeight = 14,
                CellSpawn = new PrisonPoint(-5, 0, 0), ArenaSpawn = new PrisonPoint(5, 0, 0) };
        }
        private static SentenceState Impose(SentenceStore store, string account, double duration)
        { return store.Impose(true, account, "Игрок", "Timer regression", duration, new PrisonPoint(100, 5, 200)); }
        private static SentenceStore Open(string root, long world, WriteGate gate)
        {
            var store = new SentenceStore(root, world, gate == null ? (Action)null : gate.Before);
            store.SetRegion(true, Region()); return store;
        }
        private static void Cleanup(SentenceStore store, WriteGate gate)
        {
            if (gate != null) gate.Continue.Set();
            try { if (store != null) store.Dispose(); }
            catch (IOException) { if (store == null || !store.Faulted) throw; }
            finally { if (gate != null) gate.Dispose(); }
        }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Expected an isolated timer state directory.");
                Directory.CreateDirectory(args[0]);
                BlockedReadsAndCoalescing(Path.Combine(args[0], "coalescing"));
                NoEarlyExpiry(Path.Combine(args[0], "expiry"));
                CommandOrderAndTokens(Path.Combine(args[0], "tokens"));
                DisposeDrains(Path.Combine(args[0], "dispose"));
                ImmediateWorkerFailure(Path.Combine(args[0], "worker-failure"));
                ExternalConflict(Path.Combine(args[0], "external-conflict"));
                ValidateWholeQueue(Path.Combine(args[0], "validation"));
                Console.WriteLine("PASS: " + checks + " production sentence timer checks (blocked reads, coalesced verified time, durable expiry, command/token order, shutdown and immediate worker faults). No game process started.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void BlockedReadsAndCoalescing(string root)
        {
            var gate = new WriteGate(); SentenceStore store = null;
            try
            {
                store = Open(root, 301, gate); Impose(store, Alice, 100); Impose(store, Bob, 100);
                byte[] before = File.ReadAllBytes(store.StatePath);
                Check(store.QueueTickOnline(new[] { Alice, Alice }, 1), "First timer batch enters the background writer.");
                Reached(gate.Entered, "Timer worker reaches deterministic blocked-write gate.");
                Task reads = Task.Run(() => {
                    SentenceState first = store.Find(Alice);
                    Check(first.RemainingSeconds == 100 && first.Revision == 1 && !first.PendingRelease, "Blocked worker exposes only previous durable sentence.");
                    SentenceState[] all = store.All();
                    Check(all.Length == 2 && all.All(item => item.RemainingSeconds == 100), "Roster queries return durable states while IO is blocked.");
                    PrisonRegion copy = store.Region;
                    Check(copy.Radius == 20, "Region query remains available while IO is blocked.");
                    first.RemainingSeconds = 0; all[0].PlayerName = "changed copy"; copy.Radius = 6;
                    Check(store.Find(Alice).RemainingSeconds == 100 && store.Region.Radius == 20
                        && store.Find(Alice).PlayerName == "Игрок", "Read snapshots cannot mutate queued or durable production data.");
                });
                Finished(reads, "Find, All and Region finish before the write gate is released.");
                Task queue = Task.Run(() => {
                    Check(!store.QueueTickOnline(new[] { Alice, Alice, Bob }, 2), "Busy worker coalesces rather than admitting a second writer.");
                    Check(!store.QueueTickOnline(new[] { Alice }, 3), "Further verified ticks stay in the bounded coalesced batch.");
                    Check(!store.QueueTickOnline(new string[0], 5), "Offline roster cannot contribute new time credit.");
                });
                Finished(queue, "Timer admission also finishes while disk IO is blocked.");
                Check(gate.Calls == 1 && File.ReadAllBytes(store.StatePath).SequenceEqual(before), "Only one worker exists and blocked credit is not on disk yet.");
                gate.Continue.Set(); store.TickOnline(new string[0], 0);
                SentenceState a = store.Find(Alice), b = store.Find(Bob);
                Check(a.RemainingSeconds == 94 && a.Revision == 3 && b.RemainingSeconds == 98 && b.Revision == 2,
                    "Duplicate accounts count once; verified queued debit survives going offline and commits exactly once.");
                Check(!store.QueueTickOnline(new string[0], 5) && store.Find(Alice).RemainingSeconds == 94,
                    "A drained offline queue cannot subtract more time.");
                Check(!store.Find(Alice).PendingRelease && !store.Faulted, "Successful background timer retains confinement and healthy state.");
                store.Dispose();
                using (var reopened = new SentenceStore(root, 301))
                    Check(reopened.Find(Alice).RemainingSeconds == 94 && reopened.Find(Bob).RemainingSeconds == 98,
                        "Every committed coalesced debit survives restart.");
            }
            finally { Cleanup(store, gate); }
        }
        private static void NoEarlyExpiry(string root)
        {
            var gate = new WriteGate(); SentenceStore store = null;
            try
            {
                store = Open(root, 302, gate); Impose(store, Alice, 1);
                Check(store.QueueTickOnline(new[] { Alice }, 1), "Final second queues without synchronous IO.");
                Reached(gate.Entered, "Expiry worker is paused before persistence.");
                for (int i = 0; i < 500; ++i)
                    Check(!store.QueueTickOnline(new[] { Alice }, 5), "Long blocked backlog remains one worker.");
                Check(store.Find(Alice).RemainingSeconds == 1 && !store.Find(Alice).PendingRelease,
                    "Uncommitted expiry never unlocks a prisoner.");
                gate.Continue.Set(); store.TickOnline(new string[0], 0);
                Check(store.Find(Alice).RemainingSeconds == 0 && store.Find(Alice).PendingRelease && store.Find(Alice).Revision == 2,
                    "Durable expiry clamps bounded backlog and never applies another debit to the finished sentence.");
                Check(!store.QueueTickOnline(new[] { Alice }, 1), "Already released timer creates no new worker.");
            }
            finally { Cleanup(store, gate); }
        }
        private static void CommandOrderAndTokens(string root)
        {
            var gate = new WriteGate(); SentenceStore store = null;
            try
            {
                store = Open(root, 303, gate); SentenceState original = Impose(store, Alice, 20);
                store.QueueTickOnline(new[] { Alice }, 1); Reached(gate.Entered, "Token-order timer is blocked.");
                store.QueueTickOnline(new[] { Alice }, 3);
                using (var commandStarted = new ManualResetEvent(false))
                {
                    SentenceState replacement = null;
                    Task command = Task.Run(() => {
                        commandStarted.Set();
                        SentenceState release = store.RequestRelease(true, Alice);
                        Check(release.PendingRelease && release.RemainingSeconds == 0 && release.Revision == 4,
                            "Release is serialized after first timer commit and coalesced debit.");
                        Check(store.AcknowledgeRelease(Alice, original.SentenceId), "Matching release acknowledgement clears the old sentence durably.");
                        replacement = Impose(store, Alice, 200);
                    });
                    Reached(commandStarted, "Administrative command starts during blocked IO.");
                    Check(store.Find(Alice).RemainingSeconds == 20 && !store.Find(Alice).PendingRelease,
                        "A pending administrative release exposes no unsaved early exit.");
                    gate.Continue.Set(); Finished(command, "Timer and administrative command drain without a sync-lock deadlock.");
                    Check(replacement != null && replacement.SentenceId != original.SentenceId && replacement.RemainingSeconds == 200 && replacement.Revision == 1,
                        "Old queued time cannot charge a replacement sentence.");
                    Check(!store.AcknowledgeRelease(Alice, original.SentenceId), "Old-token acknowledgement cannot erase replacement.");
                    Check(store.QueueTickOnline(new[] { Alice }, 1), "Replacement sentence starts an independent timer batch.");
                    store.TickOnline(new string[0], 0);
                    Check(store.Find(Alice).RemainingSeconds == 199 && store.Find(Alice).SentenceId == replacement.SentenceId,
                        "Only freshly authorized replacement time is subtracted.");
                    Check(gate.Calls == 2, "Administrative draining does not create overlapping background writes.");
                }
            }
            finally { Cleanup(store, gate); }
        }
        private static void DisposeDrains(string root)
        {
            var gate = new WriteGate(); SentenceStore store = null;
            try
            {
                store = Open(root, 304, gate); Impose(store, Alice, 20);
                store.QueueTickOnline(new[] { Alice }, 1); Reached(gate.Entered, "Shutdown timer is blocked.");
                store.QueueTickOnline(new[] { Alice }, 3);
                using (var started = new ManualResetEvent(false))
                {
                    Task closing = Task.Run(() => { started.Set(); store.Dispose(); });
                    Reached(started, "Dispose starts while background timer is pending.");
                    Check(store.Find(Alice).RemainingSeconds == 20, "Shutdown does not publish uncommitted elapsed time.");
                    gate.Continue.Set(); Finished(closing, "Dispose drains background and queued batches without deadlocking readers.");
                }
                Refuse<ObjectDisposedException>(() => store.Find(Alice), "Disposed store rejects subsequent queries.");
                using (var reopened = new SentenceStore(root, 304))
                    Check(reopened.Find(Alice).RemainingSeconds == 16 && reopened.Find(Alice).Revision == 3,
                        "Dispose persists pending and coalesced authorized debit before releasing the process lock.");
            }
            finally { Cleanup(store, gate); }
        }
        private static void ImmediateWorkerFailure(string root)
        {
            var gate = new WriteGate { Failure = new IOException("Injected timer write failure.") }; SentenceStore store = null;
            try
            {
                store = Open(root, 305, gate); Impose(store, Alice, 20); byte[] before = File.ReadAllBytes(store.StatePath);
                store.QueueTickOnline(new[] { Alice }, 1); Reached(gate.Entered, "Failing worker is blocked before injection.");
                store.QueueTickOnline(new[] { Alice }, 3); gate.Continue.Set(); FaultPublished(store);
                Refuse<IOException>(() => store.Find(Alice), "Find fails closed immediately after background failure.");
                Refuse<IOException>(() => store.All(), "All fails closed before another timer tick.");
                Refuse<IOException>(() => { var unused = store.Region; }, "Region fails closed before command/shutdown draining.");
                Check(File.ReadAllBytes(store.StatePath).SequenceEqual(before), "Failed worker grants no durable time credit.");
                Refuse<IOException>(() => store.Dispose(), "Shutdown surfaces the failed admitted write.");
                using (var reopened = new SentenceStore(root, 305))
                    Check(reopened.Find(Alice).RemainingSeconds == 20 && reopened.Find(Alice).Revision == 1,
                        "Failed shutdown releases the process lock and preserves previous durable sentence.");
            }
            finally { Cleanup(store, gate); }
        }
        private static void ExternalConflict(string root)
        {
            var gate = new WriteGate(); SentenceStore store = null;
            try
            {
                store = Open(root, 306, gate); Impose(store, Alice, 20);
                byte[] before = File.ReadAllBytes(store.StatePath); byte[] external = (byte[])before.Clone(); external[1] ^= 1;
                store.QueueTickOnline(new[] { Alice }, 1); Reached(gate.Entered, "Conflicting-file worker is held before IO.");
                File.WriteAllBytes(store.StatePath, external); gate.Continue.Set(); FaultPublished(store);
                Check(store.FaultReason.Contains("changed outside"), "Specific external-file conflict diagnostic survives generic worker catch.");
                Refuse<IOException>(() => store.Find(Alice), "External edit closes query admission immediately.");
                Check(File.ReadAllBytes(store.StatePath).SequenceEqual(external), "Background writer never overwrites an externally modified file.");
                Refuse<IOException>(() => store.Dispose(), "Conflicting admitted timer prevents successful shutdown claim.");
                Check(Directory.GetFiles(root, "*.tmp-*").Length == 0, "Conflicting background commit cleans staging files.");
                File.WriteAllBytes(store.StatePath, before);
                using (var reopened = new SentenceStore(root, 306))
                    Check(reopened.Find(Alice).RemainingSeconds == 20, "Administrator repair reopens the preserved last valid state.");
            }
            finally { Cleanup(store, gate); }
        }
        private static void ValidateWholeQueue(string root)
        {
            using (var store = Open(root, 307, null))
            {
                Impose(store, Alice, 20); byte[] before = File.ReadAllBytes(store.StatePath);
                Refuse<InvalidDataException>(() => store.QueueTickOnline(new[] { Alice, "not-authenticated" }, 1), "Complete roster validates before any debit is recorded.");
                Refuse<ArgumentNullException>(() => store.QueueTickOnline(null, 1), "Null roster.");
                foreach (double amount in new[] { -1, Double.NaN, Double.PositiveInfinity, SentencePolicy.MaximumTickSeconds + 1 })
                    Refuse<InvalidDataException>(() => store.QueueTickOnline(new[] { Alice }, amount), "Invalid active-time amount.");
                var oversized = new List<string>();
                for (int i = 0; i <= SentencePolicy.MaximumSentences; ++i)
                    oversized.Add("Steam_" + (76561198000000000UL + (ulong)i).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Refuse<InvalidDataException>(() => store.QueueTickOnline(oversized, 1), "Oversized authenticated roster cannot create unbounded timer state.");
                Check(!store.QueueTickOnline(new[] { Alice }, 0) && !store.QueueTickOnline(new[] { Carol }, 1),
                    "Zero duration and non-sentenced accounts create no timer work.");
                Check(store.Find(Alice).RemainingSeconds == 20 && File.ReadAllBytes(store.StatePath).SequenceEqual(before),
                    "Rejected or empty admissions leave memory, disk and pending debit unchanged.");
                store.TickOnline(new string[0], 0);
                Check(store.Find(Alice).RemainingSeconds == 20 && store.Find(Alice).Revision == 1,
                    "No hidden partial debit remains after complete-roster validation failure.");
            }
        }
    }
}
