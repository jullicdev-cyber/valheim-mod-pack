using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Runtime copies of native art and food; no hammer pieces or crafting recipes.</summary>
    public static class PrisonContent
    {
        public const string PrisonCampfirePrefab = "vmp_prison_campfire", PrisonCampfireMarker = "VMP_PP_Campfire";
        private static bool consoleRegistered, campfireRegistered, emeticRegistered, localized;
        private static readonly HashSet<string> foodsRegistered = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> arrowsRegistered = new HashSet<string>(StringComparer.Ordinal);

        public static void Register()
        {
            if (!localized) {
                LocalizationManager.Instance.GetLocalization().AddJsonFile("English",
                    "{\"vmp_pp_food_description\":\"Arena ration. Servings and their food effects expire when opponents change or imprisonment ends.\",\"vmp_pp_arrow_suffix\":\"(arena)\",\"vmp_pp_arrow_description\":\"Issued arena arrows. Remaining arrows expire when opponents change or imprisonment ends.\",\"vmp_pp_emetic_description\":\"Arena pukeberry. Use to remove food effects before changing meals. Unused servings expire when opponents change or imprisonment ends.\",\"vmp_pp_campfire\":\"Cell campfire\"}");
                LocalizationManager.Instance.GetLocalization().AddJsonFile("Russian",
                    "{\"vmp_pp_food_description\":\"Паёк арены. Порции и их пищевые эффекты исчезают при смене противников или освобождении.\",\"vmp_pp_arrow_suffix\":\"(арена)\",\"vmp_pp_arrow_description\":\"Выданные стрелы арены. Остаток исчезает при смене противников или освобождении.\",\"vmp_pp_emetic_description\":\"Тошник арены. Сбрасывает пищевые эффекты перед сменой еды. Неиспользованные порции исчезают при смене противников или освобождении.\",\"vmp_pp_campfire\":\"Костёр камеры\"}");
                localized = true;
            }
            RegisterConsole();
            RegisterCampfire();
            foreach (string source in CombatCatalog.AllFoodSources()) RegisterFood(source);
            foreach (string source in CombatCatalog.AllArrowSources()) RegisterArrows(source);
            RegisterEmetic();
        }

        private static void RegisterCampfire()
        {
            if (campfireRegistered) return;
            GameObject source = PrefabManager.Instance.GetPrefab("fire_pit");
            if (!source || !source.GetComponent<Piece>() || !source.GetComponent<ZNetView>() || !source.GetComponent<Fireplace>())
                throw new InvalidOperationException("PartyPrison requires the native campfire prefab.");
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrisonCampfirePrefab, source);
            if (!prefab) throw new InvalidOperationException("PartyPrison campfire prefab name is already registered.");
            try {
                Fireplace fire = prefab.GetComponent<Fireplace>();
                // Native 1.0.17 ABI/IL: IsBurning honors infiniteFuel; a zero
                // secPerFuel skips GetTimeSinceLastUpdate and its ZDO write.
                // disableCoverCheck exits CheckUnderTerrain before smoke and
                // roof raycasts, appropriate for this permanent enclosed cell.
                fire.m_infiniteFuel = true; fire.m_secPerFuel = 0f; fire.m_startFuel = fire.m_maxFuel = 0f;
                fire.m_canRefill = false; fire.m_canTurnOff = false; fire.m_disableCoverCheck = true;
                fire.m_igniteInterval = 0f; fire.m_smokeSpawner = null; fire.m_name = "$vmp_pp_campfire";
                // SmokeSpawner creates native smoke particles/volumes which
                // choke players under the stone roof. Remove only this clone's
                // smoke behavior; keep the native flame, light, sound and heat.
                foreach (SmokeSpawner smoke in prefab.GetComponentsInChildren<SmokeSpawner>(true)) Object.DestroyImmediate(smoke);
                Piece piece = prefab.GetComponent<Piece>();
                piece.m_name = "$vmp_pp_campfire"; piece.m_craftingStation = null;
                piece.m_resources = new Piece.Requirement[0]; piece.m_canBeRemoved = false;
                WearNTear wear = prefab.GetComponent<WearNTear>();
                if (wear) { wear.m_noRoofWear = true; wear.m_noSupportWear = true; wear.m_supports = false; }
                PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
                if (PrefabManager.Instance.GetPrefab(PrisonCampfirePrefab) != prefab)
                    throw new InvalidOperationException("Jotunn rejected the prison campfire prefab.");
                campfireRegistered = true;
            }
            catch { Object.DestroyImmediate(prefab); throw; }
        }

        private static void RegisterConsole()
        {
            if (consoleRegistered) return;
            GameObject source = PrefabManager.Instance.GetPrefab("piece_workbench");
            if (!source || !source.GetComponent<Piece>() || !source.GetComponent<ZNetView>())
                throw new InvalidOperationException("PartyPrison requires the native workbench prefab.");
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrisonConsole.PrefabName, source);
            if (!prefab) throw new InvalidOperationException("PartyPrison console prefab name is already registered.");
            try {
                // Keep native furniture visuals/colliders but remove the native
                // interaction so this object cannot open crafting or repair.
                foreach (CraftingStation station in prefab.GetComponentsInChildren<CraftingStation>(true)) {
                    // CraftingStation.Start normally hides this preview; its
                    // OnDestroy does not. Removing the station before Start
                    // leaves CircleProjector drawing and raycasting each frame.
                    GameObject marker = station.m_areaMarker;
                    if (marker) {
                        if (marker == prefab || !marker.transform.IsChildOf(prefab.transform))
                            throw new InvalidOperationException("Prison console area marker is not part of its cloned prefab.");
                        marker.SetActive(false);
                        Object.DestroyImmediate(marker);
                    }
                    Object.DestroyImmediate(station);
                }
                foreach (StationExtension extension in prefab.GetComponentsInChildren<StationExtension>(true)) Object.DestroyImmediate(extension);
                Piece piece = prefab.GetComponent<Piece>();
                piece.m_name = "Arena controls"; piece.m_description = "Prison inmate activity controls";
                piece.m_craftingStation = null; piece.m_resources = new Piece.Requirement[0];
                piece.m_comfort = 0; piece.m_comfortObject = null; piece.m_canBeRemoved = false;
                WearNTear wear = prefab.GetComponent<WearNTear>();
                if (wear) { wear.m_noRoofWear = true; wear.m_noSupportWear = true; wear.m_supports = false; }
                prefab.AddComponent<PrisonConsole>();
                // A CustomPrefab alone never enters Hammer's piece table.
                PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
                if (PrefabManager.Instance.GetPrefab(PrisonConsole.PrefabName) != prefab)
                    throw new InvalidOperationException("Jotunn rejected the prison console prefab.");
                consoleRegistered = true;
            }
            catch { Object.DestroyImmediate(prefab); throw; }
        }

        private static void RegisterFood(string sourceName)
        {
            if (foodsRegistered.Contains(sourceName)) return;
            GameObject source = PrefabManager.Instance.GetPrefab(sourceName);
            ItemDrop original = source ? source.GetComponent<ItemDrop>() : null;
            if (!original || original.m_itemData == null || original.m_itemData.m_shared == null
                || original.m_itemData.m_shared.m_food <= 0f || original.m_itemData.m_shared.m_foodStamina <= 0f)
                throw new InvalidOperationException("PartyPrison food definition is unavailable: " + sourceName);
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(CombatCatalog.FoodPrefab(sourceName), source);
            if (!prefab) throw new InvalidOperationException("PartyPrison food prefab name is already registered: " + sourceName);
            try {
                ItemDrop drop = prefab.GetComponent<ItemDrop>();
                ItemDrop.ItemData item = drop.m_itemData;
                // Unity serialization must give this prefab its own shared
                // data. Never mutate the ordinary food's global definition.
                if (ReferenceEquals(item.m_shared, original.m_itemData.m_shared))
                    throw new InvalidOperationException("Native food clone shares mutable food data: " + sourceName);
                item.m_dropPrefab = prefab; item.m_stack = item.m_quality = 1;
                item.m_customData = new Dictionary<string, string>();
                item.m_shared.m_maxStackSize = item.m_shared.m_maxQuality = 1;
                // An ordinary ground-food receiver must not absorb a tagged
                // ration candidate and discard that candidate's provenance.
                item.m_shared.m_autoStack = false;
                item.m_shared.m_description = "$vmp_pp_food_description\n" + original.m_itemData.m_shared.m_description;
                // Native food names, icons, health/stamina, burn time and eating
                // rules remain unchanged. A separate prefab and max stack 1
                // isolate tagged servings from personally acquired equivalents.
                if (!ItemManager.Instance.AddItem(new CustomItem(prefab, false)))
                    throw new InvalidOperationException("Jotunn rejected the prison food prefab: " + sourceName);
                foodsRegistered.Add(sourceName);
            }
            catch { Object.DestroyImmediate(prefab); throw; }
        }

        private static void RegisterArrows(string sourceName)
        {
            if (arrowsRegistered.Contains(sourceName)) return;
            GameObject source = PrefabManager.Instance.GetPrefab(sourceName);
            ItemDrop original = source ? source.GetComponent<ItemDrop>() : null;
            if (!original || original.m_itemData == null || original.m_itemData.m_shared == null
                || original.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Ammo
                || original.m_itemData.m_shared.m_maxStackSize < 1)
                throw new InvalidOperationException("PartyPrison arrow definition is unavailable: " + sourceName);
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(CombatCatalog.ArrowPrefab(sourceName), source);
            if (!prefab) throw new InvalidOperationException("PartyPrison arrow prefab name is already registered: " + sourceName);
            try {
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData;
                if (ReferenceEquals(item.m_shared, original.m_itemData.m_shared))
                    throw new InvalidOperationException("Native arrow clone shares mutable item data: " + sourceName);
                item.m_dropPrefab = prefab; item.m_stack = item.m_quality = 1;
                item.m_customData = new Dictionary<string, string>();
                // Native inventory matching uses this name rather than prefab
                // identity. A unique key keeps ordinary arrow stacks separate.
                string nativeName = original.m_itemData.m_shared.m_name;
                item.m_shared.m_name = nativeName + " $vmp_pp_arrow_suffix";
                item.m_shared.m_maxQuality = 1; item.m_shared.m_autoStack = false;
                item.m_shared.m_description = "$vmp_pp_arrow_description\n" + original.m_itemData.m_shared.m_description;
                if (!ItemManager.Instance.AddItem(new CustomItem(prefab, false)))
                    throw new InvalidOperationException("Jotunn rejected the prison arrow prefab: " + sourceName);
                arrowsRegistered.Add(sourceName);
            }
            catch { Object.DestroyImmediate(prefab); throw; }
        }

        private static void RegisterEmetic()
        {
            if (emeticRegistered) return;
            GameObject source = PrefabManager.Instance.GetPrefab(CombatCatalog.EmeticSource);
            ItemDrop original = source ? source.GetComponent<ItemDrop>() : null;
            if (!original || original.m_itemData == null || original.m_itemData.m_shared == null
                || original.m_itemData.m_shared.m_consumeStatusEffect == null
                || original.m_itemData.m_shared.m_consumeStatusEffect.GetType().Name != "SE_Puke")
                throw new InvalidOperationException("PartyPrison requires native pukeberries with the vomiting status effect.");
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(CombatCatalog.EmeticPrefab, source);
            if (!prefab) throw new InvalidOperationException("PartyPrison pukeberry prefab name is already registered.");
            try {
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData;
                if (ReferenceEquals(item.m_shared, original.m_itemData.m_shared))
                    throw new InvalidOperationException("Native pukeberry clone shares mutable item data.");
                item.m_dropPrefab = prefab; item.m_stack = item.m_quality = 1;
                item.m_customData = new Dictionary<string, string>();
                item.m_shared.m_maxStackSize = item.m_shared.m_maxQuality = 1; item.m_shared.m_autoStack = false;
                item.m_shared.m_name = original.m_itemData.m_shared.m_name + " $vmp_pp_arrow_suffix";
                item.m_shared.m_description = "$vmp_pp_emetic_description\n" + original.m_itemData.m_shared.m_description;
                if (!ItemManager.Instance.AddItem(new CustomItem(prefab, false)))
                    throw new InvalidOperationException("Jotunn rejected the prison pukeberry prefab.");
                emeticRegistered = true;
            }
            catch { Object.DestroyImmediate(prefab); throw; }
        }
    }

    // AutomaticFuel 1.5.1 refills Fireplace instances without checking native
    // infiniteFuel/canRefill and queries nearby containers even at capacity.
    // Skip only its handler for the system-owned, marked cell campfire.
    [HarmonyPatch]
    internal static class PrisonCampfireAutomaticFuelPatch
    {
        private const string AutomaticFuelId = "TastyChickenLegs.AutomaticFuel";
        private static MethodInfo target;
        private static bool warned;

        private static bool Prepare()
        {
            PluginInfo plugin;
            if (!Chainloader.PluginInfos.TryGetValue(AutomaticFuelId, out plugin)) return false;
            Assembly assembly = plugin.Instance == null ? null : plugin.Instance.GetType().Assembly;
            Type type = assembly == null ? null : assembly.GetType("AutomaticFuel.GameClasses.Fireplace_Patches+Fireplace_UpdateFireplace_Patch", false);
            target = type == null ? null : type.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Fireplace), typeof(ZNetView) }, null);
            if (target != null && target.ReturnType == typeof(void)) return true;
            target = null;
            if (!warned) {
                warned = true;
                BepInEx.Logging.Logger.CreateLogSource("PartyPrison.Campfire").LogWarning(
                    "Installed AutomaticFuel handler ABI is unsupported. Cell campfire has zero fuel capacity to preserve resources, but automatic-fuel scans cannot be excluded. Update the compatible modpack.");
            }
            return false;
        }

        private static MethodBase TargetMethod() { return target; }

        private static bool Prefix(Fireplace __0, ZNetView __1)
        {
            if (__0 == null || !__0.m_infiniteFuel || __1 == null || !__1.IsValid()) return true;
            ZDO zdo = __1.GetZDO();
            return zdo == null || zdo.GetPrefab() != PrisonContent.PrisonCampfirePrefab.GetStableHashCode()
                || !zdo.GetBool(ArenaBuilder.ProtectedKey, false) || !zdo.GetBool(PrisonContent.PrisonCampfireMarker, false)
                || !ReferenceEquals(__0.GetComponent<ZNetView>(), __1);
        }
    }
}
