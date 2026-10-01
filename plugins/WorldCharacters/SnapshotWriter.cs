using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ValheimModPack.WorldCharacters
{
    // Only detached managed data may be captured by work. Unity objects and game
    // callbacks belong to the creating thread and are never used by the worker.
    internal sealed class SnapshotWriter : IDisposable
    {
        private sealed class Job
        {
            internal long Sequence;
            internal long RetainedBytes;
            internal Func<object> Work;
            internal Action<object, Exception, double> Completion;
            internal object Result;
            internal Exception Error;
            internal double ElapsedMilliseconds;
        }

        private readonly object gate = new object();
        private readonly Queue<Job> waiting = new Queue<Job>();
        private readonly Queue<Job> completed = new Queue<Job>();
        private readonly Thread worker;
        private readonly int ownerThread;
        private readonly int maximumJobs;
        private readonly long maximumBytes;
        private int pendingCount;
        private long pendingBytes;
        private long admittedSequence;
        private long finishedSequence;
        private bool stopping;
        private bool draining;

        internal SnapshotWriter(int maxJobs = 64, long maxBytes = 64L * 1024 * 1024)
        {
            if (maxJobs <= 0) throw new ArgumentOutOfRangeException("maxJobs");
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException("maxBytes");
            maximumJobs = maxJobs;
            maximumBytes = maxBytes;
            ownerThread = Thread.CurrentThread.ManagedThreadId;
            worker = new Thread(Run);
            worker.Name = "World Characters snapshot writer";
            worker.IsBackground = true;
            worker.Start();
        }

        internal int PendingCount { get { lock (gate) return pendingCount; } }
        internal long PendingBytes { get { lock (gate) return pendingBytes; } }
        internal bool IsIdle { get { lock (gate) return pendingCount == 0; } }

        // Advisory only when multiple producers exist. The game integration uses
        // one admission thread, so it can check this before consuming a revision.
        internal bool CanEnqueue(long retainedBytes)
        {
            if (retainedBytes < 0) throw new ArgumentOutOfRangeException("retainedBytes");
            lock (gate) return CanAdmit(retainedBytes);
        }

        private bool CanAdmit(long retainedBytes)
        {
            return !stopping && pendingCount < maximumJobs && retainedBytes <= maximumBytes - pendingBytes;
        }

        internal bool TryEnqueue(long retainedBytes, Func<object> work, Action<object, Exception, double> completion)
        {
            if (retainedBytes < 0) throw new ArgumentOutOfRangeException("retainedBytes");
            if (work == null) throw new ArgumentNullException("work");
            if (completion == null) throw new ArgumentNullException("completion");
            lock (gate)
            {
                // Subtraction avoids overflowing when a caller supplies a very
                // large byte count. Results stay charged until their callback ends.
                if (!CanAdmit(retainedBytes)) return false;
                var job = new Job { Sequence = ++admittedSequence, RetainedBytes = retainedBytes,
                    Work = work, Completion = completion };
                waiting.Enqueue(job);
                ++pendingCount;
                pendingBytes += retainedBytes;
                Monitor.PulseAll(gate);
                return true;
            }
        }

        private void Run()
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    while (waiting.Count == 0 && !stopping) Monitor.Wait(gate);
                    if (waiting.Count == 0) return;
                    job = waiting.Dequeue();
                }
                var watch = Stopwatch.StartNew();
                try { job.Result = job.Work(); }
                catch (Exception error) { job.Error = error; }
                finally
                {
                    job.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
                    job.Work = null;
                }
                lock (gate)
                {
                    completed.Enqueue(job);
                    finishedSequence = job.Sequence;
                    Monitor.PulseAll(gate);
                }
            }
        }

        // Waits for work admitted before this call. It deliberately does not
        // dispatch callbacks: callers must Drain on the creating game thread.
        internal void Barrier()
        {
            if (Thread.CurrentThread == worker)
                throw new InvalidOperationException("A snapshot worker cannot wait on itself.");
            lock (gate)
            {
                long target = admittedSequence;
                while (finishedSequence < target) Monitor.Wait(gate);
            }
        }

        // Bounded to the results available at entry; continuous admission cannot
        // make a frame spend unbounded time dispatching newly completed work.
        // Callback failures are rethrown after dispatching the other ready results.
        internal int Drain()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerThread)
                throw new InvalidOperationException("Snapshot completions require the creating thread.");
            if (draining) throw new InvalidOperationException("Snapshot completion dispatch cannot be recursive.");
            int ready;
            lock (gate) ready = completed.Count;
            Exception callbackError = null;
            draining = true;
            try
            {
                for (int i = 0; i < ready; ++i)
                {
                    Job job;
                    lock (gate) job = completed.Dequeue();
                    try { job.Completion(job.Result, job.Error, job.ElapsedMilliseconds); }
                    catch (Exception error) { if (callbackError == null) callbackError = error; }
                    finally
                    {
                        job.Result = null;
                        job.Error = null;
                        job.Completion = null;
                        lock (gate)
                        {
                            --pendingCount;
                            pendingBytes -= job.RetainedBytes;
                        }
                    }
                }
            }
            finally { draining = false; }
            if (callbackError != null) throw callbackError;
            return ready;
        }

        // Shutdown never cancels admitted work. Completions remain available to
        // Drain after disposal, because Dispose may be called off the game thread.
        public void Dispose()
        {
            if (Thread.CurrentThread == worker)
                throw new InvalidOperationException("A snapshot worker cannot dispose itself.");
            lock (gate)
            {
                stopping = true;
                Monitor.PulseAll(gate);
            }
            worker.Join();
        }
    }
}
