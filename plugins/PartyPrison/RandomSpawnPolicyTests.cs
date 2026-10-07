using System;
using ValheimModPack.PartyPrison;

internal static class RandomSpawnPolicyTests
{
    private static int checks;
    private static void Check(bool value, string label)
    { ++checks; if (!value) throw new Exception(label); }
    private static void Reject(Action action, string label)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, label); }
    private static PrisonRegion Region(double radius)
    {
        return new PrisonRegion { Center = new PrisonPoint(80d, 12d, -50d),
            CellSpawn = new PrisonPoint(66d, 7d, -50d), Radius = radius, HalfHeight = 8d };
    }

    public static int Main()
    {
        try {
            foreach (double radius in new[] { 18d, ArenaGeometry.ExpandedRadius }) {
                PrisonRegion region = Region(radius);
                double minimum = ArenaGeometry.Divider(region) + ArenaSpawnGeometry.WallMargin;
                double maximum = ArenaGeometry.RoomHalfWidth(region) - ArenaSpawnGeometry.WallMargin;
                PrisonPoint low = ArenaSpawnGeometry.Sample(region, 0d, 0d);
                PrisonPoint high = ArenaSpawnGeometry.Sample(region, 1d, 1d);
                Check(low.X == minimum && low.Z == minimum && high.X == maximum && high.Z == maximum,
                    "old and expanded arena sampling reaches every safe boundary");
                PrisonPoint middle = ArenaSpawnGeometry.Sample(region, .5d, .5d);
                Check(Math.Abs(middle.X - (minimum + maximum) * .5d) < 1e-12d
                    && middle.X == middle.Z && middle.Y == 1d, "uniform sample includes the center rather than fixed corners");
                var random = new Random(7834); var bins = new bool[9];
                for (int i = 0; i < 256; ++i) {
                    PrisonPoint point = ArenaSpawnGeometry.Sample(region, random.NextDouble(), random.NextDouble());
                    Check(point.X >= minimum && point.X <= maximum && point.Z >= minimum && point.Z <= maximum,
                        "seeded candidate remains within the full safe arena rectangle");
                    int x = Math.Min(2, (int)(3d * (point.X - minimum) / (maximum - minimum)));
                    int z = Math.Min(2, (int)(3d * (point.Z - minimum) / (maximum - minimum)));
                    bins[x * 3 + z] = true;
                }
                foreach (bool used in bins) Check(used, "fixed-seed candidates cover every third of both arena axes");
                PrisonPoint first, repeat;
                Check(ArenaSpawnGeometry.TryChoose(region, () => .42d, point => true, out first)
                    && ArenaSpawnGeometry.TryChoose(region, () => .42d, point => true, out repeat)
                    && first.X == repeat.X && first.Z == repeat.Z,
                    "independent enemies may select exactly the same location without forced spread");
                int draws = 0, probes = 0; PrisonPoint blocked;
                Check(!ArenaSpawnGeometry.TryChoose(region, () => { ++draws; return .5d; },
                    point => { ++probes; return false; }, out blocked), "fully obstructed arena refuses a collision");
                Check(probes == ArenaSpawnGeometry.MaximumAttempts && draws == probes * 2
                    && blocked.X == 0d && blocked.Y == 0d && blocked.Z == 0d,
                    "obstruction performs exactly the bounded collision and random draw budget");
                draws = probes = 0;
                Check(ArenaSpawnGeometry.TryChoose(region, () => { ++draws; return draws % 2 == 0 ? .7d : .2d; },
                    point => ++probes == 4, out blocked) && probes == 4 && draws == 8,
                    "selection retries independently and stops immediately at the first free point");
                Check(blocked.X == minimum + (maximum - minimum) * .2d
                    && blocked.Z == minimum + (maximum - minimum) * .7d,
                    "collision retry preserves the chosen two-dimensional sample");
                foreach (double invalid in new[] { -.001d, 1.001d, Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity }) {
                    double value = invalid;
                    Reject(() => ArenaSpawnGeometry.Sample(region, value, .5d), "invalid horizontal random sample is rejected");
                    Reject(() => ArenaSpawnGeometry.Sample(region, .5d, value), "invalid depth random sample is rejected");
                }
            }
            Reject(() => ArenaSpawnGeometry.Sample(null, .5d, .5d), "missing arena is rejected");
            PrisonRegion invalidRegion = Region(Double.NaN);
            Reject(() => ArenaSpawnGeometry.Sample(invalidRegion, .5d, .5d), "non-finite radius cannot publish a candidate");
            invalidRegion = Region(26d); invalidRegion.Center.X = Double.PositiveInfinity;
            Reject(() => ArenaSpawnGeometry.Sample(invalidRegion, .5d, .5d), "non-finite center cannot publish a candidate");
            invalidRegion = Region(26d); invalidRegion.CellSpawn.Y = Double.NaN;
            Reject(() => ArenaSpawnGeometry.Sample(invalidRegion, .5d, .5d), "non-finite floor cannot publish a candidate");
            Console.WriteLine("PASS: " + checks + " independent random arena spawn policy checks."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
}
