using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace ValheimModPack.RenewableResourceTimers
{
    [BepInPlugin(Id, "Renewable Resource Timers", Version)]
    [BepInDependency("com.jotunn.jotunn", "2.30.2")]
    [BepInDependency("advize.PlantEverything", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "valheimmodpack.renewableresourcetimers";
        public const string Version = "1.0.0";
        private static Plugin instance;
        private ConfigEntry<int> mushrooms, vineberries;
        private Harmony harmony;
        private bool refreshPending;
        private bool ready;

        private void Awake()
        {
            try
            {
                mushrooms = BindMinutes("MistlandsMushroomGrowthMinutes", TimerPolicy.DefaultMushroomMinutes,
                    "Growth time for planted Jotun puffs and magecap. Their native one-time harvest is preserved. Synchronized from the server.");
                vineberries = BindMinutes("VineberryRespawnMinutes", TimerPolicy.DefaultVineberryMinutes,
                    "Recurring ashvine berry harvest interval. Initial random maturity, vine growth, spacing and yield are unchanged. Synchronized from the server.");
                instance = this;
                harmony = new Harmony(Id);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Config.SettingChanged += Changed;
                SynchronizationManager.OnConfigurationSynchronized += Synchronized;
                ready = true; refreshPending = true;
                Logger.LogInfo("Renewable Resource Timers ready: Mistlands mushroom growth 30 min / vineberry regrowth 45 min by default; native harvest rules preserved.");
            }
            catch (Exception error) { Logger.LogError("Renewable Resource Timers initialization failed: " + error); Shutdown(); }
        }

        private ConfigEntry<int> BindMinutes(string key, int value, string description)
        {
            return Config.Bind("Timers", key, value, new ConfigDescription(description,
                new AcceptableValueRange<int>(1, TimerPolicy.MaximumMinutes), new ConfigurationManagerAttributes { IsAdminOnly = true }));
        }
        private void Changed(object sender, SettingChangedEventArgs args) { refreshPending = true; }
        private void Synchronized(object sender, ConfigurationSynchronizationEventArgs args) { refreshPending = true; }

        private void Update()
        {
            if (!ready || !refreshPending) return;
            refreshPending = false;
            // A config sync can arrive after objects have spawned. Apply it to loaded instances
            // on the Unity thread; future instances are covered by their Awake hooks.
            foreach (Pickable pickable in UnityEngine.Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None)) ApplyPickable(pickable);
            foreach (Plant plant in UnityEngine.Object.FindObjectsByType<Plant>(FindObjectsSortMode.None)) ApplyPlant(plant);
        }

        internal static void ApplyPickable(Pickable pickable)
        {
            if (instance == null || pickable == null || !pickable.gameObject.scene.IsValid()) return;
            pickable.m_respawnTimeMinutes = TimerPolicy.PickableMinutes(Utils.GetPrefabName(pickable.gameObject),
                pickable.m_respawnTimeMinutes, instance.vineberries.Value);
        }
        internal static void ApplyPlant(Plant plant)
        {
            if (instance == null || plant == null || !plant.gameObject.scene.IsValid()) return;
            float seconds;
            if (!TimerPolicy.TryGrowthSeconds(Utils.GetPrefabName(plant.gameObject), instance.mushrooms.Value, out seconds)) return;
            plant.m_growTime = seconds; plant.m_growTimeMax = seconds;
        }

        private void Shutdown()
        {
            ready = false;
            Config.SettingChanged -= Changed;
            SynchronizationManager.OnConfigurationSynchronized -= Synchronized;
            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
            if (ReferenceEquals(instance, this)) instance = null;
        }
        private void OnDestroy() { Shutdown(); }

        [HarmonyPatch(typeof(Pickable), "Awake")]
        private static class PickableAwake
        {
            [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyAfter("advize.PlantEverything")]
            private static void Postfix(Pickable __instance) { ApplyPickable(__instance); }
        }
        [HarmonyPatch(typeof(Pickable), "UpdateRespawn")]
        private static class PickableRespawn
        {
            [HarmonyPrefix, HarmonyPriority(Priority.Last), HarmonyAfter("advize.PlantEverything")]
            private static void Prefix(Pickable __instance) { ApplyPickable(__instance); }
        }
        [HarmonyPatch(typeof(Plant), "Awake")]
        private static class PlantAwake
        {
            [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyAfter("advize.PlantEverything")]
            private static void Postfix(Plant __instance) { ApplyPlant(__instance); }
        }
        [HarmonyPatch(typeof(Plant), "GetGrowTime")]
        private static class PlantGrowTime
        {
            [HarmonyPrefix, HarmonyPriority(Priority.Last), HarmonyAfter("advize.PlantEverything")]
            private static void Prefix(Plant __instance) { ApplyPlant(__instance); }
        }
    }
}
