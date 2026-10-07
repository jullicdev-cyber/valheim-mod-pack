using System;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Immutable host-relative placement; no world object or terrain is touched.</summary>
    public sealed class PrisonPlacementPlan
    {
        public const double CenterDistance = 40d;
        private const double MaximumCoordinate = 20000d;
        private const double MinimumHorizontalLook = 0.0001d;
        private readonly PrisonPoint origin;
        public PrisonPoint Origin { get { return origin; } }
        public double DirectionX { get; private set; }
        public double DirectionZ { get; private set; }
        public double FacingYawDegrees { get; private set; }

        private PrisonPlacementPlan(PrisonPoint origin, double directionX, double directionZ, double yaw)
        { this.origin = origin; DirectionX = directionX; DirectionZ = directionZ; FacingYawDegrees = yaw; }

        public static PrisonPlacementPlan Create(PrisonPoint host, PrisonPoint look, PrisonPoint fallbackForward)
        {
            RequireWorldPoint(host); RequireDirection(look); RequireDirection(fallbackForward);
            double x, z;
            if (!HorizontalDirection(look, out x, out z) && !HorizontalDirection(fallbackForward, out x, out z))
                throw new InvalidOperationException("Посмотрите перед собой, чтобы выбрать направление постройки тюрьмы.");
            PrisonPoint origin = new PrisonPoint(host.X + x * CenterDistance, host.Y, host.Z + z * CenterDistance);
            RequireWorldPoint(origin);
            // Unity's local +X rotates to (cos(yaw), -sin(yaw)). Align it with
            // the host's horizontal look so the western lobby entrance faces the host.
            double yaw = Math.Atan2(-z, x) * 180d / Math.PI;
            if (yaw < 0d) yaw += 360d;
            if (yaw >= 360d) yaw -= 360d;
            return new PrisonPlacementPlan(origin, x, z, yaw);
        }

        private static bool HorizontalDirection(PrisonPoint direction, out double x, out double z)
        {
            x = z = 0d;
            // Scale before normalizing: even a finite, unusually scaled direction
            // must not overflow while calculating pitch or horizontal length.
            double scale = Math.Max(Math.Abs(direction.X), Math.Max(Math.Abs(direction.Y), Math.Abs(direction.Z)));
            if (scale == 0d) return false;
            double sx = direction.X / scale, sz = direction.Z / scale;
            double horizontal = Math.Sqrt(sx * sx + sz * sz);
            if (horizontal < MinimumHorizontalLook) return false;
            x = sx / horizontal; z = sz / horizontal;
            return true;
        }

        private static void RequireWorldPoint(PrisonPoint point)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z) || Math.Abs(point.X) > MaximumCoordinate ||
                Math.Abs(point.Y) > MaximumCoordinate || Math.Abs(point.Z) > MaximumCoordinate)
                throw new ArgumentException("Неверные координаты для постройки тюрьмы.");
        }

        private static void RequireDirection(PrisonPoint direction)
        {
            if (!Finite(direction.X) || !Finite(direction.Y) || !Finite(direction.Z))
                throw new ArgumentException("Не удалось прочитать направление взгляда для постройки тюрьмы.");
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }

    /// <summary>Geometry selected from the durable region, without object scans or mutable layout caches.</summary>
    public static class ArenaGeometry
    {
        public const double ExpandedRadius = 26d;
        public const double HalfWidth = 18d;
        public const double Height = 12d;
        public const double ArenaMinimum = -10d;
        public const double LegacyHalfWidth = 12d;
        public const double LegacyHeight = 8d;
        public const double LegacyArenaMinimum = -4d;

        public static bool Expanded(PrisonRegion region)
        { return region != null && region.Radius >= ExpandedRadius - .001d; }
        public static double RoomHalfWidth(PrisonRegion region)
        { return Expanded(region) ? HalfWidth : LegacyHalfWidth; }
        public static double RoomHeight(PrisonRegion region)
        { return Expanded(region) ? Height : LegacyHeight; }
        public static double Divider(PrisonRegion region)
        { return Expanded(region) ? ArenaMinimum : LegacyArenaMinimum; }

        // Order alternates opposite corners, then the four side approaches. Even
        // a two-enemy wave therefore starts on different sides of the prisoner.
        public static PrisonPoint Spawn(PrisonRegion region, int index)
        {
            if (index < 0 || index >= 8) throw new ArgumentOutOfRangeException("index");
            if (!Expanded(region)) {
                int spoke;
                switch (index) {
                    case 0: spoke = 0; break; case 1: spoke = 4; break;
                    case 2: spoke = 2; break; case 3: spoke = 6; break;
                    case 4: spoke = 1; break; case 5: spoke = 5; break;
                    case 6: spoke = 3; break; default: spoke = 7; break;
                }
                double angle = spoke * Math.PI / 4d;
                return new PrisonPoint(4d + Math.Cos(angle) * 4d, 1d, 4d + Math.Sin(angle) * 4d);
            }
            switch (index) {
                case 0: return new PrisonPoint(-6d, 1d, -6d);
                case 1: return new PrisonPoint(14d, 1d, 14d);
                case 2: return new PrisonPoint(-6d, 1d, 14d);
                case 3: return new PrisonPoint(14d, 1d, -6d);
                case 4: return new PrisonPoint(4d, 1d, -6d);
                case 5: return new PrisonPoint(14d, 1d, 6d);
                case 6: return new PrisonPoint(-6d, 1d, 4d);
                default: return new PrisonPoint(4d, 1d, 14d);
            }
        }
    }
}
