// Test-only BepInEx plugin. Never include this file in ExpeditionLoadouts.dll.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx;
using BepInEx.Bootstrap;
using Jotunn.Managers;
using UnityEngine;
using ValheimModPack.ExpeditionLoadouts;

namespace ValheimModPack.LoadoutSmoke
{
    [BepInPlugin("valheimmodpack.qol.smokeprobe", "Independent QoL native smoke probe", "1.0.0")]
    [BepInDependency("valheimmodpack.expeditionloadouts", "1.0.0")]
    [BepInDependency("valheimmodpack.chestsearch", "1.0.0")]
    [BepInDependency("valheimmodpack.confirmpinremoval", "1.2.0")]
    [BepInDependency("valheimmodpack.eaqsquickstackbridge", "1.1.0")]
    [BepInDependency("valheimmodpack.interfaceinputfix", "1.0.0")]
    [BepInDependency("valheimmodpack.renewableresourcetimers", "1.0.0")]
    [BepInDependency("valheimmodpack.nordicradio", "1.1.1")]
    [BepInDependency("isimp.Bindrune", "0.5.0")]
    [BepInDependency("Azumatt.Recycle_N_Reclaim", "1.4.5")]
    [BepInDependency("com.orianaventure.mod.VentureFloatingItems", "1.0.1")]
    public sealed class BackendNativeProbe : BaseUnityPlugin
    {
        private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private float started;
        private bool finished;
        private int checks;
        private string report = "";
        private static string Root { get { return Environment.GetEnvironmentVariable("VMP_QOL_SMOKE_ROOT"); } }

        private void Awake()
        {
            if (String.IsNullOrEmpty(Root)) { enabled = false; return; }
            started = Time.realtimeSinceStartup;
            Utils.SetSaveDataPath(Path.Combine(Root, "Saves"));
            PrefabManager.OnVanillaPrefabsAvailable += Available;
        }

        private void Available()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Available;
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            while (ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab("Wood") == null) yield return null;
            // Let every BepInEx Start bind its settings before inspecting optional compatibility.
            yield return null;
            try
            {
                foreach (string id in new[] { "valheimmodpack.expeditionloadouts", "valheimmodpack.chestsearch", "valheimmodpack.confirmpinremoval", "valheimmodpack.nordicradio" })
                {
                    Check(Chainloader.PluginInfos.ContainsKey(id), "Loaded " + id);
                    Check(Chainloader.PluginInfos[id].Instance.enabled, "Enabled " + id);
                }
                var plugin = (ValheimModPack.ExpeditionLoadouts.Plugin)Chainloader.PluginInfos[ValheimModPack.ExpeditionLoadouts.Plugin.Id].Instance;
                Check(plugin.Service != null, "Native RPC patch initialized");
                report += RecycleNativeChecks.Run();
                report += FloatingItemsNativeChecks.Run();
                report += FermenterNativeChecks.Run();
                object mapPlugin = Chainloader.PluginInfos["valheimmodpack.confirmpinremoval"].Instance;
                Check(!(bool)mapPlugin.GetType().GetField("failed", All).GetValue(mapPlugin)
                    && mapPlugin.GetType().GetField("history", All).GetValue(mapPlugin) != null, "Map confirmation and history initialized");
                object searchPlugin = Chainloader.PluginInfos["valheimmodpack.chestsearch"].Instance;
                Check((bool)searchPlugin.GetType().GetField("ready", All).GetValue(searchPlugin), "ChestSearch initialization complete");
                foreach (string prefab in new[] { "piece_chest_wood", "piece_chest", "piece_chest_private", "piece_chest_blackmetal" })
                {
                    GameObject chest = PrefabManager.Instance.GetPrefab(prefab);
                    Check(chest != null && chest.GetComponent<Container>() != null && chest.GetComponent<Piece>() != null, "Native container allowlist " + prefab);
                }
                CheckNativeMoves();
                CheckDestinationSelection(plugin.Service);
                CheckOptionalApis();
                if (GUIManager.CustomGUIFront == null)
                    typeof(GUIManager).GetMethod("TryCreateGUI", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(GUIManager.Instance, null);
                Check(GUIManager.CustomGUIFront != null, "Native probe GUI canvas initialized");
                report += ValheimModPack.ChestSearch.NativeChecks.Run() + "\n";
                report += ValheimModPack.InterfaceInputFix.NativeChecks.Run() + "\n";
                report += ValheimModPack.InterfaceInputFix.BindruneNativeChecks.Run() + "\n";
                report += ValheimModPack.RenewableResourceTimers.NativeChecks.Run() + "\n";
                string mapProbe = Path.Combine(Root, "PinHistoryNativeChecks.dll");
                if (!File.Exists(mapProbe)) throw new FileNotFoundException("Map native probe missing", mapProbe);
                var mapChecks = Assembly.LoadFrom(mapProbe).GetType("ValheimModPack.PinRemoval.NativeChecks", true);
                report += (string)mapChecks.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null) + "\n";
                var bridgeChecks = Assembly.LoadFrom(Path.Combine(Root, "EAQSAzuNativeChecks.dll"))
                    .GetType("ValheimModPack.BridgeSmoke.NativeChecks", true);
                report += (string)bridgeChecks.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null) + "\n";
                Finish("PASS: " + checks + " native assertions.\n" + report, 0);
            }
            catch (Exception error) { Finish("FAIL after " + checks + " checks: " + error + "\n" + report, 2); }
        }

        private void CheckNativeMoves()
        {
            Inventory source = new Inventory("Probe source", null, 8, 4);
            Inventory destination = new Inventory("Probe destination", null, 8, 6);
            ItemDrop.ItemData wood = Item("Wood", 40, 0, 0);
            source.GetAllItems().Add(wood);
            Check(destination.MoveItemToThis(source, wood, 12, 2, 1), "Native partial move succeeded");
            Check(wood.m_stack == 28 && destination.GetItemAt(2, 1).m_stack == 12 && source.GetAllItems().Contains(wood), "Native partial decrement and source identity");
            Check(destination.MoveItemToThis(source, wood, 28, 2, 1), "Native whole remainder moved");
            Check(wood.m_stack == 0 && source.GetAllItems().Count == 0 && destination.GetItemAt(2, 1).m_stack == 40, "Whole source removed and original reference decremented");
            ItemDrop.ItemData stone = Item("Stone", 20, 0, 0);
            source.GetAllItems().Add(stone);
            Check(!destination.MoveItemToThis(source, stone, 10, 2, 1), "Native unlike destination rejects move");
            Check(stone.m_stack == 20 && destination.GetItemAt(2, 1).m_stack == 40, "Native rejected move conserves both inventories");
            ItemDrop.ItemData sword = Item("SwordBronze", 1, 1, 0);
            sword.m_quality = 2; sword.m_variant = 0; sword.m_worldLevel = 1;
            sword.m_durability = 7; sword.m_crafterID = 81234; sword.m_crafterName = "Native fixture";
            source.GetAllItems().Add(sword);
            Check(destination.MoveItemToThis(source, sword, 1, 3, 1), "Native non-stackable equipment move succeeded");
            var movedSword = destination.GetItemAt(3, 1);
            Check(movedSword != null && movedSword.m_quality == 2 && movedSword.m_worldLevel == 1
                && movedSword.m_durability == 7 && movedSword.m_crafterID == 81234 && movedSword.m_crafterName == "Native fixture"
                && !movedSword.m_equipped && !source.GetAllItems().Contains(sword), "Equipment transfer preserves quality, world level, wear and crafter without equipping");
            report += "Actual Inventory.MoveItemToThis: explicit partial/whole movement, original stack reference, removal and occupied-cell rejection verified.\n";
        }

        private void CheckDestinationSelection(ChestService service)
        {
            FieldInfo guardField = typeof(ChestService).GetField("guard", All);
            object previous = guardField.GetValue(service);
            Type guardType = guardField.FieldType;
            object guard = FormatterServices.GetUninitializedObject(guardType);
            guardType.GetField("visible", All).SetValue(guard, typeof(BackendNativeProbe).GetMethod("VisibleRows", All));
            guardType.GetField("fullHeight", All).SetValue(guard, typeof(BackendNativeProbe).GetMethod("FullHeight", All));
            var favorites = new FavoriteFixture();
            favorites.Cells.Add("0,1");
            guardType.GetField("favorites", All).SetValue(guard, favorites);
            guardType.GetField("favoriteCell", All).SetValue(guard, typeof(FavoriteFixture).GetMethod("Cell", All));
            guardType.GetField("favoriteItem", All).SetValue(guard, typeof(FavoriteFixture).GetMethod("Item", All));
            guardField.SetValue(service, guard);
            MethodInfo find = typeof(ChestService).GetMethod("FindDestination", All);
            try
            {
                Inventory destination = new Inventory("Probe protected inventory", null, 8, 6);
                destination.GetAllItems().Add(Item("Wood", 1, 0, 0));
                destination.GetAllItems().Add(Item("Wood", 1, 0, 5));
                destination.GetAllItems().Add(Item("Wood", 1, 0, 1));
                ItemDrop.ItemData supply = Item("Wood", 10, 0, 0);
                object[] args = { destination, supply, 10, new Vector2i(-1, -1), 0 };
                Check((bool)find.Invoke(service, args), "Production selector finds unprotected cell");
                var cell = (Vector2i)args[3];
                Check(cell.x == 1 && cell.y == 1 && (int)args[4] == 10, "Production selector excludes hotbar, favorite and EAQS cells");
                for (int y = 1; y < 5; y++) for (int x = 0; x < 8; x++)
                    if (destination.GetItemAt(x, y) == null && (x != 7 || y != 4)) destination.GetAllItems().Add(Item("Stone", 50, x, y));
                args = new object[] { destination, supply, 10, new Vector2i(-1, -1), 0 };
                Check((bool)find.Invoke(service, args), "Production selector uses extra visible row");
                cell = (Vector2i)args[3];
                Check(cell.x == 7 && cell.y == 4, "Exact last visible destination");
                destination.GetAllItems().Add(Item("Stone", 50, 7, 4));
                args = new object[] { destination, supply, 10, new Vector2i(-1, -1), 0 };
                Check(!(bool)find.Invoke(service, args), "Full visible inventory does not spill into empty hidden cells");
                report += "Production destination selection over actual native inventories: fixture EAQS rows 5/6, hotbar, favorite slot, extra row and full-inventory exclusion verified.\n";
            }
            finally { guardField.SetValue(service, previous); }
        }

        private void CheckOptionalApis()
        {
            Type eaqs = null, quick = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                eaqs = eaqs ?? assembly.GetType("EquipmentAndQuickSlots.API");
                quick = quick ?? assembly.GetType("QuickStackStore.UserConfig");
            }
            Check(eaqs != null && eaqs.GetMethod("GetVisibleRows", All) != null && eaqs.GetMethod("GetFullHeight", All) != null, "Installed EAQS protection API resolves");
            Check(quick != null && quick.GetMethod("GetPlayerConfig", All) != null && quick.GetMethod("IsSlotFavorited", All) != null
                && quick.GetMethod("IsItemNameFavorited", All) != null, "Installed Quick Stack favorite API resolves");
            report += "Optional installed EAQS and Quick Stack protection APIs resolve in the real Unity/BepInEx process.\n";
        }

        private static ItemDrop.ItemData Item(string prefab, int count, int x, int y)
        {
            GameObject definition = ObjectDB.instance.GetItemPrefab(prefab);
            ItemDrop.ItemData item = definition.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = definition;
            item.m_stack = count;
            item.m_equipped = false;
            item.m_gridPos = new Vector2i(x, y);
            item.m_customData.Clear();
            return item;
        }

        private static int VisibleRows() { return 5; }
        private static int FullHeight() { return 6; }
        private sealed class FavoriteFixture
        {
            internal readonly HashSet<string> Cells = new HashSet<string>();
            public bool Cell(Vector2i p) { return Cells.Contains(p.x + "," + p.y); }
            public bool Item(ItemDrop.ItemData.SharedData data) { return false; }
        }
        private void Check(bool condition, string label) { checks++; if (!condition) throw new InvalidOperationException(label); }
        private void Update() { if (!finished && Time.realtimeSinceStartup - started > 90) Finish("FAIL timeout waiting for native prefab/data initialization", 3); }
        private void Finish(string result, int code)
        {
            if (finished) return;
            finished = true;
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "result.txt"), result);
            Logger.LogInfo(result);
            Application.Quit(code);
        }
        private void OnDestroy() { PrefabManager.OnVanillaPrefabsAvailable -= Available; }
    }
}
