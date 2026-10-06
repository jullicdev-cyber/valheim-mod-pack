using System;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Immutable host-relative placement; no world object or terrain is touched.</summary>
    public sealed class PrisonPlacementPlan
    {
        public const double CenterDistance = 32d;
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
}
