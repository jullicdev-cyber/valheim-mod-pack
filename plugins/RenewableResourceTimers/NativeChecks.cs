// Optional native-engine smoke helper. Never include in the shipped plugin.
using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace ValheimModPack.RenewableResourceTimers
{
    public static class NativeChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException("Renewable timers native check: " + message); }

        public static string Run()
        {
            checks = 0;
            Check(Chainloader.PluginInfos.ContainsKey(Plugin.Id), "plugin loaded");
            var plugin = (Plugin)Chainloader.PluginInfos[Plugin.Id].Instance;
            Check(plugin.enabled && (bool)typeof(Plugin).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(plugin), "initialization complete");
            var compatibility = (NetworkCompatibilityAttribute)Attribute.GetCustomAttribute(typeof(Plugin), typeof(NetworkCompatibilityAttribute));
            Check(compatibility.EnforceModOnClients == CompatibilityLevel.EveryoneMustHaveMod && compatibility.EnforceSameVersion == VersionStrictness.Patch, "all-peer matching-version requirement");
            foreach (var key in plugin.Config.Keys)
            {
                bool synchronized = false;
                foreach (object tag in plugin.Config[key].Description.Tags)
                {
                    var configTag = tag as ConfigurationManagerAttributes;
                    if (configTag != null && configTag.IsAdminOnly) synchronized = true;
                }
                Check(synchronized, "admin-only synchronized config: " + key.Key);
            }
            foreach (var entry in new[] { new object[] { typeof(Pickable), "Awake" }, new object[] { typeof(Pickable), "UpdateRespawn" },
                new object[] { typeof(Plant), "Awake" }, new object[] { typeof(Plant), "GetGrowTime" } })
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method((Type)entry[0], (string)entry[1]));
                Check(info != null && info.Owners.Contains(Plugin.Id), "registered patch: " + entry[0] + "." + entry[1]);
            }
            var parent = new GameObject("RenewableTimers.NativeProbe.Inactive");
            parent.SetActive(false);
            try
            {
                CheckVine(parent.transform, plugin);
                CheckPlant(parent.transform, plugin, "sapling_jotunpuffs", true);
                CheckPlant(parent.transform, plugin, "sapling_magecap", true);
                CheckPlant(parent.transform, plugin, "sapling_carrot", false);
                CheckPlant(parent.transform, plugin, "VineAsh_sapling", false);
                foreach (string name in new[] { "Pickable_Mushroom_JotunPuffs", "Pickable_Mushroom_Magecap", "Pickable_Stone", "Pickable_Flint" })
                {
                    var prefab = Prefab(name);
                    var source = prefab.GetComponent<Pickable>();
                    Check(source != null, "native Pickable exists: " + name);
                    var clone = UnityEngine.Object.Instantiate(prefab, parent.transform, false);
                    Check(!clone.activeInHierarchy, "probe instance stays inactive");
                    var pickable = clone.GetComponent<Pickable>();
                    float original = pickable.m_respawnTimeMinutes;
                    GameObject hidden = pickable.m_hideWhenPicked;
                    Apply("ApplyPickable", pickable);
                    Check(pickable.m_respawnTimeMinutes == original && pickable.m_hideWhenPicked == hidden, "one-shot/mineral native behavior preserved: " + name);
                    Check(source.m_respawnTimeMinutes == original, "prefab not mutated: " + name);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
            return "RenewableResourceTimers native PASS " + checks + ": real prefabs, inactive clone fields, synchronized config tags and four installed Harmony hooks; live multiplayer timing not simulated.";
        }
        private static GameObject Prefab(string name)
        {
            var value = PrefabManager.Instance.GetPrefab(name);
            Check(value != null, "prefab available: " + name);
            return value;
        }
        private static void Apply(string method, object value)
        { typeof(Plugin).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { value }); }
        private static void CheckVine(Transform parent, Plugin plugin)
        {
            var prefab = Prefab("VineAsh");
            var source = prefab.GetComponent<Pickable>();
            Check(source != null && source.m_respawnTimeMinutes > 0, "ashvine is naturally renewable");
            float original = source.m_respawnTimeMinutes;
            var clone = UnityEngine.Object.Instantiate(prefab, parent, false);
            Check(!clone.activeInHierarchy, "vine probe remains inactive");
            var value = clone.GetComponent<Pickable>();
            float initialMin = value.m_respawnTimeInitMin, initialMax = value.m_respawnTimeInitMax;
            int amount = value.m_amount;
            GameObject hidden = value.m_hideWhenPicked;
            Apply("ApplyPickable", value);
            int desired = (int)plugin.Config["Timers", "VineberryRespawnMinutes"].BoxedValue;
            Check(value.m_respawnTimeMinutes == TimerPolicy.SafeMinutes(desired, 45), "vine configured minutes applied");
            Check(value.m_respawnTimeInitMin == initialMin && value.m_respawnTimeInitMax == initialMax, "initial maturity randomness preserved");
            Check(value.m_amount == amount && value.m_hideWhenPicked == hidden, "yield and visual remain native");
            Check(source.m_respawnTimeMinutes == original, "source vine prefab unchanged");
        }
        private static void CheckPlant(Transform parent, Plugin plugin, string name, bool changed)
        {
            var prefab = Prefab(name);
            var source = prefab.GetComponent<Plant>();
            Check(source != null, "native Plant exists: " + name);
            float originalMin = source.m_growTime, originalMax = source.m_growTimeMax;
            var clone = UnityEngine.Object.Instantiate(prefab, parent, false);
            Check(!clone.activeInHierarchy, "plant probe remains inactive");
            var value = clone.GetComponent<Plant>();
            float radius = value.m_growRadius;
            Apply("ApplyPlant", value);
            int desired = (int)plugin.Config["Timers", "MistlandsMushroomGrowthMinutes"].BoxedValue;
            float seconds = TimerPolicy.SafeMinutes(desired, 30) * 60f;
            Check(changed ? value.m_growTime == seconds && value.m_growTimeMax == seconds : value.m_growTime == originalMin && value.m_growTimeMax == originalMax,
                "correct growth scope and units: " + name);
            Check(value.m_growRadius == radius, "growth spacing unchanged: " + name);
            Check(source.m_growTime == originalMin && source.m_growTimeMax == originalMax, "plant prefab unchanged: " + name);
        }
    }
}
