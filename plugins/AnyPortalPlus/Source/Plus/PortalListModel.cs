// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace XPortal.Plus
{
    public enum PortalSort
    {
        Name,
        Distance,
        Created
    }

    public sealed class PortalEntry
    {
        public string Id;
        public string Name;
        public string Biome;
        public long CreatedUtcTicks;
        public double X, Y, Z;
        public int Icon = -1;
    }

    /// <summary>Builds a detached portal view without changing the shared portal registry.</summary>
    public static class PortalListModel
    {
        public const int MaximumEntries = 8192;
        public const int MaximumNameLength = 256;
        public const int MaximumSearchLength = 256;
        public const int MaximumIdLength = 256;
        private const CompareOptions NameOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        private static readonly string[] BiomeOrder =
        {
            "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands",
            "AshLands", "DeepNorth", "Ocean"
        };

        public static List<PortalEntry> Query(IList<PortalEntry> entries, string currentId,
            double x, double y, double z, string search, PortalSort sort,
            bool descending, bool groupByBiome)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            if (entries.Count > MaximumEntries) throw new ArgumentException("Too many portals.", "entries");
            RequirePosition(x, y, z, "position");
            if (!Enum.IsDefined(typeof(PortalSort), sort)) throw new ArgumentOutOfRangeException("sort");
            search = search ?? string.Empty;
            if (search.Length > MaximumSearchLength) throw new ArgumentException("Search text is too long.", "search");
            search = search.Trim();

            CompareInfo names = CultureInfo.CurrentCulture.CompareInfo;
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            List<PortalEntry> result = new List<PortalEntry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                PortalEntry entry = entries[i];
                RequireEntry(entry);
                if (!ids.Add(entry.Id)) throw new ArgumentException("Duplicate portal identity.", "entries");
                if (string.Equals(entry.Id, currentId, StringComparison.Ordinal)) continue;
                string name = entry.Name ?? string.Empty;
                if (search.Length != 0 && names.IndexOf(name, search, NameOptions) < 0) continue;
                result.Add(Copy(entry));
            }

            result.Sort(delegate(PortalEntry left, PortalEntry right)
            {
                if (groupByBiome)
                {
                    int biome = CompareBiome(left.Biome, right.Biome);
                    if (biome != 0) return biome;
                }
                int compared;
                if (sort == PortalSort.Created)
                {
                    // Old saves have no creation timestamp; unknown is last in both directions.
                    if (left.CreatedUtcTicks == 0 && right.CreatedUtcTicks != 0) return 1;
                    if (right.CreatedUtcTicks == 0 && left.CreatedUtcTicks != 0) return -1;
                    compared = left.CreatedUtcTicks.CompareTo(right.CreatedUtcTicks);
                }
                else if (sort == PortalSort.Distance)
                    compared = Distance(left, x, y, z).CompareTo(Distance(right, x, y, z));
                else
                    compared = names.Compare(left.Name, right.Name, NameOptions);
                if (compared != 0) return descending ? -Math.Sign(compared) : Math.Sign(compared);
                // Keep ties deterministic even when the sort direction is reversed.
                return StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            return result;
        }

        public static double Distance(PortalEntry entry, double x, double y, double z)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            RequirePosition(entry.X, entry.Y, entry.Z, "entry");
            RequirePosition(x, y, z, "position");
            double dx = Math.Abs(entry.X - x), dy = Math.Abs(entry.Y - y), dz = Math.Abs(entry.Z - z);
            double maximum = Math.Max(dx, Math.Max(dy, dz));
            if (maximum == 0 || double.IsInfinity(maximum)) return maximum;
            // Scaling avoids overflow while retaining the vertical distance for elevated portals.
            dx /= maximum; dy /= maximum; dz /= maximum;
            return maximum * Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static string NameDisplay(PortalEntry entry)
        {
            return entry == null ? string.Empty : entry.Name ?? string.Empty;
        }

        private static int CompareBiome(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
            int rank = BiomeRank(left).CompareTo(BiomeRank(right));
            if (rank != 0) return rank;
            // Unknown biomes remain usable and sort after the established game biomes.
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static int BiomeRank(string biome)
        {
            for (int i = 0; i < BiomeOrder.Length; i++)
                if (string.Equals(BiomeOrder[i], biome, StringComparison.OrdinalIgnoreCase)) return i;
            return BiomeOrder.Length;
        }

        private static void RequireEntry(PortalEntry entry)
        {
            if (entry == null) throw new ArgumentException("Portal entry is missing.", "entries");
            if (string.IsNullOrEmpty(entry.Id) || entry.Id.Length > MaximumIdLength)
                throw new ArgumentException("Invalid portal identity.", "entries");
            if ((entry.Name != null && entry.Name.Length > MaximumNameLength) ||
                (entry.Biome != null && entry.Biome.Length > MaximumNameLength))
                throw new ArgumentException("Portal text is too long.", "entries");
            if (entry.CreatedUtcTicks < 0 || entry.CreatedUtcTicks > DateTime.MaxValue.Ticks)
                throw new ArgumentException("Invalid portal creation timestamp.", "entries");
            RequirePosition(entry.X, entry.Y, entry.Z, "entries");
            if (entry.Icon < -1) throw new ArgumentException("Invalid portal icon.", "entries");
        }

        private static void RequirePosition(double x, double y, double z, string parameter)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z) ||
                double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z))
                throw new ArgumentException("Portal coordinates must be finite.", parameter);
        }

        private static PortalEntry Copy(PortalEntry entry)
        {
            return new PortalEntry
            {
                Id = entry.Id, Name = entry.Name ?? string.Empty, Biome = entry.Biome ?? string.Empty,
                CreatedUtcTicks = entry.CreatedUtcTicks, X = entry.X, Y = entry.Y, Z = entry.Z,
                Icon = entry.Icon
            };
        }
    }
}
