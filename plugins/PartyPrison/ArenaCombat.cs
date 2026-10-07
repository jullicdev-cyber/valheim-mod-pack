using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    public static partial class ArenaBuilder
    {
        public const string GearKey = "VMP_PP_Gear", KitFamilyKey = "VMP_PP_KitFamily", KitDifficultyKey = "VMP_PP_KitDifficulty", KitRevisionKey = "VMP_PP_KitRevision";
        private static float combatPausedUntil;
        private static float nextStoredGearQuery;
        private static ZDOMan storedGearManager;
        private static PrisonRegion storedGearRegion;
        private static List<ZDO> storedGearChests;
        private static readonly MethodInfo NativeContainerSave = typeof(Container).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private sealed class SeenGear { internal float At; }
        private static ConditionalWeakTable<ItemDrop.ItemData, SeenGear> observedGear = new ConditionalWeakTable<ItemDrop.ItemData, SeenGear>();

        public static void GetCombatChoice(PrisonRegion region, out int family, out int difficulty, out string token, out int revision)
        {
            ZDO kit = GetKitZdo(region);
            family = kit == null ? 0 : kit.GetInt(KitFamilyKey, 0);
            difficulty = kit == null ? 0 : kit.GetInt(KitDifficultyKey, 0);
            token = kit == null ? "" : kit.GetString(KitTokenKey, "");
            revision = kit == null ? 0 : kit.GetInt(KitRevisionKey, 0);
            if (family < 0 || family >= CombatCatalog.FamilyCount) family = 0;
            if (difficulty < 0 || difficulty >= CombatCatalog.DifficultyCount) difficulty = 0;
        }

        /// <summary>Validate a complete replacement in a detached native inventory before publishing.</summary>
        public static int ConfigureSentenceKit(PrisonRegion region, string token, int family, int difficulty)
        {
            RequireHost(); PrisonCombatLoadout loadout = CombatCatalog.Get(family, difficulty);
            ZDO kit = GetKitZdo(region);
            if (kit == null) throw new InvalidOperationException("Сундук снаряжения ещё не загружен; подойдите к тюрьме.");
            Container chest = GetKitContainer(region);
            if (chest == null || chest.IsInUse()) throw new InvalidOperationException("Закройте сундук снаряжения перед сменой противников.");
            int previous = kit.GetInt(KitRevisionKey, 0);
            int revision = kit.GetString(KitTokenKey, "") == token ? checked(previous + 1) : 1;
            string tag = PrisonGearPolicy.Tag(ZNet.instance.GetWorldUID(), token, revision);
            // Resolve every definition before touching the live native chest.
            var stock = new List<GameObject>();
            foreach (string name in loadout.GearPrefabs) stock.Add(RequireItemPrefab(name));
            var food = new List<GameObject>();
            for (int i = 0; i < loadout.FoodPrefabs.Length; ++i) {
                GameObject prefab = RequireItemPrefab(loadout.FoodPrefabs[i]);
                ItemDrop.ItemData definition = prefab.GetComponent<ItemDrop>().m_itemData;
                if (!IsFoodLoanType(definition) || definition.m_shared.m_maxStackSize != 1)
                    throw new InvalidOperationException("Некорректный паёк арены: " + prefab.name);
                bool healthFood = definition.m_shared.m_food > definition.m_shared.m_foodStamina;
                if (healthFood != (i < 2)) throw new InvalidOperationException("Неверный тип пайка арены: " + prefab.name);
                food.Add(prefab);
            }
            if (ContainerLoad == null) throw new MissingMethodException("Container", "Load");
            var storage = new PrisonKitStorage(kit, chest.GetInventory());
            chest.GetComponent<ZNetView>().ClaimOwnership(); ContainerLoad.Invoke(chest, null);
            Inventory native = chest.GetInventory();
            if (native == null) throw new InvalidOperationException("Не открыт инвентарь сундука снаряжения.");
            Inventory candidate = storage.WorkingCopy(native);
            var remove = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData item in candidate.GetAllItems()) if (IsGeneratedGear(item)) remove.Add(item);
            foreach (ItemDrop.ItemData item in remove) candidate.RemoveItem(item);
            foreach (GameObject prefab in stock) {
                ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                item.m_dropPrefab = prefab; item.m_equipped = false;
                bool supplies = item.m_shared.m_maxStackSize > 1;
                if (supplies) {
                    bool present = false;
                    foreach (ItemDrop.ItemData existing in candidate.GetAllItems()) if (existing.m_shared.m_name == item.m_shared.m_name) { present = true; break; }
                    if (present) continue;
                    item.m_stack = Math.Min(100, item.m_shared.m_maxStackSize);
                }
                else {
                    if (!IsGearType(item)) throw new InvalidOperationException("Предмет набора не является бронёй или оружием: " + prefab.name);
                    item.m_stack = 1; item.m_quality = Math.Max(1, Math.Min(loadout.GearQuality, item.m_shared.m_maxQuality));
                    item.m_durability = item.GetMaxDurability();
                    item.m_customData = item.m_customData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.m_customData);
                    item.m_customData[GearKey] = tag; item.m_customData[KitStockKey] = token;
                }
                if (!candidate.AddItem(item)) throw new InvalidOperationException("Освободите место в сундуке снаряжения. Личные вещи в нём сохранены.");
            }
            foreach (GameObject prefab in food)
                for (int serving = 0; serving < loadout.FoodServings; ++serving) {
                    ItemDrop.ItemData item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                    item.m_dropPrefab = prefab; item.m_stack = item.m_quality = 1; item.m_equipped = false;
                    item.m_customData = item.m_customData == null ? new Dictionary<string, string>() : new Dictionary<string, string>(item.m_customData);
                    item.m_customData[GearKey] = tag; item.m_customData[KitStockKey] = token;
                    if (!candidate.AddItem(item)) throw new InvalidOperationException("Освободите место для пайков в сундуке снаряжения. Личные вещи в нём сохранены.");
                }
            storage.Publish(chest, candidate);
            kit.Set(CustodyInventory.PublicKey, true);
            kit.Set(KitFamilyKey, family); kit.Set(KitDifficultyKey, difficulty); kit.Set(KitRevisionKey, revision);
            kit.Set(KitTokenKey, token); ZDOMan.instance.ForceSendZDO(kit.m_uid);
            return revision;
        }

        public static bool IsGeneratedGear(ItemDrop.ItemData item)
        {
            if (!IsGearType(item) || item.m_shared.m_maxStackSize != 1 || item.m_customData == null) return false;
            string value; long world; string token; int revision; Guid legacy;
            if (item.m_customData.TryGetValue(GearKey, out value)) return PrisonGearPolicy.TryParse(value, out world, out token, out revision);
            return item.m_customData.TryGetValue(KitStockKey, out value) && Guid.TryParseExact(value, "N", out legacy);
        }

        private static bool IsGearType(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (IsFoodLoanType(item)) return true;
            switch (item.m_shared.m_itemType) {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder: return true;
                default: return false;
            }
        }

        private static bool IsFoodLoanType(ItemDrop.ItemData item)
        {
            return item != null && item.m_shared != null && item.m_dropPrefab != null
                && item.m_shared.m_food > 0f && item.m_shared.m_foodStamina > 0f
                && item.m_shared.m_maxStackSize == 1 && CombatCatalog.IsFoodPrefab(item.m_dropPrefab.name);
        }

        /// <summary>Every owning peer removes obsolete loan gear from its character, leaving farm loot intact.</summary>
        public static int RemoveObsoleteGear(Player player, long world, string activeToken, int activeRevision)
        {
            if (player == null || player.GetInventory() == null) return 0;
            return RemoveGearCore(player.GetInventory(), world, activeToken, activeRevision, item => {
                player.RemoveEquipAction(item); if (item.m_equipped) player.UnequipItem(item, false);
            });
        }

        /// <summary>Briefly tolerate native chest updates arriving before their authoritative sentence snapshot.</summary>
        public static int RemoveObsoleteGearWhenSettled(Player player, long world, string activeToken, int activeRevision)
        {
            if (player == null || player.GetInventory() == null) return 0;
            return RemoveGearCore(player.GetInventory(), world, activeToken, activeRevision, item => {
                player.RemoveEquipAction(item); if (item.m_equipped) player.UnequipItem(item, false);
            }, item => {
                string value, token; long taggedWorld; int revision;
                if (!item.m_customData.TryGetValue(GearKey, out value) || !PrisonGearPolicy.TryParse(value, out taggedWorld, out token, out revision)
                    || token == activeToken) return true;
                SeenGear seen;
                if (!observedGear.TryGetValue(item, out seen)) { observedGear.Add(item, new SeenGear { At = Time.realtimeSinceStartup }); return false; }
                return Time.realtimeSinceStartup - seen.At >= 2f;
            });
        }

        public static int RemoveObsoleteInventoryGear(Inventory inventory, long world, string activeToken, int activeRevision)
        { return inventory == null ? 0 : RemoveGearCore(inventory, world, activeToken, activeRevision, null); }

        public static bool IsObsoleteGear(ItemDrop.ItemData item, long world, string activeToken, int activeRevision)
        {
            if (!IsGearType(item) || item.m_customData == null) return false;
            string gear, legacy; item.m_customData.TryGetValue(GearKey, out gear); item.m_customData.TryGetValue(KitStockKey, out legacy);
            return PrisonGearPolicy.ShouldRemove(gear, legacy, item.m_shared.m_maxStackSize, world, activeToken, activeRevision);
        }

        private static int RemoveGearCore(Inventory inventory, long world, string activeToken, int activeRevision, Action<ItemDrop.ItemData> beforeRemove, Func<ItemDrop.ItemData, bool> canRemove = null)
        {
            List<ItemDrop.ItemData> remove = null;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems()) {
                if (IsObsoleteGear(item, world, activeToken, activeRevision) && (canRemove == null || canRemove(item))) {
                    if (remove == null) remove = new List<ItemDrop.ItemData>();
                    remove.Add(item);
                }
            }
            if (remove == null) return 0;
            foreach (ItemDrop.ItemData item in remove) {
                if (beforeRemove != null) beforeRemove(item);
                inventory.RemoveItem(item);
            }
            return remove.Count;
        }

        /// <summary>Loan gear stored in the five prison chests expires too; native personal deposits remain.</summary>
        public static int ExpireStoredPrisonGear(PrisonRegion region, long world, string activeToken, int activeRevision)
        {
            RequireHost(); if (world != ZNet.instance.GetWorldUID()) throw new ArgumentException("Equipment maintenance belongs to another world.");
            return RemoveObsoleteStoredGear(region, activeToken, activeRevision);
        }

        public static int RemoveObsoleteStoredGear(PrisonRegion region, string activeToken, int activeRevision)
        {
            RequireHost(); if (region == null || ContainerLoad == null) return 0;
            if (NativeContainerSave == null) throw new MissingMethodException("Container", "Save");
            long world = ZNet.instance.GetWorldUID();
            List<ZDO> chests = StoredGearChests(region);
            int removed = 0;
            foreach (ZDO zdo in chests) {
                // A legacy escrow container is still a durable handoff and must
                // not be touched by ordinary generated-equipment maintenance.
                if (!zdo.GetBool(CustodyInventory.PublicKey, false)) continue;
                ZNetView view = ZNetScene.instance.FindInstance(zdo);
                Container chest = view == null || !view.IsValid() ? null : view.GetComponent<Container>();
                if (chest == null || chest.IsInUse()) continue;
                // Inspect what the native container has already loaded. Do not
                // force an unvalidated raw load merely to decide whether expiry
                // is needed; a pending ordinary native sync can defer cleanup.
                Inventory inventory = chest.GetInventory(); bool stale = false;
                foreach (ItemDrop.ItemData item in inventory.GetAllItems()) {
                    if (IsObsoleteGear(item, world, activeToken, activeRevision)) { stale = true; break; }
                }
                if (!stale) stale = CustodyInventory.HasObsoleteBackpackGear(inventory, world, activeToken, activeRevision);
                if (!stale) continue;
                // Strict snapshots are paid only when there is an actual stale
                // item to remove, never by the common empty/unchanged scan.
                var storage = new PrisonKitStorage(zdo, inventory);
                view.ClaimOwnership(); ContainerLoad.Invoke(chest, null);
                Inventory candidate = storage.WorkingCopy(chest.GetInventory());
                int count = RemoveObsoleteInventoryGear(candidate, world, activeToken, activeRevision);
                count += CustodyInventory.ExpireBackpackGear(candidate, world, activeToken, activeRevision);
                if (count > 0) {
                    // Backpack serialization changes an outer item's custom
                    // data without raising the native inventory change event.
                    storage.Publish(chest, candidate);
                    removed += count; ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                }
            }
            return removed;
        }

        private static List<ZDO> StoredGearChests(PrisonRegion region)
        {
            float now = Time.realtimeSinceStartup;
            if (storedGearManager == ZDOMan.instance && SameRegion(storedGearRegion, region) && storedGearChests != null && now < nextStoredGearQuery)
                return storedGearChests;
            var chests = new List<ZDO>();
            // One bounded native sector query supplies all four foyer chests and
            // the cell kit, cached for one second even when a choice is changed.
            foreach (ZDO zdo in TaggedRegionObjects(ProtectedKey, region))
                if (InsideStructure(region, zdo.GetPosition()) && (zdo.GetBool(CustodyKey, false) || zdo.GetBool(KitKey, false))) chests.Add(zdo);
            storedGearManager = ZDOMan.instance; storedGearRegion = region.Copy();
            storedGearChests = chests; nextStoredGearQuery = now + 1f; return chests;
        }

        private static void ResetCombatRuntime()
        {
            combatPausedUntil = 0f; InvalidateStoredGearCache();
            observedGear = new ConditionalWeakTable<ItemDrop.ItemData, SeenGear>();
        }

        private static void InvalidateStoredGearCache()
        {
            nextStoredGearQuery = 0f;
            storedGearManager = null; storedGearRegion = null; storedGearChests = null;
        }

        public static int ResetAfterDefeat(PrisonRegion region)
        {
            RequireHost(); int removed = CleanupMobs(region, true);
            combatPausedUntil = Time.realtimeSinceStartup + 8f; return removed;
        }

        public static bool CanSpawnWave(PrisonRegion region)
        { return region != null && Time.realtimeSinceStartup >= combatPausedUntil; }
    }
}
