using System;
using System.Collections.Generic;

namespace ValheimModPack.PortalFinder
{
    internal sealed class PortalRecord
    {
        public string Id;
        public string Name;
        public double X;
        public double Y;
        public double Z;
    }

    internal sealed class PortalMatch
    {
        public PortalRecord Portal;
        public double Distance;
    }

    internal static class PortalSearch
    {
        // IDs are ordinal identities. For conflicting copies of one ID, use the
        // lowest ordinal Name (null sorts first), then X, Z and Y. Invalid copies
        // are ignored before deduplication; unnamed portals remain eligible.
        // Every accepted record is copied, so results never retain caller records.
        public static PortalMatch Find(IEnumerable<PortalRecord> portals, double x, double z)
        {
            if (portals == null || !Finite(x) || !Finite(z)) return null;

            var unique = new Dictionary<string, PortalRecord>(StringComparer.Ordinal);
            foreach (PortalRecord source in portals)
            {
                if (source == null) continue;
                var copy = new PortalRecord { Id = source.Id, Name = source.Name,
                    X = source.X, Y = source.Y, Z = source.Z };
                if (String.IsNullOrWhiteSpace(copy.Id) || !Finite(copy.X)
                    || !Finite(copy.Y) || !Finite(copy.Z)) continue;

                // Canonicalize signed zero so otherwise identical duplicates also
                // have identical coordinates regardless of enumeration order.
                if (copy.X == 0) copy.X = 0;
                if (copy.Y == 0) copy.Y = 0;
                if (copy.Z == 0) copy.Z = 0;
                PortalRecord previous;
                if (!unique.TryGetValue(copy.Id, out previous) || CompareCopy(copy, previous) < 0)
                    unique[copy.Id] = copy;
            }

            PortalRecord best = null;
            double bestSquared = 0;
            double bestExtreme = 0;
            double bestDistance = 0;
            foreach (PortalRecord portal in unique.Values)
            {
                double dx = portal.X - x;
                double dz = portal.Z - z;
                double squared = dx * dx + dz * dz;
                double extreme = 0;
                double distance;
                if (Double.IsInfinity(squared))
                {
                    // Finite coordinates can overflow subtraction or squaring.
                    // Quarter differences and a scaled length preserve ordering
                    // even when the actual distance cannot fit in a double.
                    extreme = Length(portal.X * 0.25 - x * 0.25,
                        portal.Z * 0.25 - z * 0.25);
                    distance = extreme * 4;
                }
                else if (squared == 0)
                {
                    // Preserve nonzero subnormal/tiny distances whose square
                    // rounds to zero instead of treating them as ties at origin.
                    extreme = Length(dx, dz);
                    distance = extreme;
                }
                else distance = Math.Sqrt(squared);

                int comparison = best == null ? -1 : squared.CompareTo(bestSquared);
                if (comparison == 0 && (squared == 0 || Double.IsInfinity(squared)))
                    comparison = extreme.CompareTo(bestExtreme);
                if (comparison < 0 || (comparison == 0
                    && StringComparer.Ordinal.Compare(portal.Id, best.Id) < 0))
                {
                    best = portal;
                    bestSquared = squared;
                    bestExtreme = extreme;
                    bestDistance = distance;
                }
            }
            return best == null ? null : new PortalMatch { Portal = best, Distance = bestDistance };
        }

        private static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static int CompareCopy(PortalRecord left, PortalRecord right)
        {
            int result = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (result != 0) return result;
            result = left.X.CompareTo(right.X);
            if (result != 0) return result;
            result = left.Z.CompareTo(right.Z);
            return result != 0 ? result : left.Y.CompareTo(right.Y);
        }

        private static double Length(double x, double z)
        {
            double scale = Math.Max(Math.Abs(x), Math.Abs(z));
            if (scale == 0) return 0;
            x /= scale;
            z /= scale;
            return scale * Math.Sqrt(x * x + z * z);
        }
    }
}
