using System;
using ValheimModPack.PartyPrison;

internal static class PlacementPlanTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new InvalidOperationException("FAIL: " + message); }

    private static void Near(double actual, double expected, string message)
    { Check(Math.Abs(actual - expected) < 0.00000001d, message); }

    private static void Reject(Action action, string message)
    {
        ++checks;
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("FAIL: accepted " + message);
    }

    private static PrisonPlacementPlan Plan(PrisonPoint host, PrisonPoint look)
    { return PrisonPlacementPlan.Create(host, look, new PrisonPoint(0d, 0d, 1d)); }

    private static void Directions()
    {
        PrisonPoint host = new PrisonPoint(100d, 45d, -200d);
        PrisonPlacementPlan north = Plan(host, new PrisonPoint(0d, 0d, 1d));
        Near(north.Origin.X, 100d, "looking north keeps the host's east/west coordinate");
        Near(north.Origin.Z, -160d, "looking north places the expanded prison40 metres ahead");
        Near(north.FacingYawDegrees, 270d, "northward local +X leaves the western door facing south toward the host");
        PrisonPlacementPlan east = Plan(host, new PrisonPoint(1d, 0d, 0d));
        Near(east.Origin.X, 140d, "looking east places the prison along the look ray");
        Near(east.Origin.Z, -200d, "looking east does not displace the plot sideways");
        Near(east.FacingYawDegrees, 0d, "eastward local +X faces the direction of look");
        PrisonPlacementPlan south = Plan(host, new PrisonPoint(0d, 0d, -1d));
        Near(south.Origin.Z, -240d, "looking south places the prison ahead rather than behind the host");
        Near(south.FacingYawDegrees, 90d, "southward prison entrance faces north");
        PrisonPlacementPlan west = Plan(host, new PrisonPoint(-1d, 0d, 0d));
        Near(west.Origin.X, 60d, "looking west places the prison at a negative world displacement");
        Near(west.FacingYawDegrees, 180d, "westward prison entrance faces east");
        PrisonPlacementPlan northeast = Plan(host, new PrisonPoint(1d, 0d, 1d));
        Near(northeast.Origin.X - host.X, 40d / Math.Sqrt(2d), "diagonal horizontal direction is normalized");
        Near(northeast.Origin.Z - host.Z, 40d / Math.Sqrt(2d), "diagonal target remains on the same look ray");
        Near(northeast.FacingYawDegrees, 315d, "diagonal local +X matches the host's look");
    }

    private static void PitchAndFallback()
    {
        PrisonPoint host = new PrisonPoint(0d, 80d, 0d), east = new PrisonPoint(1d, 0d, 0d);
        foreach (double pitch in new[] { -100d, -1d, 0d, 1d, 100d }) {
            PrisonPlacementPlan plan = Plan(host, new PrisonPoint(3d, pitch, 4d));
            Near(plan.Origin.X, 24d, "camera pitch does not change east/west placement");
            Near(plan.Origin.Z, 32d, "camera pitch does not change north/south placement");
            Near(plan.Origin.Y, 80d, "placement preserves the host elevation for native terrain planning");
        }
        foreach (PrisonPoint vertical in new[] { new PrisonPoint(0d, 1d, 0d), new PrisonPoint(0d, -1d, 0d),
            new PrisonPoint(0d, 0d, 0d), new PrisonPoint(0.000000001d, 1d, -0.000000001d) }) {
            PrisonPlacementPlan fallback = PrisonPlacementPlan.Create(host, vertical, east);
            Near(fallback.Origin.X, 40d, "vertical or zero look uses the retained native yaw");
            Near(fallback.Origin.Z, 0d, "vertical fallback does not choose an unrelated compass direction");
        }
        PrisonPlacementPlan scaled = Plan(host, new PrisonPoint(3E200d, 0d, 4E200d));
        Near(scaled.DirectionX, 0.6d, "scaled finite look avoids squaring overflow");
        Near(scaled.DirectionZ, 0.8d, "scaled finite look preserves the correct direction");
        Reject(delegate { PrisonPlacementPlan.Create(host, new PrisonPoint(), new PrisonPoint()); }, "two absent horizontal directions");
        Reject(delegate { PrisonPlacementPlan.Create(host, new PrisonPoint(0d, 1d, 0d), new PrisonPoint(0d, -1d, 0d)); }, "two vertical directions");
    }

    private static void ClearanceAndSnapshot()
    {
        PrisonPoint host = new PrisonPoint(-1500d, 38d, 2000d);
        foreach (double degrees in new[] { 0d, 15d, 45d, 80d, 90d, 135d, 180d, 230d, 270d, 315d, 359d }) {
            double angle = degrees * Math.PI / 180d;
            PrisonPlacementPlan plan = Plan(host, new PrisonPoint(Math.Cos(angle), 0d, Math.Sin(angle)));
            double dx = plan.Origin.X - host.X, dz = plan.Origin.Z - host.Z;
            Near(Math.Sqrt(dx * dx + dz * dz), 40d, "placement distance is stable for every compass heading");
            Near(plan.DirectionX * plan.DirectionX + plan.DirectionZ * plan.DirectionZ, 1d, "horizontal placement direction has unit length");
            double yaw = plan.FacingYawDegrees * Math.PI / 180d;
            Near(Math.Cos(yaw), plan.DirectionX, "Unity's local +X faces the captured horizontal look");
            Near(-Math.Sin(yaw), plan.DirectionZ, "Unity's yaw convention preserves the captured look direction");
            double hostLocalX = (host.X - plan.Origin.X) * plan.DirectionX + (host.Z - plan.Origin.Z) * plan.DirectionZ;
            Near(hostLocalX, -40d, "the host stays beyond the complete expanded clearance square");
            Check(Math.Abs(hostLocalX) > 20.5d * Math.Sqrt(2d) + 4d + 1d, "the host's physical collider is clear of the largest rotated construction footprint");
            // The expanded public-lobby doorway is at local(-18,-14).
            double entranceX = plan.Origin.X - 18d * plan.DirectionX + 14d * plan.DirectionZ;
            double entranceZ = plan.Origin.Z - 18d * plan.DirectionZ - 14d * plan.DirectionX;
            double entranceForward = (entranceX - host.X) * plan.DirectionX + (entranceZ - host.Z) * plan.DirectionZ;
            Near(entranceForward, 22d, "the western public entrance lies ahead of the host");
            Check((host.X - entranceX) * -plan.DirectionX + (host.Z - entranceZ) * -plan.DirectionZ > 0d,
                "the public entrance opens toward the host");
        }
        PrisonPoint look = new PrisonPoint(1d, 0d, 0d);
        PrisonPlacementPlan snapshot = Plan(host, look);
        host.X = 0d; look.Z = 1d;
        PrisonPoint originCopy = snapshot.Origin; originCopy.X = 999d;
        Near(snapshot.Origin.X, -1460d, "confirmation snapshot is unaffected by later host, look, or returned-point mutation");
        Near(snapshot.DirectionZ, 0d, "confirmation retains the original direction");
    }

    private static void InvalidInputs()
    {
        PrisonPoint origin = new PrisonPoint(0d, 40d, 0d), look = new PrisonPoint(1d, 0d, 0d);
        foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, 1E100d }) {
            Reject(delegate { Plan(new PrisonPoint(bad, 40d, 0d), look); }, "invalid host x");
            Reject(delegate { Plan(new PrisonPoint(0d, bad, 0d), look); }, "invalid host elevation");
            Reject(delegate { Plan(new PrisonPoint(0d, 40d, bad), look); }, "invalid host z");
        }
        foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity }) {
            Reject(delegate { Plan(origin, new PrisonPoint(bad, 0d, 1d)); }, "nonfinite look x");
            Reject(delegate { Plan(origin, new PrisonPoint(0d, bad, 1d)); }, "nonfinite look pitch");
            Reject(delegate { Plan(origin, new PrisonPoint(1d, 0d, bad)); }, "nonfinite look z");
            Reject(delegate { PrisonPlacementPlan.Create(origin, look, new PrisonPoint(bad, 0d, 1d)); }, "nonfinite fallback x");
            Reject(delegate { PrisonPlacementPlan.Create(origin, look, new PrisonPoint(1d, bad, 0d)); }, "nonfinite fallback pitch");
            Reject(delegate { PrisonPlacementPlan.Create(origin, look, new PrisonPoint(0d, 0d, bad)); }, "nonfinite fallback z");
        }
        Reject(delegate { Plan(new PrisonPoint(19990d, 40d, 0d), look); }, "target beyond the permitted world coordinate bound");
        Near(Plan(new PrisonPoint(19990d, 40d, 0d), new PrisonPoint(-1d, 0d, 0d)).Origin.X, 19950d,
            "an inward-facing placement near the coordinate boundary remains usable");
    }

    private static void ExpandedArena()
    {
        var current = new PrisonRegion { Radius = 26, Center = new PrisonPoint(0, 6, 0),
            CellSpawn = new PrisonPoint(-14, 1, 0), ArenaSpawn = new PrisonPoint(4, 1, 4), HalfHeight = 8 };
        var legacy = new PrisonRegion { Radius = 18, Center = new PrisonPoint(0, 4, 0),
            CellSpawn = new PrisonPoint(-8, 1, 0), ArenaSpawn = new PrisonPoint(4, 1, 4), HalfHeight = 8 };
        double area = Math.Pow(ArenaGeometry.RoomHalfWidth(current) - ArenaGeometry.Divider(current), 2d);
        double oldArea = Math.Pow(ArenaGeometry.RoomHalfWidth(legacy) - ArenaGeometry.Divider(legacy), 2d);
        Check(area >= oldArea * 3d && area <= oldArea * 3.1d, "new arena is three times the former usable rectangle within native block rounding");
        Near(ArenaGeometry.RoomHeight(current) - ArenaGeometry.RoomHeight(legacy), 4d, "roof is two native2m stone blocks higher");
        Check(!ArenaGeometry.Expanded(legacy) && !ArenaGeometry.Expanded(null), "saved old regions never silently acquire expanded bounds");
        var anchors = new System.Collections.Generic.List<PrisonPoint>();
        for (int index = 0; index < 8; ++index) {
            PrisonPoint point = ArenaGeometry.Spawn(current, index); anchors.Add(point);
            Check(point.X >= -8d && point.X <= 16d && point.Z >= -8d && point.Z <= 16d,
                "enemy spawn leaves at least2m inside arena walls and dividers");
            Check(point.Y == 1d, "ground enemies share finished floor clearance");
            foreach (PrisonPoint earlier in anchors)
                if (earlier.X != point.X || earlier.Z != point.Z)
                    Check(Math.Pow(earlier.X - point.X, 2d) + Math.Pow(earlier.Z - point.Z, 2d) >= 16d,
                        "distinct spawn approaches do not overlap a large ground enemy capsule");
            // Obstacles use their complete retained bodies, not only top decks.
            Check(!NearRectangle(point, -4d, 2d, 8d, 14d, 1d) && !NearRectangle(point, 8d, 14d, -2d, 4d, 1d),
                "spawn never intersects either raised island body");
            Check(!NearRectangle(point, -1d, 3d, -.5d, .5d, 1d) && !NearRectangle(point, 7.5d, 8.5d, 7d, 11d, 1d),
                "spawn never intersects either independent cover wall");
        }
        Check((anchors[0].X - 4d) * (anchors[1].X - 4d) < 0d && (anchors[0].Z - 4d) * (anchors[1].Z - 4d) < 0d,
            "a two-enemy wave begins at opposite approaches");
        for (int index = 0; index < 8; ++index) {
            PrisonPoint point = ArenaGeometry.Spawn(legacy, index);
            Check(point.X >= 0d && point.X <= 8d && point.Z >= 0d && point.Z <= 8d,
                "legacy prison retains safe old spawn ring without expansion");
        }
        Reject(delegate { ArenaGeometry.Spawn(current, -1); }, "negative enemy spawn index");
        Reject(delegate { ArenaGeometry.Spawn(current, 8); }, "unbounded enemy spawn index");
    }

    private static bool NearRectangle(PrisonPoint p, double minX, double maxX, double minZ, double maxZ, double radius)
    { return p.X >= minX - radius && p.X <= maxX + radius && p.Z >= minZ - radius && p.Z <= maxZ + radius; }

    public static int Main()
    {
        try {
            Directions(); PitchAndFallback(); ClearanceAndSnapshot(); InvalidInputs(); ExpandedArena();
            Console.WriteLine("PASS: " + checks + " host-look prison placement checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
