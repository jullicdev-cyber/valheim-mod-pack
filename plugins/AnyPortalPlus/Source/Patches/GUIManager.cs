// AnyPortal+ fork additions, 2026-10-06. GPL-3.0.
using HarmonyLib;

namespace XPortal.Patches
{
    [HarmonyPatch(typeof(Jotunn.Managers.GUIManager), "ResetInputBlock")]
    internal static class GUIManager_ResetInputBlock
    {
        private static void Postfix()
        {
            UI.PortalConfigurationPanel.Instance.OnInputBlockReset();
        }
    }
}
