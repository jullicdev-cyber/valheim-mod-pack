using System;

namespace ValheimModPack.WorldCharacters
{
    // A full bucket admits one completed writer queue in a single main-thread
    // drain. Subsequent drains share the same five-per-second refill budget.
    internal sealed class SnapshotRateLimit
    {
        private readonly int capacity;
        private readonly double refillPerSecond;
        private double tokens;
        private double lastSeconds;
        private bool hasTime;

        internal SnapshotRateLimit(int capacity = 64, double refillPerSecond = 5)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            if (refillPerSecond <= 0 || Double.IsNaN(refillPerSecond) || Double.IsInfinity(refillPerSecond))
                throw new ArgumentOutOfRangeException("refillPerSecond");
            this.capacity = capacity;
            this.refillPerSecond = refillPerSecond;
            tokens = capacity;
        }

        // The caller supplies monotonic seconds and owns this instance's thread.
        // Invalid time never spends tokens or moves the refill clock backwards.
        internal bool TryTake(double monotonicSeconds)
        {
            if (Double.IsNaN(monotonicSeconds) || Double.IsInfinity(monotonicSeconds)) return false;
            if (hasTime && monotonicSeconds < lastSeconds) return false;
            if (hasTime)
            {
                double elapsed = monotonicSeconds - lastSeconds;
                // Finite endpoints can still overflow their difference. Scaling
                // them first preserves a very small configured refill rate.
                double refill = Double.IsInfinity(elapsed)
                    ? monotonicSeconds * refillPerSecond - lastSeconds * refillPerSecond
                    : elapsed * refillPerSecond;
                tokens = Math.Min(capacity, tokens + refill);
            }
            lastSeconds = monotonicSeconds;
            hasTime = true;

            // Repeated 0.2-second ticks can round one token slightly below one.
            // Keep that arithmetic noise from rejecting the steady refill rate.
            if (tokens < 1 - 1e-12) return false;
            tokens = Math.Max(0, tokens - 1);
            return true;
        }
    }
}
