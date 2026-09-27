using System;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.ChestSearch
{
    public enum ReadFailure { None, Unsupported, Unavailable, Denied, Busy, Stale }
    public sealed class ChestSnapshot
    {
        public Container Container;
        public ZDOID Id;
        public uint Revision;
        public float Distance;
        public ItemDrop.ItemData[] Items;
    }

    // No Load(), ownership requests, RPCs, container events, or inventory writes.
    // A stale replicated inventory is omitted until vanilla refreshes it.
    public sealed class NativeChestReader
    {
        private readonly FieldInfo revision = RequireField("m_lastRevision", typeof(uint));
        private readonly FieldInfo loading = RequireField("m_loading", typeof(bool));
        private readonly MethodInfo checkAccess = typeof(Container).GetMethod("CheckAccess", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
        public NativeChestReader()
        {
            if (checkAccess == null || checkAccess.ReturnType != typeof(bool)) throw new MissingMethodException("Container.CheckAccess API changed");
        }
        private static FieldInfo RequireField(string name, Type expected)
        {
            var field = typeof(Container).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null || field.FieldType != expected) throw new MissingFieldException("Container." + name + " API changed");
            return field;
        }
        public bool TryRead(Container chest, Player player, float radius, out ChestSnapshot snapshot, out ReadFailure failure)
        {
            snapshot = null; failure = ReadFailure.Unavailable;
            try
            {
                if (chest == null || !chest.isActiveAndEnabled || player == null || !ReferenceEquals(player, Player.m_localPlayer)
                    || player.IsDead() || player.GetPlayerID() == 0 || Single.IsNaN(radius) || Single.IsInfinity(radius) || radius <= 0 || radius > 80) return false;
                float distance = Vector3.Distance(player.transform.position, chest.transform.position);
                if (Single.IsNaN(distance) || Single.IsInfinity(distance) || distance > radius) return false;
                failure = ReadFailure.Unsupported;
                Piece piece = chest.GetComponent<Piece>();
                ZNetView view = chest.GetComponent<ZNetView>();
                if (piece == null || !piece.IsPlacedByPlayer() || view == null || !view.IsValid()
                    || chest.m_rootObjectOverride != null || chest.m_wagon != null || chest.m_autoDestroyEmpty
                    || chest.GetComponentInParent<Character>() != null || chest.GetComponentInParent<TombStone>() != null
                    || chest.GetComponentInParent<ItemDrop>() != null || chest.GetComponentInParent<Ship>() != null
                    || chest.GetComponentInParent<Vagon>() != null) return false;
                ZDO zdo = view.GetZDO();
                if (zdo == null || !zdo.IsValid() || ZNetScene.instance == null) return false;
                GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null || !SearchText.StandardChest(prefab.name)) return false;
                failure = ReadFailure.Denied;
                // Always honor the ward, even if another mod weakened m_checkGuardStone.
                if (!PrivateArea.CheckAccess(chest.transform.position, 0f, false, false)
                    || !(bool)checkAccess.Invoke(chest, new object[] { player.GetPlayerID() })) return false;
                failure = ReadFailure.Busy;
                if (chest.IsInUse() || zdo.GetInt(ZDOVars.s_inUse, 0) != 0) return false;
                failure = ReadFailure.Stale;
                if ((bool)loading.GetValue(chest) || zdo.DataRevision == 0 || (uint)revision.GetValue(chest) != zdo.DataRevision) return false;
                Inventory inventory = chest.GetInventory();
                if (inventory == null) return false;
                uint before = zdo.DataRevision;
                var source = inventory.GetAllItems();
                if (source == null || source.Count > 4096) return false;
                var items = source.ToArray();
                if (before != zdo.DataRevision || (uint)revision.GetValue(chest) != before) return false;
                snapshot = new ChestSnapshot { Container = chest, Id = zdo.m_uid, Revision = before, Distance = distance, Items = items };
                failure = ReadFailure.None;
                return true;
            }
            catch { failure = ReadFailure.Unavailable; return false; }
        }
    }
}
