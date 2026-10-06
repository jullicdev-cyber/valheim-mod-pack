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
        Near(north.Origin.Z, -168d, "looking north places the prison 32 metres ahead");
        Near(north.FacingYawDegrees, 270d, "northward local +X leaves the western door facing south toward the host");
        PrisonPlacementPlan east = Plan(host, new PrisonPoint(1d, 0d, 0d));
        Near(east.Origin.X, 132d, "looking east places the prison along the look ray");
        Near(east.Origin.Z, -200d, "looking east does not displace the plot sideways");
        Near(east.FacingYawDegrees, 0d, "eastward local +X faces the direction of look");
        PrisonPlacementPlan south = Plan(host, new PrisonPoint(0d, 0d, -1d));
        Near(south.Origin.Z, -232d, "looking south places the prison ahead rather than behind the host");
        Near(south.FacingYawDegrees, 90d, "southward prison entrance faces north");
        PrisonPlacementPlan west = Plan(host, new PrisonPoint(-1d, 0d, 0d));
        Near(west.Origin.X, 68d, "looking west places the prison at a negative world displacement");
        Near(west.FacingYawDegrees, 180d, "westward prison entrance faces east");
        PrisonPlacementPlan northeast = Plan(host, new PrisonPoint(1d, 0d, 1d));
        Near(northeast.Origin.X - host.X, 32d / Math.Sqrt(2d), "diagonal horizontal direction is normalized");
        Near(northeast.Origin.Z - host.Z, 32d / Math.Sqrt(2d), "diagonal target remains on the same look ray");
        Near(northeast.FacingYawDegrees, 315d, "diagonal local +X matches the host's look");
    }

    private static void PitchAndFallback()
    {
        PrisonPoint host = new PrisonPoint(0d, 80d, 0d), east = new PrisonPoint(1d, 0d, 0d);
        foreach (double pitch in new[] { -100d, -1d, 0d, 1d, 100d }) {
            PrisonPlacementPlan plan = Plan(host, new PrisonPoint(3d, pitch, 4d));
            Near(plan.Origin.X, 19.2d, "camera pitch does not change east/west placement");
            Near(plan.Origin.Z, 25.6d, "camera pitch does not change north/south placement");
            Near(plan.Origin.Y, 80d, "placement preserves the host elevation for native terrain planning");
        }
        foreach (PrisonPoint vertical in new[] { new PrisonPoint(0d, 1d, 0d), new PrisonPoint(0d, -1d, 0d),
            new PrisonPoint(0d, 0d, 0d), new PrisonPoint(0.000000001d, 1d, -0.000000001d) }) {
            PrisonPlacementPlan fallback = PrisonPlacementPlan.Create(host, vertical, east);
            Near(fallback.Origin.X, 32d, "vertical or zero look uses the retained native yaw");
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
            Near(Math.Sqrt(dx * dx + dz * dz), 32d, "placement distance is stable for every compass heading");
            Near(plan.DirectionX * plan.DirectionX + plan.DirectionZ * plan.DirectionZ, 1d, "horizontal placement direction has unit length");
            double yaw = plan.FacingYawDegrees * Math.PI / 180d;
            Near(Math.Cos(yaw), plan.DirectionX, "Unity's local +X faces the captured horizontal look");
            Near(-Math.Sin(yaw), plan.DirectionZ, "Unity's yaw convention preserves the captured look direction");
            double hostLocalX = (host.X - plan.Origin.X) * plan.DirectionX + (host.Z - plan.Origin.Z) * plan.DirectionZ;
            Near(hostLocalX, -32d, "the host stays beyond the complete expanded clearance square");
            Check(Math.Abs(hostLocalX) > 14.5d + 4d + 1d, "the host's physical collider is clear of the construction footprint");
            // The existing public-lobby doorway is at local (-12, -8).
            double entranceX = plan.Origin.X - 12d * plan.DirectionX + 8d * plan.DirectionZ;
            double entranceZ = plan.Origin.Z - 12d * plan.DirectionZ - 8d * plan.DirectionX;
            double entranceForward = (entranceX - host.X) * plan.DirectionX + (entranceZ - host.Z) * plan.DirectionZ;
            Near(entranceForward, 20d, "the western public entrance lies ahead of the host");
            Check((host.X - entranceX) * -plan.DirectionX + (host.Z - entranceZ) * -plan.DirectionZ > 0d,
                "the public entrance opens toward the host");
        }
        PrisonPoint look = new PrisonPoint(1d, 0d, 0d);
        PrisonPlacementPlan snapshot = Plan(host, look);
        host.X = 0d; look.Z = 1d;
        PrisonPoint originCopy = snapshot.Origin; originCopy.X = 999d;
        Near(snapshot.Origin.X, -1468d, "confirmation snapshot is unaffected by later host, look, or returned-point mutation");
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
        Near(Plan(new PrisonPoint(19990d, 40d, 0d), new PrisonPoint(-1d, 0d, 0d)).Origin.X, 19958d,
            "an inward-facing placement near the coordinate boundary remains usable");
    }

    public static int Main()
    {
        try {
            Directions(); PitchAndFallback(); ClearanceAndSnapshot(); InvalidInputs();
            Console.WriteLine("PASS: " + checks + " host-look prison placement checks.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
