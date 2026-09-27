using System;

namespace ValheimModPack.ExpeditionLoadouts
{
    internal static class TransferPolicy
    {
        internal const int MaximumTarget = 9999;
        internal const int MaximumTargets = 64;

        internal static int Deficit(int wanted, int owned)
        {
            if (wanted < 1 || wanted > MaximumTarget || owned < 0) return 0;
            return Math.Max(0, wanted - owned);
        }

        internal static bool OrdinaryCell(int x, int y, int width, int height, int visibleRows, bool favorite)
        {
            // Hotbar, all EAQS rows and marked empty cells remain untouched.
            return width > 0 && width <= 32 && visibleRows > 1 && visibleRows <= height
                && height <= 32 && x >= 0 && x < width && y >= 1 && y < visibleRows && !favorite;
        }

        internal static int MoveAmount(int deficit, int sourceCount, int destinationCount, int maximumStack)
        {
            if (deficit <= 0 || sourceCount <= 0 || destinationCount < 0 || maximumStack <= 0
                || destinationCount >= maximumStack) return 0;
            return Math.Min(deficit, Math.Min(sourceCount, maximumStack - destinationCount));
        }

        internal static bool ValidTarget(SupplyTarget target)
        {
            return target != null && !String.IsNullOrEmpty(target.Prefab) && target.Prefab.Length <= 128
                && target.Prefab.IndexOfAny(new[] { '\r', '\n', '\0', '/', '\\' }) < 0
                && target.Quality > 0 && target.Quality <= 100 && target.Count > 0 && target.Count <= MaximumTarget;
        }

        internal static bool StandardChest(string prefab)
        {
            return prefab == "piece_chest_wood" || prefab == "piece_chest" || prefab == "piece_chest_private"
                || prefab == "piece_chest_blackmetal";
        }
    }
}
