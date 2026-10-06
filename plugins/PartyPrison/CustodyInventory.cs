using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using BepInEx.Bootstrap;
using UnityEngine;
using WCInventory = ValheimModPack.WorldCharacters.NativeInventory;

namespace ValheimModPack.PartyPrison
{
    public sealed class CustodyInsertionException : InvalidOperationException
    {
        public bool AppliedAmbiguously { get; private set; }
        public CustodyInsertionException(string message, bool appliedAmbiguously, Exception inner)
            : base(message, inner) { AppliedAmbiguously = appliedAmbiguously; }
    }
    public static class CustodyInventory
    {
        public const string OwnerKey = "VMP_PP_CustodyOwner", TokenKey = "VMP_PP_CustodyToken",
            ReleasedKey = "VMP_PP_CustodyReleased", HashKey = "VMP_PP_CustodyHash", ReceiptKey = "VMP_PP_CustodyCleared", PublicKey = "VMP_PP_PublicChest";
        private static readonly string[] EquipmentKeys = { "eaqs_slot", "eaqs_player", "eaqs_parked", "eaqs_weaponshield" };
        private static readonly FieldInfo PlayerCustom = AccessTools.Field(typeof(Player), "m_customData");
        private static readonly MethodInfo ContainerLoad = AccessTools.Method(typeof(Container), "Load", Type.EmptyTypes);
        private static readonly FieldInfo ContainerInventory = AccessTools.Field(typeof(Container), "m_inventory");
        private static readonly FieldInfo ContainerView = AccessTools.Field(typeof(Container), "m_nview");
        // Inventory operations are hot paths even when nobody is imprisoned.
        // Weak identity keys avoid retaining unloaded container/inventory cycles;
        // the live ZDO marker is checked below, since builders tag after Awake.
        private static readonly ConditionalWeakTable<Inventory, Container> InventoryContainers = new ConditionalWeakTable<Inventory, Container>();
        private static readonly MethodInfo NativeAdd = AccessTools.Method(typeof(Inventory), "AddItem",
            new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
        private static readonly BackpackAccessCache<ItemDrop.ItemData, Inventory> BackpackAdapterCache = new BackpackAccessCache<ItemDrop.ItemData, Inventory>();
        private static readonly Func<BackpackAccess<ItemDrop.ItemData, Inventory>> ResolveBackpackAdapter = ResolveBackpackApi;
        private static readonly List<BackpackGearEntry> EmptyBackpackPlan = new List<BackpackGearEntry>(0);

        public static byte[] Capture(Player player)
        {
            if (player == null || player.IsDead()) throw new InvalidOperationException("The prisoner's character is not ready.");
            Inventory inventory = player.GetInventory(); VerifyEquipmentRoot(inventory);
            return Capture(inventory);
        }
        public static byte[] CaptureForAdmission(Player player)
        {
            if (player == null || player.IsDead() || player.IsTeleporting()) throw new InvalidOperationException("The prisoner's character is not ready to deposit belongings.");
            // Stop equipped torches consuming durability while the immutable
            // offer crosses the paced channel. Items stay in their native root.
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems().ToArray())
            { player.RemoveEquipAction(item); if (item.m_equipped) player.UnequipItem(item, false); }
            return Capture(player);
        }
        public static byte[] Capture(Inventory inventory)
        {
            if (inventory == null) throw new ArgumentNullException("inventory");
            foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToArray())
            {
                if (IsLoan(item)) throw new InvalidOperationException("Remove prison loan equipment before confiscating personal belongings.");
                SyncBackpack(item);
            }
            byte[] bytes = Save(inventory); CustodyStore.Payload(bytes);
            Inventory restored = Decode(bytes);
            RequireEquivalent(inventory.GetAllItems(), restored.GetAllItems());
            return bytes;
        }

        // EAQS in this pack persists equipment/quick slots in extra rows of the
        // native root. Checking the API prevents a future alternate inventory
        // implementation from silently leaving equipped items behind.
        private static void VerifyEquipmentRoot(Inventory root)
        {
            if (!Chainloader.PluginInfos.ContainsKey("randyknapp.mods.equipmentandquickslots")) return;
            Type api = AccessTools.TypeByName("EquipmentAndQuickSlots.API");
            if (api == null) throw new NotSupportedException("Equipment slot persistence API is unavailable.");
            foreach (string methodName in new[] { "GetEquipmentSlotItems", "GetQuickSlotItems" })
            {
                MethodInfo method = api.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                if (method == null) throw new MissingMethodException("EAQS", methodName);
                IEnumerable<ItemDrop.ItemData> items = method.Invoke(null, null) as IEnumerable<ItemDrop.ItemData>;
                if (items == null) throw new InvalidDataException("EAQS returned invalid equipment inventory.");
                foreach (ItemDrop.ItemData item in items)
                    if (item != null && !root.GetAllItems().Any(v => System.Object.ReferenceEquals(v, item)))
                        throw new NotSupportedException("Separate EAQS inventories require an updated custody adapter.");
            }
        }
        private static void SyncBackpack(ItemDrop.ItemData item)
        {
            if (item == null || item.m_dropPrefab == null || item.m_stack < 1) throw new InvalidDataException("Invalid item in personal inventory.");
            BackpackAccess<ItemDrop.ItemData, Inventory> access = BackpackApi();
            if (access == null || !access.IsBackpack(item)) return;
            if (access.Inventory(item) == null) throw new InvalidDataException("Backpack inventory is unavailable.");
            access.Serialize(access.Component(item));
            // Contents stay inside the bag's native custom-data payload. They
            // must never also be added as independent stacks to custody chests.
        }

        public static string Fingerprint(byte[] bytes) { return CustodyStore.Fingerprint(bytes); }
        public static int Count(byte[] bytes) { return Decode(bytes).GetAllItems().Count; }
        public static Inventory Decode(byte[] bytes)
        {
            CustodyStore.Payload(bytes);
            var inventory = new Inventory("PartyPrison.Custody", null, 255, 255); var package = new ZPackage(bytes);
            inventory.Load(package);
            if (package.GetPos() != package.Size()) throw new InvalidDataException("Trailing native inventory data.");
            List<ValheimModPack.WorldCharacters.StoredItem> expected;
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                expected = WCInventory.ReadInventory(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing custody inventory bytes.");
                // Validate nested custom-data payloads without unpacking them.
                foreach (var unused in WCInventory.IncludingBackpacks(expected)) { }
            }
            var roundtrip = Save(inventory);
            using (var stream = new MemoryStream(roundtrip, false)) using (var reader = new BinaryReader(stream))
                WCInventory.RequireSameItems(expected, WCInventory.ReadInventory(reader));
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                if (item.m_dropPrefab == null || item.m_stack < 1 || item.m_stack > item.m_shared.m_maxStackSize)
                    throw new InvalidDataException("Unsupported item stack in custody payload.");
            return inventory;
        }
        private static byte[] Save(Inventory inventory) { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }
        private static bool IsLoan(ItemDrop.ItemData item)
        { string value; return item != null && item.m_customData != null && item.m_customData.TryGetValue(ArenaBuilder.LoanKey, out value) && value == "1"; }

        private sealed class BackpackGearEntry
        {
            internal Inventory Inventory;
            internal object Component;
            internal Action<object> Serialize;
        }
        // Use the same installed backpack API as custody capture. Plan the
        // complete bounded tree before removing anything, then serialize from
        // leaves to roots so nested removals become durable bag custom data.
        public static int ExpireBackpackGear(Player player, long world, string activeToken, int activeRevision)
        { return player == null ? 0 : ExpireBackpackGear(player.GetInventory(), world, activeToken, activeRevision); }
        public static int ExpireBackpackGear(Inventory root, long world, string activeToken, int activeRevision)
        {
            List<BackpackGearEntry> plan = BackpackGearPlan(root);
            int removed = 0;
            for (int i = plan.Count - 1; i >= 0; --i)
                removed += ArenaBuilder.RemoveObsoleteInventoryGear(plan[i].Inventory, world, activeToken, activeRevision);
            if (removed != 0)
                for (int i = plan.Count - 1; i >= 0; --i) plan[i].Serialize(plan[i].Component);
            return removed;
        }
        public static bool HasObsoleteBackpackGear(Inventory root, long world, string activeToken, int activeRevision)
        {
            foreach (BackpackGearEntry entry in BackpackGearPlan(root))
                foreach (ItemDrop.ItemData item in entry.Inventory.GetAllItems())
                    if (ArenaBuilder.IsObsoleteGear(item, world, activeToken, activeRevision)) return true;
            return false;
        }
        private static List<BackpackGearEntry> BackpackGearPlan(Inventory root)
        {
            if (root == null) return EmptyBackpackPlan;
            BackpackAccess<ItemDrop.ItemData, Inventory> access = BackpackApi();
            if (access == null) return EmptyBackpackPlan;
            List<ItemDrop.ItemData> items = root.GetAllItems(); bool hasBag = false;
            for (int i = 0; i < items.Count; ++i) if (access.IsBackpack(items[i])) { hasBag = true; break; }
            if (!hasBag) return EmptyBackpackPlan;
            var plan = new List<BackpackGearEntry>();
            var visited = new HashSet<Inventory>(); visited.Add(root);
            int itemCount = 0; PlanBackpackGear(root, access, 0, visited, plan, ref itemCount);
            return plan;
        }
        private static void PlanBackpackGear(Inventory root, BackpackAccess<ItemDrop.ItemData, Inventory> access, int depth, HashSet<Inventory> visited, List<BackpackGearEntry> plan, ref int itemCount)
        {
            ItemDrop.ItemData[] items = root.GetAllItems().ToArray();
            itemCount += items.Length;
            if (itemCount > 8192) throw new InvalidDataException("Backpack equipment cleanup exceeds its item limit; inventories retained.");
            foreach (ItemDrop.ItemData item in items)
            {
                if (item == null || !access.IsBackpack(item)) continue;
                Inventory contents = access.Inventory(item);
                if (contents == null) throw new InvalidDataException("Backpack inventory is unavailable; contents retained.");
                if (!visited.Add(contents)) continue;
                if (depth >= 4 || plan.Count >= 128) throw new InvalidDataException("Backpack equipment cleanup exceeds its nesting or bag limit; inventories retained.");
                plan.Add(new BackpackGearEntry { Inventory = contents, Component = access.Component(item), Serialize = access.Serialize });
                PlanBackpackGear(contents, access, depth + 1, visited, plan, ref itemCount);
            }
        }
        private static BackpackAccess<ItemDrop.ItemData, Inventory> BackpackApi()
        {
            BackpackAccess<ItemDrop.ItemData, Inventory> cached = BackpackAdapterCache.Value;
            return cached ?? BackpackAdapterCache.Get(Time.realtimeSinceStartup, ResolveBackpackAdapter);
        }
        private static BackpackAccess<ItemDrop.ItemData, Inventory> ResolveBackpackApi()
        {
            // An optional plugin can load after this class. Retry absence at a
            // bounded interval, using exact assembly lookup rather than AllTypes.
            Type api = LoadedType("AdventureBackpacks.API.ABAPI");
            if (api == null) return null;
            // Several mods embed independent Vapok copies. The holder must be
            // the exact copy used by AdventureBackpacks, never a namesake.
            Type extensions = api.Assembly.GetType("Vapok.Common.Managers.ItemExtensions", false);
            Type component = api.Assembly.GetType("AdventureBackpacks.Components.BackpackComponent", false);
            return BackpackAccess<ItemDrop.ItemData, Inventory>.Create(api, extensions, component);
        }
        private static Type LoadedType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            { Type type = assembly.GetType(name, false); if (type != null) return type; }
            return null;
        }

        public static bool HasClearReceipt(Player player, long world, string token, string hash)
        {
            if (player == null || world == 0 || String.IsNullOrEmpty(hash)) return false;
            CustodyStore.Token(token); string actual; Dictionary<string, string> custom = Custom(player);
            return custom.TryGetValue(ReceiptKey, out actual) && actual == Receipt(world, token, hash);
        }
        public static bool HasClearReceipt(Player player, long world, string token)
        { string hash; return TryGetClearReceiptHash(player, world, token, out hash); }
        public static bool TryGetClearReceiptHash(Player player, long world, string token, out string hash)
        {
            hash = "";
            if (player == null || world == 0) return false;
            CustodyStore.Token(token); string actual;
            if (!Custom(player).TryGetValue(ReceiptKey, out actual)) return false;
            string prefix = world.ToString("x16", CultureInfo.InvariantCulture) + ":" + token + ":";
            if (actual == null || actual.Length != prefix.Length + 64 || !actual.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string candidate = actual.Substring(prefix.Length);
            foreach (char c in candidate) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            hash = candidate; return true;
        }
        public static void ClearReceipt(Player player, long world, string token)
        {
            if (player == null) return; CustodyStore.Token(token);
            Dictionary<string, string> custom = Custom(player); string receipt;
            if (custom.TryGetValue(ReceiptKey, out receipt) && receipt.StartsWith(world.ToString("x16", CultureInfo.InvariantCulture) + ":" + token + ":", StringComparison.Ordinal))
                custom.Remove(ReceiptKey);
        }
        public static void ClearExact(Player player, byte[] originalPayload, long world, string token)
        {
            if (player == null || player.IsDead() || player.IsTeleporting() || world == 0) throw new InvalidOperationException("Prisoner is busy or unavailable.");
            CustodyStore.Token(token); CustodyStore.Payload(originalPayload);
            string hash = Fingerprint(originalPayload);
            if (HasClearReceipt(player, world, token, hash)) return;
            Dictionary<string, string> custom = Custom(player); string prior;
            if (custom.TryGetValue(ReceiptKey, out prior) && prior.Length != 0) throw new InvalidOperationException("A prior confiscation receipt is unresolved.");
            Inventory root = player.GetInventory(); byte[] current = Capture(player);
            if (Fingerprint(current) != hash)
            {
                // EAQS can change equipment bookkeeping. An old Prepared
                // torch can also have burnt down while its offer was in flight.
                if (!EquivalentForAdmission(originalPayload, current)) throw new InvalidOperationException("Personal inventory changed after custody was prepared; no items removed.");
            }
            // Validate all receipt access and all bytes before any mutation.
            foreach (ItemDrop.ItemData item in root.GetAllItems().ToArray())
            { player.RemoveEquipAction(item); if (item.m_equipped) player.UnequipItem(item, false); }
            try { root.RemoveAll(); }
            catch { if (root.GetAllItems().Count != 0) throw; }
            if (root.GetAllItems().Count != 0) throw new InvalidOperationException("Personal inventory could not be completely cleared.");
            custom[ReceiptKey] = Receipt(world, token, hash);
        }
        private static Dictionary<string, string> Custom(Player player)
        {
            Dictionary<string, string> custom = PlayerCustom == null ? null : PlayerCustom.GetValue(player) as Dictionary<string, string>;
            if (custom == null) throw new NotSupportedException("Native durable player custom data is unavailable."); return custom;
        }
        private static string Receipt(long world, string token, string hash)
        { return world.ToString("x16", CultureInfo.InvariantCulture) + ":" + token + ":" + hash; }

        public static byte[][] PrepareChestPayloads(byte[] originalPayload, int width, int height)
        {
            if (width < 1 || width > 32 || height < 1 || height > 32) throw new InvalidDataException("Unsupported custody chest dimensions.");
            List<ItemDrop.ItemData> items = Decode(originalPayload).GetAllItems(); int capacity = width * height;
            if (items.Count > 4 * capacity) throw new InvalidOperationException("All personal belongings must fit in the four iron chests before confiscation.");
            var chests = new Inventory[4]; for (int i = 0; i < 4; ++i) chests[i] = new Inventory("PartyPrison.Custody." + i, null, width, height);
            for (int n = 0; n < items.Count; ++n)
            {
                ItemDrop.ItemData item = items[n].Clone(); int slot = n % capacity;
                item.m_equipped = false; item.m_gridPos = new Vector2i(slot % width, slot / width);
                foreach (string key in EquipmentKeys) item.m_customData.Remove(key);
                chests[n / capacity].GetAllItems().Add(item);
            }
            byte[][] payloads = new byte[4][]; var actual = new List<ItemDrop.ItemData>();
            for (int i = 0; i < 4; ++i) { payloads[i] = Save(chests[i]); actual.AddRange(Decode(payloads[i]).GetAllItems()); }
            RequireEquivalent(items, actual); return payloads;
        }
        private static string ItemIdentity(ItemDrop.ItemData source)
        {
            ItemDrop.ItemData item = source.Clone(); item.m_gridPos = new Vector2i(0, 0); item.m_equipped = false;
            foreach (string key in EquipmentKeys) item.m_customData.Remove(key);
            const string magic = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
            string value; if (item.m_customData.TryGetValue(magic, out value) && value == "") item.m_customData.Remove(magic);
            var package = new ZPackage(); item.Save(package); return Fingerprint(package.GetArray());
        }
        private static void RequireEquivalent(IEnumerable<ItemDrop.ItemData> expected, IEnumerable<ItemDrop.ItemData> actual)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ItemDrop.ItemData item in expected) { string id = ItemIdentity(item); int n; counts.TryGetValue(id, out n); counts[id] = n + 1; }
            foreach (ItemDrop.ItemData item in actual)
            { string id = ItemIdentity(item); int n; if (!counts.TryGetValue(id, out n) || n == 0) throw new InvalidDataException("Native custody roundtrip changed item data."); counts[id] = n - 1; }
            if (counts.Values.Any(n => n != 0)) throw new InvalidDataException("Native custody roundtrip lost personal belongings.");
        }

        public static bool AddExact(Inventory destination, int rows, byte[] oneItemPayload)
        {
            if (destination == null) throw new ArgumentNullException("destination");
            Inventory decoded = Decode(oneItemPayload);
            if (decoded.GetAllItems().Count != 1) throw new InvalidDataException("A custody grant must contain exactly one item stack.");
            if (NativeAdd == null) throw new MissingMethodException("Native exact-cell item insertion API is unavailable.");
            ItemDrop.ItemData item = decoded.GetAllItems()[0].Clone(); item.m_equipped = false;
            ItemDrop.ItemData[] before = destination.GetAllItems().ToArray();
            string[] identities = before.Select(ItemIdentity).ToArray();
            int xCell = -1, yCell = -1; rows = Math.Min(Math.Max(0, rows), destination.GetHeight());
            for (int y = 0; y < rows && xCell < 0; ++y) for (int x = 0; x < destination.GetWidth(); ++x)
                if (!before.Any(v => v.m_gridPos.x == x && v.m_gridPos.y == y)) { xCell = x; yCell = y; break; }
            if (xCell < 0) return false;
            item.m_gridPos = new Vector2i(xCell, yCell); string expected = ItemIdentity(item); Exception failure = null;
            bool reported = false;
            try { reported = (bool)NativeAdd.Invoke(destination, new object[] { item, item.m_stack, xCell, yCell, false }); }
            catch (Exception e) { failure = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e; }
            try
            {
            ItemDrop.ItemData[] after = destination.GetAllItems().ToArray();
            var added = after.Where(v => !before.Any(prior => System.Object.ReferenceEquals(prior, v))).ToArray();
            bool unchanged = BeforeUnchanged(before, identities, after);
            // Equipment callbacks can legitimately relocate/equip the inserted
            // stack. Compare complete item data with slot bookkeeping normalized.
            if (unchanged && after.Length == before.Length + 1 && added.Length == 1 && ItemIdentity(added[0]) == expected)
                return true; // Also succeeds if Changed callbacks threw after mutation.
            if (unchanged && added.Length == 0 && after.Length == before.Length)
            {
                if (failure != null) throw new CustodyInsertionException("Custody insertion failed before any inventory change.", false, failure);
                if (!reported) return false;
                throw new CustodyInsertionException("Native insertion reported success without inserting the item.", false, null);
            }
            if (unchanged)
            {
                // Roll back only newly introduced objects. Existing player item
                // references (including equipped objects) are never replaced.
                foreach (ItemDrop.ItemData extra in added)
                {
                    try { destination.RemoveItem(extra); }
                    catch { if (destination.GetAllItems().Any(v => System.Object.ReferenceEquals(v, extra))) break; }
                }
                ItemDrop.ItemData[] restored = destination.GetAllItems().ToArray();
                if (restored.Length == before.Length && BeforeUnchanged(before, identities, restored))
                    throw new CustodyInsertionException("Custody insertion changed the item; newly added objects were rolled back.", false, failure);
            }
            throw new CustodyInsertionException("Custody insertion has an ambiguous effect. Preserve the grant receipt and require host recovery before retrying.", true, failure);
            }
            catch (CustodyInsertionException) { throw; }
            catch (Exception e) { throw new CustodyInsertionException("Custody insertion post-state cannot be verified. Require host recovery before retrying.", true, e); }
        }
        private static bool BeforeUnchanged(ItemDrop.ItemData[] before, string[] identities, ItemDrop.ItemData[] after)
        {
            for (int i = 0; i < before.Length; ++i)
                if (!after.Any(v => System.Object.ReferenceEquals(v, before[i])) || ItemIdentity(before[i]) != identities[i]) return false;
            return true;
        }

        public static void RequireEmptyChests(ZDO[] chests)
        {
            RequireFour(chests);
            foreach (ZDO chest in chests)
            { byte[] bytes = chest.GetByteArray(ZDOVars.s_items, null); if (bytes != null && Count(bytes) != 0) throw new InvalidOperationException("Collect the previous belongings from all four custody chests first."); }
        }
        // Writing is allowed solely after the protected empty character save is
        // durable. A partially applied Cleared deposit resumes by matching each
        // chest's token and original hash; it never overwrites changed contents.
        public static void Deposit(ZDO[] chests, CustodyRecord record, int width, int height)
        {
            RequireHost(); RequireFour(chests);
            if (record == null || record.Closed || record.NeedsRecovery || record.PublicAccess || record.Stage != CustodyStage.Cleared)
                throw new InvalidOperationException("Custody deposit requires durable, unreleased confiscation.");
            byte[][] payloads = PrepareChestPayloads(record.OriginalPayload, width, height);
            for (int i = 0; i < 4; ++i)
            {
                ZDO chest = chests[i]; string assigned = chest.GetString(TokenKey, ""); byte[] current = chest.GetByteArray(ZDOVars.s_items, null);
                if (assigned == record.SentenceId)
                {
                    if (chest.GetString(OwnerKey, "") != record.AccountId || chest.GetString(HashKey, "") != Fingerprint(payloads[i])
                        || chest.GetBool(ReleasedKey, false) || current == null) throw new InvalidDataException("Partial custody chest does not match its journal.");
                    RequireEquivalent(Decode(payloads[i]).GetAllItems(), Decode(current).GetAllItems());
                }
                else if (current != null && Count(current) != 0) throw new InvalidOperationException("Custody chest is occupied; belongings preserved.");
            }
            for (int i = 0; i < 4; ++i)
            {
                ZDO chest = chests[i]; if (chest.GetString(TokenKey, "") == record.SentenceId) continue;
                chest.SetOwner(ZNet.GetUID()); chest.Persistent = true;
                chest.Set(OwnerKey, record.AccountId); chest.Set(TokenKey, record.SentenceId); chest.Set(ReleasedKey, false); chest.Set(PublicKey, false);
                chest.Set(HashKey, Fingerprint(payloads[i])); chest.Set(ZDOVars.s_items, payloads[i]);
                ReloadLoadedChest(chest);
            }
            if (!ChestsMatch(chests, record, width, height)) throw new InvalidDataException("Custody chest deposit verification failed.");
        }
        public static bool ChestsMatch(ZDO[] chests, CustodyRecord record, int width, int height)
        {
            RequireFour(chests); if (record == null) return false;
            byte[][] expected = PrepareChestPayloads(record.OriginalPayload, width, height);
            for (int i = 0; i < 4; ++i)
            {
                ZDO chest = chests[i]; byte[] current = chest.GetByteArray(ZDOVars.s_items, null);
                if (chest.GetString(TokenKey, "") != record.SentenceId || chest.GetString(OwnerKey, "") != record.AccountId
                    || chest.GetString(HashKey, "") != Fingerprint(expected[i]) || current == null) return false;
                try { RequireEquivalent(Decode(expected[i]).GetAllItems(), Decode(current).GetAllItems()); }
                catch (InvalidDataException) { return false; }
            }
            return true;
        }
        public static bool AllChestsEmpty(ZDO[] chests, string token)
        {
            RequireFour(chests); CustodyStore.Token(token);
            foreach (ZDO chest in chests)
            { byte[] bytes = chest.GetByteArray(ZDOVars.s_items, null); if (chest.GetString(TokenKey, "") != token || bytes == null || Count(bytes) != 0) return false; }
            return true;
        }
        public static void SetReleased(ZDO[] chests, string account, string token)
        {
            RequireHost(); RequireFour(chests); SentencePolicy.RequireAccountId(account); CustodyStore.Token(token);
            foreach (ZDO chest in chests)
                if (chest.GetString(OwnerKey, "") != account || chest.GetString(TokenKey, "") != token)
                    throw new InvalidDataException("Custody release account/token mismatch.");
            foreach (ZDO chest in chests) chest.Set(ReleasedKey, true);
        }
        public static string[] ChestIdentities(ZDO[] chests)
        { RequireFour(chests); return chests.Select(c => c.m_uid.ToString()).ToArray(); }
        public static bool IsPublic(ZDO chest) { return chest != null && chest.GetBool(PublicKey, false); }
        public static void SetPublic(ZDO[] chests, bool value)
        {
            RequireHost(); RequireFour(chests);
            foreach (ZDO chest in chests)
                if (chest.GetBool(PublicKey, false) != value) { chest.SetOwner(ZNet.GetUID()); chest.Set(PublicKey, value); }
        }
        public static void SetNativeItems(ZDO chest, byte[] payload)
        {
            RequireHost(); if (chest == null) throw new ArgumentNullException("chest"); Decode(payload);
            chest.SetOwner(ZNet.GetUID()); chest.Persistent = true; chest.Set(ZDOVars.s_items, payload); ReloadLoadedChest(chest);
        }
        public static bool Equivalent(byte[] expected, byte[] actual)
        {
            try { RequireEquivalent(Decode(expected).GetAllItems(), Decode(actual).GetAllItems()); return true; }
            catch (InvalidDataException) { return false; }
        }
        public static bool EquivalentForAdmission(byte[] expectedPayload, byte[] actualPayload)
        {
            try
            {
                List<ItemDrop.ItemData> expected = Decode(expectedPayload).GetAllItems();
                List<ItemDrop.ItemData> actual = Decode(actualPayload).GetAllItems();
                var unmatchedTorches = expected.Where(IsNativeTorch).ToList();
                foreach (ItemDrop.ItemData item in actual)
                {
                    if (!IsNativeTorch(item)) continue;
                    float maximum = item.GetMaxDurability();
                    if (!FiniteDurability(maximum) || !FiniteDurability(item.m_durability) || item.m_durability < 0 || item.m_durability > maximum) return false;
                    string identity = TorchIdentity(item); ItemDrop.ItemData matched = null;
                    foreach (ItemDrop.ItemData prior in unmatchedTorches)
                    {
                        float priorMaximum = prior.GetMaxDurability();
                        if (!FiniteDurability(priorMaximum) || !FiniteDurability(prior.m_durability) || prior.m_durability < 0
                            || prior.m_durability > priorMaximum || item.m_durability > prior.m_durability || TorchIdentity(prior) != identity) continue;
                        // Least sufficient original wear handles multiple
                        // otherwise identical torches without borrowing wear.
                        if (matched == null || prior.m_durability < matched.m_durability) matched = prior;
                    }
                    if (matched == null) return false;
                    item.m_durability = matched.m_durability; unmatchedTorches.Remove(matched);
                }
                if (unmatchedTorches.Count != 0) return false;
                // Only detached native Decode results were normalized. The
                // immutable backup and actual player inventory remain untouched.
                RequireEquivalent(expected, actual); return true;
            }
            catch (Exception) { return false; }
        }
        private static bool IsNativeTorch(ItemDrop.ItemData item)
        { return item != null && item.m_dropPrefab != null && item.m_dropPrefab.name == "Torch"; }
        private static bool FiniteDurability(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static string TorchIdentity(ItemDrop.ItemData source)
        { ItemDrop.ItemData detached = source.Clone(); detached.m_durability = 0; return ItemIdentity(detached); }
        internal static void RegisterContainer(Container container)
        {
            if (container == null || ContainerInventory == null) return;
            Inventory inventory = ContainerInventory.GetValue(container) as Inventory;
            if (inventory == null) return;
            Container registered;
            if (InventoryContainers.TryGetValue(inventory, out registered) && System.Object.ReferenceEquals(registered, container)) return;
            InventoryContainers.Remove(inventory);
            InventoryContainers.Add(inventory, container);
        }
        internal static bool IsCustodyContainer(Container container)
        {
            // Lifecycle/access prefixes register first, then share the exact
            // native-view classification for masking and load scope.
            return container != null && ContainerInventory != null
                && ChestForInventory(ContainerInventory.GetValue(container) as Inventory) != null;
        }
        public static ZDO ChestForInventory(Inventory inventory)
        {
            if (inventory == null) return null;
            Container container;
            if (!InventoryContainers.TryGetValue(inventory, out container)) return null;
            if (container == null || ContainerInventory == null || !System.Object.ReferenceEquals(ContainerInventory.GetValue(container), inventory))
            { InventoryContainers.Remove(inventory); return null; }
            // Native containers can resolve their view through m_rootObjectOverride.
            ZNetView view = ContainerView == null ? null : ContainerView.GetValue(container) as ZNetView;
            return view != null && view.IsValid() && view.GetZDO().GetBool("VMP_PP_Custody", false)
                && !view.GetZDO().GetBool(PublicKey, false) ? view.GetZDO() : null;
        }
        private static void RequireFour(ZDO[] chests)
        {
            if (chests == null || chests.Length != 4 || chests.Any(c => c == null) || chests.Select(c => c.m_uid).Distinct().Count() != 4)
                throw new InvalidDataException("Four distinct native custody chests are required.");
            for (int i = 0; i < 4; ++i)
                if (!chests[i].GetBool("VMP_PP_Custody", false) || chests[i].GetInt("VMP_PP_CustodyIndex", -1) != i)
                    throw new InvalidDataException("Custody chests must retain their permanent ordered markers.");
        }
        private static void ReloadLoadedChest(ZDO chest)
        {
            if (ZNetScene.instance == null || ContainerLoad == null) return;
            GameObject instance = ZNetScene.instance.FindInstance(chest.m_uid);
            Container container = instance == null ? null : instance.GetComponent<Container>();
            if (container != null) ContainerLoad.Invoke(container, null);
        }
        private static void RequireHost()
        { if (ZNet.instance == null || !ZNet.instance.IsServer()) throw new UnauthorizedAccessException("Only the host may write custody chests."); }
    }
}
