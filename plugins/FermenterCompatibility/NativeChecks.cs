// Test-only: runs inside the isolated full-pack Valheim probe, with no player/world loaded.
using System;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.LoadoutSmoke
{
    internal static class FermenterNativeChecks
    {
        internal static string Run()
        {
            var plugin = Chainloader.PluginInfos["valheimmodpack.fermentercompatibility"].Instance;
            if (!(bool)plugin.GetType().GetProperty("Patched").GetValue(plugin, null)) throw new Exception("Crafty compatibility not installed");
            if (!(bool)plugin.GetType().GetProperty("PlusPatched").GetValue(plugin, null)) throw new Exception("V+ compatibility not installed");
            if (Player.m_localPlayer != null) throw new Exception("This test must not run with a live character");
            Type type = Chainloader.PluginInfos["Azumatt.AzuCraftyBoxes"].Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Patches.SearchContainersAsWell", true);
            MethodInfo patch = AccessTools.Method(type, "Postfix");
            if (!Harmony.GetPatchInfo(patch).Owners.Contains("valheimmodpack.fermentercompatibility.crafty")) throw new Exception("Missing vendor guard");
            var holder = new GameObject("VMP.IsolatedFermenter"); holder.SetActive(false);
            Fermenter fermenter = holder.AddComponent<Fermenter>();
            var inventory = new Inventory("isolated supplies", null, 2, 2);
            ItemDrop.ItemData sentinel = new ItemDrop.ItemData();
            try
            {
                object[] arguments = { fermenter, inventory, sentinel };
                patch.Invoke(null, arguments);
                if (!ReferenceEquals(arguments[2], sentinel)) throw new Exception("Guard changed the vanilla result");
                // Exercise the actual Harmony-wrapped vanilla method as used by V+ automatic filling.
                fermenter.m_conversion.Clear();
                if (AccessTools.Method(typeof(Fermenter), "FindCookableItem").Invoke(fermenter, new object[] { inventory }) != null)
                    throw new Exception("An empty chest invented a recipe input");
                patch.Invoke(null, new object[] { null, inventory, sentinel });
                patch.Invoke(null, new object[] { fermenter, null, sentinel });
                if (inventory.GetAllItems().Count != 0) throw new Exception("Background guard mutated inventory");
                MethodInfo conversion = AccessTools.Method(typeof(Fermenter), "GetItemConversion", new[] { typeof(int) });
                if (conversion.Invoke(fermenter, new object[] { 0 }) != null) throw new Exception("Missing recipe must stay null");
                var recipe = new Fermenter.ItemConversion { m_from = ObjectDB.instance.GetItemPrefab("Wood").GetComponent<ItemDrop>(), m_producedItems = 4 };
                fermenter.m_conversion.Add(recipe);
                if (!ReferenceEquals(recipe, conversion.Invoke(fermenter, new object[] { "Wood".GetStableHashCode() })) || recipe.m_producedItems != 6)
                    throw new Exception("Valid V+ production count must still apply");
                return "FermenterCompatibility PASS: actual vendor guards, playerless FindCookableItem, unchanged result/null arguments, missing recipe stays null, valid V+ yield remains six; no inventory mutation. Manual player interaction remains a gameplay check.\n";
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }
    }
}
