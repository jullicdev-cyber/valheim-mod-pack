using System;
using System.Collections.Generic;
using System.IO;

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
        public const double HalfWidth = 19.5d;
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

        /// <summary>The administrator supplies the plateau height; water and slope are intentionally unrestricted.</summary>
        public static TerrainLevelPlan CreateAnywhere(double centerX, double centerZ, double targetHeight,
            IList<double> groundHeights, int touchedCompilers)
        {
            RequireCoordinate(centerX); RequireCoordinate(centerZ); RequireHeight(targetHeight);
            if (groundHeights == null || groundHeights.Count < 4 || groundHeights.Count > MaximumSamples)
                throw new InvalidOperationException("Не удалось полностью проверить высоту площадки под тюрьму.");
            if (touchedCompilers < 1 || touchedCompilers > MaximumCompilers)
                throw new InvalidOperationException("Площадка затрагивает слишком много участков земли.");
            double low = Double.MaxValue, high = Double.MinValue, largest = 0d;
            for (int i = 0; i < groundHeights.Count; ++i) {
                double ground = groundHeights[i]; RequireHeight(ground);
                low = Math.Min(low, ground); high = Math.Max(high, ground);
                largest = Math.Max(largest, Math.Abs(targetHeight - ground));
            }
            return new TerrainLevelPlan(centerX, centerZ, targetHeight, low, high, largest, groundHeights.Count, touchedCompilers);
        }

        public static double EnclosingExtent(double yawDegrees)
        { return EnclosingExtent(yawDegrees, true); }

        public static double EnclosingExtent(double yawDegrees, bool expanded)
        {
            if (!Finite(yawDegrees)) throw new ArgumentException("Неверное направление постройки.");
            double radians = yawDegrees * Math.PI / 180d;
            return ((expanded ? HalfWidth : 13.5d) + FootprintPadding) * (Math.Abs(Math.Cos(radians)) + Math.Abs(Math.Sin(radians)));
        }

        /// <summary>A virtual interior has physical floors, without an exterior terrain compiler to level.</summary>
        public static TerrainLevelPlan CreateInterior(double centerX, double centerZ, double targetHeight)
        {
            RequireCoordinate(centerX); RequireCoordinate(centerZ); RequireHeight(targetHeight);
            return new TerrainLevelPlan(centerX, centerZ, targetHeight, targetHeight, targetHeight, 0d, 0, 0);
        }

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

    /// <summary>Small versioned per-compiler record. Absolute local heights survive native terrain's +/-8 m clamp.</summary>
    internal static class TerrainOverrides
    {
        internal const string Key = "VMP_PP_ForcedGround";
        internal const int MaximumPitch = 129;
        internal const int MaximumEntries = MaximumPitch * MaximumPitch;
        private const int Version = 1;

        internal static SortedDictionary<int, float> Decode(byte[] data, int pitch)
        {
            RequirePitch(pitch);
            SortedDictionary<int, float> values = new SortedDictionary<int, float>();
            if (data == null || data.Length == 0) return values;
            if (data.Length < 12 || data.Length > 12 + MaximumEntries * 8)
                throw new InvalidOperationException("Повреждена запись высот земли под тюрьмой.");
            using (BinaryReader reader = new BinaryReader(new MemoryStream(data, false))) {
                int version = reader.ReadInt32(), savedPitch = reader.ReadInt32(), count = reader.ReadInt32();
                if (version != Version || savedPitch != pitch || count < 0 || count > pitch * pitch || data.Length != 12 + count * 8)
                    throw new InvalidOperationException("Неподдерживаемая запись высот земли под тюрьмой.");
                int previous = -1;
                for (int i = 0; i < count; ++i) {
                    int index = reader.ReadInt32(); float height = reader.ReadSingle();
                    if (index <= previous || index >= pitch * pitch || !ValidHeight(height))
                        throw new InvalidOperationException("Повреждена вершина земли под тюрьмой.");
                    values.Add(index, height); previous = index;
                }
            }
            return values;
        }

        internal static byte[] Encode(int pitch, IDictionary<int, float> values)
        {
            RequirePitch(pitch);
            if (values == null || values.Count > pitch * pitch) throw new ArgumentException("Неверное число вершин земли.");
            SortedDictionary<int, float> sorted = new SortedDictionary<int, float>(values);
            using (MemoryStream stream = new MemoryStream()) {
                using (BinaryWriter writer = new BinaryWriter(stream)) {
                    writer.Write(Version); writer.Write(pitch); writer.Write(sorted.Count);
                    foreach (KeyValuePair<int, float> item in sorted) {
                        if (item.Key < 0 || item.Key >= pitch * pitch || !ValidHeight(item.Value))
                            throw new ArgumentException("Неверная вершина земли.");
                        writer.Write(item.Key); writer.Write(item.Value);
                    }
                    writer.Flush(); return stream.ToArray();
                }
            }
        }

        private static void RequirePitch(int pitch)
        { if (pitch < 2 || pitch > MaximumPitch) throw new InvalidOperationException("Неподдерживаемый размер компилятора земли."); }
        private static bool ValidHeight(float value)
        { return !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 10000f; }
    }
}
