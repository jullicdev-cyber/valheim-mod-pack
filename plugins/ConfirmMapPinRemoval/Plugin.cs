using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;

namespace ValheimModPack.PinRemoval
{
    [BepInPlugin(Id, "Confirm Map Pin Removal", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.confirmpinremoval";
        private Harmony harmony;
        private static Plugin plugin;
        private static MethodInfo closest;
        private static FieldInfo pins;

        private void Awake()
        {
            plugin = this;
            try
            {
                closest = AccessTools.Method(typeof(Minimap), "GetClosestPinToCursor", Type.EmptyTypes);
                pins = AccessTools.Field(typeof(Minimap), "m_pins");
                var target = AccessTools.Method(typeof(Minimap), "RemovePinUnderPointer", Type.EmptyTypes);
                if (closest == null || pins == null || target == null)
                    throw new MissingMemberException("Valheim map API changed; confirmation patch not installed.");
                harmony = new Harmony(Id);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(Plugin), "BeforeRemoveUnderPointer"));
                Logger.LogInfo("Map pin removal confirmation active (mouse/touch pointer action).");
            }
            catch (Exception error) { Logger.LogError(error); }
        }

        private static bool BeforeRemoveUnderPointer(Minimap __instance)
        {
            try
            {
                if (UnifiedPopup.IsVisible()) return false;
                var pin = (Minimap.PinData)closest.Invoke(__instance, null);
                if (pin == null) return true; // Let vanilla handle an empty click.
                if (!pin.m_save) return false;
                var request = new Confirmation<Minimap.PinData>(pin);
                bool russian = Localization.instance.GetSelectedLanguage() == "Russian";
                string name = Regex.Replace(pin.m_name ?? "", "<[^>]*>", "").Replace("\n", " ").Replace("\r", " ");
                if (String.IsNullOrWhiteSpace(name)) name = russian ? "без названия" : "unnamed";
                if (name.Length > 120) name = name.Substring(0, 120) + "…";
                bool closed = false;
                Action close = delegate { if (!closed) { closed = true; UnifiedPopup.Pop(); } };
                UnifiedPopup.Push(new YesNoPopup(
                    russian ? "Удаление метки" : "Remove map pin",
                    russian ? "Удалить метку «" + name + "»?" : "Remove pin “" + name + "”?",
                    delegate
                    {
                        if (closed) return;
                        close();
                        request.Confirm(
                            candidate => __instance != null && ReferenceEquals(Minimap.instance, __instance)
                                && candidate.m_save && ((List<Minimap.PinData>)pins.GetValue(__instance)).Contains(candidate),
                            candidate => __instance.RemovePin(candidate));
                    },
                    delegate { request.Cancel(); close(); }, false, true));
                return false;
            }
            catch (Exception error)
            {
                // Never fall through to deletion if creating the dialog failed.
                if (plugin != null) plugin.Logger.LogError("Pin removal blocked after confirmation error: " + error);
                return false;
            }
        }

        private void OnDestroy()
        {
            if (harmony != null) harmony.UnpatchSelf();
            plugin = null;
        }
    }
}
