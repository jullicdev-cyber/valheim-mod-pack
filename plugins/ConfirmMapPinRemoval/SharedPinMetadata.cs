using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    internal sealed class SharedPinMetadata : IDisposable
    {
        private const string RpcName = "VMP_PinMetadata_1", DataKey = "vmp_pin_metadata_1";
        private static readonly FieldInfo ViewField = AccessTools.Field(typeof(MapTable), "m_nview");
        private static SharedPinMetadata active;
        private readonly Action<Exception> report;
        private readonly Dictionary<MapTable, Receipt> receipts = new Dictionary<MapTable, Receipt>();
        private readonly Dictionary<MapTable, byte[]> writes = new Dictionary<MapTable, byte[]>();
        private bool disposed;
        private sealed class Receipt
        {
            internal long Sender, World;
            internal byte[] Hash;
            internal HashSet<string> Present;
            internal float Received;
        }
        internal SharedPinMetadata(Harmony harmony, Action<Exception> reporter)
        {
            report = reporter;
            if (ViewField == null) throw new MissingFieldException("MapTable.m_nview");
            Patch(harmony, "Start", Type.EmptyTypes, null, "Register");
            Patch(harmony, "OnWrite", new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "BeforeWrite", "AfterWrite");
            Patch(harmony, "GetMapData", new[] { typeof(byte[]) }, null, "CaptureWrite");
            Patch(harmony, "RPC_MapData", new[] { typeof(long), typeof(ZPackage) }, null, "MapReceived");
            Patch(harmony, "OnRead", new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool) }, null, "AfterRead");
            active = this;
        }
        private static void Patch(Harmony harmony, string method, Type[] args, string before, string after)
        {
            MethodInfo target = AccessTools.Method(typeof(MapTable), method, args);
            if (target == null) throw new MissingMethodException("MapTable." + method);
            harmony.Patch(target, prefix: before == null ? null : new HarmonyMethod(typeof(SharedPinMetadata), before),
                postfix: after == null ? null : new HarmonyMethod(typeof(SharedPinMetadata), after));
        }
        private static void Register(MapTable __instance)
        {
            SharedPinMetadata service = active;
            if (service == null || service.disposed) return;
            try
            {
                ZNetView view = View(__instance); if (view == null || !view.IsValid()) return;
                MapTable table = __instance;
                view.Register<ZPackage>(RpcName, (sender, package) => service.Receive(table, sender, package));
            }
            catch (Exception error) { service.report(error); }
        }
        private static void BeforeWrite(MapTable __instance) { if (active != null) active.writes.Remove(__instance); }
        private static void CaptureWrite(MapTable __instance, ZPackage __result)
        {
            if (active == null || __result == null) return;
            try { active.writes[__instance] = SharedPinCodec.Hash(__result.GetArray()); }
            catch (Exception error) { active.report(error); }
        }
        private static void AfterWrite(MapTable __instance, Humanoid __1, ItemDrop.ItemData __2)
        {
            SharedPinMetadata service = active; if (service == null) return;
            byte[] hash; if (!service.writes.TryGetValue(__instance, out hash)) return;
            service.writes.Remove(__instance);
            try
            {
                if (!LocalAccess(__instance, __1, __2)) return;
                var records = PinHistoryController.ExportSharedMetadata();
                if (records.Count == 0) return;
                var payload = new SharedPinCodec.Payload { World = ZNet.instance.GetWorldUID(), MapHash = hash, Records = records };
                var package = new ZPackage(SharedPinCodec.Encode(payload));
                View(__instance).InvokeRPC(RpcName, new object[] { package });
            }
            catch (Exception error) { service.report(error); }
        }
        private static void MapReceived(MapTable __instance, long __0, ZPackage __1)
        {
            SharedPinMetadata service = active; if (service == null || service.disposed) return;
            try
            {
                ZNetView view = View(__instance);
                if (view == null || !view.IsValid() || !view.IsOwner() || ZNet.instance == null) return;
                byte[] current = view.GetZDO().GetByteArray("data", null);
                if (current == null || !SharedPinCodec.SameHash(SharedPinCodec.Hash(current), SharedPinCodec.Hash(__1.GetArray()))) return;
                var receipt = new Receipt { Sender = __0, World = ZNet.instance.GetWorldUID(), Hash = SharedPinCodec.Hash(current),
                    Present = SharedPinCodec.ReadVanillaKeys(current), Received = Time.realtimeSinceStartup };
                service.Prune();
                if (service.receipts.Count >= 128 && !service.receipts.ContainsKey(__instance)) return;
                service.receipts[__instance] = receipt;
                // Preserve metadata from previous writers only for pins that survived the actual native update.
                var old = Read(view);
                var kept = old != null && old.World == receipt.World ? SharedPinCodec.Merge(old.Records, new PinRecord[0], receipt.Present) : new List<PinRecord>();
                Write(view, new SharedPinCodec.Payload { World = receipt.World, MapHash = receipt.Hash, Records = kept });
            }
            catch (Exception error) { service.report(error); }
        }
        private void Receive(MapTable table, long sender, ZPackage package)
        {
            if (disposed || table == null || package == null || package.Size() > SharedPinCodec.MaximumBytes) return;
            try
            {
                Prune(); Receipt receipt; ZNetView view = View(table);
                if (view == null || !view.IsValid() || !view.IsOwner() || !receipts.TryGetValue(table, out receipt)
                    || sender != receipt.Sender || Time.realtimeSinceStartup - receipt.Received > 15f || ZNet.instance == null) return;
                var incoming = SharedPinCodec.Decode(package.GetArray());
                byte[] current = view.GetZDO().GetByteArray("data", null);
                if (incoming.World != receipt.World || incoming.World != ZNet.instance.GetWorldUID()
                    || !SharedPinCodec.SameHash(incoming.MapHash, receipt.Hash) || current == null
                    || !SharedPinCodec.SameHash(incoming.MapHash, SharedPinCodec.Hash(current))) return;
                var previous = Read(view);
                var records = SharedPinCodec.Merge(previous == null ? (IEnumerable<PinRecord>)new PinRecord[0] : previous.Records,
                    incoming.Records, receipt.Present);
                Write(view, new SharedPinCodec.Payload { World = receipt.World, MapHash = receipt.Hash, Records = records });
                receipts.Remove(table); // One bounded extension per accepted native map write.
            }
            catch (Exception error) { report(error); }
        }
        private static void AfterRead(MapTable __instance, Humanoid __1, ItemDrop.ItemData __2)
        {
            if (active == null) return;
            try
            {
                if (!LocalAccess(__instance, __1, __2)) return;
                ZNetView view = View(__instance); var payload = Read(view);
                byte[] current = view.GetZDO().GetByteArray("data", null);
                if (payload == null || payload.World != ZNet.instance.GetWorldUID() || current == null
                    || !SharedPinCodec.SameHash(payload.MapHash, SharedPinCodec.Hash(current))) return;
                PinHistoryController.ImportSharedMetadata(payload.Records);
            }
            catch (Exception error) { active.report(error); }
        }
        private static bool LocalAccess(MapTable table, Humanoid human, ItemDrop.ItemData item)
        {
            Player player = Player.m_localPlayer; ZNetView view = View(table);
            return table != null && player != null && ReferenceEquals(human, player) && item == null
                && PinHistoryController.SafePlayer(player) && ZNet.instance != null && view != null && view.IsValid()
                && (table.transform.position - player.transform.position).sqrMagnitude <= 64f
                && PrivateArea.CheckAccess(table.transform.position, 0f, false, false);
        }
        private static SharedPinCodec.Payload Read(ZNetView view)
        {
            byte[] bytes = view.GetZDO().GetByteArray(DataKey, null);
            return bytes == null ? null : SharedPinCodec.Decode(bytes);
        }
        private static void Write(ZNetView view, SharedPinCodec.Payload value)
        { if (view.IsOwner()) view.GetZDO().Set(DataKey, SharedPinCodec.Encode(value)); }
        private static ZNetView View(MapTable table) { return table == null ? null : ViewField.GetValue(table) as ZNetView; }
        private void Prune()
        {
            var stale = new List<MapTable>();
            foreach (var entry in receipts) if (entry.Key == null || Time.realtimeSinceStartup - entry.Value.Received > 15f) stale.Add(entry.Key);
            foreach (MapTable table in stale) receipts.Remove(table);
        }
        internal static PinRecord Canonical(PinRecord pin, long character)
        {
            PinRecord result = pin.Copy(); if (result.Owner == 0) result.Owner = character;
            if (String.IsNullOrEmpty(result.Author) && result.Owner == character)
                result.Author = Splatform.PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.ToString();
            return result;
        }
        public void Dispose() { disposed = true; receipts.Clear(); writes.Clear(); if (ReferenceEquals(active, this)) active = null; }
    }
}
