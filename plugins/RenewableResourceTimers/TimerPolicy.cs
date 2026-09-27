using System;

namespace ValheimModPack.RenewableResourceTimers
{
    public static class TimerPolicy
    {
        public const int DefaultMushroomMinutes = 30;
        public const int DefaultVineberryMinutes = 45;
        public const int MaximumMinutes = 10080;

        public static int SafeMinutes(int minutes, int fallback)
        { return minutes >= 1 && minutes <= MaximumMinutes ? minutes : fallback; }

        public static float PickableMinutes(string prefab, float original, int vineberryMinutes)
        {
            // The Mistlands mushroom pickables are one-shot crops, including natural ones.
            // Never turn a zero-respawn harvest into a permanent resource spawner.
            if (prefab != "VineAsh" || Single.IsNaN(original) || Single.IsInfinity(original) || original <= 0) return original;
            return SafeMinutes(vineberryMinutes, DefaultVineberryMinutes);
        }

        public static bool IsMushroomSapling(string prefab)
        { return prefab == "sapling_jotunpuffs" || prefab == "sapling_magecap"; }

        public static bool TryGrowthSeconds(string prefab, int mushroomMinutes, out float seconds)
        {
            seconds = 0;
            if (!IsMushroomSapling(prefab)) return false;
            seconds = SafeMinutes(mushroomMinutes, DefaultMushroomMinutes) * 60f;
            return true;
        }
    }
}
