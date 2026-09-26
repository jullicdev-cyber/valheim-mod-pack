using System;
using ValheimModPack;

internal static class SlotPolicyTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    public static void Main()
    {
        // Include native inventory upgrades and all five configurable EAQS extra rows.
        for (int visible = 4; visible <= 14; visible++)
        {
            int full = visible + 3, ordinary = 0, hidden = 0;
            for (int y = 0; y < full; y++)
                for (int x = 0; x < 8; x++)
                {
                    bool blocked = SlotPolicy.IsProtected(x, y, 8, full, visible, full);
                    Check(blocked == (y >= visible), "Wrong visible/hidden boundary");
                    if (blocked) hidden++; else ordinary++;
                }
            Check(ordinary == visible * 8 && hidden == 24, "Lost ordinary cells or exposed hidden cells");
            Check(SlotPolicy.IsProtected(0, 0, 8, full - 1, visible, full), "Mismatched height must fail closed");
            Check(SlotPolicy.IsProtected(0, 0, 7, full, visible, full), "Mismatched width must fail closed");
        }
        Check(!SlotPolicy.IsProtected(7, 4, 8, 8, 5, 8), "The added eighth cell in row five must remain sortable");
        Check(SlotPolicy.IsProtected(0, 5, 8, 8, 5, 8), "First hidden cell must be protected even when empty");
        Check(SlotPolicy.IsProtected(-1, 0, 8, 8, 5, 8), "Negative x");
        Check(SlotPolicy.IsProtected(8, 0, 8, 8, 5, 8), "Out-of-range x");
        Check(SlotPolicy.IsProtected(0, -1, 8, 8, 5, 8), "Negative y");
        Check(SlotPolicy.IsProtected(0, 0, 8, 0, 0, 0), "Uninitialized API must fail closed");
        Console.WriteLine("OK: inventory boundary regression tests, 4-14 visible rows, reserved cells and invalid geometry.");
    }
}
