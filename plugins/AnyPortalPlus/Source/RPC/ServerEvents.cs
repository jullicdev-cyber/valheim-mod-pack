// AnyPortal+ fork changes, 2026-10-06. Original by SpikeHimself, GPL-3.0.
using System;
using System.Collections.Generic;
using XPortal.Plus;

namespace XPortal.RPC.Server
{
    internal static class ServerEvents
    {
        private sealed class PendingUpdate { internal long Sender, World; internal ZNet Network; internal KnownPortal Portal; internal int Attempts; }
        private static readonly Dictionary<ZDOID, PendingUpdate> pending = new Dictionary<ZDOID, PendingUpdate>();
        private static readonly Dictionary<long, float> syncRequests = new Dictionary<long, float>();
        internal static void Reset() { pending.Clear(); syncRequests.Clear(); }
        internal static void RPC_SyncRequest(long sender, string reason)
        {
            if (!PlusRpcAuthority.IsClientSender(sender)) return;
            float last, now = UnityEngine.Time.realtimeSinceStartup;
            if (syncRequests.TryGetValue(sender, out last) && now - last < 1) return;
            syncRequests[sender] = now;
            XPortal.ProcessSyncRequest("Portal list requested");
        }
        internal static void RPC_AddOrUpdateRequest(long sender, ZPackage pkg)
        {
            if (!PlusRpcAuthority.IsClientSender(sender)) return;
            try
            {
                KnownPortal requested = new KnownPortal(pkg);
                if (PlusRpcAuthority.LivePortal(requested.Id) == null)
                {
                    if (requested.CreatedUtcTicks <= 0) return;
                    PendingUpdate existing;
                    if (pending.TryGetValue(requested.Id, out existing))
                    {
                        if (existing.Sender == sender) existing.Portal = requested;
                        return;
                    }
                    if (pending.Count >= 32) return;
                    var update = new PendingUpdate { Sender = sender, Network = ZNet.instance, World = ZNet.instance.GetWorldUID(), Portal = requested };
                    pending[requested.Id] = update;
                    QueuedAction.Queue(Retry, delay: 15, state: update); return;
                }
                Apply(requested); pending.Remove(requested.Id);
            }
            catch (Exception error) { Log.Warning("AnyPortal+ rejected portal update: " + error.Message); }
        }
        private static void Retry(bool delayed, object state)
        {
            var update = (PendingUpdate)state; PendingUpdate current;
            if (!pending.TryGetValue(update.Portal.Id, out current) || !ReferenceEquals(current, update)) return;
            if (!ReferenceEquals(update.Network, ZNet.instance) || update.World != ZNet.instance.GetWorldUID()
                || !PlusRpcAuthority.IsClientSender(update.Sender) || ++update.Attempts > 20)
            { pending.Remove(update.Portal.Id); return; }
            if (PlusRpcAuthority.LivePortal(update.Portal.Id) == null)
            { QueuedAction.Queue(Retry, delay: 15, state: update); return; }
            pending.Remove(update.Portal.Id);
            try { Apply(update.Portal); } catch (Exception error) { Log.Warning("Portal placement update cancelled: " + error.Message); }
        }
        private static void Apply(KnownPortal requested)
        {
            KnownPortal portal = PlusRpcAuthority.Canonical(requested, PlusRpcAuthority.LivePortal(requested.Id));
            KnownPortalsManager.Instance.AddOrUpdate(portal); ZdoTools.UpdateFromKnownPortal(state: portal); SendToClient.SyncPortal(portal);
            if (portal.HasTarget())
            {
                ZDO targetZdo = PlusRpcAuthority.LivePortal(portal.Target);
                KnownPortal target = new KnownPortal(targetZdo.m_uid, targetZdo.GetPosition())
                { Name = PlusPortalMetadata.CleanName(targetZdo.GetString("tag")), Target = targetZdo.GetZDOID(XPortal.Key_TargetId),
                    PreviousId = targetZdo.GetZDOID(XPortal.Key_PreviousId) };
                if (!target.HasTarget())
                {
                    target.Target = portal.Id; KnownPortalsManager.Instance.AddOrUpdate(target);
                    ZdoTools.UpdateFromKnownPortal(state: target); SendToClient.SyncPortal(target);
                }
            }
        }
        internal static void RPC_RemoveRequest(long sender, ZDOID portalId)
        {
            if (!PlusRpcAuthority.IsClientSender(sender) || portalId.IsNone()) return;
            ZNet network = ZNet.instance; long world = network.GetWorldUID();
            QueuedAction.Queue((delayed, state) =>
            {
                if (ReferenceEquals(network, ZNet.instance) && world == ZNet.instance.GetWorldUID())
                    XPortal.ProcessSyncRequest("World portal was removed");
            }, delay: 3);
        }
        internal static void RPC_ConfigRequest(long sender)
        {
            if (!PlusRpcAuthority.IsClientSender(sender)) return;
            SendToClient.Config(sender, XPortalConfig.Instance.PackLocalConfig());
        }
    }
}
