using System;
using System.Collections.Generic;

namespace ValheimModPack.PartyPrison
{
    /// <summary>A bounded, immutable decision. Native terrain enumeration and mutation stay outside this policy.</summary>
    public sealed class TerrainLevelPlan
    {
        public double CenterX { get; private set; }
        public double CenterZ { get; private set; }
        public double TargetHeight { get; private set; }
        public double FloorHeight { get { return TargetHeight + TerrainPlan.FloorClearance; } }
        public double LowestGroundHeight { get; private set; }
        public double HighestGroundHeight { get; private set; }
        public double LargestGroundChange { get; private set; }
        public int SampleCount { get; private set; }
        public int CompilerCount { get; private set; }

        internal TerrainLevelPlan(double x, double z, double target, double low, double high, double change, int count, int compilers)
        {
            CenterX = x; CenterZ = z; TargetHeight = target; LowestGroundHeight = low;
            HighestGroundHeight = high; LargestGroundChange = change; SampleCount = count; CompilerCount = compilers;
        }
    }

    public static class TerrainPlan
    {
        public const double HalfWidth = 13.5d;
        // Native heightmap vertex selection rounds to a metre. Protect this skirt as well as the visible square.
        public const double FootprintPadding = 1d;
        public const double AltarProtectionRadius = 27d;
        public const double FloorClearance = 0.15d;
        public const double WaterClearance = 0.5d;
        public const double MaximumGroundChange = 6d;
        // Keep a margin below Valheim's native +/-8 m level limit, which is measured from original terrain.
        public const double MaximumNativeLevelDelta = 7.9d;
        public const int MaximumCompilers = 64;
        public const int MaximumSamples = 4096;
        private const double MaximumCoordinate = 20000d;
        private const double MaximumHeight = 10000d;

        /// <summary>All values must describe every vertex that the native square level operation can touch.</summary>
        public static TerrainLevelPlan Create(double centerX, double centerZ, double altarX, double altarZ,
            double waterLevel, IList<double> groundHeights, IList<double> originalBaseHeights, int touchedCompilers)
        {
            RequireFootprint(centerX, centerZ, altarX, altarZ);
            RequireHeight(waterLevel);
            if (groundHeights == null || originalBaseHeights == null || groundHeights.Count < 4 ||
                groundHeights.Count > MaximumSamples || groundHeights.Count != originalBaseHeights.Count)
                throw new InvalidOperationException("Не удалось полностью проверить высоту площадки под тюрьму.");
            if (touchedCompilers < 1 || touchedCompilers > MaximumCompilers)
                throw new InvalidOperationException("Площадка затрагивает слишком много участков земли.");

            double[] sorted = new double[groundHeights.Count];
            double low = Double.MaxValue, high = Double.MinValue;
            for (int i = 0; i < groundHeights.Count; ++i) {
                double ground = groundHeights[i]; RequireHeight(ground); RequireHeight(originalBaseHeights[i]);
                if (ground <= waterLevel + WaterClearance)
                    throw new InvalidOperationException("Тюрьме нужна сухая площадка вдали от воды.");
                sorted[i] = ground; low = Math.Min(low, ground); high = Math.Max(high, ground);
            }
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            double target = sorted.Length % 2 == 0 ? sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) * 0.5d : sorted[middle];
            if (target <= waterLevel + WaterClearance)
                throw new InvalidOperationException("После выравнивания площадка окажется слишком близко к воде.");
            double largest = 0d;
            for (int i = 0; i < groundHeights.Count; ++i) {
                double change = Math.Abs(target - groundHeights[i]);
                largest = Math.Max(largest, change);
                if (change > MaximumGroundChange)
                    throw new InvalidOperationException("Слишком крутой склон: выравнивание меняет землю более чем на 6 м.");
                if (Math.Abs(target - originalBaseHeights[i]) > MaximumNativeLevelDelta)
                    throw new InvalidOperationException("Площадка выходит за безопасный предел выравнивания земли в Valheim.");
            }
            return new TerrainLevelPlan(centerX, centerZ, target, low, high, largest, groundHeights.Count, touchedCompilers);
        }

        public static void RequireFootprint(double centerX, double centerZ, double altarX, double altarZ)
        {
            RequireCoordinate(centerX); RequireCoordinate(centerZ); RequireCoordinate(altarX); RequireCoordinate(altarZ);
            double extent = HalfWidth + FootprintPadding;
            // Distance from the altar centre to the closest point of the complete axis-aligned square.
            // Merely testing the centre or its four corners misses a crossing through the middle of an edge.
            double dx = Math.Max(0d, Math.Abs(altarX - centerX) - extent);
            double dz = Math.Max(0d, Math.Abs(altarZ - centerZ) - extent);
            if (dx * dx + dz * dz <= AltarProtectionRadius * AltarProtectionRadius)
                throw new InvalidOperationException("Нельзя выравнивать землю внутри охраняемой области жертвенных камней.");
        }

        private static void RequireCoordinate(double value)
        {
            if (!Finite(value) || Math.Abs(value) > MaximumCoordinate)
                throw new ArgumentException("Неверные координаты площадки под тюрьму.");
        }

        private static void RequireHeight(double value)
        {
            if (!Finite(value) || Math.Abs(value) > MaximumHeight)
                throw new ArgumentException("Не удалось прочитать высоту площадки под тюрьму.");
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
