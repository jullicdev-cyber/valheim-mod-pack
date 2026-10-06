using System;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Bounds for complete physical roots selected by a bounded prison-site collision query.</summary>
    public static class ClearanceFootprint
    {
        public const double MaximumEdgeOverhang = 4d;
        public const double MaximumHeight = 100d;
        private const double MaximumCoordinate = 20000d;

        public static bool ContainsRoot(double centerX, double centerZ, double sizeX, double sizeZ,
            double originX, double originZ, double yawDegrees, double extent)
        {
            if (!Coordinate(centerX) || !Coordinate(centerZ) || !Coordinate(originX) || !Coordinate(originZ) ||
                !Finite(sizeX) || !Finite(sizeZ) || sizeX < 0d || sizeZ < 0d ||
                !Finite(yawDegrees) || !Finite(extent) || extent < 12d || extent > 20d)
                return false;

            double normalized = yawDegrees % 360d;
            if (normalized < 0d) normalized += 360d;
            double cosine, sine;
            // Cardinal rotations are exactly axis-aligned. Avoid a trigonometric
            // rounding residue rejecting a root which exactly touches the border.
            if (normalized == 0d) { cosine = 1d; sine = 0d; }
            else if (normalized == 90d) { cosine = 0d; sine = 1d; }
            else if (normalized == 180d) { cosine = -1d; sine = 0d; }
            else if (normalized == 270d) { cosine = 0d; sine = -1d; }
            else {
                double radians = normalized * Math.PI / 180d;
                cosine = Math.Cos(radians); sine = Math.Sin(radians);
            }
            double dx = centerX - originX, dz = centerZ - originZ;
            double localX = cosine * dx - sine * dz;
            double localZ = sine * dx + cosine * dz;
            // Project the world AABB on both local axes. These extrema cover all
            // four corners, including a long root which exceeds the visible square.
            double halfX = Math.Abs(cosine) * sizeX * 0.5d + Math.Abs(sine) * sizeZ * 0.5d;
            double halfZ = Math.Abs(sine) * sizeX * 0.5d + Math.Abs(cosine) * sizeZ * 0.5d;
            double limit = extent + MaximumEdgeOverhang;
            return Finite(localX) && Finite(localZ) && Finite(halfX) && Finite(halfZ) &&
                Math.Abs(localX) + halfX <= limit && Math.Abs(localZ) + halfZ <= limit;
        }

        private static bool Coordinate(double value) { return Finite(value) && Math.Abs(value) <= MaximumCoordinate; }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
