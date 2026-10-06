// Test-only code compiled into the isolated QoL probe, never shipped as a plugin.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Bindrune.Discovery;
using HarmonyLib;
using Recycle_N_Reclaim;
using Recycle_N_Reclaim.GamePatches.Recycling;
using Recycle_N_Reclaim.YAMLStuff;
using UnityEngine;

public static class RecycleNativeChecks
{
    private static int checks;
    public static string Run()
    {
        checks = 0;
        string root = Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT");
        Check(!String.IsNullOrEmpty(root) && Path.GetFullPath(Paths.BepInExRootPath).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), "isolated files required");
        Check(Player.m_localPlayer == null, "no real character involved");
        var info = Chainloader.PluginInfos["Azumatt.Recycle_N_Reclaim"];
        Check(info.Metadata.Version == new System.Version("1.4.5") && info.Instance.enabled, "exact release loaded");
        Check(Recycle_N_ReclaimPlugin.HasEpicLoot, "Epic Loot API recognized");
        Check(Recycle_N_ReclaimPlugin.RecyclingTabButtonHolder != null, "station tab controller initialized");
        Check(Recycle_N_ReclaimPlugin.RecyclingRate.Value == .5f, "50 percent reclaim rate");
        Check(Recycle_N_ReclaimPlugin.discardInvEnabled.Value == Recycle_N_ReclaimPlugin.Toggle.Off, "inventory discard off");
        Check(Recycle_N_ReclaimPlugin.ContainerRecyclingEnabled.Value == Recycle_N_ReclaimPlugin.Toggle.Off, "bulk container recycle off");
        Check(Recycle_N_ReclaimPlugin.HideEquippedItemsInRecyclingTab.Value == Recycle_N_ReclaimPlugin.Toggle.On, "equipped items hidden");
        Check(Recycle_N_ReclaimPlugin.IgnoreItemsOnHotbar.Value == Recycle_N_ReclaimPlugin.Toggle.On, "hotbar hidden");
        Check(Recycle_N_ReclaimPlugin.RequireExactCraftingStationForRecycling.Value == Recycle_N_ReclaimPlugin.Toggle.On, "station requirements retained");
        Check(Recycle_N_ReclaimPlugin.UndoRecycleGracePeriodSeconds.Value == 0, "unsafe custom-data undo disabled");
        foreach (var key in new[] { Recycle_N_ReclaimPlugin.hotKey, Recycle_N_ReclaimPlugin.TrashingKeybind, Recycle_N_ReclaimPlugin.TrashingModifierKeybind1, Recycle_N_ReclaimPlugin.UndoRecycleKeybind })
            Check(key.Value.MainKey == KeyCode.None, "no accidental discard hotkey");
        string tab = Localization.instance.Localize("$azumatt_recycle_n_reclaim_reclaim_tab");
        Check(!String.IsNullOrEmpty(tab) && !tab.StartsWith("$"), "localized station tab");
        BindRegistry.Refresh();
        Check(BindRegistry.All.Count(b => b.OwnerGuid == "Azumatt.Recycle_N_Reclaim" && b.Handle is ConfigEntryBase) >= 4, "Bindrune discovers shortcut settings");
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel")).Owners.Contains("Azumatt.Recycle_N_Reclaim"), "crafting Harmony hook installed");

        bool hasBackpacks = Chainloader.PluginInfos.ContainsKey("vapok.mods.adventurebackpacks");
        GameObject[] bags = new GameObject[0];
        if (hasBackpacks)
        {
            bags = ObjectDB.instance.m_items.Where(p => p != null && p.GetComponent<ItemDrop>() != null && (p.name.StartsWith("Backpack") || p.name == "CapeIronBackpack" || p.name == "CapeSilverBackpack")).ToArray();
            Check(bags.Length >= 6, "actual Adventure Backpacks prefabs loaded");
            foreach (var bag in bags) Check(GroupUtils.IsPrefabExcludedInReclaiming(bag.name), "backpack excluded: " + bag.name);
        }
        Check(!GroupUtils.IsPrefabExcludedInReclaiming("Hammer"), "ordinary tools allowed");
        Check(!GroupUtils.GetRecycleRateOverride("ArmorLeatherChest").HasValue, "no example armor rate override");

        var hammer = Item("Hammer", 1, 0, 1);
        var context = new RecyclingAnalysisContext(hammer);
        context.Recipe = ObjectDB.instance.GetRecipe(hammer);
        Check(context.Recipe != null, "real hammer recipe available");
        AccessTools.Method(typeof(Reclaimer), "AnalyzeMaterialYieldForItem").Invoke(null, new object[] { context });
        var yields = context.Entries.Where(e => !e.InitialRecipeHadZero).ToArray();
        Check(yields.Length == 2 && yields.All(e => e.Amount == 1), "real recipe gives one wood and one stone at 50 percent; actual=" + String.Join(",", context.Entries.Select(e => e.Prefab.name + "=" + e.Amount).ToArray()));
        Check(context.Entries.Where(e => e.InitialRecipeHadZero).All(e => e.Amount == 0), "unused Valheim 1.0 upgrader requirements produce no resources");

        var full = new Inventory("Full fixture", null, 8, 5);
        for (int y = 0; y < 5; y++) for (int x = 0; x < 8; x++) full.GetAllItems().Add(Item("Stone", 50, x, y));
        AccessTools.Method(typeof(Reclaimer), "AnalyzeInventoryHasEnoughEmptySlots").Invoke(null, new object[] { context, full });
        Check(context.RecyclingImpediments.Count > 0 && full.GetAllItems().Count == 40, "full inventory refuses analysis without mutation");

        var inventory = new Inventory("Reclaim fixture", null, 8, 5);
        inventory.GetAllItems().Add(hammer);
        context.RecyclingImpediments.Clear();
        AccessTools.Method(typeof(Reclaimer), "AnalyzeInventoryHasEnoughEmptySlots").Invoke(null, new object[] { context, inventory });
        Check(context.RecyclingImpediments.Count == 0, "space available for yield");
        var apply = Recycle_N_ReclaimPlugin.ApplyCraftedBy.Value;
        try
        {
            // Only this isolated fixture skips author stamping; no Player is created.
            Recycle_N_ReclaimPlugin.ApplyCraftedBy.Value = Recycle_N_ReclaimPlugin.Toggle.Off;
            Reclaimer.DoInventoryChanges(context, inventory, null);
            Check(!inventory.GetAllItems().Contains(hammer), "reclaim consumes exactly the selected source item");
            Check(inventory.CountItems("$item_wood") == 1 && inventory.CountItems("$item_stone") == 1, "real inventory receives expected resources");
        }
        finally { Recycle_N_ReclaimPlugin.ApplyCraftedBy.Value = apply; }
        return "Recycle N Reclaim 1.4.5 PASS " + checks + ": native load, config, localization, Epic Loot detection, Bindrune, " + bags.Length + " backpack exclusions, recipe yield and isolated inventory mutation. Live workstation UI/multiplayer not simulated.\n"
            + (hasBackpacks ? "" : "SKIP: Adventure Backpacks exclusion checks; optional plugin is absent.\n");
    }
    private static ItemDrop.ItemData Item(string name, int count, int x, int y)
    {
        var prefab = ObjectDB.instance.GetItemPrefab(name);
        var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
        item.m_dropPrefab = prefab; item.m_stack = count; item.m_quality = 1; item.m_gridPos = new Vector2i(x, y); item.m_equipped = false;
        return item;
    }
    private static void Check(bool value, string description)
    { checks++; if (!value) throw new InvalidOperationException("Recycle: " + description); }
}
