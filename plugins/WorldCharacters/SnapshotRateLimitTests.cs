using System;
using ValheimModPack.WorldCharacters;

internal static class SnapshotRateLimitTests
{
    private static int passed;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        ++passed;
    }

    private static void Refuse(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { ++passed; return; }
        throw new Exception("Did not refuse: " + message);
    }

    private static void Burst(SnapshotRateLimit limiter, double seconds, int count, string message)
    {
        for (int i = 0; i < count; ++i)
            Check(limiter.TryTake(seconds), message + ": snapshot " + (i + 1));
        Check(!limiter.TryTake(seconds), message + ": budget exhausted");
    }

    private static void CompletedQueueAndRefill()
    {
        var limiter = new SnapshotRateLimit();
        Burst(limiter, 0, 64, "a complete writer queue fits one drain");
        Check(!limiter.TryTake(0), "another drain in the same frame cannot renew the budget");
        Check(!limiter.TryTake(0.199), "less than 0.2 seconds does not refill one snapshot");
        Check(limiter.TryTake(0.2), "0.2 seconds refills one snapshot");
        Check(!limiter.TryTake(0.2), "the refilled snapshot can be consumed only once");
        Check(!limiter.TryTake(0.399999999), "fractional refill remains below a whole snapshot");
        Check(limiter.TryTake(0.4), "refill survives refused attempts between snapshots");
    }

    private static void DrainTimingAndSteadyRate()
    {
        var batched = new SnapshotRateLimit();
        var steady = new SnapshotRateLimit();
        Burst(batched, 0, 64, "initial batch");
        Burst(steady, 0, 64, "initial steady budget");
        for (int second = 1; second <= 10; ++second)
        {
            // The same five admissions arrive together after a worker drain,
            // or separately every 0.2 seconds while the main thread keeps up.
            Burst(batched, second, 5, "batched refill at second " + second);
            for (int tick = 1; tick <= 5; ++tick)
            {
                double seconds = second - 1 + tick / 5.0;
                Check(steady.TryTake(seconds), "steady five-per-second admission at " + seconds);
                Check(!steady.TryTake(seconds), "repeated steady drain cannot mint tokens at " + seconds);
            }
        }
        Check(!batched.TryTake(10) && !steady.TryTake(10), "both drain patterns consume the same refill budget");
    }

    private static void BoundedRefillAndCustomLimits()
    {
        var limiter = new SnapshotRateLimit();
        Burst(limiter, 12, 64, "initial bucket at a nonzero clock");
        Burst(limiter, 1000000, 64, "a long idle gap refills only the capacity");
        Check(!limiter.TryTake(1000000), "long idle gap does not accumulate a second bucket");
        Burst(limiter, 1000001, 5, "default refill continues after capacity saturation");

        var custom = new SnapshotRateLimit(3, 2);
        Burst(custom, -10, 3, "custom capacity accepts a finite initial clock");
        Check(!custom.TryTake(-9.75), "custom refill keeps fractional tokens");
        Check(custom.TryTake(-9.5), "custom refill admits one snapshot after half a second");
        Burst(custom, 0, 3, "custom long-gap refill is capped");

        var fast = new SnapshotRateLimit(2, Double.MaxValue);
        Burst(fast, 0, 2, "largest finite refill rate starts with a bounded bucket");
        Burst(fast, 2, 2, "overflowing refill multiplication is capped");

        var slow = new SnapshotRateLimit(4, 1e-308);
        Burst(slow, -Double.MaxValue, 4, "small refill starts full");
        Burst(slow, Double.MaxValue, 3, "overflowing elapsed time retains the small refill rate");
    }

    private static void ClockValidation()
    {
        var limiter = new SnapshotRateLimit(1, 5);
        Check(!limiter.TryTake(Double.NaN), "NaN cannot consume the initial bucket");
        Check(!limiter.TryTake(Double.PositiveInfinity), "positive infinity cannot consume the initial bucket");
        Check(!limiter.TryTake(Double.NegativeInfinity), "negative infinity cannot consume the initial bucket");
        Check(limiter.TryTake(10), "a valid clock still receives the initial bucket");
        Check(!limiter.TryTake(9), "a backwards clock cannot refill an exhausted bucket");
        Check(!limiter.TryTake(9.2), "advancing within a backwards interval cannot refill");
        Check(!limiter.TryTake(10), "a backwards clock cannot lower the high-water timestamp");
        Check(!limiter.TryTake(Double.NaN), "NaN cannot refill an exhausted bucket");
        Check(!limiter.TryTake(Double.PositiveInfinity), "positive infinity cannot refill an exhausted bucket");
        Check(!limiter.TryTake(Double.NegativeInfinity), "negative infinity cannot refill an exhausted bucket");
        Check(!limiter.TryTake(10.1), "invalid timestamps cannot renew the initial bucket");
        Check(limiter.TryTake(10.2), "valid elapsed time survives invalid timestamp attempts");
        Check(!limiter.TryTake(10.2), "clock recovery refills exactly one token");

        var partiallyFull = new SnapshotRateLimit(2, 5);
        Check(partiallyFull.TryTake(10), "first token at a valid timestamp");
        Check(!partiallyFull.TryTake(9), "backwards timestamps are refused even when tokens remain");
        Check(!partiallyFull.TryTake(Double.NaN), "nonfinite timestamps are refused even when tokens remain");
        Check(partiallyFull.TryTake(10), "invalid attempts do not spend an available token");
        Check(!partiallyFull.TryTake(10), "invalid attempts do not create extra tokens");
    }

    private static void ConstructorValidation()
    {
        Refuse(delegate { new SnapshotRateLimit(0); }, "zero capacity");
        Refuse(delegate { new SnapshotRateLimit(-1); }, "negative capacity");
        Refuse(delegate { new SnapshotRateLimit(1, 0); }, "zero refill");
        Refuse(delegate { new SnapshotRateLimit(1, -1); }, "negative refill");
        Refuse(delegate { new SnapshotRateLimit(1, Double.NaN); }, "NaN refill");
        Refuse(delegate { new SnapshotRateLimit(1, Double.PositiveInfinity); }, "infinite refill");
        Refuse(delegate { new SnapshotRateLimit(1, Double.NegativeInfinity); }, "negative infinite refill");
        var slowest = new SnapshotRateLimit(1, Double.Epsilon);
        Check(slowest.TryTake(0), "smallest positive finite refill is valid");
        Check(!slowest.TryTake(Double.MaxValue), "smallest refill cannot overflow into a full bucket");
        var largest = new SnapshotRateLimit(Int32.MaxValue);
        Check(largest.TryTake(0) && largest.TryTake(0), "largest integer capacity is valid");
    }

    private static void Main()
    {
        CompletedQueueAndRefill();
        DrainTimingAndSteadyRate();
        BoundedRefillAndCustomLimits();
        ClockValidation();
        ConstructorValidation();
        Console.WriteLine("PASS: " + passed + " snapshot rate-limit assertions.");
    }
}
