// Detached menu fixtures only; excluded from the released gameplay assembly.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
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
                        if (item.m_shared.m_maxStackSize == 1 || ArenaBuilder.IsIssuedAmmo(item)) {
                            item.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(6789, new string('1', 32), 1);
                            check(ArenaBuilder.IsGeneratedGear(item), "loadout item is valid isolated removable equipment or ammunition: " + name);
                            check(!item.m_customData.ContainsKey(ArenaBuilder.LoanKey), "ordinary native chest equipment avoids legacy pickup guards: " + name);
                        }
                    }
                    ItemDrop.ItemData nativeArrow = Item(loadout.ArrowSource, 1), arrow = Item(loadout.ArrowPrefab, 1);
                    check(ArenaBuilder.IsIssuedAmmo(arrow) && !ArenaBuilder.IsIssuedAmmo(nativeArrow)
                        && arrow.m_shared.m_name != nativeArrow.m_shared.m_name && arrow.m_shared.m_ammoType == nativeArrow.m_shared.m_ammoType
                        && arrow.m_shared.m_damages.Equals(nativeArrow.m_shared.m_damages)
                        && arrow.m_shared.m_icons[0] == nativeArrow.m_shared.m_icons[0]
                        && arrow.m_shared.m_maxStackSize == nativeArrow.m_shared.m_maxStackSize && !arrow.m_shared.m_autoStack
                        && !System.Object.ReferenceEquals(arrow.m_shared, nativeArrow.m_shared),
                        "biome arrows preserve native combat and icons but isolate their name and ground stacking: " + loadout.ArrowSource);
                    for (int i = 0; i < loadout.FoodPrefabs.Length; ++i) {
                        ItemDrop.ItemData source = Item(loadout.FoodSources[i], 1), food = Item(loadout.FoodPrefabs[i], 1);
                        check(food.m_shared.m_food > 0 && food.m_shared.m_foodStamina > 0
                            && (food.m_shared.m_food > food.m_shared.m_foodStamina) == (i < 2),
                            "each enemy and grade offers two native health foods and two stamina foods: " + loadout.MobPrefab + "/" + difficulty);
                        check(food.m_shared.m_food == source.m_shared.m_food && food.m_shared.m_foodStamina == source.m_shared.m_foodStamina
                            && food.m_shared.m_foodBurnTime == source.m_shared.m_foodBurnTime && food.m_shared.m_foodRegen == source.m_shared.m_foodRegen
                            && food.m_shared.m_foodEitr == source.m_shared.m_foodEitr && food.m_shared.m_name == source.m_shared.m_name
                            && food.m_shared.m_icons.Length == source.m_shared.m_icons.Length && food.m_shared.m_icons[0] == source.m_shared.m_icons[0],
                            "arena food keeps native benefits, duration, food identity and inventory icon: " + loadout.FoodPrefabs[i]);
                        check(food.m_shared.m_maxStackSize == 1 && source.m_shared.m_maxStackSize > 1 && !food.m_shared.m_autoStack
                            && !System.Object.ReferenceEquals(food.m_shared, source.m_shared), "arena ration cannot merge into personal food or alter its native definition");
                    }
                }
            CheckFoodLoans(check);
            CheckFoodDragging(check);
            CheckIssuedAmmo(check);
            CheckIssuedEmetics(check);
            CheckConsumedFoodLoans(check);
            CheckConsole(check);
            CheckCampfire(check);
            CheckCampfireAutomaticFuel(check);

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

        private static void CheckConsole(Action<bool, string> check)
        {
            GameObject console = Prefab(PrisonConsole.PrefabName), hammer = Prefab("Hammer");
            check(console != null && console.GetComponent<ZNetView>() != null && console.GetComponent<PrisonConsole>() != null
                && console.GetComponentInChildren<Collider>() != null, "unique floor console has native network identity, collision and interaction");
            check(console.GetComponentsInChildren<CraftingStation>(true).Length == 0 && console.GetComponentsInChildren<StationExtension>(true).Length == 0,
                "arena console does not open workbench crafting or count as a crafting station");
            check(console.GetComponent<Piece>() != null && !console.GetComponent<Piece>().m_canBeRemoved
                && console.GetComponent<Piece>().m_resources.Length == 0, "console is a protected fixture without deconstruction rewards");
            check(hammer != null && hammer.GetComponent<ItemDrop>() != null
                && !hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces.m_pieces.Contains(console), "console cannot be placed through the hammer menu");
            check(!console.GetComponent<PrisonConsole>().ValidNetworkObject
                && !console.GetComponent<PrisonConsole>().Interact(null, false, false), "inactive prefab cannot open prisoner UI or impersonate a world fixture");
        }

        private static void CheckFoodLoans(Action<bool, string> check)
        {
            const long world = 6789; string token = new string('4', 32), oldToken = new string('5', 32);
            var inventory = new Inventory("PartyPrison.FoodNative", null, 8, 4);
            ItemDrop.ItemData personal = Item("Sausages", 7), oldFood = Gear(CombatCatalog.FoodPrefab("Sausages"), world, token, 1);
            ItemDrop.ItemData active = Gear(CombatCatalog.FoodPrefab("Sausages"), world, token, 2);
            ItemDrop.ItemData future = Gear(CombatCatalog.FoodPrefab("Sausages"), world, token, 3);
            ItemDrop.ItemData previousSentence = Gear(CombatCatalog.FoodPrefab("TurnipStew"), world, oldToken, 1);
            ItemDrop.ItemData otherWorld = Gear(CombatCatalog.FoodPrefab("TurnipStew"), world + 1, token, 1);
            ItemDrop.ItemData noTag = Item(CombatCatalog.FoodPrefab("ShocklateSmoothie"), 1);
            foreach (ItemDrop.ItemData item in new[] { personal, oldFood, active, future, previousSentence, otherWorld, noTag })
                check(inventory.AddItem(item), "native food fixture inserts each separate serving and personal stack");
            check(inventory.GetAllItems().Count == 7 && personal.m_stack == 7 && oldFood.m_stack == 1,
                "same-named native sausages and arena sausages remain separate when loan food is inserted");
            check(inventory.AddItem(Item("Sausages", 2)) && personal.m_stack == 9 && oldFood.m_stack == 1,
                "personal food added later joins its native stack and cannot contaminate a tagged ration");
            check(ArenaBuilder.IsGeneratedGear(oldFood) && !ArenaBuilder.IsGeneratedGear(personal) && !ArenaBuilder.IsGeneratedGear(noTag),
                "only recognized food prefabs with valid loan provenance participate in expiration");
            check(ArenaBuilder.RemoveObsoleteInventoryGear(inventory, world, token, 2) == 2
                && inventory.GetAllItems().Contains(active) && inventory.GetAllItems().Contains(future), "food from old equipment revisions and sentences expires while current and future servings survive");
            var saved = new ZPackage(); inventory.Save(saved);
            var restored = new Inventory("PartyPrison.FoodRestored", null, 8, 4); restored.Load(new ZPackage(saved.GetArray()));
            check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 2 && restored.GetAllItems().Count == 3,
                "native persisted food provenance removes remaining current and future loan servings on release");
            check(restored.GetAllItems().Any(item => item.m_dropPrefab.name == "Sausages" && item.m_stack == 9)
                && restored.GetAllItems().Any(item => item.m_customData.ContainsKey(ArenaBuilder.GearKey))
                && restored.GetAllItems().Any(item => item.m_dropPrefab.name == CombatCatalog.FoodPrefab("ShocklateSmoothie")),
                "release preserves personal food, other-world rations and untagged data");
            check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 0, "loan food cleanup is idempotent after save and release");
        }

        private static void CheckIssuedAmmo(Action<bool, string> check)
        {
            const long world = 6789; string token = new string('7', 32);
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.ammoquickstack");
            MethodInfo nativeStack = AccessTools.Method(typeof(Inventory), "StackAll", new[] { typeof(Inventory), typeof(bool) });
            check(nativeStack != null, "ammo quick-stack fixture resolves the actual native Inventory.StackAll ABI");
            try {
                // Keep the native transfer body and every production/vendor
                // inventory patch. Replace only its live equipment/stat edges
                // for the explicitly marked detached fixture inventories.
                fixture.Patch(nativeStack, transpiler: new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("ScopeQuickStackContext", FireFixtureFlags)));
            foreach (string name in CombatCatalog.AllArrowSources()) {
                string loanName = CombatCatalog.ArrowPrefab(name);
                var inventory = new Inventory("PartyPrison.AmmoNative", null, 8, 4);
                ItemDrop.ItemData personal = Item(name, 58), old = Gear(loanName, world, token, 1), current = Gear(loanName, world, token, 2);
                old.m_stack = 25; current.m_stack = 10;
                check(inventory.AddItem(personal) && inventory.AddItem(old) && inventory.AddItem(current)
                    && inventory.GetAllItems().Count == 3 && old.m_stack == 25 && current.m_stack == 10 && personal.m_stack == 58,
                    "native automatic insertion separates personal arrows and old/current issued revisions: " + name);
                ItemDrop.ItemData sameRevision = Gear(loanName, world, token, 2); sameRevision.m_stack = 12;
                check(inventory.AddItem(sameRevision) && current.m_stack == 22 && old.m_stack == 25 && inventory.GetAllItems().Count == 3,
                    "same-provenance issued arrows retain ordinary native stacking: " + name);
                var source = new Inventory("PartyPrison.AmmoSource", null, 8, 4);
                ItemDrop.ItemData incoming = Gear(loanName, world, token, 3); incoming.m_stack = 7;
                check(source.AddItem(incoming), "ammo drag fixture inserts future revision");
                byte[] before = Save(inventory), sourceBefore = Save(source);
                check(!inventory.MoveItemToThis(source, incoming, 3, current.m_gridPos.x, current.m_gridPos.y)
                    && Save(inventory).SequenceEqual(before) && Save(source).SequenceEqual(sourceBefore),
                    "manual drag cannot merge different-issued revisions or lose provenance: " + name);
                check(inventory.MoveItemToThis(inventory, current, 3, 3, 0), "issued arrow stack can split into an empty native slot");
                ItemDrop.ItemData split = inventory.GetItemAt(3, 0);
                check(split != null && split.m_stack == 3 && current.m_stack == 19
                    && split.m_customData[ArenaBuilder.GearKey] == current.m_customData[ArenaBuilder.GearKey]
                    && split.m_dropPrefab.name == loanName, "native splitting retains exact quantity and issued-arrow metadata: " + name);
                check(ArenaBuilder.RemoveObsoleteInventoryGear(inventory, world, token, 2) == 1
                    && current.m_stack == 19 && split.m_stack == 3 && personal.m_stack == 58,
                    "loadout change removes only old issued arrow stacks, keeping split current ammo and personal arrows: " + name);
                var saved = new ZPackage(); inventory.Save(saved);
                var restored = new Inventory("PartyPrison.AmmoRestored", null, 8, 4); restored.Load(new ZPackage(saved.GetArray()));
                check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 2
                    && restored.GetAllItems().Count == 1 && restored.GetAllItems()[0].m_dropPrefab.name == name && restored.GetAllItems()[0].m_stack == 58,
                    "native save/reload preserves loan expiry on all split stacks and personal arrows exactly: " + name);
                check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 0, "issued arrow release cleanup is idempotent");

                var target = new Inventory("PartyPrison.AmmoQuickStackTarget", null, 8, 4);
                var from = new Inventory("PartyPrison.AmmoQuickStackSource", null, 8, 4);
                ItemDrop.ItemData targetOld = Gear(loanName, world, token, 1), fromCurrent = Gear(loanName, world, token, 2);
                targetOld.m_stack = 20; fromCurrent.m_stack = 8;
                ItemDrop.ItemData targetPersonal = Item(name, 5), fromPersonal = Item(name, 9);
                check(target.AddItem(targetOld) && target.AddItem(targetPersonal) && from.AddItem(fromCurrent) && from.AddItem(fromPersonal), "quick-stack fixture inserts loan and personal ammo");
                quickStackInventories.Add(target); int equipmentCalls = quickStackEquipmentCalls, statCalls = quickStackStatCalls;
                try { target.StackAll(from, false); } finally { quickStackInventories.Remove(target); }
                check(quickStackEquipmentCalls == equipmentCalls + 2 && quickStackStatCalls == statCalls + 1
                    && Player.m_localPlayer == null && Game.instance == null,
                    "native quick-stack transfer runs with only scoped detached equipment/stat context and no player or world registration");
                check(targetOld.m_stack == 20 && targetPersonal.m_stack == 14 && target.GetAllItems().Any(item =>
                    item.m_dropPrefab.name == loanName && item.m_stack == 8 && item.m_customData[ArenaBuilder.GearKey] == fromCurrent.m_customData[ArenaBuilder.GearKey])
                    && from.GetAllItems().Count == 0, "native StackAll transfers loan revisions separately while quick-stacking ordinary arrows: " + name);
                check(ArenaBuilder.CanStackIssuedAmmo(null, fromCurrent) && ArenaBuilder.CanStackIssuedAmmo(targetOld, null)
                    && !ArenaBuilder.CanStackIssuedAmmo(targetOld, fromCurrent) && !ArenaBuilder.CanStackIssuedAmmo(targetPersonal, fromCurrent),
                    "capacity preview stays usable while actual incompatible ammo pairs cannot stack");
                ItemDrop.ItemData untagged = Item(loanName, 4);
                check(!ArenaBuilder.CanStackIssuedAmmo(fromCurrent, untagged) && !ArenaBuilder.IsGeneratedGear(untagged),
                    "untagged isolated ammo cannot contaminate valid provenance");
                var positioned = new Inventory("PartyPrison.AmmoPreferredPosition", null, 8, 4);
                ItemDrop.ItemData positionedOld = Gear(loanName, world, token, 1), positionedCurrent = Gear(loanName, world, token, 2);
                positionedOld.m_stack = 9; positionedCurrent.m_stack = 4;
                check(positioned.AddItem(positionedOld) && positioned.AddItem(positionedCurrent, new Vector2i(1, 0))
                    && positionedOld.m_stack == 9 && positionedCurrent.m_stack == 4 && positioned.GetAllItems().Count == 2
                    && positioned.GetItemAt(1, 0) == positionedCurrent,
                    "native preferred-position insertion establishes provenance context before its direct stack query: " + name);
                ItemDrop.ItemData positionedSame = Gear(loanName, world, token, 2); positionedSame.m_stack = 7;
                check(positioned.AddItem(positionedSame, new Vector2i(2, 0)) && positionedCurrent.m_stack == 11
                    && positionedOld.m_stack == 9 && positioned.GetAllItems().Count == 2,
                    "native preferred-position insertion may merge matching issued revisions: " + name);
                MethodInfo capacity = typeof(Inventory).GetMethod("FindFreeStackItem", FireFixtureFlags);
                byte[] positionedBefore = Save(positioned);
                check(System.Object.ReferenceEquals(capacity.Invoke(positioned, new object[] { positionedOld.m_shared.m_name, 1, 0f }), positionedOld)
                    && Save(positioned).SequenceEqual(positionedBefore), "direct null-context native capacity preview remains read-only and usable: " + name);
            }
            }
            finally { fixture.UnpatchSelf(); quickStackInventories.Clear(); quickStackEquipmentCalls = quickStackStatCalls = 0; }
        }

        private static readonly HashSet<Inventory> quickStackInventories = new HashSet<Inventory>();
        private static int quickStackEquipmentCalls, quickStackStatCalls;

        private static bool FixtureStackEquipped(Humanoid actor, ItemDrop.ItemData item, Inventory destination)
        {
            if (!quickStackInventories.Contains(destination)) return actor.IsItemEquiped(item);
            ++quickStackEquipmentCalls;
            return item.m_equipped;
        }
        private static void FixtureStackStat(Game game, PlayerStatType stat, float amount, bool includeClient, Inventory destination)
        {
            if (!quickStackInventories.Contains(destination)) { game.IncrementPlayerStat(stat, amount, includeClient); return; }
            ++quickStackStatCalls;
        }
        private static IEnumerable<CodeInstruction> ScopeQuickStackContext(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo equipped = AccessTools.Method(typeof(Humanoid), "IsItemEquiped", new[] { typeof(ItemDrop.ItemData) });
            MethodInfo stat = AccessTools.Method(typeof(Game), "IncrementPlayerStat", new[] { typeof(PlayerStatType), typeof(float), typeof(bool) });
            int equipmentCalls = 0, statCalls = 0;
            foreach (CodeInstruction instruction in instructions) {
                MethodInfo wrapper = null;
                if (instruction.Calls(equipped)) { wrapper = typeof(CombatNativeChecks).GetMethod("FixtureStackEquipped", FireFixtureFlags); ++equipmentCalls; }
                else if (instruction.Calls(stat)) { wrapper = typeof(CombatNativeChecks).GetMethod("FixtureStackStat", FireFixtureFlags); ++statCalls; }
                if (wrapper == null) { yield return instruction; continue; }
                var destination = new CodeInstruction(OpCodes.Ldarg_0);
                destination.labels.AddRange(instruction.labels); destination.blocks.AddRange(instruction.blocks);
                yield return destination;
                yield return new CodeInstruction(OpCodes.Call, wrapper);
            }
            if (equipmentCalls != 1 || statCalls != 1) throw new InvalidOperationException("Native quick-stack context ABI changed; fixture does not replace transfer behavior.");
        }

        private static Player.Food Food(ItemDrop.ItemData item, float time)
        {
            return new Player.Food { m_name = item.m_dropPrefab.name, m_item = item, m_time = time,
                m_health = item.m_shared.m_food, m_stamina = item.m_shared.m_foodStamina, m_eitr = item.m_shared.m_foodEitr };
        }

        private static void CheckIssuedEmetics(Action<bool, string> check)
        {
            const long world = 6789; string token = new string('9', 32);
            ItemDrop.ItemData definition = Item(CombatCatalog.EmeticPrefab, 1), native = Item(CombatCatalog.EmeticSource, 5);
            check(ArenaBuilder.IsIssuedEmetic(definition) && !ArenaBuilder.IsIssuedEmetic(native)
                && definition.m_shared.m_maxStackSize == 1 && !definition.m_shared.m_autoStack
                && definition.m_shared.m_name != native.m_shared.m_name
                && !System.Object.ReferenceEquals(definition.m_shared, native.m_shared)
                && definition.m_shared.m_consumeStatusEffect != null && definition.m_shared.m_consumeStatusEffect.GetType().Name == "SE_Puke"
                && definition.m_shared.m_consumeStatusEffect == native.m_shared.m_consumeStatusEffect
                && definition.m_shared.m_itemType == native.m_shared.m_itemType && definition.m_shared.m_icons[0] == native.m_shared.m_icons[0]
                && definition.m_shared.m_food == native.m_shared.m_food && definition.m_shared.m_foodStamina == native.m_shared.m_foodStamina,
                "isolated emetics preserve actual native pukeberry consumption/status/icon without mutating the ordinary template");
            var inventory = new Inventory("PartyPrison.EmeticNative", null, 8, 4);
            ItemDrop.ItemData old = Gear(CombatCatalog.EmeticPrefab, world, token, 1), current = Gear(CombatCatalog.EmeticPrefab, world, token, 2);
            ItemDrop.ItemData future = Gear(CombatCatalog.EmeticPrefab, world, token, 3), other = Gear(CombatCatalog.EmeticPrefab, world + 1, token, 1);
            foreach (ItemDrop.ItemData item in new[] { native, old, current, future, other }) check(inventory.AddItem(item), "emetic fixture inserts separate loan servings and personal stack");
            check(inventory.GetAllItems().Count == 5 && native.m_stack == 5 && old.m_stack == 1 && current.m_stack == 1,
                "issued single-serving emetics never mix with personal pukeberries or each other");
            check(ArenaBuilder.IsGeneratedGear(old) && !ArenaBuilder.IsGeneratedGear(native)
                && ArenaBuilder.RemoveObsoleteInventoryGear(inventory, world, token, 2) == 1 && inventory.GetAllItems().Contains(current)
                && inventory.GetAllItems().Contains(future) && inventory.GetAllItems().Contains(other),
                "opponent change expires only old tagged emetics while future/current and other-world serving provenance survives");
            var saved = new ZPackage(); inventory.Save(saved);
            var restored = new Inventory("PartyPrison.EmeticRestored", null, 8, 4); restored.Load(new ZPackage(saved.GetArray()));
            check(ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 2 && restored.GetAllItems().Count == 2
                && restored.GetAllItems().Any(item => item.m_dropPrefab.name == CombatCatalog.EmeticSource && item.m_stack == 5)
                && ArenaBuilder.RemoveObsoleteInventoryGear(restored, world, "", 0) == 0,
                "save/reload retains emetic expiry and keeps the exact personal stack after release");
        }

        private static void CheckConsumedFoodLoans(Action<bool, string> check)
        {
            const long world = 6789; string token = new string('8', 32);
            Player.Food personal = Food(Item("Sausages", 1), 123.25f);
            Player.Food old = Food(Gear(CombatCatalog.FoodPrefab("Sausages"), world, token, 1), 80f);
            Player.Food current = Food(Gear(CombatCatalog.FoodPrefab("TurnipStew"), world, token, 2), 90f);
            float health = personal.m_health, stamina = personal.m_stamina, eitr = personal.m_eitr;
            var foods = new List<Player.Food> { personal, old, current };
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, token, 2, false) == 1
                && foods.SequenceEqual(new[] { personal, current }), "opponent change removes only expired consumed ration buffs");
            Player.Food future = Food(Gear(CombatCatalog.FoodPrefab("Honey"), world, token, 3), 70f); foods.Add(future);
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, token, 2, true) == 0,
                "current and future tagged ration buffs tolerate chest/state delivery ordering");
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, "", 0, true) == 2 && foods.Single() == personal
                && personal.m_time == 123.25f && personal.m_health == health && personal.m_stamina == stamina && personal.m_eitr == eitr,
                "release removes issued food effects without ticking or altering personal food benefits");
            Player.Food restored = Food(Prefab(CombatCatalog.FoodPrefab("Honey")).GetComponent<ItemDrop>().m_itemData, 61f);
            foods.Add(restored);
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, token, 2, false) == 0 && restored.m_time == 61f,
                "a natively restored untagged ration remains active within the unchanged sentence");
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, token, 3, true) == 1 && foods.Single() == personal,
                "a loadout epoch expires restored prefab-only food identity after native food tags were lost");
            foods.Add(Food(Item(CombatCatalog.FoodPrefab("Honey"), 1), 60f));
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, "", 0, false) == 1 && foods.Single() == personal,
                "no active sentence always removes restored issued food effects");
            foods.Add(Food(Gear(CombatCatalog.FoodPrefab("Honey"), world + 1, token, 1), 60f));
            check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, world, "", 0, true) == 0,
                "known other-world consumed-food provenance is preserved");
            CheckFoodRefresh(check);
        }

        private static readonly HashSet<GameObject> foodFixtureObjects = new HashSet<GameObject>();
        private static bool SkipFoodPlayerLifecycle(Component __instance)
        { return __instance == null || !foodFixtureObjects.Contains(__instance.gameObject); }

        private static void CheckFoodRefresh(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "food refresh fixture uses no live character or world");
            MethodInfo eat = AccessTools.Method(typeof(Player), "EatFood");
            Patches patches = Harmony.GetPatchInfo(eat);
            check(patches != null && patches.Postfixes.Any(patch => patch.owner == Plugin.Id
                && patch.PatchMethod.DeclaringType.FullName == "ValheimModPack.PartyPrison.PrisonFoodRefreshPatch"),
                "issued-food identity postfix is installed on actual native EatFood");
            MethodInfo refresh = typeof(Plugin).Assembly.GetType("ValheimModPack.PartyPrison.PrisonFoodRefreshPatch", true).GetMethod("Postfix", FireFixtureFlags);
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.foodrefresh"); GameObject node = null;
            try {
                var seen = new HashSet<MethodInfo>();
                foreach (Type type in new[] { typeof(Player), typeof(Humanoid), typeof(Character) })
                    foreach (string name in new[] { "Awake", "OnDestroy" }) {
                        MethodInfo method = AccessTools.Method(type, name, Type.EmptyTypes);
                        if (method != null && seen.Add(method)) fixture.Patch(method,
                            prefix: new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("SkipFoodPlayerLifecycle", FireFixtureFlags)) { priority = Priority.First });
                    }
                node = new GameObject("PartyPrison.DetachedFoodRefresh"); node.SetActive(false); foodFixtureObjects.Add(node);
                Player player = node.AddComponent<Player>(); var foods = player.GetFoods(); foods.Clear();
                ItemDrop.ItemData ordinary = Item("Sausages", 1), ration = Gear(CombatCatalog.FoodPrefab("Sausages"), 6789, new string('8', 32), 2);
                Player.Food food = Food(ordinary, 79.5f); foods.Add(food);
                refresh.Invoke(null, new object[] { player, Gear(CombatCatalog.EmeticPrefab, 6789, new string('8', 32), 2), true });
                check(foods.Single() == food && food.m_item == ordinary && food.m_name == "Sausages" && food.m_time == 79.5f,
                    "non-food emetic consumption is never marked as a consumed-ration buff");
                refresh.Invoke(null, new object[] { player, ration, true });
                check(foods.Single() == food && food.m_item == ration && food.m_name == ration.m_dropPrefab.name && food.m_time == 79.5f,
                    "successful ordinary-food refresh by ration adopts actual issued identity without duplicating or ticking food");
                refresh.Invoke(null, new object[] { player, ordinary, false });
                check(food.m_item == ration && food.m_name == ration.m_dropPrefab.name && food.m_time == 79.5f,
                    "failed EatFood cannot replace or erase consumed-ration provenance");
                refresh.Invoke(null, new object[] { player, ordinary, true });
                check(foods.Single() == food && food.m_item == ordinary && food.m_name == "Sausages" && food.m_time == 79.5f
                    && ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, 6789, "", 0, true) == 0,
                    "successful personal-food refresh adopts actual ordinary identity and survives release");
                refresh.Invoke(null, new object[] { player, ration, true });
                check(ArenaBuilder.RemoveObsoleteFoodEffectsFrom(foods, 6789, "", 0, true) == 1 && foods.Count == 0,
                    "ordinary-to-issued refreshed food expires after release");
            }
            finally {
                if (node != null) { UnityEngine.Object.DestroyImmediate(node); foodFixtureObjects.Remove(node); }
                fixture.UnpatchSelf(); foodFixtureObjects.Clear();
            }
            check(Player.m_localPlayer == null && Game.instance == null, "food identity fixture leaves native player/world registrations unchanged");
        }

        private static readonly HashSet<Inventory> foodDragInventories = new HashSet<Inventory>();
        private static int foodDragBodyCalls;
        private static void CountFoodDragBody(Inventory inventory)
        { if (foodDragInventories.Contains(inventory)) ++foodDragBodyCalls; }
        private static IEnumerable<CodeInstruction> ProbeFoodDragBody(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, typeof(CombatNativeChecks).GetMethod("CountFoodDragBody", FireFixtureFlags));
            foreach (CodeInstruction instruction in instructions) yield return instruction;
        }

        private static void RejectFoodDrag(Action<bool, string> check, Inventory destination, Inventory source,
            ItemDrop.ItemData item, int amount, Vector2i position, string label)
        {
            byte[] sourceBefore = Save(source), destinationBefore = Save(destination);
            ItemDrop.ItemData[] sourceItems = source.GetAllItems().ToArray(), destinationItems = destination.GetAllItems().ToArray();
            int beforeCalls = foodDragBodyCalls;
            check(!destination.MoveItemToThis(source, item, amount, position.x, position.y), "native manual drag rejects mixed food prefabs: " + label);
            check(foodDragBodyCalls == beforeCalls, "mixed food drag is blocked before native target-capacity or quantity mutation: " + label);
            check(Save(source).SequenceEqual(sourceBefore) && Save(destination).SequenceEqual(destinationBefore)
                && source.GetAllItems().SequenceEqual(sourceItems) && destination.GetAllItems().SequenceEqual(destinationItems),
                "rejected food drag preserves both inventories, item identities, amounts, coordinates and provenance: " + label);
        }

        private static void CheckFoodDragging(Action<bool, string> check)
        {
            MethodInfo slotAdd = AccessTools.Method(typeof(Inventory), "AddItem",
                new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            check(slotAdd != null && slotAdd.ReturnType == typeof(bool), "food drag fixture uses the actual native slot-add ABI");
            Patches patches = Harmony.GetPatchInfo(slotAdd);
            check(patches != null && patches.Prefixes.Any(patch => patch.owner == Plugin.Id
                && patch.PatchMethod.DeclaringType.FullName == "ValheimModPack.PartyPrison.SlotStackIsolationPatch"),
                "food drag guard is installed on native slot-add, including InventoryGui drag transfers");
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.fooddrag");
            try {
                fixture.Patch(slotAdd, transpiler: new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("ProbeFoodDragBody", FireFixtureFlags)));
                const long world = 6789; string token = new string('6', 32);
                foreach (string sourceName in CombatCatalog.AllFoodSources()) {
                    var personalInventory = new Inventory("PartyPrison.PersonalFoodDrag", null, 8, 4);
                    var rationInventory = new Inventory("PartyPrison.RationFoodDrag", null, 8, 4);
                    foodDragInventories.Add(personalInventory); foodDragInventories.Add(rationInventory);
                    ItemDrop.ItemData personal = Item(sourceName, 7), ration = Gear(CombatCatalog.FoodPrefab(sourceName), world, token, 2);
                    personal.m_customData["food_drag_personal"] = sourceName;
                    check(personalInventory.AddItem(personal) && rationInventory.AddItem(ration), "drag fixture inserts personal food and separately tagged ration: " + sourceName);
                    RejectFoodDrag(check, personalInventory, rationInventory, ration, 1, personal.m_gridPos, sourceName + " ration onto personal stack");
                    RejectFoodDrag(check, rationInventory, personalInventory, personal, 2, ration.m_gridPos, sourceName + " personal split onto ration");
                    ItemDrop.ItemData secondRation = Gear(CombatCatalog.FoodPrefab(sourceName), world, token, 2);
                    check(personalInventory.AddItem(secondRation), "same-prefab ration fixture inserts a separate valid serving");
                    byte[] fullTarget = Save(personalInventory), unchangedSource = Save(rationInventory);
                    int beforeCalls = foodDragBodyCalls;
                    check(!personalInventory.MoveItemToThis(rationInventory, ration, 1, secondRation.m_gridPos.x, secondRation.m_gridPos.y)
                        && foodDragBodyCalls == beforeCalls + 1 && Save(personalInventory).SequenceEqual(fullTarget)
                        && Save(rationInventory).SequenceEqual(unchangedSource),
                        "same-prefab serving reaches the native capacity rule without a custom refusal or provenance loss: " + sourceName);
                    check(personalInventory.RemoveItem(secondRation), "same-prefab capacity fixture removes only its own extra serving");
                    byte[] unchangedPersonal = Save(personalInventory);
                    beforeCalls = foodDragBodyCalls;
                    check(personalInventory.MoveItemToThis(rationInventory, ration, 1, 1, 0) && foodDragBodyCalls == beforeCalls + 1,
                        "a ration may be dragged into an empty native inventory slot: " + sourceName);
                    ItemDrop.ItemData moved = personalInventory.GetItemAt(1, 0);
                    check(rationInventory.GetAllItems().Count == 0 && personalInventory.GetAllItems().Count == 2 && personal.m_stack == 7
                        && System.Object.ReferenceEquals(personalInventory.GetItemAt(personal.m_gridPos.x, personal.m_gridPos.y), personal)
                        && moved != null && moved.m_stack == 1 && moved.m_dropPrefab == ration.m_dropPrefab
                        && moved.m_customData[ArenaBuilder.GearKey] == ration.m_customData[ArenaBuilder.GearKey]
                        && moved.m_customData[ArenaBuilder.KitStockKey] == token,
                        "accepted empty-slot transfer retains ration metadata and the original personal item: " + sourceName);
                    beforeCalls = foodDragBodyCalls;
                    check(personalInventory.MoveItemToThis(personalInventory, moved, 1, 2, 0) && foodDragBodyCalls == beforeCalls + 1,
                        "a ration can be rearranged within the same inventory: " + sourceName);
                    ItemDrop.ItemData rearranged = personalInventory.GetItemAt(2, 0);
                    check(personalInventory.GetItemAt(1, 0) == null && personalInventory.GetAllItems().Count == 2
                        && rearranged != null && rearranged.m_stack == 1 && rearranged.m_customData[ArenaBuilder.GearKey] == ration.m_customData[ArenaBuilder.GearKey],
                        "same-inventory drag keeps exactly one tagged serving: " + sourceName);
                    check(ArenaBuilder.RemoveObsoleteInventoryGear(personalInventory, world, "", 0) == 1
                        && Save(personalInventory).SequenceEqual(unchangedPersonal) && personalInventory.GetAllItems().Single() == personal,
                        "dragged ration still expires and leaves the full original personal inventory unchanged: " + sourceName);
                    foodDragInventories.Remove(personalInventory); foodDragInventories.Remove(rationInventory);
                }
                foreach (string name in new[] { "Sausages", "ArrowWood" }) {
                    var source = new Inventory("PartyPrison.OrdinaryDragSource", null, 8, 4);
                    var destination = new Inventory("PartyPrison.OrdinaryDragDestination", null, 8, 4);
                    foodDragInventories.Add(source); foodDragInventories.Add(destination);
                    ItemDrop.ItemData incoming = Item(name, 7), target = Item(name, 3);
                    incoming.m_customData["ordinary_drag_source"] = "retain"; target.m_customData["ordinary_drag_target"] = "retain";
                    check(source.AddItem(incoming) && destination.AddItem(target), "ordinary food/arrow drag fixtures insert native stacks");
                    int beforeCalls = foodDragBodyCalls;
                    check(destination.MoveItemToThis(source, incoming, 2, target.m_gridPos.x, target.m_gridPos.y)
                        && foodDragBodyCalls == beforeCalls + 1 && incoming.m_stack == 5 && target.m_stack == 5
                        && source.GetAllItems().Single() == incoming && destination.GetAllItems().Single() == target
                        && incoming.m_customData.ContainsKey("ordinary_drag_source") && target.m_customData.ContainsKey("ordinary_drag_target"),
                        "ordinary native split-and-stack preserves counts and both item identities: " + name);
                    beforeCalls = foodDragBodyCalls;
                    check(destination.MoveItemToThis(source, incoming, 2, 1, 0) && foodDragBodyCalls == beforeCalls + 1
                        && incoming.m_stack == 3 && target.m_stack == 5 && source.GetAllItems().Single() == incoming
                        && destination.GetItemAt(1, 0).m_stack == 2 && destination.GetItemAt(1, 0).m_customData.ContainsKey("ordinary_drag_source"),
                        "ordinary native stack splitting into an empty slot is unchanged: " + name);
                    foodDragInventories.Remove(source); foodDragInventories.Remove(destination);
                }
            }
            finally { foodDragInventories.Clear(); foodDragBodyCalls = 0; fixture.UnpatchSelf(); }
        }

        private static void CheckCampfire(Action<bool, string> check)
        {
            GameObject source = Prefab("fire_pit"), prefab = Prefab(PrisonContent.PrisonCampfirePrefab), hammer = Prefab("Hammer");
            check(source != null && source.GetComponent<Fireplace>() != null && prefab != null
                && prefab.GetComponent<Fireplace>() != null && prefab.GetComponent<ZNetView>() != null,
                "prison campfire uses a separate native-networked Fireplace prefab");
            Fireplace fire = prefab.GetComponent<Fireplace>(), original = source.GetComponent<Fireplace>();
            check(fire.m_infiniteFuel && fire.m_secPerFuel == 0f && fire.m_startFuel == 0f && fire.m_maxFuel == 0f
                && !fire.m_canRefill && !fire.m_canTurnOff && fire.m_disableCoverCheck && fire.m_igniteInterval == 0f,
                "enclosed-cell fire remains lit without fuel consumption, periodic fuel-time writes or ignition spread");
            check(fire.m_smokeSpawner == null && prefab.GetComponentsInChildren<SmokeSpawner>(true).Length == 0,
                "cell campfire cannot emit native suffocating smoke volumes under its stone roof");
            check(prefab.GetComponent<Piece>() != null && !prefab.GetComponent<Piece>().m_canBeRemoved
                && prefab.GetComponent<Piece>().m_resources.Length == 0
                && !hammer.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces.m_pieces.Contains(prefab),
                "cell fire is a protected fixture without hammer placement or dismantling rewards");
            EffectArea[] nativeAreas = source.GetComponentsInChildren<EffectArea>(true), cloneAreas = prefab.GetComponentsInChildren<EffectArea>(true);
            check(nativeAreas.Length == cloneAreas.Length && nativeAreas.Length > 0,
                "smokeless campfire retains its native heat/rest and other effect areas");
            bool heat = false;
            for (int i = 0; i < nativeAreas.Length; ++i) {
                check(nativeAreas[i].m_type == cloneAreas[i].m_type && nativeAreas[i].m_statusEffect == cloneAreas[i].m_statusEffect,
                    "native campfire effect-area semantics remain unchanged in the cell clone");
                if ((cloneAreas[i].m_type & EffectArea.Type.Heat) != 0) heat = true;
            }
            check(heat && fire.m_enabledObject != null && fire.m_enabledObject != original.m_enabledObject
                && prefab.GetComponentsInChildren<Light>(true).Length == source.GetComponentsInChildren<Light>(true).Length
                && prefab.GetComponentsInChildren<Component>(true).Count(value => value.GetType().FullName == "UnityEngine.ParticleSystem")
                    == source.GetComponentsInChildren<Component>(true).Count(value => value.GetType().FullName == "UnityEngine.ParticleSystem"),
                "cell fire keeps separate native flame visuals, lights, particles and heat");
            check(original.m_smokeSpawner != null && source.GetComponentsInChildren<SmokeSpawner>(true).Length > 0
                && !original.m_infiniteFuel && original.m_secPerFuel > 0f,
                "ordinary campfire smoke and fuel behavior is untouched by the prison clone");
        }

        private const BindingFlags FireFixtureFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static GameObject fireFixtureObject;
        private static ZDO fireFixtureZdo;
        private static int vendorBodyCalls;

        private static bool SkipFireFixtureAwake(Component __instance)
        { return __instance == null || __instance.gameObject != fireFixtureObject; }
        private static bool SkipFireFixtureRevision(ZDO __instance)
        { return !System.Object.ReferenceEquals(__instance, fireFixtureZdo); }
        private static void CountVendorBody() { ++vendorBodyCalls; }
        private static IEnumerable<CodeInstruction> VendorBodyProbe(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Call, typeof(CombatNativeChecks).GetMethod("CountVendorBody", FireFixtureFlags));
            foreach (CodeInstruction instruction in instructions) yield return instruction;
        }

        private static void CheckCampfireAutomaticFuel(Action<bool, string> check)
        {
            BepInEx.PluginInfo plugin;
            if (!Chainloader.PluginInfos.TryGetValue("TastyChickenLegs.AutomaticFuel", out plugin)) return;
            Type handler = plugin.Instance.GetType().Assembly.GetType("AutomaticFuel.GameClasses.Fireplace_Patches+Fireplace_UpdateFireplace_Patch", false);
            MethodInfo postfix = handler == null ? null : handler.GetMethod("Postfix", FireFixtureFlags, null, new[] { typeof(Fireplace), typeof(ZNetView) }, null);
            check(postfix != null && postfix.ReturnType == typeof(void), "installed AutomaticFuel fireplace handler matches the verified ABI");
            Patches patches = Harmony.GetPatchInfo(postfix);
            check(patches != null && patches.Prefixes.Any(patch => patch.owner == Plugin.Id
                && patch.PatchMethod.DeclaringType.FullName == "ValheimModPack.PartyPrison.PrisonCampfireAutomaticFuelPatch"),
                "PartyPrison registers its scoped guard on the actual AutomaticFuel vendor handler");
            var fixture = new Harmony("valheimmodpack.partyprison.nativeprobe.campfirefuel");
            try {
                check(Player.m_localPlayer == null && Game.instance == null, "automatic-fuel fixture runs without a user character or world");
                var awake = new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("SkipFireFixtureAwake", FireFixtureFlags)); awake.priority = Priority.First;
                fixture.Patch(typeof(ZNetView).GetMethod("Awake", FireFixtureFlags), prefix: awake);
                fixture.Patch(typeof(Fireplace).GetMethod("Awake", FireFixtureFlags), prefix: awake);
                fixture.Patch(typeof(ZDO).GetMethod("IncreaseDataRevision", FireFixtureFlags),
                    prefix: new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("SkipFireFixtureRevision", FireFixtureFlags)));
                fixture.Patch(postfix, transpiler: new HarmonyMethod(typeof(CombatNativeChecks).GetMethod("VendorBodyProbe", FireFixtureFlags)));
                fireFixtureObject = new GameObject("PartyPrison.NativeFixture.CellFire"); fireFixtureObject.SetActive(false);
                ZNetView view = fireFixtureObject.AddComponent<ZNetView>(); Fireplace fire = fireFixtureObject.AddComponent<Fireplace>();
                fire.m_infiniteFuel = true; fire.m_maxFuel = fire.m_startFuel = fire.m_secPerFuel = 0f;
                fireFixtureZdo = new ZDO { m_uid = new ZDOID(-643591874, 1) };
                typeof(ZDO).GetField("m_prefab", FireFixtureFlags).SetValue(fireFixtureZdo, PrisonContent.PrisonCampfirePrefab.GetStableHashCode());
                typeof(ZNetView).GetField("m_zdo", FireFixtureFlags).SetValue(view, fireFixtureZdo);
                fireFixtureZdo.Set(ArenaBuilder.ProtectedKey, true); fireFixtureZdo.Set(PrisonContent.PrisonCampfireMarker, true);
                vendorBodyCalls = 0; uint revision = fireFixtureZdo.DataRevision;
                postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 0 && fireFixtureZdo.DataRevision == revision,
                    "marked native cell fire bypasses the entire vendor body: no refueling coroutine, chest query or native fuel mutation");
                fireFixtureZdo.Set(ArenaBuilder.ProtectedKey, false); postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 1, "an unprotected fireplace keeps the ordinary vendor path");
                fireFixtureZdo.Set(ArenaBuilder.ProtectedKey, true); fireFixtureZdo.Set(PrisonContent.PrisonCampfireMarker, false);
                postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 2, "a fireplace missing the prison campfire marker keeps its ordinary vendor path");
                fireFixtureZdo.Set(PrisonContent.PrisonCampfireMarker, true);
                typeof(ZDO).GetField("m_prefab", FireFixtureFlags).SetValue(fireFixtureZdo, "fire_pit".GetStableHashCode());
                postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 3, "ordinary campfire prefab keeps its vendor path even with unrelated custom markers");
                typeof(ZDO).GetField("m_prefab", FireFixtureFlags).SetValue(fireFixtureZdo, PrisonContent.PrisonCampfirePrefab.GetStableHashCode());
                fire.m_infiniteFuel = false; postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 4, "a finite-fuel fireplace is never suppressed by the prison compatibility guard");
                fire.m_infiniteFuel = true; typeof(ZNetView).GetField("m_zdo", FireFixtureFlags).SetValue(view, null);
                postfix.Invoke(null, new object[] { fire, view });
                check(vendorBodyCalls == 5, "an invalid native view is never mistaken for a system cell campfire");
            }
            finally {
                if (fireFixtureObject != null) UnityEngine.Object.DestroyImmediate(fireFixtureObject);
                if (fireFixtureZdo != null) typeof(ZDO).GetMethod("Reset", FireFixtureFlags).Invoke(fireFixtureZdo, null);
                fireFixtureObject = null; fireFixtureZdo = null; fixture.UnpatchSelf();
            }
            check(Player.m_localPlayer == null && Game.instance == null, "fuel compatibility fixture leaves no native player or world registration");
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
