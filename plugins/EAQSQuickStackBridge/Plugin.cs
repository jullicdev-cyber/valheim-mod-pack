using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimModPack
{
    [BepInPlugin(Id, "EAQS Quick Stack Bridge", "1.0.0")]
    [BepInDependency(QuickStack, "1.4.15")]
    [BepInDependency(Eaqs, "3.1.3")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.eaqsquickstackbridge";
        private const string QuickStack = "goldenrevolver.quick_stack_store";
        private const string Eaqs = "randyknapp.mods.equipmentandquickslots";
        private Harmony harmony;

        private void Awake()
        {
            try
            {
                // Defaults on disk stay disabled, so removing this bridge restores the guard.
                SetSortingControls(false);
                if (Chainloader.PluginInfos[QuickStack].Metadata.Version != new System.Version("1.4.15")
                    || Chainloader.PluginInfos[Eaqs].Metadata.Version != new System.Version("3.1.3"))
                    throw new NotSupportedException("Bridge requires Quick Stack 1.4.15 and EAQS 3.1.3; revalidate before updating.");
                var target = AccessTools.Method("QuickStackStore.CompatibilitySupport:InternalIsEquipOrQuickSlot",
                    new Type[] { typeof(int), typeof(int), typeof(Vector2i), typeof(bool) });
                var sort = AccessTools.Method("QuickStackStore.SortModule:SortPlayerInv");
                if (target == null || target.ReturnType != typeof(bool) || sort == null)
                    throw new MissingMethodException("Quick Stack patch targets changed");
                harmony = new Harmony(Id);
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(Plugin), "ProtectSlots"));
                harmony.Patch(sort, prefix: new HarmonyMethod(typeof(Plugin), "ValidatePlayerInventory"));
                SetSortingControls(true);
                Logger.LogInfo("EAQS slot protection active; inventory sorting enabled with O and buttons. Disk config remains guarded.");
            }
            catch (Exception error)
            {
                if (harmony != null) harmony.UnpatchSelf();
                SetSortingControls(false);
                Logger.LogError("Bridge initialization failed; player sorting remains disabled: " + error);
            }
        }

        private static void ProtectSlots(int __0, int __1, Vector2i __2, ref bool __result)
        {
            // Quick Stack invokes this for both items and possible destination cells.
            // Protect every hidden row, not merely currently occupied equipment slots.
            try
            {
                __result |= SlotPolicy.IsProtected(__2.x, __2.y, __1, __0,
                    EquipmentAndQuickSlots.API.GetVisibleRows(), EquipmentAndQuickSlots.API.GetFullHeight());
            }
            catch { __result = true; }
        }

        private static bool ValidatePlayerInventory(Inventory __0)
        {
            try
            {
                if (Player.m_localPlayer == null || !ReferenceEquals(__0, Player.m_localPlayer.GetInventory())) return false;
                int visible = EquipmentAndQuickSlots.API.GetVisibleRows();
                return !SlotPolicy.IsProtected(0, 0, __0.GetWidth(), __0.GetHeight(), visible,
                    EquipmentAndQuickSlots.API.GetFullHeight());
            }
            catch { return false; }
        }

        private static void SetSortingControls(bool enabled)
        {
            ConfigFile cfg = Chainloader.PluginInfos[QuickStack].Instance.Config;
            bool previous = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;
            try
            {
                var display = cfg[new ConfigDefinition("4 - Sorting", "DisplaySortButtons")];
                display.BoxedValue = Enum.Parse(display.SettingType, enabled ? "Both" : "OnlyContainerButton");
                var key = cfg[new ConfigDefinition("4 - Sorting", "SortKeybind")];
                key.BoxedValue = new KeyboardShortcut(enabled ? UnityEngine.KeyCode.O : UnityEngine.KeyCode.None);
            }
            finally { cfg.SaveOnConfigSet = previous; }
        }

        private void OnDestroy()
        {
            try { SetSortingControls(false); }
            finally { if (harmony != null) harmony.UnpatchSelf(); }
        }
    }
}
