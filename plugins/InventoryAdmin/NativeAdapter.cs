using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    public sealed class CapturedInventory
    {
        public InventoryView View;
        internal Player Player;
        internal Inventory Root;
        internal readonly Dictionary<string, NativeEntry> Entries = new Dictionary<string, NativeEntry>();
    }
    internal sealed class NativeEntry
    {
        internal Inventory Inventory;
        internal ItemDrop.ItemData Item, Backpack;
        internal int X, Y;
        internal bool Equipped;
    }
    public static class NativeAdapter
    {
        private static readonly string[] EquipmentKeys = { "eaqs_slot", "eaqs_player", "eaqs_parked", "eaqs_weaponshield" };
        private static readonly Type BackpackApi = AccessTools.TypeByName("AdventureBackpacks.API.ABAPI");
        private static readonly MethodInfo BackpackInventory = BackpackApi == null ? null : AccessTools.Method(BackpackApi, "TryGetBackpackInventory");
        private static readonly Type ItemExtensions = AccessTools.TypeByName("Vapok.Common.Managers.ItemExtensions");
        private static readonly Type BackpackComponent = AccessTools.TypeByName("AdventureBackpacks.Components.BackpackComponent");
        private static readonly MethodInfo Data = ItemExtensions == null ? null : AccessTools.Method(ItemExtensions, "Data", new[] { typeof(ItemDrop.ItemData) });
        private static Inventory Bag(ItemDrop.ItemData item)
        {
            if (BackpackInventory == null) return null;
            object[] args = { item, null };
            return (bool)BackpackInventory.Invoke(null, args) ? (Inventory)args[1] : null;
        }
        private static void SyncBag(ItemDrop.ItemData item)
        {
            Inventory bag = Bag(item);
            if (bag == null) return;
            if (Data == null || BackpackComponent == null) throw new NotSupportedException("Backpack persistence API unavailable.");
            object extensions = Data.Invoke(null, new object[] { item });
            MethodInfo generic = extensions.GetType().GetMethods().First(m => m.Name == "GetOrCreate" && m.IsGenericMethodDefinition
                && m.GetParameters().Length == 1);
            object component = generic.MakeGenericMethod(BackpackComponent).Invoke(extensions, new object[] { "" });
            component.GetType().GetMethod("Serialize", Type.EmptyTypes).Invoke(component, null);
        }
        public static bool Safe(Player player)
        {
            if (player == null || player.IsDead() || player.IsTeleporting() || player.InCutscene()) return false;
            if (InventoryGui.instance != null && InventoryGui.IsVisible()) return false;
            Type patches = AccessTools.TypeByName("AdventureBackpacks.Patches.InventoryGuiPatches");
            FieldInfo opened = patches == null ? null : patches.GetField("BackpackIsOpen", BindingFlags.Static | BindingFlags.Public);
            if (opened != null && (bool)opened.GetValue(null)) return false;
            return true;
        }
        public static int VisibleRows(Inventory inventory)
        {
            if (!Chainloader.PluginInfos.ContainsKey("randyknapp.mods.equipmentandquickslots")) return inventory.GetHeight();
            Type api = AccessTools.TypeByName("EquipmentAndQuickSlots.API");
            if (api == null) throw new NotSupportedException("EAQS slot API unavailable.");
            return (int)api.GetMethod("GetVisibleRows", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }
        public static CapturedInventory BuildView(Player player, long peer, string owner, long character, string requestId)
        {
            if (player == null || player.GetPlayerID() != character || player.IsDead()) throw new InvalidOperationException("Character is unavailable.");
            CapturedInventory result = BuildView(player.GetInventory(), VisibleRows(player.GetInventory()), peer, owner, character, requestId);
            result.Player = player; return result;
        }
        public static CapturedInventory BuildView(Inventory inventory, int visibleRows, long peer, string owner, long character, string requestId)
        {
            var result = new CapturedInventory { Root = inventory, View = new InventoryView { RequestId = requestId, TargetPeerId = peer,
                TargetOwner = owner, TargetCharacter = character, Revision = DateTime.UtcNow.Ticks } };
            var visited = new HashSet<Inventory>();
            Capture(result, inventory, visibleRows, "inventory", null, visited);
            return result;
        }
        private static void Capture(CapturedInventory result, Inventory inventory, int rows, string group, ItemDrop.ItemData parent, HashSet<Inventory> visited)
        {
            if (!visited.Add(inventory)) throw new InvalidDataException("Recursive backpack inventory.");
            foreach (ItemDrop.ItemData item in inventory.GetAllItems().ToArray())
            {
                if (item == null || item.m_dropPrefab == null || item.m_stack < 1) throw new InvalidDataException("Invalid inventory item.");
                if (result.View.Items.Count >= InventoryCodec.MaximumItems) throw new InvalidDataException("Inventory item limit exceeded.");
                Inventory bag = Bag(item); if (bag != null) SyncBag(item);
                string id = Guid.NewGuid().ToString("N");
                result.Entries.Add(id, new NativeEntry { Inventory = inventory, Item = item, Backpack = parent,
                    X = item.m_gridPos.x, Y = item.m_gridPos.y, Equipped = item.m_equipped });
                result.View.Items.Add(new ItemInfo { SlotId = id, Fingerprint = Fingerprint(item), Prefab = item.m_dropPrefab.name,
                    Name = item.m_shared.m_name, Stack = item.m_stack, Quality = item.m_quality, Variant = item.m_variant,
                    X = item.m_gridPos.x, Y = item.m_gridPos.y, Durability = item.m_durability, Equipped = item.m_equipped,
                    Container = parent != null ? group : item.m_gridPos.y >= rows ? "equipment" : "inventory" });
                // Bags are viewed one level deep. A modded bag inside a bag is refused rather than guessed.
                if (bag != null && parent == null) Capture(result, bag, bag.GetHeight(), "backpack:" + item.m_dropPrefab.name, item, visited);
                else if (bag != null) throw new NotSupportedException("Nested backpacks are not supported.");
            }
        }
        public static string Fingerprint(ItemDrop.ItemData item)
        {
            var package = new ZPackage(); item.Save(package);
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(package.GetArray())).Replace("-", "").ToLowerInvariant();
        }
        private static NativeEntry Validate(CapturedInventory view, string id, string fingerprint, int count)
        {
            NativeEntry entry;
            if (view == null || !view.Entries.TryGetValue(id, out entry) || !entry.Inventory.ContainsItem(entry.Item)) throw new InvalidOperationException("Inventory changed. Refresh the view.");
            if (entry.Backpack != null && !view.Root.ContainsItem(entry.Backpack)) throw new InvalidOperationException("Backpack moved. Refresh the view.");
            if (entry.Backpack != null && !ReferenceEquals(Bag(entry.Backpack), entry.Inventory)) throw new InvalidOperationException("Backpack inventory changed. Refresh the view.");
            if (entry.Backpack != null) SyncBag(entry.Backpack); SyncBag(entry.Item);
            if (entry.Item.m_gridPos.x != entry.X || entry.Item.m_gridPos.y != entry.Y || entry.Item.m_equipped != entry.Equipped
                || Fingerprint(entry.Item) != fingerprint || count < 1 || count > entry.Item.m_stack)
                throw new InvalidOperationException("Inventory changed. Refresh the view.");
            if (view.Player != null && !Safe(view.Player)) throw new InvalidOperationException("Player is busy, dead or teleporting. Try again.");
            return entry;
        }
        public static byte[] Prepare(CapturedInventory view, string id, string fingerprint, int count)
        {
            NativeEntry entry = Validate(view, id, fingerprint, count);
            var clone = entry.Item.Clone(); clone.m_stack = count; clone.m_equipped = false; clone.m_gridPos = new Vector2i(0, 0);
            foreach (string key in EquipmentKeys) clone.m_customData.Remove(key);
            var inventory = new Inventory("InventoryAdmin.Escrow", null, 1, 1);
            inventory.GetAllItems().Add(clone);
            var package = new ZPackage(); inventory.Save(package); byte[] bytes = package.GetArray();
            if (bytes.Length > InventoryCodec.MaximumItemBytes) throw new InvalidDataException("Item data exceeds transfer limit.");
            // Validate the exact native representation before removing anything.
            ItemDrop.ItemData decoded = ReadBlob(bytes);
            if (decoded.m_stack != count || decoded.m_dropPrefab.name != clone.m_dropPrefab.name) throw new InvalidDataException("Item round trip failed.");
            return bytes;
        }
        public static void Remove(CapturedInventory view, string id, string fingerprint, int count)
        {
            NativeEntry entry = Validate(view, id, fingerprint, count);
            if (view.Player != null && entry.Backpack == null)
            {
                view.Player.RemoveEquipAction(entry.Item);
                if (entry.Item.m_equipped) view.Player.UnequipItem(entry.Item, false);
            }
            int expected = entry.Item.m_stack - count;
            try
            {
                if (!entry.Inventory.RemoveItem(entry.Item, count)) throw new InvalidOperationException("Native item removal refused.");
            }
            catch
            {
                // Changed callbacks may throw after the native mutation. Report a
                // successful effect if the exact expected post-state exists.
                if (expected == 0 ? entry.Inventory.ContainsItem(entry.Item) : !entry.Inventory.ContainsItem(entry.Item) || entry.Item.m_stack != expected) throw;
            }
            if (entry.Backpack != null) SyncBag(entry.Backpack);
            view.Entries.Remove(id);
        }
        public static ItemDrop.ItemData ReadBlob(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > InventoryCodec.MaximumItemBytes) throw new InvalidDataException("Invalid native item bytes.");
            var inventory = new Inventory("InventoryAdmin.Escrow", null, 1, 1); var package = new ZPackage(bytes); inventory.Load(package);
            var items = inventory.GetAllItems();
            if (package.GetPos() != package.Size() || items.Count != 1 || items[0].m_stack <= 0 || items[0].m_dropPrefab == null)
                throw new InvalidDataException("Native item payload must contain exactly one valid item.");
            return items[0];
        }
        private static Vector2i Cell(Inventory destination, int rows)
        {
            rows = Math.Min(rows, destination.GetHeight());
            for (int y = 0; y < rows; ++y) for (int x = 0; x < destination.GetWidth(); ++x)
                if (destination.GetItemAt(x, y) == null) return new Vector2i(x, y);
            return new Vector2i(-1, -1);
        }
        public static bool CanAdd(Inventory destination, int rows, byte[] bytes)
        { ItemDrop.ItemData item = ReadBlob(bytes); return item.m_stack <= item.m_shared.m_maxStackSize && Cell(destination, rows).x >= 0; }
        public static bool CanAdd(Player player, byte[] bytes)
        { return Safe(player) && CanAdd(player.GetInventory(), VisibleRows(player.GetInventory()), bytes); }
        public static void Add(Inventory destination, int rows, byte[] bytes)
        {
            ItemDrop.ItemData item = ReadBlob(bytes); Vector2i cell = Cell(destination, rows);
            if (cell.x < 0 || item.m_stack > item.m_shared.m_maxStackSize) throw new InvalidOperationException("No free ordinary inventory slot for this stack.");
            int count = item.m_stack;
            item.m_gridPos = cell; string expected = Fingerprint(item);
            MethodInfo add = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            if (add == null) throw new MissingMethodException("Inventory exact-cell insertion API unavailable.");
            bool result;
            try { result = (bool)add.Invoke(destination, new object[] { item, count, cell.x, cell.y, false }); }
            catch (TargetInvocationException)
            {
                ItemDrop.ItemData actual = destination.GetItemAt(cell.x, cell.y);
                if (actual == null || Fingerprint(actual) != expected) throw;
                result = true;
            }
            ItemDrop.ItemData added = destination.GetItemAt(cell.x, cell.y);
            if (!result || added == null || added.m_stack != count || Fingerprint(added) != expected) throw new InvalidOperationException("Native destination insertion did not complete exactly.");
        }
        public static void Add(Player player, byte[] bytes)
        {
            if (!Safe(player)) throw new InvalidOperationException("Receiving player is busy, dead or teleporting.");
            Add(player.GetInventory(), VisibleRows(player.GetInventory()), bytes);
        }
    }
}
