// Detached menu fixtures only; excluded from the released gameplay assembly.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class CombatNativeChecks
    {
        public static bool BackpackFixtureSkipped { get; private set; }

        private static GameObject Prefab(string name)
        {
            GameObject value = ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(name);
            if (value == null && ObjectDB.instance != null) value = ObjectDB.instance.GetItemPrefab(name);
            return value ?? PrefabManager.Instance.GetPrefab(name);
        }

        private static ItemDrop.ItemData Item(string name, int count)
        {
            GameObject prefab = Prefab(name);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null) throw new InvalidOperationException("Combat fixture item unavailable: " + name);
            ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab; item.m_stack = count; item.m_equipped = false;
            item.m_customData = item.m_customData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.m_customData);
            return item;
        }

        private static ItemDrop.ItemData Gear(string name, long world, string token, int revision)
        {
            ItemDrop.ItemData item = Item(name, 1);
            item.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(world, token, revision);
            item.m_customData[ArenaBuilder.KitStockKey] = token;
            return item;
        }

        public static void Run(Action<bool, string> check)
        {
            BackpackFixtureSkipped = false;
            var inspected = new HashSet<string>();
            for (int family = 0; family < CombatCatalog.FamilyCount; ++family)
                for (int difficulty = 0; difficulty < CombatCatalog.DifficultyCount; ++difficulty) {
                    PrisonCombatLoadout loadout = CombatCatalog.Get(family, difficulty);
                    GameObject mob = Prefab(loadout.MobPrefab);
                    check(mob != null && mob.GetComponent<ZNetView>() != null && mob.GetComponent<Character>() != null
                        && mob.GetComponent<MonsterAI>() != null && mob.GetComponent<CharacterDrop>() != null,
                        "catalog native enemy supports AI, stars, networking and ordinary loot: " + loadout.MobPrefab);
                    foreach (string name in loadout.GearPrefabs) {
                        if (!inspected.Add(name)) continue;
                        ItemDrop.ItemData item = Item(name, 1);
                        check(item.m_shared.m_maxQuality >= 1 && item.m_shared.m_maxStackSize >= 1,
                            "loadout native item defines quality and stack size: " + name);
                        if (item.m_shared.m_maxStackSize == 1) {
                            item.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(6789, new string('1', 32), 1);
                            check(ArenaBuilder.IsGeneratedGear(item), "loadout nonstackable item is valid removable armor or weapon: " + name);
                            check(!item.m_customData.ContainsKey(ArenaBuilder.LoanKey), "ordinary native chest equipment avoids legacy pickup guards: " + name);
                        }
                    }
                }

            const long world = 6789;
            string token = new string('1', 32), oldToken = new string('2', 32);
            var inventory = new Inventory("PartyPrison.CombatNative", null, 8, 8);
            var personal = Item("SwordBronze", 1); personal.m_customData["fixture_personal"] = "preserve";
            var loot = Item("Wood", 17); loot.m_customData["fixture_farm"] = "preserve";
            var arrows = Item("ArrowWood", 61);
            // Even malformed provenance on a merged supply stack must not erase it.
            arrows.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(world, oldToken, 1);
            var current = Gear("ArmorLeatherChest", world, token, 2);
            var future = Gear("ArmorLeatherLegs", world, token, 3);
            var old = Gear("HelmetLeather", world, token, 1);
            var priorSentence = Gear("MaceBronze", world, oldToken, 1);
            var otherWorld = Gear("ShieldWood", world + 1, token, 1);
            var legacy = Item("AxeBronze", 1); legacy.m_customData[ArenaBuilder.KitStockKey] = oldToken;
            var unrelated = Item("Resin", 9); unrelated.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(world, oldToken, 1);
            foreach (ItemDrop.ItemData item in new[] { personal, loot, arrows, current, future, old, priorSentence, otherWorld, legacy, unrelated })
                check(inventory.AddItem(item), "native inventory fixture insertion: " + item.m_dropPrefab.name);
            int removed = ArenaBuilder.RemoveObsoleteInventoryGear(inventory, world, token, 2);
            check(removed == 3 && inventory.GetAllItems().Count == 7, "choice replacement removes only obsolete armor, weapons and legacy stock");
            check(inventory.GetAllItems().Contains(current) && inventory.GetAllItems().Contains(otherWorld) && inventory.GetAllItems().Contains(personal),
                "current kit, personal equipment and unrelated world gear stay intact");
            check(inventory.GetAllItems().Contains(future), "new native chest gear is preserved if it arrives before the matching state revision");
            check(loot.m_stack == 17 && arrows.m_stack == 61 && unrelated.m_stack == 9 && inventory.GetAllItems().Contains(loot)
                && inventory.GetAllItems().Contains(arrows) && inventory.GetAllItems().Contains(unrelated), "farmed resources and mixed arrow stacks survive kit changes");
            check(ArenaBuilder.RemoveObsoleteInventoryGear(inventory, world, "", 0) == 2, "release removes all active armor without removing normal loot");
            var package = new ZPackage(); inventory.Save(package);
            var restored = new Inventory("PartyPrison.CombatRestored", null, 8, 8); restored.Load(new ZPackage(package.GetArray()));
            check(restored.GetAllItems().Count == 5 && restored.GetAllItems().Any(item => item.m_customData.ContainsKey("fixture_farm") && item.m_stack == 17)
                && restored.GetAllItems().Any(item => item.m_customData.ContainsKey("fixture_personal"))
                && restored.GetAllItems().Any(item => item.m_dropPrefab.name == "ArrowWood" && item.m_stack == 61),
                "native save/reload preserves all farm loot and personal equipment after release");
            check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 0, "release cleanup is idempotent after native persistence");
            CheckBackpackReplacement(check);
        }

        private static byte[] Save(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }

        private static void CheckBackpackReplacement(Action<bool, string> check)
        {
            Type api = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("AdventureBackpacks.API.ABAPI", false)).FirstOrDefault(type => type != null);
            if (api == null) { BackpackFixtureSkipped = true; return; }
            Type extensions = api.Assembly.GetType("Vapok.Common.Managers.ItemExtensions", true);
            Type componentType = api.Assembly.GetType("AdventureBackpacks.Components.BackpackComponent", true);
            MethodInfo data = extensions.GetMethod("Data", new[] { typeof(ItemDrop.ItemData) });
            check(extensions.Assembly == api.Assembly && componentType.Assembly == api.Assembly && data.ReturnType.Assembly == api.Assembly,
                "native backpack holder and component use the API's embedded Vapok copy");
            var nativeInventory = (Func<ItemDrop.ItemData, Inventory>)Delegate.CreateDelegate(typeof(Func<ItemDrop.ItemData, Inventory>), api.GetMethod("GetBackpackInventory", new[] { typeof(ItemDrop.ItemData) }));
            ItemDrop.ItemData bag = null;
            foreach (GameObject prefab in ObjectDB.instance.m_items.Where(value => value != null && value.name.StartsWith("Backpack", StringComparison.Ordinal) && value.GetComponent<ItemDrop>() != null))
            {
                var initializing = new Inventory("Combat backpack initialization", null, 8, 4);
                if (!initializing.AddItem(Item(prefab.name, 1))) continue;
                initializing.Load(new ZPackage(Save(initializing)));
                ItemDrop.ItemData candidate = initializing.GetAllItems().Single();
                Inventory capacity = nativeInventory(candidate);
                if (capacity != null && capacity.GetWidth() * capacity.GetHeight() >= 4) { bag = candidate; break; }
            }
            check(bag != null, "replacement fixture uses an initialized native backpack with sufficient capacity");
            var root = new Inventory("Combat backpack root", null, 8, 4);
            check(root.AddItem(bag), "replacement fixture inserts the native backpack into its root inventory");
            const long world = 6789; string token = new string('3', 32);
            check(CustodyInventory.ExpireBackpackGear(root, world, token, 2) == 0, "initial empty native backpack warms the production adapter without removal");

            object adapter = typeof(CustodyInventory).GetMethod("BackpackApi", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Func<ItemDrop.ItemData, Inventory> typedInventory = (Func<ItemDrop.ItemData, Inventory>)adapter.GetType().GetField("Inventory", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(adapter);
            Func<ItemDrop.ItemData, object> typedComponent = (Func<ItemDrop.ItemData, object>)adapter.GetType().GetField("Component", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(adapter);
            Action<object> typedSerialize = (Action<object>)adapter.GetType().GetField("Serialize", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(adapter);
            Inventory first = nativeInventory(bag);
            check(System.Object.ReferenceEquals(first, typedInventory(bag)), "cached typed getter initially returns the native API's current storage reference");
            object component = typedComponent(bag);
            check(component.GetType() == componentType, "cached open-instance component getter binds the exact installed native component");
            var replacement = new Inventory("Combat replaced backpack", null, first.GetWidth(), first.GetHeight());
            ItemDrop.ItemData farm = Item("Wood", 13), personal = Item("SwordBronze", 1);
            personal.m_customData["combat_bag_personal"] = "preserve";
            foreach (ItemDrop.ItemData item in new[] { farm, personal, Gear("HelmetLeather", world, token, 3), Gear("AxeBronze", world, token, 1) })
                check(replacement.AddItem(item), "replacement inventory admits each ordinary and issued item");
            replacement.Load(new ZPackage(Save(replacement)));
            componentType.GetMethod("SetInventory", new[] { typeof(Inventory) }).Invoke(component, new object[] { replacement });
            typedSerialize(component);
            check(!System.Object.ReferenceEquals(first, nativeInventory(bag)) && System.Object.ReferenceEquals(replacement, typedInventory(bag)),
                "a warm adapter observes SetInventory replacement without retaining the old inventory reference");
            check(CustodyInventory.HasObsoleteBackpackGear(root, world, token, 2) && CustodyInventory.ExpireBackpackGear(root, world, token, 2) == 1,
                "fresh traversal removes obsolete gear from replaced native storage");
            check(typedInventory(bag).GetAllItems().Count == 3 && typedInventory(bag).GetAllItems().Any(item => item.m_customData.ContainsKey(ArenaBuilder.GearKey)),
                "newer issued equipment arriving before its state revision survives backpack replacement");
            check(replacement.AddItem(Gear("MaceBronze", world, token, 1)), "the existing backpack inventory accepts newly inserted obsolete gear");
            typedSerialize(typedComponent(bag));
            check(CustodyInventory.ExpireBackpackGear(root, world, token, 2) == 1 && !CustodyInventory.HasObsoleteBackpackGear(root, world, token, 2),
                "in-place bag content mutation is seen on the next full traversal without a root inventory change event");

            byte[] snapshot = Save(root);
            root.Load(new ZPackage(snapshot)); ItemDrop.ItemData loadedBag = root.GetAllItems().Single();
            Inventory loaded = typedInventory(loadedBag);
            check(!System.Object.ReferenceEquals(bag, loadedBag) && !System.Object.ReferenceEquals(replacement, loaded) && loaded.GetAllItems().Count == 3,
                "the cached adapter resolves fresh item and storage references after native load");
            check(loaded.AddItem(Gear("SwordBronze", world, token, 1)), "loaded backpack storage accepts stale issued gear");
            typedSerialize(typedComponent(loadedBag));
            check(CustodyInventory.ExpireBackpackGear(root, world, token, 2) == 1, "stale gear inserted after Deserialize is removed with the existing bound adapter");
            check(CustodyInventory.ExpireBackpackGear(root, world, "", 0) == 1, "release immediately removes remaining issued armor after bag replacement and reload");
            var persisted = new Inventory("Combat replaced backpack persisted", null, 8, 4); persisted.Load(new ZPackage(Save(root)));
            Inventory retained = typedInventory(persisted.GetAllItems().Single());
            check(retained.GetAllItems().Count == 2 && retained.GetAllItems().Any(item => item.m_dropPrefab.name == "Wood" && item.m_stack == 13)
                && retained.GetAllItems().Any(item => item.m_customData.ContainsKey("combat_bag_personal")),
                "native serialization after replacement and release preserves farmed resources and personal weapons");
            check(CustodyInventory.ExpireBackpackGear(persisted, world, "", 0) == 0, "warm adapter release cleanup remains idempotent after replacement persistence");
        }
    }
}
