using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ValheimModPack.WorldCharacters;

internal static class SnapshotWriterTests
{
    private static int passed;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        ++passed;
    }
    private static void Wait(WaitHandle signal, string message)
    {
        Check(signal.WaitOne(5000), message);
    }
    private static void Refuse(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { ++passed; return; }
        catch (InvalidOperationException) { ++passed; return; }
        throw new Exception("Did not refuse: " + message);
    }

    private static void BlockedIoDoesNotBlockMain()
    {
        using (var writer = new SnapshotWriter(4, 100))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        {
            int owner = Thread.CurrentThread.ManagedThreadId;
            int worker = owner;
            int callbacks = 0;
            bool persisted = false;
            Check(writer.TryEnqueue(20, delegate
            {
                worker = Thread.CurrentThread.ManagedThreadId;
                entered.Set();
                if (!release.WaitOne(5000)) throw new TimeoutException("fake disk was not released");
                persisted = true;
                return "durable";
            }, delegate(object result, Exception error, double elapsed)
            {
                Check(Thread.CurrentThread.ManagedThreadId == owner, "completion runs on owner thread");
                Check(persisted && error == null && (string)result == "durable", "ACK observes completed durable work");
                Check(elapsed >= 0, "completion receives measured duration");
                ++callbacks;
            }), "blocked disk admitted");
            Wait(entered, "worker entered fake disk");
            try
            {
                // A fail-safe release makes an accidental lock across I/O a
                // bounded test failure instead of leaving the test process hung.
                using (var failSafe = new Timer(delegate { release.Set(); }, null, 2000, Timeout.Infinite))
                {
                    var watch = Stopwatch.StartNew();
                    Check(writer.TryEnqueue(20, delegate { return "second"; }, delegate { ++callbacks; }), "admission proceeds during blocked disk");
                    Check(writer.Drain() == 0, "Drain does not wait for an unfinished write");
                    Check(watch.ElapsedMilliseconds < 1000, "main thread returns while disk remains blocked");
                    Check(!persisted && callbacks == 0, "no callback or ACK before durable work");
                    Check(writer.PendingCount == 2 && writer.PendingBytes == 40 && !writer.IsIdle, "running and queued jobs stay charged");
                }
            }
            finally { release.Set(); }
            writer.Barrier();
            Check(worker != owner, "disk work uses a different thread");
            Check(callbacks == 0, "Barrier does not dispatch callbacks");
            Check(writer.PendingCount == 2 && writer.PendingBytes == 40 && !writer.IsIdle, "undrained completions remain charged");
            Check(writer.Drain() == 2 && callbacks == 2, "completed callbacks dispatched exactly once");
            Check(writer.IsIdle && writer.PendingBytes == 0 && writer.Drain() == 0, "draining releases all accounting");
        }
    }

    private static void FifoAndFailure()
    {
        using (var writer = new SnapshotWriter(32, 1024))
        {
            var workOrder = new List<int>();
            var callbackOrder = new List<int>();
            int active = 0;
            int maximumActive = 0;
            var diskError = new IOException("durability failed");
            for (int i = 0; i < 20; ++i)
            {
                int index = i;
                Check(writer.TryEnqueue(10, delegate
                {
                    int current = Interlocked.Increment(ref active);
                    maximumActive = Math.Max(maximumActive, current);
                    try
                    {
                        workOrder.Add(index);
                        if (index == 7) throw diskError;
                        return index;
                    }
                    finally { Interlocked.Decrement(ref active); }
                }, delegate(object result, Exception error, double elapsed)
                {
                    callbackOrder.Add(index);
                    if (index == 7) Check(object.ReferenceEquals(error, diskError) && result == null, "durability error delivered without successful result");
                    else Check(error == null && (int)result == index, "successful result follows its work");
                }), "FIFO job admitted " + i);
            }
            writer.Barrier();
            Check(workOrder.Count == 20 && maximumActive == 1, "one worker processes all jobs despite a write failure");
            Check(callbackOrder.Count == 0, "worker never invokes callback");
            writer.Drain();
            for (int i = 0; i < 20; ++i)
                Check(workOrder[i] == i && callbackOrder[i] == i, "FIFO work and completion order " + i);
            Check(writer.IsIdle, "failed write releases capacity after callback");
        }
    }

    private static void BoundsAndValidation()
    {
        Refuse(delegate { new SnapshotWriter(0, 1); }, "nonpositive job bound");
        Refuse(delegate { new SnapshotWriter(1, 0); }, "nonpositive byte bound");
        using (var writer = new SnapshotWriter(2, 30))
        {
            Refuse(delegate { writer.TryEnqueue(-1, delegate { return null; }, delegate {}); }, "negative retained bytes");
            Refuse(delegate { writer.CanEnqueue(-1); }, "negative capacity query");
            Refuse(delegate { writer.TryEnqueue(1, null, delegate {}); }, "missing work");
            Refuse(delegate { writer.TryEnqueue(1, delegate { return null; }, null); }, "missing completion");
            Check(!writer.TryEnqueue(long.MaxValue, delegate { return null; }, delegate {}), "oversized charge cannot overflow byte accounting");
            Check(writer.CanEnqueue(30) && !writer.CanEnqueue(31) && !writer.CanEnqueue(long.MaxValue), "capacity query respects initial byte cap without overflow");
            Check(writer.TryEnqueue(20, delegate { return null; }, delegate {}), "first byte bounded job admitted");
            writer.Barrier();
            Check(writer.CanEnqueue(10) && !writer.CanEnqueue(11), "capacity query charges completed undrained bytes");
            Check(!writer.TryEnqueue(11, delegate { return null; }, delegate {}), "undrained completion enforces byte cap");
            Check(writer.TryEnqueue(10, delegate { return null; }, delegate {}), "exact byte boundary admitted");
            writer.Barrier();
            Check(!writer.TryEnqueue(0, delegate { return null; }, delegate {}), "undrained completion enforces job cap");
            Check(!writer.CanEnqueue(0), "capacity query respects completed undrained job cap");
            Check(writer.PendingCount == 2 && writer.PendingBytes == 30, "full bounds count completed results");
            writer.Drain();
            Check(writer.CanEnqueue(30), "capacity query sees capacity released by callbacks");
            Check(writer.TryEnqueue(30, delegate { return null; }, delegate {}), "drained capacity reusable");
            writer.Barrier(); writer.Drain();
        }
        using (var writer = new SnapshotWriter(9, 90))
        using (var start = new ManualResetEvent(false))
        {
            int accepted = 0;
            int callbacks = 0;
            var threads = new Thread[8];
            Exception threadError = null;
            for (int i = 0; i < threads.Length; ++i)
            {
                threads[i] = new Thread(delegate()
                {
                    try
                    {
                        start.WaitOne();
                        for (int j = 0; j < 100; ++j)
                            if (writer.TryEnqueue(10, delegate { return null; }, delegate { ++callbacks; }))
                                Interlocked.Increment(ref accepted);
                    }
                    catch (Exception error) { Interlocked.CompareExchange(ref threadError, error, null); }
                });
                threads[i].Start();
            }
            start.Set();
            foreach (Thread thread in threads) Check(thread.Join(5000), "concurrent admission thread finished");
            Check(threadError == null && accepted == 9, "concurrent admission cannot exceed job or byte bound");
            writer.Barrier();
            Check(writer.PendingCount == 9 && writer.PendingBytes == 90 && callbacks == 0, "concurrent results remain charged until main thread drains");
            writer.Drain();
            Check(callbacks == 9 && writer.IsIdle, "all concurrently admitted jobs completed once");
        }
    }

    private static void CompletionFailuresAndThreadRules()
    {
        using (var writer = new SnapshotWriter(8, 100))
        {
            int callbacks = 0;
            var error = new InvalidOperationException("callback failed");
            writer.TryEnqueue(10, delegate { return null; }, delegate { ++callbacks; throw error; });
            writer.TryEnqueue(10, delegate { return null; }, delegate { ++callbacks; });
            writer.Barrier();
            Exception caught = null;
            try { writer.Drain(); } catch (Exception failure) { caught = failure; }
            Check(object.ReferenceEquals(caught, error), "callback failure rethrown to caller");
            Check(callbacks == 2 && writer.IsIdle && writer.PendingBytes == 0, "one callback failure does not lose remaining results or capacity");
            writer.TryEnqueue(10, delegate
            {
                Refuse(delegate { writer.Barrier(); }, "worker self barrier");
                Refuse(delegate { writer.Dispose(); }, "worker self dispose");
                return null;
            }, delegate
            {
                Refuse(delegate { writer.Drain(); }, "recursive completion dispatch");
                ++callbacks;
            });
            writer.Barrier();
            Exception wrongThread = null;
            var thread = new Thread(delegate()
            {
                try { writer.Drain(); } catch (Exception failure) { wrongThread = failure; }
            });
            thread.Start();
            Check(thread.Join(5000), "wrong-thread drain returned");
            Check(wrongThread is InvalidOperationException && writer.PendingCount == 1, "wrong-thread drain refuses before removing results");
            writer.Drain();
            Check(callbacks == 3 && writer.IsIdle, "owner can drain after wrong-thread attempt");
        }
    }

    private static void ShutdownWaitsAndPreservesResults()
    {
        var writer = new SnapshotWriter(4, 100);
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var disposeStarted = new ManualResetEvent(false))
        using (var disposeDone = new ManualResetEvent(false))
        {
            int writes = 0;
            int callbacks = 0;
            Exception disposeError = null;
            writer.TryEnqueue(20, delegate
            {
                entered.Set();
                if (!release.WaitOne(5000)) throw new TimeoutException("shutdown disk was not released");
                ++writes; return null;
            }, delegate { ++callbacks; });
            writer.TryEnqueue(20, delegate { ++writes; return null; }, delegate { ++callbacks; });
            Wait(entered, "shutdown worker started");
            var disposer = new Thread(delegate()
            {
                disposeStarted.Set();
                try { writer.Dispose(); } catch (Exception error) { disposeError = error; }
                finally { disposeDone.Set(); }
            });
            disposer.Start();
            Wait(disposeStarted, "shutdown requested");
            try { Check(!disposeDone.WaitOne(100), "Dispose waits for blocked admitted disk work"); }
            finally { release.Set(); }
            Wait(disposeDone, "shutdown finished after disk released");
            Check(disposer.Join(5000) && disposeError == null && writes == 2, "shutdown preserves and finishes every admitted write");
            Check(callbacks == 0 && writer.PendingCount == 2 && !writer.IsIdle, "shutdown does not dispatch main-thread callbacks");
            Check(!writer.TryEnqueue(1, delegate { return null; }, delegate {}), "shutdown stops new admissions");
            Check(!writer.CanEnqueue(0), "capacity query refuses all admissions after shutdown");
            writer.Barrier();
            Check(writer.Drain() == 2 && callbacks == 2 && writer.IsIdle, "completions remain drainable after Dispose");
            writer.Dispose();
            Check(writer.IsIdle, "Dispose is idempotent");
        }
    }

    public static int Main()
    {
        try
        {
            BlockedIoDoesNotBlockMain();
            FifoAndFailure();
            BoundsAndValidation();
            CompletionFailuresAndThreadRules();
            ShutdownWaitsAndPreservesResults();
            Console.WriteLine("PASS: SnapshotWriter " + passed + " assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
