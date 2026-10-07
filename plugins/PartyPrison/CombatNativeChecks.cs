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
                        if (item.m_shared.m_maxStackSize == 1) {
                            item.m_customData[ArenaBuilder.GearKey] = PrisonGearPolicy.Tag(6789, new string('1', 32), 1);
                            check(ArenaBuilder.IsGeneratedGear(item), "loadout nonstackable item is valid removable armor or weapon: " + name);
                            check(!item.m_customData.ContainsKey(ArenaBuilder.LoanKey), "ordinary native chest equipment avoids legacy pickup guards: " + name);
                        }
                    }
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
                        check(food.m_shared.m_maxStackSize == 1 && source.m_shared.m_maxStackSize > 1
                            && !System.Object.ReferenceEquals(food.m_shared, source.m_shared), "arena ration cannot merge into personal food or alter its native definition");
                    }
                }
            CheckFoodLoans(check);
            CheckFoodDragging(check);
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
