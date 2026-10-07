using System;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Independent uniform spawn candidates, with a bounded native collision budget.</summary>
    public static class ArenaSpawnGeometry
    {
        public const int MaximumAttempts = 12;
        public const double WallMargin = 1.3d;

        public static PrisonPoint Sample(PrisonRegion region, double horizontal, double depth)
        {
            if (region == null || !SentencePolicy.IsFinitePoint(region.Center)
                || !SentencePolicy.IsFinitePoint(region.CellSpawn) || Double.IsNaN(region.Radius)
                || Double.IsInfinity(region.Radius) || region.Radius <= 0d)
                throw new ArgumentException("Неверная геометрия арены.", "region");
            RequireSample(horizontal, "horizontal"); RequireSample(depth, "depth");
            double minimum = ArenaGeometry.Divider(region) + WallMargin;
            double maximum = ArenaGeometry.RoomHalfWidth(region) - WallMargin;
            return new PrisonPoint(minimum + (maximum - minimum) * horizontal, 1d,
                minimum + (maximum - minimum) * depth);
        }

        // No wave index, used-point set or artificial distance between enemies:
        // later mobs may independently choose the same patch of the arena.
        public static bool TryChoose(PrisonRegion region, Func<double> random,
            Func<PrisonPoint, bool> clear, out PrisonPoint offset)
        {
            if (random == null) throw new ArgumentNullException("random");
            if (clear == null) throw new ArgumentNullException("clear");
            for (int attempt = 0; attempt < MaximumAttempts; ++attempt) {
                PrisonPoint candidate = Sample(region, random(), random());
                if (clear(candidate)) { offset = candidate; return true; }
            }
            offset = new PrisonPoint(); return false;
        }

        private static void RequireSample(double value, string parameter)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0d || value > 1d)
                throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
