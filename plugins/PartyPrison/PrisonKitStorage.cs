using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using NativeInventory = ValheimModPack.WorldCharacters.NativeInventory;
using StoredItem = ValheimModPack.WorldCharacters.StoredItem;

namespace ValheimModPack.PartyPrison
{
    /// <summary>Strict, short-lived transaction for a real equipment/food change, never a periodic scan.</summary>
    public sealed class PrisonKitStorage
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo Loading = typeof(Container).GetField("m_loading", Fields);
        private static readonly FieldInfo LastRevision = typeof(Container).GetField("m_lastRevision", Fields);
        private static readonly FieldInfo NativeView = typeof(Container).GetField("m_nview", Fields);
        private static readonly MethodInfo SaveContainer = typeof(Container).GetMethod("Save", Fields, null, Type.EmptyTypes, null);
        private readonly ZDO zdo;
        private readonly int width, height;
        private readonly byte[] raw;
        private readonly string legacy;
        private readonly byte[] original;

        public PrisonKitStorage(ZDO zdo, Inventory inventory)
        {
            if (zdo == null || inventory == null) throw new ArgumentNullException("equipment storage");
            this.zdo = zdo; width = inventory.GetWidth(); height = inventory.GetHeight();
            byte[] saved = zdo.GetByteArray(ZDOVars.s_items, null);
            raw = saved == null ? null : (byte[])saved.Clone();
            legacy = zdo.GetString(ZDOVars.s_items, "");
            if (raw != null) original = raw;
            else if (legacy.Length != 0) {
                try { original = Convert.FromBase64String(legacy); }
                catch (FormatException error) { throw new InvalidDataException("Invalid legacy equipment inventory; saved items are protected.", error); }
            }
            // This is deliberately before Container.Load, which silently skips
            // missing prefabs and marks the shortened inventory as loaded.
            if (original != null) Validate(original, width, height);
        }

        public Inventory WorkingCopy(Inventory inventory)
        {
            RequireDimensions(inventory);
            byte[] loaded = Save(inventory); Validate(loaded, width, height);
            if (raw != null || original != null && inventory.GetAllItems().Count != 0) RequireSame(original, loaded);
            byte[] source = original ?? loaded;
            var candidate = new Inventory("PartyPrison.KitReplacement", null, width, height);
            candidate.Load(new ZPackage(source)); RequireSame(source, Save(candidate));
            return candidate;
        }

        public void Publish(Container container, Inventory candidate)
        {
            if (container == null || Loading == null || LastRevision == null || NativeView == null || SaveContainer == null)
                throw new MissingMemberException("Native equipment container transaction ABI is unavailable.");
            ZNetView view = NativeView.GetValue(container) as ZNetView;
            if (view == null || !view.IsValid() || !ReferenceEquals(view.GetZDO(), zdo))
                throw new InvalidDataException("Equipment container backing identity changed; saved items are protected.");
            Inventory inventory = container.GetInventory(); RequireDimensions(inventory); RequireDimensions(candidate);
            PublishCore(inventory, candidate, action => {
                bool previous = (bool)Loading.GetValue(container);
                Loading.SetValue(container, true);
                try { action(); } finally { Loading.SetValue(container, previous); }
            }, () => SaveContainer.Invoke(container, null), reload => LastRevision.SetValue(container, reload ? zdo.DataRevision ^ UInt32.MaxValue : zdo.DataRevision));
        }

        // The delegate seam also exercises failures without creating any native
        // world, ownership or client RPC object in the isolated menu fixture.
        public void PublishCore(Inventory inventory, Inventory candidate, Action<Action> withoutCallbacks, Action persist, Action<bool> syncRevision)
        {
            RequireDimensions(inventory); RequireDimensions(candidate);
            if (withoutCallbacks == null || persist == null) throw new ArgumentNullException("native container callbacks");
            byte[] before = Save(inventory), replacement = Save(candidate);
            Validate(before, width, height); Validate(replacement, width, height);
            // A silent loss while constructing the candidate must be detected
            // before its bytes reach the live chest or the backing ZDO.
            var proof = new Inventory("PartyPrison.KitProof", null, width, height);
            proof.Load(new ZPackage(replacement)); RequireSame(replacement, Save(proof));
            try {
                withoutCallbacks(() => {
                    inventory.Load(new ZPackage(replacement)); RequireSame(replacement, Save(inventory));
                });
                persist();
                byte[] written = zdo.GetByteArray(ZDOVars.s_items, null);
                if (written == null) throw new InvalidDataException("Equipment inventory was not persisted.");
                RequireSame(replacement, written);
                if (syncRevision != null) syncRevision(false);
            }
            catch (Exception failure) {
                Exception rollbackFailure = null;
                try { withoutCallbacks(() => { inventory.Load(new ZPackage(before)); RequireSame(before, Save(inventory)); }); }
                catch (Exception error) { rollbackFailure = error; }
                finally {
                    // Restore the exact durable bytes even if a vendor callback
                    // rejected the local reload. Never publish a shortened save.
                    if (raw == null) ZDOExtraData.RemoveByteArray(zdo.m_uid, ZDOVars.s_items);
                    else zdo.Set(ZDOVars.s_items, (byte[])raw.Clone());
                    if (legacy.Length == 0) ZDOExtraData.RemoveString(zdo.m_uid, ZDOVars.s_items);
                    else zdo.Set(ZDOVars.s_items, legacy);
                    if (syncRevision != null) syncRevision(rollbackFailure != null);
                }
                if (rollbackFailure != null) throw new AggregateException("Equipment change failed; original saved contents remain protected, but local inventory recovery requires reloading the chest.", failure, rollbackFailure);
                throw;
            }
        }

        private void RequireDimensions(Inventory inventory)
        {
            if (inventory == null || inventory.GetWidth() != width || inventory.GetHeight() != height)
                throw new InvalidDataException("Equipment inventory dimensions changed during replacement.");
        }

        private static void Validate(byte[] bytes, int width, int height)
        {
            Inventory decoded = CustodyInventory.Decode(bytes);
            foreach (ItemDrop.ItemData item in decoded.GetAllItems())
                if (item.m_gridPos.x < 0 || item.m_gridPos.y < 0 || item.m_gridPos.x >= width || item.m_gridPos.y >= height)
                    throw new InvalidDataException("Equipment inventory has hidden or invalid slots; saved items are protected.");
        }

        private static List<StoredItem> Read(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream)) {
                List<StoredItem> items = NativeInventory.ReadInventory(reader);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing equipment inventory data.");
                return items;
            }
        }

        private static void RequireSame(byte[] expected, byte[] actual)
        { NativeInventory.RequireSameItems(Read(expected), Read(actual)); }

        private static byte[] Save(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }
    }
}
