using System;
using System.Collections.Generic;

namespace ValheimModPack.PartyPrison
{
    internal static class TerrainPlanTests
    {
        private static int assertions;

        private static void Check(bool result, string description)
        { ++assertions; if (!result) throw new Exception("FAIL: " + description); }

        private static void Reject(Action operation, string description)
        {
            ++assertions;
            try { operation(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("FAIL: accepted " + description);
        }

        private static TerrainLevelPlan Plan(IList<double> ground, IList<double> original, int compilers)
        { return TerrainPlan.Create(52d, 0d, 0d, 0d, 30d, ground, original, compilers); }

        private static double[] Repeated(double height, int count)
        { double[] result = new double[count]; for (int i = 0; i < count; ++i) result[i] = height; return result; }

        public static int Main()
        {
            try {
                AltarProtection(); HeightDecision(); TerrainLimits(); BoundedInput();
                Console.WriteLine("PartyPrison terrain planning checks passed: " + assertions);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static void AltarProtection()
        {
            Reject(delegate { TerrainPlan.RequireFootprint(0d, 0d, 0d, 0d); }, "flattening directly under altars");
            // All four corners are outside the protected circle at this position,
            // but the middle of the near edge passes through it.
            Reject(delegate { TerrainPlan.RequireFootprint(40d, 0d, 0d, 0d); }, "edge crossing despite clear corners");
            double edge = TerrainPlan.HalfWidth + TerrainPlan.FootprintPadding + TerrainPlan.AltarProtectionRadius;
            Reject(delegate { TerrainPlan.RequireFootprint(edge, 0d, 0d, 0d); }, "exactly touching the protected boundary");
            TerrainPlan.RequireFootprint(edge + 0.00001d, 0d, 0d, 0d);
            Check(true, "site immediately beyond the protected boundary is allowed");
            foreach (double sign in new double[] { -1d, 1d }) {
                Reject(delegate { TerrainPlan.RequireFootprint(sign * 40d, 0d, 0d, 0d); }, "east/west boundary is symmetric");
                Reject(delegate { TerrainPlan.RequireFootprint(0d, sign * 40d, 0d, 0d); }, "north/south boundary is symmetric");
                TerrainPlan.RequireFootprint(sign * 52d, 0d, 0d, 0d);
                TerrainPlan.RequireFootprint(0d, sign * 52d, 0d, 0d);
                Check(true, "clear outer ring is allowed on both world axes");
            }
            Reject(delegate { TerrainPlan.RequireFootprint(32d, 32d, 0d, 0d); }, "diagonal corner intersects altar circle");
            TerrainPlan.RequireFootprint(40d, 40d, 0d, 0d);
            Check(true, "clear diagonal site is allowed");
            Reject(delegate { TerrainPlan.RequireFootprint(1040d, -500d, 1000d, -500d); }, "translated altar has the same exclusion");
            TerrainPlan.RequireFootprint(1052d, -500d, 1000d, -500d);
            Check(true, "geometry supports a nonzero altar position");
            foreach (double invalid in new double[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, 1E100 }) {
                Reject(delegate { TerrainPlan.RequireFootprint(invalid, 0d, 0d, 0d); }, "invalid site x coordinate");
                Reject(delegate { TerrainPlan.RequireFootprint(52d, invalid, 0d, 0d); }, "invalid site z coordinate");
                Reject(delegate { TerrainPlan.RequireFootprint(52d, 0d, invalid, 0d); }, "invalid altar x coordinate");
                Reject(delegate { TerrainPlan.RequireFootprint(52d, 0d, 0d, invalid); }, "invalid altar z coordinate");
            }
        }

        private static void HeightDecision()
        {
            double[] flat = Repeated(40d, 841);
            TerrainLevelPlan plane = Plan(flat, flat, 4);
            Check(plane.TargetHeight == 40d, "an already flat site is left at its existing elevation");
            Check(plane.FloorHeight > plane.TargetHeight && plane.FloorHeight - plane.TargetHeight < 0.2d,
                "stone floor sits slightly above the levelled ground");
            Check(plane.LowestGroundHeight == 40d && plane.HighestGroundHeight == 40d && plane.LargestGroundChange == 0d,
                "flat site requires no height change");
            Check(plane.CenterX == 52d && plane.CenterZ == 0d && plane.SampleCount == 841 && plane.CompilerCount == 4,
                "decision retains the bounded native operation context");

            double[] unsorted = { 46d, 39d, 41d, 40d, 41d };
            TerrainLevelPlan median = Plan(unsorted, unsorted, 1);
            Check(median.TargetHeight == 41d, "an isolated high patch does not raise the entire site");
            Check(median.LowestGroundHeight == 39d && median.HighestGroundHeight == 46d && median.LargestGroundChange == 5d,
                "plan captures actual cut/fill limits");
            Check(unsorted[0] == 46d && unsorted[1] == 39d, "planning does not reorder caller-owned terrain data");
            unsorted[0] = 200d;
            Check(median.TargetHeight == 41d && median.HighestGroundHeight == 46d, "plan is unaffected by later input changes");

            double[] even = { 37d, 42d, 39d, 40d };
            Check(Plan(even, even, 1).TargetHeight == 39.5d, "even sample count uses both central elevations");
            double[] reordered = { 40d, 39d, 42d, 37d };
            Check(Plan(reordered, reordered, 1).TargetHeight == 39.5d, "target is independent of native tile enumeration order");
            TerrainLevelPlan alternateWater = TerrainPlan.Create(52d, 0d, 0d, 0d, 100d, Repeated(101d, 4), Repeated(101d, 4), 1);
            Check(alternateWater.TargetHeight == 101d, "world water level is supplied instead of hard-coded");
        }

        private static void TerrainLimits()
        {
            double[] boundary = { 44d, 50d, 50d, 50d, 56d };
            Check(Plan(boundary, boundary, 1).LargestGroundChange == 6d, "six-metre bounded cut/fill is allowed");
            double[] above = { 43.99d, 50d, 50d, 50d, 56d };
            Reject(delegate { Plan(above, above, 1); }, "cut/fill exceeding six metres");
            double[] spike = { 40d, 40d, 40d, 80d };
            Reject(delegate { Plan(spike, spike, 1); }, "narrow cliff or spike cannot be hidden by the median");

            // The current ground can be perfectly flat while a previous player
            // modification has already used up Valheim's original-terrain limit.
            double[] flat = Repeated(40d, 4);
            Check(Plan(flat, Repeated(32.5d, 4), 1).TargetHeight == 40d, "existing modifications within native capacity remain supported");
            Reject(delegate { Plan(flat, Repeated(32d, 4), 1); }, "native level clamp would leave raised terrain uneven");
            Reject(delegate { Plan(flat, Repeated(48d, 4), 1); }, "native level clamp would leave lowered terrain uneven");
            double[] oneBaseOutside = { 40d, 40d, 40d, 31d };
            Reject(delegate { Plan(flat, oneBaseOutside, 1); }, "one unsafe original vertex rejects the complete operation");

            foreach (double wet in new double[] { 0d, 30d, 30.5d }) {
                double[] submerged = { 40d, 40d, 40d, wet };
                Reject(delegate { Plan(submerged, submerged, 1); }, "a submerged or shoreline vertex is not filled automatically");
            }
            Check(Plan(Repeated(30.5001d, 4), Repeated(30.5001d, 4), 1).TargetHeight > 30.5d,
                "dry low ground just beyond the water clearance is allowed");
        }

        private static void BoundedInput()
        {
            double[] valid = Repeated(40d, 4);
            Reject(delegate { Plan(null, valid, 1); }, "missing current terrain");
            Reject(delegate { Plan(valid, null, 1); }, "missing original terrain");
            Reject(delegate { Plan(new double[0], new double[0], 1); }, "empty terrain enumeration");
            Reject(delegate { Plan(Repeated(40d, 3), Repeated(40d, 3), 1); }, "incomplete terrain enumeration");
            Reject(delegate { Plan(valid, Repeated(40d, 5), 1); }, "misaligned current/original sample counts");
            Reject(delegate { Plan(Repeated(40d, TerrainPlan.MaximumSamples + 1), Repeated(40d, TerrainPlan.MaximumSamples + 1), 1); }, "unbounded terrain sample count");
            Check(Plan(Repeated(40d, TerrainPlan.MaximumSamples), Repeated(40d, TerrainPlan.MaximumSamples), 1).SampleCount == TerrainPlan.MaximumSamples,
                "maximum bounded sample count is supported");
            Reject(delegate { Plan(valid, valid, 0); }, "no compiler belongs to the terrain operation");
            Reject(delegate { Plan(valid, valid, -1); }, "negative compiler count");
            Reject(delegate { Plan(valid, valid, TerrainPlan.MaximumCompilers + 1); }, "too many affected native compilers");
            Check(Plan(valid, valid, TerrainPlan.MaximumCompilers).CompilerCount == TerrainPlan.MaximumCompilers,
                "compiler count has an inclusive finite bound");
            foreach (double invalid in new double[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, 1E100 }) {
                double[] corrupt = { 40d, 40d, invalid, 40d };
                Reject(delegate { Plan(corrupt, valid, 1); }, "invalid current terrain height");
                Reject(delegate { Plan(valid, corrupt, 1); }, "invalid original terrain height");
                Reject(delegate { TerrainPlan.Create(52d, 0d, 0d, 0d, invalid, valid, valid, 1); }, "invalid world water level");
            }
        }
    }
}
