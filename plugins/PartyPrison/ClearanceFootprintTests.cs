using System;
using ValheimModPack.PartyPrison;

internal static class ClearanceFootprintTests
{
    private static int checks;
    private static void Check(bool condition, string description)
    { ++checks; if (!condition) throw new InvalidOperationException("FAIL: " + description); }

    private static bool Contains(double x, double z, double width, double depth, double yaw)
    { return ClearanceFootprint.ContainsRoot(x, z, width, depth, 0d, 0d, yaw, 14.5d); }

    private static void RootSize()
    {
        Check(Contains(0d, 0d, 31d, 31d, 0d), "a centred 31-metre rock fits within the allowed 37-metre footprint");
        Check(Contains(0d, 0d, 37d, 37d, 0d), "the complete 37-metre border is allowed");
        Check(!Contains(0d, 0d, 37.1d, 20d, 0d), "a root extending beyond the east/west limit is rejected");
        Check(!Contains(0d, 0d, 20d, 37.1d, 0d), "a root extending beyond the north/south limit is rejected");
        Check(Contains(0d, 0d, 29.1d, 10d, 0d), "the visible 29-metre query width does not impose a second whole-root size cap");
        Check(Contains(18.5d, 0d, 0d, 0d, 0d), "a point exactly four metres beyond the query edge is bounded");
        Check(!Contains(18.5001d, 0d, 0d, 0d, 0d), "a point just past the allowed overhang is rejected");
        Check(Contains(16.5d, 0d, 4d, 6d, 0d), "a displaced whole root may touch the outer boundary");
        Check(!Contains(16.5001d, 0d, 4d, 6d, 0d), "a displaced root's far corner is checked rather than only its centre");
        Check(!Contains(18d, 18d, 2d, 2d, 0d), "an in-range centre cannot hide both out-of-range corners");
        Check(Contains(-16.5d, 0d, 4d, 6d, 0d) && Contains(0d, -16.5d, 6d, 4d, 0d),
            "negative-axis roots use the same boundary");
        Check(ClearanceFootprint.MaximumEdgeOverhang == 4d && ClearanceFootprint.MaximumHeight == 100d,
            "whole-root overhang and independent vertical limit retain their finite bounds");
    }

    private static void Rotations()
    {
        Check(Contains(0d, 0d, 31d, 13d, 45d), "a long narrow rock fits inside the rotated expanded square");
        Check(!Contains(0d, 0d, 31d, 31d, 45d), "world AABB corners outside a rotated square are rejected");
        Check(Contains(0d, 0d, 37d, 10d, 90d), "a right-angle plot swaps horizontal axes without shrinking the boundary");
        Check(Contains(0d, 0d, 10d, 37d, -90d), "negative right-angle yaw is supported");
        foreach (double cardinal in new[] { 0d, 90d, 180d, 270d, -180d })
            Check(Contains(0d, 0d, 37d, 37d, cardinal), "a square touching the exact border remains bounded after a cardinal rotation");
        Check(Contains(12d, 12d, 1d, 1d, 45d), "diagonal displacement is transformed into the prison's local axes");
        Check(!Contains(14d, 14d, 1d, 1d, 45d), "diagonal displacement cannot cross the rotated outer edge");
        Check(Contains(12d, -12d, 1d, 1d, 45d), "the other rotated axis uses the same coverage rule");
        foreach (double yaw in new[] { 0d, 15d, 30d, 45d, 80d, 135d, -37d }) {
            Check(Contains(2d, -3d, 20d, 10d, yaw) == Contains(2d, -3d, 20d, 10d, yaw + 360d),
                "equivalent positive full-turn yaw gives the same decision");
            Check(Contains(2d, -3d, 20d, 10d, yaw) == Contains(2d, -3d, 20d, 10d, yaw - 720d),
                "equivalent negative full-turn yaw gives the same decision");
        }
        // The real oak's solid LOD collider bounds fit at 45 degrees; its
        // viewblock bounds belong to visibility handling and must be filtered first.
        Check(Contains(0d, 0d, 16.121d, 20.957d, 45d), "native oak physical bounds fit the expanded rotated footprint");
        Check(!Contains(0d, 0d, 30.576d, 35.856d, 45d), "oak viewblock bounds explain the historical false size rejection");
    }

    private static void TranslationAndExtent()
    {
        Check(ClearanceFootprint.ContainsRoot(1016.5d, -2000d, 4d, 6d, 1000d, -2000d, 0d, 14.5d),
            "world-coordinate translation preserves a touching boundary");
        Check(!ClearanceFootprint.ContainsRoot(1016.501d, -2000d, 4d, 6d, 1000d, -2000d, 0d, 14.5d),
            "translation preserves an out-of-bounds decision");
        Check(ClearanceFootprint.ContainsRoot(-1000d, 2000d, 31d, 13d, -1000d, 2000d, 45d, 14.5d),
            "nonzero mixed-sign coordinates support a rotated plot");
        Check(ClearanceFootprint.ContainsRoot(0d, 0d, 32d, 32d, 0d, 0d, 0d, 12d), "smallest allowed query extent keeps the four-metre skirt");
        Check(ClearanceFootprint.ContainsRoot(0d, 0d, 48d, 48d, 0d, 0d, 0d, 20d), "largest query extent remains bounded");
        Check(!ClearanceFootprint.ContainsRoot(0d, 0d, 0d, 0d, 0d, 0d, 0d, 11.999d), "too-small clearance extent is rejected");
        Check(!ClearanceFootprint.ContainsRoot(0d, 0d, 0d, 0d, 0d, 0d, 0d, 20.001d), "too-large clearance extent is rejected");
    }

    private static void InvalidInputs()
    {
        Check(!Contains(0d, 0d, -1d, 1d, 0d) && !Contains(0d, 0d, 1d, -1d, 0d), "negative dimensions cannot reverse the coverage check");
        foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity }) {
            Check(!Contains(bad, 0d, 4d, 4d, 0d), "invalid x coordinates reject the root");
            Check(!Contains(0d, bad, 4d, 4d, 0d), "invalid z coordinates reject the root");
            Check(!Contains(0d, 0d, bad, 4d, 0d), "invalid widths reject the root");
            Check(!Contains(0d, 0d, 4d, bad, 0d), "invalid depths reject the root");
            Check(!Contains(0d, 0d, 4d, 4d, bad), "invalid yaw rejects the root");
            Check(!ClearanceFootprint.ContainsRoot(0d, 0d, 4d, 4d, bad, 0d, 0d, 14.5d), "invalid origin x rejects the root");
            Check(!ClearanceFootprint.ContainsRoot(0d, 0d, 4d, 4d, 0d, bad, 0d, 14.5d), "invalid origin z rejects the root");
            Check(!ClearanceFootprint.ContainsRoot(0d, 0d, 4d, 4d, 0d, 0d, 0d, bad), "invalid extent rejects the root");
        }
        Check(!ClearanceFootprint.ContainsRoot(1E100d, 1E100d, 0d, 0d, 1E100d, 1E100d, 0d, 14.5d),
            "equally huge coordinates cannot cancel into a seemingly valid local point");
        Check(!Contains(Double.MaxValue, -Double.MaxValue, 4d, 4d, 45d), "overflowing coordinate differences fail closed");
        Check(!Contains(0d, 0d, Double.MaxValue, Double.MaxValue, 45d), "huge dimensions cannot overflow into a valid footprint");
    }

    public static int Main()
    {
        try {
            RootSize(); Rotations(); TranslationAndExtent(); InvalidInputs();
            Console.WriteLine("PASS: " + checks + " prison clearance footprint checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
