// Test-only checks against the vendor DLL and real game prefabs, never shipped as a plugin.
using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using VentureValheim.FloatingItems;
using VendorFloating = VentureValheim.FloatingItems.FloatingItems;

public static class FloatingItemsNativeChecks
{
    private static int checks;
    public static string Run()
    {
        checks = 0;
        string root = Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
        Check(!String.IsNullOrEmpty(root) && Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "isolated process required");
        Check(Player.m_localPlayer == null, "no user character loaded");
        const string id = "com.orianaventure.mod.VentureFloatingItems";
        var plugin = Chainloader.PluginInfos[id];
        Check(plugin.Metadata.Version == new System.Version("1.0.1") && plugin.Instance.enabled, "vendor release loaded");
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(ObjectDB), "Awake")).Owners.Contains(id), "ObjectDB hook installed");
        Check(!FloatingItemsPlugin.GetFloatEverything(), "author's selective defaults retained");
        Check(FloatingItemsPlugin.GetSinkingItems() == "BronzeNails, IronNails", "nail exclusions retained");
        Check(FloatingItemsPlugin.GetFloatTrophies() && FloatingItemsPlugin.GetFloatMeat() && FloatingItemsPlugin.GetFloatHides() && FloatingItemsPlugin.GetFloatGearAndCraftable() && FloatingItemsPlugin.GetFloatTreasure(), "standard categories enabled");
        VendorFloating.Update();
        var shouldFloat = AccessTools.Method(typeof(VendorFloating), "ShouldFloat");
        foreach (string name in new[] { "SwordBronze", "TrophyDeer", "DeerMeat", "DeerHide", "Coins", "SerpentScale", "BonemawSerpentTooth" })
        {
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            Check(prefab != null, "real item available: " + name);
            Check((bool)shouldFloat.Invoke(null, new object[] { name.ToLowerInvariant(), prefab }), "category matches: " + name);
        }

        // Inactive clones exercise component changes without spawning network objects or touching a world.
        var holder = new GameObject("Floating test fixtures");
        holder.SetActive(false);
        GameObject sword = null;
        try
        {
            sword = UnityEngine.Object.Instantiate(ObjectDB.instance.GetItemPrefab("SwordBronze"), holder.transform);
            sword.name = "FloatingTestSword";
            var body = sword.GetComponent<Rigidbody>();
            Check(body != null && sword.GetComponentInChildren<Collider>() != null, "fixture has native item physics");
            body.centerOfMass = new Vector3(-3f, -3f, -3f);
            var apply = AccessTools.Method(typeof(VendorFloating), "ApplyFloatingComponent");
            apply.Invoke(null, new object[] { sword });
            apply.Invoke(null, new object[] { sword });
            var floating = sword.GetComponent<Floating>();
            Check(floating != null && floating.enabled && sword.GetComponents<Floating>().Length == 1, "one enabled floating component after repeated apply");
            Check(Mathf.Approximately(floating.m_waterLevelOffset, .7f), "vendor water offset set");
            Check(body.centerOfMass == Vector3.zero, "extreme negative center of mass normalized");
            AccessTools.Method(typeof(VendorFloating), "DisableFloatingComponent").Invoke(null, new object[] { sword });
            Check(!floating.enabled, "explicit sinking disables buoyancy");
            var noPhysics = new GameObject("No physics fixture");
            noPhysics.transform.SetParent(holder.transform);
            apply.Invoke(null, new object[] { noPhysics });
            Check(noPhysics.GetComponent<Floating>() == null, "objects without item physics ignored");
        }
        finally
        {
            foreach (string field in new[] { "FloatingAddedPrefabs", "FloatingDisabledPrefabs" })
                ((System.Collections.Generic.HashSet<string>)AccessTools.Field(typeof(VendorFloating), field).GetValue(null)).Remove("floatingtestsword");
            UnityEngine.Object.DestroyImmediate(holder);
        }
        return "Venture Floating Items 1.0.1 PASS " + checks + ": real vendor load/hook/defaults, seven native item categories, inactive-clone buoyancy and center-of-mass safeguard. Live water and multiplayer not simulated.\n";
    }
    private static void Check(bool value, string detail)
    { checks++; if (!value) throw new InvalidOperationException("Floating Items: " + detail); }
}
