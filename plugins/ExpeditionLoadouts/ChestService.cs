using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.ExpeditionLoadouts
{
    /// <summary>
    /// Serial, bounded use of the game's normal container ownership handshake.
    /// Never claims ownership, edits remote ZDO inventories, or moves existing player items.
    /// </summary>
    public sealed class ChestService : IDisposable
    {
        private const string HarmonyId = "valheimmodpack.expeditionloadouts.transfer";
        private const float RequestTimeout = 8f;
        private static readonly FieldInfo ViewField = AccessTools.Field(typeof(Container), "m_nview");
        private static readonly MethodInfo AccessMethod = AccessTools.Method(typeof(Container), "CheckAccess", new[] { typeof(long) });
        private static readonly MethodInfo LoadMethod = AccessTools.Method(typeof(Container), "Load", Type.EmptyTypes);
        private static readonly MethodInfo SaveMethod = AccessTools.Method(typeof(Container), "Save", Type.EmptyTypes);
        private static readonly FieldInfo CurrentContainerField = AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        private static readonly FieldInfo DragItemField = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo LastRevisionField = AccessTools.Field(typeof(Container), "m_lastRevision");
        private static readonly FieldInfo LoadingField = AccessTools.Field(typeof(Container), "m_loading");
        private static ChestService active;
        private readonly ManualLogSource log;
        private readonly Harmony harmony;
        private readonly List<Container> queue = new List<Container>();
        // Cancelled requests still consume their eventual response; it must not open a UI unexpectedly.
        private readonly Dictionary<Container, long> lateResponses = new Dictionary<Container, long>();
        private List<SupplyTarget> targets;
        private Player player;
        private Container pending;
        private InventoryGuard guard;
        private float radius;
        private float deadline;
        private long requestOwner;
        private int responseFrame;
        private bool granted;
        private int added;
        private bool disposed;

        public bool IsBusy { get; private set; }
        public string Status { get; private set; }
        public int TotalAdded { get { return added; } }

        public ChestService(ManualLogSource logger)
        {
            if (active != null) throw new InvalidOperationException("Only one loadout transfer service is supported.");
            log = logger;
            if (ViewField == null || AccessMethod == null || LoadMethod == null || SaveMethod == null
                || CurrentContainerField == null || DragItemField == null || LastRevisionField == null || LoadingField == null)
                throw new MissingMethodException("Valheim container API changed; transfers disabled.");
            harmony = new Harmony(HarmonyId);
            MethodInfo response = AccessTools.Method(typeof(Container), "RPC_OpenResponse", new[] { typeof(long), typeof(bool) });
            if (response == null) throw new MissingMethodException("Container.RPC_OpenResponse");
            harmony.Patch(response, prefix: new HarmonyMethod(typeof(ChestService), "OpenResponse"));
            active = this;
            Status = "";
        }

        public bool Begin(Player owner, List<SupplyTarget> requested, float range)
        {
            if (disposed || IsBusy || !ValidPlayer(owner)) return false;
            try
            {
                PruneRetired(owner);
                if (lateResponses.Count >= 128) { Status = T("Ожидается ответ сундуков. Повторите после переподключения.", "Too many unanswered chest requests. Reconnect before retrying."); return false; }
                guard = new InventoryGuard(owner);
                guard.VisibleRows(owner.GetInventory()); // Fail closed if an installed slot/favorite API changed.
                targets = Normalize(requested);
                if (targets.Count == 0) { Status = T("Набор пуст или содержит неподдерживаемые предметы.", "The loadout is empty or has no supported supplies."); return false; }
                player = owner;
                radius = Mathf.Clamp(range, 1f, 30f);
                if (Single.IsNaN(radius) || Single.IsInfinity(radius)) radius = 10f;
                added = 0;
                queue.Clear();
                foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
                    if (CanUse(container, player, radius) && !Busy(container) && !lateResponses.ContainsKey(container)) queue.Add(container);
                queue.Sort(delegate(Container a, Container b) {
                    return (a.transform.position - player.transform.position).sqrMagnitude.CompareTo((b.transform.position - player.transform.position).sqrMagnitude);
                });
                // A heavily built base cannot keep a request running indefinitely.
                int maximumQueue = 128 - lateResponses.Count;
                if (queue.Count > maximumQueue) queue.RemoveRange(maximumQueue, queue.Count - maximumQueue);
                IsBusy = true;
                Status = T("Пополняю набор…", "Restocking loadout…");
                Tick();
                return true;
            }
            catch (Exception error) { Fail(error); return false; }
        }

        public void Tick()
        {
            if (!ReferenceEquals(player, null) && !ReferenceEquals(player, Player.m_localPlayer))
            {
                Cancel();
                lateResponses.Clear();
                player = null;
            }
            PruneRetired(Player.m_localPlayer);
            if (!IsBusy || disposed) return;
            try
            {
                if (!ValidPlayer(player) || ContainerUiOpen()) { Cancel(); return; }
                if (pending != null)
                {
                    if (!CanUse(pending, player, radius)) { RetirePending(); return; }
                    ZNetView view = View(pending);
                    if (Time.realtimeSinceStartup >= deadline) { RetirePending(); return; }
                    if (!granted || Time.frameCount <= responseFrame || view == null || !view.IsOwner()) return;
                    Container container = pending;
                    pending = null;
                    granted = false;
                    Transfer(container);
                }
                if (!HasDeficit()) { Complete(); return; }
                while (queue.Count > 0)
                {
                    Container candidate = queue[0];
                    queue.RemoveAt(0);
                    if (!CanUse(candidate, player, radius) || Busy(candidate)) continue;
                    // Do not acquire unrelated containers. The confirmed owner refresh below remains authoritative.
                    LoadMethod.Invoke(candidate, null);
                    if (!Fresh(candidate)) continue;
                    if (!HasWantedSupply(candidate.GetInventory())) continue;
                    ZNetView view = View(candidate);
                    pending = candidate;
                    requestOwner = view.GetZDO().GetOwner();
                    deadline = Time.realtimeSinceStartup + RequestTimeout;
                    granted = false;
                    // Identical request to Container.Interact, including personal-chest permission checks by owner.
                    view.InvokeRPC("RPC_RequestOpen", new object[] { player.GetPlayerID() });
                    return;
                }
                Complete();
            }
            catch (Exception error) { Fail(error); }
        }

        public void Cancel()
        {
            if (!IsBusy) return;
            RetirePending();
            IsBusy = false;
            queue.Clear();
            Status = T("Пополнение остановлено. Получено: ", "Restocking stopped. Received: ") + added;
        }

        public void Dispose()
        {
            if (disposed) return;
            Cancel();
            disposed = true;
            if (active == this) active = null;
            harmony.UnpatchSelf();
        }

        public static List<SupplyTarget> SnapshotInventory(Player owner)
        {
            return CaptureInventory(owner).Items;
        }

        // Reading equipment, hotbar and EAQS slots is safe. Transfer destinations
        // remain restricted to unprotected ordinary cells by FindDestination.
        public static InventoryCapture CaptureInventory(Player owner)
        {
            var result = new InventoryCapture();
            if (!ValidPlayer(owner)) return result;
            var protection = new InventoryGuard(owner);
            Inventory inventory = owner.GetInventory();
            protection.VisibleRows(inventory); // Validate the installed EAQS layout before reading its extra row.
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item == null || item.m_dropPrefab == null || item.m_shared == null || item.m_stack <= 0
                    || item.m_gridPos.x < 0 || item.m_gridPos.x >= inventory.GetWidth()
                    || item.m_gridPos.y < 0 || item.m_gridPos.y >= inventory.GetHeight())
                { ++result.SkippedInvalid; continue; }
                if (item.m_customData != null && item.m_customData.Count > 0) { ++result.SkippedCustomData; continue; }
                if (!SupplyDefinition(item)) { ++result.SkippedUnsupported; continue; }
                var target = new SupplyTarget { Prefab = item.m_dropPrefab.name, Quality = item.m_quality,
                    Variant = item.m_variant, WorldLevel = item.m_worldLevel, Count = Math.Min(TransferPolicy.MaximumTarget, item.m_stack) };
                if (!TransferPolicy.ValidTarget(target) || target.Quality > Math.Max(1, item.m_shared.m_maxQuality))
                { ++result.SkippedInvalid; continue; }
                SupplyTarget found = result.Items.Find(delegate(SupplyTarget existing) { return SameTarget(existing, target); });
                if (found != null)
                {
                    if ((long)found.Count + item.m_stack > TransferPolicy.MaximumTarget) ++result.SkippedLimit;
                    found.Count = (int)Math.Min(TransferPolicy.MaximumTarget, (long)found.Count + item.m_stack);
                }
                else if (result.Items.Count < TransferPolicy.MaximumTargets)
                {
                    result.Items.Add(target);
                    if (item.m_stack > TransferPolicy.MaximumTarget) ++result.SkippedLimit;
                }
                else { ++result.SkippedLimit; continue; }
                ++result.IncludedSlots;
            }
            return result;
        }

        // Count existing protected items too, so a full arrow stack in an EAQS slot is not requested again.
        public static int CountOwned(Player owner, SupplyTarget target)
        {
            if (owner == null || !TransferPolicy.ValidTarget(target)) return 0;
            int total = 0;
            foreach (ItemDrop.ItemData item in owner.GetInventory().GetAllItems())
                if (Matches(item, target)) total = (int)Math.Min(TransferPolicy.MaximumTarget, (long)total + Math.Max(0, item.m_stack));
            return total;
        }

        private static bool OpenResponse(Container __instance, long __0, bool __1)
        {
            ChestService service = active;
            if (service == null) return true;
            if (ReferenceEquals(service.pending, __instance))
            {
                // A different sender cannot grant a transaction we requested from the current owner.
                if (__0 != service.requestOwner) return false;
                if (!__1) { service.pending = null; service.granted = false; return false; }
                service.granted = true;
                service.responseFrame = Time.frameCount;
                return false;
            }
            long expected;
            if (service.lateResponses.TryGetValue(__instance, out expected) && expected == __0)
            {
                service.lateResponses.Remove(__instance);
                return false;
            }
            return true;
        }

        private void RetirePending()
        {
            if (pending != null && !granted) lateResponses[pending] = requestOwner;
            pending = null;
            granted = false;
        }

        private void PruneRetired(Player current)
        {
            if (!ReferenceEquals(player, null) && !ReferenceEquals(player, current)) lateResponses.Clear();
            if (lateResponses.Count == 0) return;
            var destroyed = new List<Container>();
            foreach (Container container in lateResponses.Keys) if (container == null) destroyed.Add(container);
            foreach (Container container in destroyed) lateResponses.Remove(container);
        }

        private void Transfer(Container container)
        {
            ZNetView view = View(container);
            if (!CanUse(container, player, radius) || view == null || !view.IsOwner() || Busy(container)) return;
            bool acquired = false;
            try
            {
                // The old owner sent its ZDO before granting ownership, as for normal chest opening.
                LoadMethod.Invoke(container, null);
                if (!Fresh(container)) return;
                acquired = true; // Also release if a sound/effect callback throws inside SetInUse.
                container.SetInUse(true);
                acquired = container.IsInUse() && view.IsOwner();
                if (!acquired) return;
                Inventory source = container.GetInventory();
                Inventory destination = player.GetInventory();
                foreach (SupplyTarget target in targets)
                {
                    // Snapshot references, not copies; native MoveItemToThis consumes these exact instances.
                    var supplies = new List<ItemDrop.ItemData>(source.GetAllItems());
                    foreach (ItemDrop.ItemData item in supplies)
                    {
                        if (!SupplyItem(item) || !Matches(item, target)) continue;
                        while (item.m_stack > 0 && source.GetAllItems().Contains(item))
                        {
                            if (!ValidPlayer(player) || !CanUse(container, player, radius) || !view.IsOwner()
                                || !container.IsInUse() || ContainerUiOpen()) return;
                            int deficit = TransferPolicy.Deficit(target.Count, CountOwned(player, target));
                            if (deficit == 0) break;
                            Vector2i cell;
                            int amount;
                            if (!FindDestination(destination, item, deficit, out cell, out amount)) break;
                            int before = item.m_stack;
                            // Explicit vanilla destination; no FindEmptySlot/auto-equip/sort code can select EAQS rows.
                            destination.MoveItemToThis(source, item, amount, cell.x, cell.y);
                            int moved = Math.Max(0, before - item.m_stack);
                            added += moved;
                            if (moved == 0) break;
                        }
                    }
                }
            }
            finally
            {
                if (acquired && view != null && view.IsValid() && view.IsOwner())
                {
                    // Also persist if a third-party inventory-changed callback threw after a native decrement.
                    try
                    {
                        // Native AddItem decrements first; a failing Changed callback can leave a zero stack in the source.
                        try
                        {
                            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(container.GetInventory().GetAllItems()))
                                if (item.m_stack == 0) container.GetInventory().RemoveItem(item);
                        }
                        finally { SaveMethod.Invoke(container, null); }
                    }
                    finally { container.SetInUse(false); }
                }
            }
        }

        private bool FindDestination(Inventory inventory, ItemDrop.ItemData source, int deficit, out Vector2i cell, out int amount)
        {
            cell = new Vector2i(-1, -1);
            amount = 0;
            int rows = guard.VisibleRows(inventory);
            // Existing ordinary stacks first, then empty ordinary cells. Never touch equipment/hotbar/favorites.
            for (int pass = 0; pass < 2; pass++)
                for (int y = 1; y < rows; y++)
                    for (int x = 0; x < inventory.GetWidth(); x++)
                    {
                        var position = new Vector2i(x, y);
                        if (!TransferPolicy.OrdinaryCell(x, y, inventory.GetWidth(), inventory.GetHeight(), rows, guard.FavoriteCell(position))) continue;
                        ItemDrop.ItemData existing = inventory.GetItemAt(x, y);
                        if (pass == 0)
                        {
                            if (existing == null || existing.m_equipped || guard.FavoriteItem(existing) || !SupplyItem(existing)
                                || existing.m_dropPrefab.name != source.m_dropPrefab.name || existing.m_quality != source.m_quality
                                || existing.m_variant != source.m_variant || existing.m_worldLevel != source.m_worldLevel
                                || !existing.IsSameType(source)) continue;
                        }
                        else if (existing != null) continue;
                        int take = TransferPolicy.MoveAmount(deficit, source.m_stack, existing == null ? 0 : existing.m_stack, source.m_shared.m_maxStackSize);
                        if (take <= 0) continue;
                        cell = position;
                        amount = take;
                        return true;
                    }
            return false;
        }

        private bool HasWantedSupply(Inventory inventory)
        {
            if (inventory == null) return false;
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                if (SupplyItem(item))
                    foreach (SupplyTarget target in targets)
                        if (Matches(item, target) && TransferPolicy.Deficit(target.Count, CountOwned(player, target)) > 0) return true;
            return false;
        }

        private bool HasDeficit()
        {
            foreach (SupplyTarget target in targets) if (TransferPolicy.Deficit(target.Count, CountOwned(player, target)) > 0) return true;
            return false;
        }

        private void Complete()
        {
            IsBusy = false;
            int missing = 0;
            foreach (SupplyTarget target in targets) missing += TransferPolicy.Deficit(target.Count, CountOwned(player, target));
            Status = T("Получено: ", "Received: ") + added
                + (missing == 0 ? T(". Набор готов.", ". Loadout ready.")
                    : T(". Не хватает: ", ". Still missing: ") + missing + T(" (нет запасов, места или доступа).", " (supplies, space or access unavailable)."));
        }

        private void Fail(Exception error)
        {
            Cancel();
            Status = T("Пополнение остановлено из-за ошибки; подробности в журнале.", "Restocking stopped after an error; see the log.");
            if (log != null) log.LogError("Loadout transfer stopped safely: " + error);
        }

        private static List<SupplyTarget> Normalize(List<SupplyTarget> requested)
        {
            var result = new List<SupplyTarget>();
            if (requested == null || requested.Count > TransferPolicy.MaximumTargets) return result;
            foreach (SupplyTarget target in requested)
            {
                if (!TransferPolicy.ValidTarget(target)) continue;
                GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(target.Prefab) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || !SupplyDefinition(drop.m_itemData) || target.Quality > Math.Max(1, drop.m_itemData.m_shared.m_maxQuality)) continue;
                SupplyTarget same = result.Find(delegate(SupplyTarget t) { return SameTarget(t, target); });
                if (same != null) same.Count = Math.Max(same.Count, target.Count);
                else result.Add(new SupplyTarget { Prefab = target.Prefab, Quality = target.Quality,
                    Variant = target.Variant, WorldLevel = target.WorldLevel, Count = target.Count });
            }
            return result;
        }

        private static bool SupplyItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_dropPrefab == null || !SupplyDefinition(item)
                || item.m_stack <= 0 || item.m_equipped || (item.m_customData != null && item.m_customData.Count > 0)) return false;
            return true;
        }

        private static bool SupplyDefinition(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null || item.m_shared.m_maxStackSize < 1) return false;
            ItemDrop.ItemData.ItemType type = item.m_shared.m_itemType;
            switch (type)
            {
                case ItemDrop.ItemData.ItemType.Material:
                case ItemDrop.ItemData.ItemType.Consumable:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                case ItemDrop.ItemData.ItemType.Fish:
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                case ItemDrop.ItemData.ItemType.Trophy:
                case ItemDrop.ItemData.ItemType.Misc:
                    return true;
                default:
                    return false;
            }
        }

        private static bool Matches(ItemDrop.ItemData item, SupplyTarget target)
        {
            // Do not count a magic/custom instance as its ordinary counterpart.
            // Equipped items count as held, but are never transfer sources/destinations.
            return item != null && item.m_dropPrefab != null && SupplyDefinition(item)
                && (item.m_customData == null || item.m_customData.Count == 0)
                && item.m_dropPrefab.name == target.Prefab && item.m_quality == target.Quality
                && item.m_variant == target.Variant && item.m_worldLevel == target.WorldLevel;
        }

        private static bool SameTarget(SupplyTarget first, SupplyTarget second)
        {
            return first.Prefab == second.Prefab && first.Quality == second.Quality
                && first.Variant == second.Variant && first.WorldLevel == second.WorldLevel;
        }

        private static bool ValidPlayer(Player owner)
        {
            return owner != null && owner == Player.m_localPlayer && !owner.IsDead() && !owner.IsTeleporting()
                && !owner.IsSleeping() && !owner.InCutscene() && owner.GetInventory() != null;
        }

        private static bool ContainerUiOpen()
        {
            // Root UI stays open while transferring; only a chest/drag operation conflicts.
            return InventoryGui.instance != null && (CurrentContainerField.GetValue(InventoryGui.instance) != null || DragItemField.GetValue(InventoryGui.instance) != null);
        }

        private static ZNetView View(Container container) { return container != null ? ViewField.GetValue(container) as ZNetView : null; }

        private static bool CanUse(Container container, Player owner, float range)
        {
            if (container == null || owner == null || !container.isActiveAndEnabled
                || (container.transform.position - owner.transform.position).sqrMagnitude > range * range) return false;
            if (container.m_autoDestroyEmpty || container.m_rootObjectOverride != null || container.m_wagon != null
                || container.GetComponentInParent<Player>() != null || container.GetComponentInParent<TombStone>() != null
                || container.GetComponentInParent<ItemDrop>() != null || container.GetComponentInParent<Ship>() != null) return false;
            Piece piece = container.GetComponent<Piece>();
            ZNetView view = View(container);
            if (piece == null || !piece.IsPlacedByPlayer() || view == null || !view.IsValid() || !view.HasOwner()
                || !TransferPolicy.StandardChest(Utils.GetPrefabName(container.gameObject))
                || !Convert.ToBoolean(AccessMethod.Invoke(container, new object[] { owner.GetPlayerID() }))) return false;
            return !container.m_checkGuardStone || PrivateArea.CheckAccess(container.transform.position, 0f, false, false);
        }

        private static bool Fresh(Container container)
        {
            ZNetView view = View(container);
            return view != null && view.IsValid() && !Convert.ToBoolean(LoadingField.GetValue(container))
                && Convert.ToUInt32(LastRevisionField.GetValue(container)) == view.GetZDO().DataRevision;
        }

        private static bool Busy(Container container)
        {
            ZNetView view = View(container);
            return container.IsInUse() || view == null || view.GetZDO().GetInt("InUse", 0) != 0;
        }

        private static string T(string russian, string english)
        {
            return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? russian : english;
        }

        private sealed class InventoryGuard
        {
            private readonly MethodInfo visible;
            private readonly MethodInfo fullHeight;
            private readonly object favorites;
            private readonly MethodInfo favoriteCell;
            private readonly MethodInfo favoriteItem;

            internal InventoryGuard(Player owner)
            {
                if (Chainloader.PluginInfos.ContainsKey("randyknapp.mods.equipmentandquickslots"))
                {
                    Type api = AccessTools.TypeByName("EquipmentAndQuickSlots.API");
                    visible = api == null ? null : AccessTools.Method(api, "GetVisibleRows", Type.EmptyTypes);
                    fullHeight = api == null ? null : AccessTools.Method(api, "GetFullHeight", Type.EmptyTypes);
                    if (visible == null || fullHeight == null) throw new MissingMethodException("EAQS visible-row API unavailable");
                }
                if (Chainloader.PluginInfos.ContainsKey("goldenrevolver.quick_stack_store"))
                {
                    Type type = AccessTools.TypeByName("QuickStackStore.UserConfig");
                    MethodInfo get = type == null ? null : AccessTools.Method(type, "GetPlayerConfig", new[] { typeof(long) });
                    favoriteCell = type == null ? null : AccessTools.Method(type, "IsSlotFavorited", new[] { typeof(Vector2i) });
                    favoriteItem = type == null ? null : AccessTools.Method(type, "IsItemNameFavorited", new[] { typeof(ItemDrop.ItemData.SharedData) });
                    if (get == null || favoriteCell == null || favoriteItem == null) throw new MissingMethodException("Quick Stack favorite API unavailable");
                    favorites = get.Invoke(null, new object[] { owner.GetPlayerID() });
                    if (favorites == null) throw new InvalidOperationException("Quick Stack preferences unavailable");
                }
            }

            internal int VisibleRows(Inventory inventory)
            {
                int rows = visible == null ? inventory.GetHeight() : Convert.ToInt32(visible.Invoke(null, null));
                if (rows < 1 || rows > inventory.GetHeight() || (fullHeight != null && Convert.ToInt32(fullHeight.Invoke(null, null)) != inventory.GetHeight()))
                    throw new InvalidOperationException("Inventory dimensions are inconsistent with EAQS");
                return rows;
            }

            internal bool FavoriteCell(Vector2i position) { return favorites != null && Convert.ToBoolean(favoriteCell.Invoke(favorites, new object[] { position })); }
            internal bool FavoriteItem(ItemDrop.ItemData item) { return favorites != null && Convert.ToBoolean(favoriteItem.Invoke(favorites, new object[] { item.m_shared })); }
        }
    }
}
