// Detached menu fixtures only; excluded from the released gameplay assembly.
using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class CombatNativeChecks
    {
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
        }
    }
}
