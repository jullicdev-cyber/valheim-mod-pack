// Test only. Requires the isolated QoL smoke environment; never shipped with the mod.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
namespace ValheimModPack.BridgeSmoke
{
    public static class NativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException("Favorite bridge native check: " + message); }
        private static bool OwnedPrefix(MethodInfo target, string owner)
        {
            var info = Harmony.GetPatchInfo(target);
            if (info == null) return false;
            foreach (var patch in info.Prefixes) if (patch.owner == owner) return true;
            return false;
        }
        public static string Run()
        {
            checks = 0;
            string root = Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
            if (String.IsNullOrEmpty(root)) throw new InvalidOperationException("Native favorite probe requires an isolated smoke root");
            string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(Paths.ConfigPath).StartsWith(boundary, StringComparison.OrdinalIgnoreCase), "Fixture writes stay inside isolated config directory");
            Check(Player.m_localPlayer == null, "Probe only runs without a live player");
            Check(Chainloader.PluginInfos.ContainsKey(Plugin.Id) && Chainloader.PluginInfos[Plugin.Id].Metadata.Version == new System.Version("1.2.0"), "Bridge 1.2.0 loaded");
            Check(Chainloader.PluginInfos.ContainsKey("Azumatt.AzuAutoStore") && Chainloader.PluginInfos["Azumatt.AzuAutoStore"].Metadata.Version == new System.Version("3.1.6"), "Azu 3.1.6 loaded");
            object bridge = Chainloader.PluginInfos[Plugin.Id].Instance;
            object state = typeof(Plugin).GetField("autoStore", All).GetValue(bridge);
            Check(state != null && (bool)state.GetType().GetProperty("Ready", All).GetValue(state, null), "Guard, state linkage and filters initialized before shortcuts");
            Check(state.GetType().GetField("getConfig", All).GetValue(state) != null && state.GetType().GetField("isFavorite", All).GetValue(state) != null, "Actual favorite API methods resolved");
            string guardOwner = Plugin.Id + ".azustore-guard", favoriteOwner = Plugin.Id + ".azustore-favorites";
            foreach (var entry in new[] { new[] { "AzuAutoStore.AzuAutoStorePlugin", "Update" }, new[] { "AzuAutoStore.Util.Functions", "TryStore" } })
                Check(OwnedPrefix(AccessTools.Method(AccessTools.TypeByName(entry[0]), entry[1], Type.EmptyTypes), guardOwner), "Actual manual dispatch guard installed: " + entry[1]);
            Check(OwnedPrefix(AccessTools.Method(AccessTools.TypeByName("AzuAutoStore.Util.Functions"), "TryStoreThisItem", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }), guardOwner), "Actual single-item dispatch guard installed");
            Type quickType = AccessTools.TypeByName("QuickStackStore.UserConfig"), azuType = AccessTools.TypeByName("AzuAutoStore.Patches.Favoriting.UserConfig");
            foreach (string target in new[] { "VanillaContainers", "BackpackContainer", "kgDrawer", "mkzDrawer" })
                Check(OwnedPrefix(AccessTools.Method(AccessTools.TypeByName("AzuAutoStore.Interfaces." + target), "CantStoreFavorite", new[] { typeof(ItemDrop.ItemData), azuType }), favoriteOwner), "Native favorite filter installed: " + target);
            Check(OwnedPrefix(azuType.GetMethod("Save", All), favoriteOwner), "Azu two-list writer is intercepted");
            var field = state.GetType().GetField("stateLink", All); object previousLink = field.GetValue(state);
            try
            {
                foreach (bool quickFirst in new[] { true, false })
                {
                    long id = quickFirst ? 911007001001L : 911007001002L;
                    if (Environment.GetEnvironmentVariable("VMP_BIND_RESTART") == "1") id += 100;
                    string primary = Path.Combine(Paths.ConfigPath, "QuickStackStore_player_" + id + ".dat");
                    string legacy = Path.Combine(Paths.ConfigPath, "AzuAutoStore_player_" + id + ".dat");
                    Check(!File.Exists(primary) && !File.Exists(legacy), "Native fixture never overwrites existing preference files");
                    var legacyConfig = FormatterServices.GetUninitializedObject(azuType);
                    azuType.GetField("_favoritedSlots", All).SetValue(legacyConfig, new HashSet<Vector2i> { new Vector2i(2, 2) });
                    azuType.GetField("_favoritedItems", All).SetValue(legacyConfig, new HashSet<string> { "$item_wood" });
                    azuType.GetMethod("WriteFile", All).Invoke(legacyConfig, new object[] { legacy });
                    byte[] legacyBefore = File.ReadAllBytes(legacy);
                    ResetLink(state, field, quickType, azuType);
                    object quick, azu;
                    if (quickFirst) { quick = quickType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id }); azu = azuType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id }); }
                    else { azu = azuType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id }); quick = quickType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id }); }
                    var slots = (HashSet<Vector2i>)quickType.GetField("favoritedSlots", All).GetValue(quick);
                    var items = (HashSet<string>)quickType.GetField("favoritedItems", All).GetValue(quick);
                    var trash = (HashSet<string>)quickType.GetField("trashFlaggedItems", All).GetValue(quick);
                    Check(items.Contains("$item_wood") && slots.Contains(new Vector2i(2, 2)), "Native missing-primary migration works with either first getter");
                    Check(ReferenceEquals(items, azuType.GetField("_favoritedItems", All).GetValue(azu)) && ReferenceEquals(slots, azuType.GetField("_favoritedSlots", All).GetValue(azu)), "Native Azu/Quick Stack caches share exact sets");
                    trash.Add("$item_trophy_neck"); items.Clear(); slots.Clear(); quickType.GetMethod("Save", All).Invoke(quick, null);
                    var shared = new ItemDrop.ItemData.SharedData { m_name = "$item_stone" };
                    azuType.GetMethod("ToggleItemNameFavoriting", All).Invoke(azu, new object[] { shared });
                    Check(items.Contains("$item_stone") && trash.Contains("$item_trophy_neck"), "Real Azu toggle updates Quick Stack memory and preserves trash flags");
                    for (int reload = 0; reload < 2; reload++)
                    {
                        ((IDictionary)quickType.GetField("playerConfigs", All).GetValue(null)).Remove(id);
                        ((IDictionary)azuType.GetField("PlayerConfigs", All).GetValue(null)).Remove(id);
                        ResetLink(state, field, quickType, azuType);
                        quick = quickType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id });
                        azu = azuType.GetMethod("GetPlayerConfig", All).Invoke(null, new object[] { id });
                        items = (HashSet<string>)quickType.GetField("favoritedItems", All).GetValue(quick);
                        trash = (HashSet<string>)quickType.GetField("trashFlaggedItems", All).GetValue(quick);
                        Check(!items.Contains("$item_wood") && items.Contains("$item_stone"), "Native reload never resurrects stale legacy favorites");
                        Check(trash.Contains("$item_trophy_neck"), "Actual native three-list file retains trash flags after Azu save and reload");
                    }
                    Check(Convert.ToBase64String(File.ReadAllBytes(legacy)) == Convert.ToBase64String(legacyBefore), "Legacy native file kept unchanged");
                    ((IDictionary)quickType.GetField("playerConfigs", All).GetValue(null)).Remove(id);
                    ((IDictionary)azuType.GetField("PlayerConfigs", All).GetValue(null)).Remove(id);
                }
                return "PASS: " + checks + " native Azu/Quick Stack bridge assertions: actual Harmony hooks, canonical live sets, both first-reader orders, vendor serialization/trash flags and two reloads. Live K-to-chest gameplay remains an in-game check.";
            }
            finally { field.SetValue(state, previousLink); }
        }
        private static void ResetLink(object state, FieldInfo field, Type quick, Type azu)
        {
            object replacement = Activator.CreateInstance(field.FieldType, All, null, new object[] { Paths.ConfigPath,
                quick.GetMethod("GetPlayerConfig", All), azu.GetMethod("GetPlayerConfig", All), quick.GetMethod("Save", All),
                quick.GetField("favoritedSlots", All), quick.GetField("favoritedItems", All), azu.GetField("_favoritedSlots", All), azu.GetField("_favoritedItems", All) }, null);
            field.SetValue(state, replacement);
        }
    }
}
