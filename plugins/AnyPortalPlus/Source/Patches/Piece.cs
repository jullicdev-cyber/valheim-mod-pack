// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
using HarmonyLib;

namespace XPortal.Patches
{
    [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
    static class Piece_SetCreator
    {

        static void Postfix(Piece __instance)
        {
            // To those brave enough to dive into this to see how it works: Good luck. This will cost you your sanity.
            //
            // Piece.SetCreator is called by Player.PlacePiece, but before WearNTear.OnPlaced is called.
            // For reasons unknown to mankind (some say "inlining"), a patch on WearNTear.OnPlaced does not work if a patch on Player.PlacePiece was applied first.
            // However, we can't just assume that Piece.SetCreator is only called when a piece is being placed. It might be used for other things too (I honestly don't know).
            //
            // So here's a crazy work-around:
            // With a tiny frame delay, we can check *afterwards* if WearNTear.OnPlaced has run, and "pretend-patch" it that way.
            // If you've read this without what-the-fucking out loud at least once, you too are insane. Welcome to the club.
            WearNTear placed = __instance.GetComponent<WearNTear>();
            QueuedAction.Queue((delayed, state) =>
            {
                // Bind to this piece rather than a shared field overwritten by
                // another player placing a piece during the same frame.
                if (placed != null && placed.m_createTime == -1f) WearNTear_OnPlaced.Postfix(placed);
            }, delay: 1);
        }
    }
}
