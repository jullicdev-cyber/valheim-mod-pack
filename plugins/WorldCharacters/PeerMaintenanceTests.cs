using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ValheimModPack.WorldCharacters;

internal static class PeerMaintenanceTests
{
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); ++checks; }
    private static void Refuse(double interval)
    {
        try { new PeerMaintenance<int, object>(interval); }
        catch (ArgumentOutOfRangeException) { ++checks; return; }
        throw new Exception("Accepted invalid maintenance period.");
    }
    private static int Main()
    {
        try
        {
            foreach (double invalid in new[] { 0, -1, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity }) Refuse(invalid);
            var peers = new Dictionary<int, object>(); var a = new object(); var b = new object(); var c = new object();
            peers.Add(1, a); peers.Add(2, b);
            var maintenance = new PeerMaintenance<int, object>(.5);
            Check(maintenance.TryCapture(peers, 0) && maintenance.Count == 2, "First frame captures peers.");
            Check(ReferenceEquals(maintenance[0], a) && ReferenceEquals(maintenance[1], b), "Capture retains identity, without copying session state.");
            peers.Clear(); peers.Add(3, c);
            Check(maintenance.Count == 2 && ReferenceEquals(maintenance[1], b), "Disconnect during rejection does not invalidate current view.");
            Check(!maintenance.TryCapture(peers, .499) && maintenance.Count == 2, "No rescan before maintenance deadline.");
            Check(maintenance.TryCapture(peers, .5) && maintenance.Count == 1 && ReferenceEquals(maintenance[0], c), "New connections captured at next deadline.");
            Check(!maintenance.TryCapture(peers, .51), "A scan cannot repeat every game frame.");
            Check(maintenance.TryCapture(peers, 90) && !maintenance.TryCapture(peers, 90.1), "Long stalls do not cause catch-up scans.");
            maintenance.Reset(); Check(maintenance.Count == 0 && maintenance.TryCapture(peers, 1), "Network reset clears references and time budget.");
            peers.Clear(); Check(maintenance.TryCapture(peers, 1.5) && maintenance.Count == 0, "Departed peers no longer retained.");

            // This exercises the exact production buffer, not a duplicated loop.
            // Timings describe managed CLR maintenance only, never live FPS.
            for (int i = 0; i < 8; ++i) peers.Add(i, new object());
            maintenance.Reset(); maintenance.TryCapture(peers, 0);
            AppDomain.MonitoringIsEnabled = true;
            const int iterations = 100000;
            var clock = Stopwatch.StartNew(); long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 1; i <= iterations; ++i)
            { maintenance.TryCapture(peers, i * .5); if (maintenance.Count != 8) throw new Exception("Peers lost in warm maintenance."); }
            long cachedBytes = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before; double cachedMs = clock.Elapsed.TotalMilliseconds;
            Check(cachedBytes < 1024, "Warm eight-peer maintenance must not allocate arrays/enumerators.");
            // Previous production implementation called Values.ToArray every
            // frame. Warm it before measuring the comparison baseline.
            peers.Values.ToArray(); before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize; clock.Restart();
            for (int i = 0; i < iterations; ++i) if (peers.Values.ToArray().Length != 8) throw new Exception("Invalid baseline.");
            long previousBytes = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before; double previousMs = clock.Elapsed.TotalMilliseconds;
            Check(previousBytes > 1024 * 1024, "Baseline exposes per-frame peer-array allocation.");
            Console.WriteLine("PASS: " + checks + " peer maintenance checks. Managed CLR 100000 eight-peer passes: cached=" + cachedBytes + " B / " + cachedMs.ToString("F2") + " ms; former ToArray=" + previousBytes + " B / " + previousMs.ToString("F2") + " ms. Not an FPS test.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
