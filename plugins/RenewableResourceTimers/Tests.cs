using System;
using ValheimModPack.RenewableResourceTimers;

internal static class TimerTests
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static int Main()
    {
        try
        {
            float seconds;
            Check(TimerPolicy.TryGrowthSeconds("sapling_jotunpuffs", 30, out seconds) && seconds == 1800, "Jotun puffs minutes converted to seconds");
            Check(TimerPolicy.TryGrowthSeconds("sapling_magecap", 30, out seconds) && seconds == 1800, "Magecap fixed thirty-minute growth");
            Check(TimerPolicy.TryGrowthSeconds("sapling_magecap", 60, out seconds) && seconds == 3600, "Synchronized growth setting respected");
            foreach (string prefab in new[] { null, "", "sapling_carrot", "sapling_turnip", "sapling_barley", "sapling_flax", "sapling_oat", "sapling_kale", "sapling_potato", "Beech_Sapling", "VineAsh_sapling", "modded_sapling_magecap", "sapling_magecap_copy" })
                Check(!TimerPolicy.TryGrowthSeconds(prefab, 30, out seconds), "Unrelated plant unchanged: " + prefab);
            Check(TimerPolicy.PickableMinutes("VineAsh", 200, 45) == 45, "Vine regrowth forty-five minutes");
            Check(TimerPolicy.PickableMinutes("VineAsh", 200, 60) == 60, "Synchronized vine setting respected");
            foreach (string prefab in new[] { null, "", "Pickable_Mushroom_JotunPuffs", "Pickable_Mushroom_Magecap", "Pickable_Mushroom", "Pickable_Mushroom_yellow", "Pickable_SmokePuff", "RaspberryBush", "Pickable_Thistle", "Pickable_Stone", "Pickable_Flint", "Pickable_Branch", "Pickable_Carrot", "VineAsh_fake" })
                Check(TimerPolicy.PickableMinutes(prefab, 240, 45) == 240 && TimerPolicy.PickableMinutes(prefab, 0, 45) == 0, "Native harvesting preserved: " + prefab);
            Check(TimerPolicy.PickableMinutes("VineAsh", 0, 45) == 0, "Never enable respawn on a disabled vine");
            Check(Single.IsNaN(TimerPolicy.PickableMinutes("VineAsh", Single.NaN, 45)), "Malformed source does not enable respawn");
            Check(Single.IsPositiveInfinity(TimerPolicy.PickableMinutes("VineAsh", Single.PositiveInfinity, 45)), "Infinite source is not rewritten");
            foreach (int invalid in new[] { 0, -1, Int32.MaxValue, TimerPolicy.MaximumMinutes + 1 })
            {
                Check(TimerPolicy.TryGrowthSeconds("sapling_magecap", invalid, out seconds) && seconds == 1800, "Invalid growth safely defaults");
                Check(TimerPolicy.PickableMinutes("VineAsh", 200, invalid) == 45, "Invalid respawn safely defaults");
            }
            Check(TimerPolicy.TryGrowthSeconds("sapling_magecap", TimerPolicy.MaximumMinutes, out seconds) && seconds == 604800, "Upper bound cannot overflow seconds");
            Console.WriteLine("PASS " + checks + " renewable-resource timer policy assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
