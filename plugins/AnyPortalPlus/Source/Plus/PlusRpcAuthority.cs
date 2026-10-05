// AnyPortal+ additions, 2026-10-06. GPL-3.0, see ../../LICENSE.
using System;
using System.Linq;
using System.IO;

namespace XPortal.Plus
{
    internal static class PlusRpcAuthority
    {
        internal static bool IsServerSender(long sender)
        { return !Environment.IsServer && ZNet.instance != null && ZRoutedRpc.instance != null
            && sender != 0 && sender == Environment.ServerPeerId; }
        internal static bool IsClientSender(long sender)
        {
            if (!Environment.IsServer || ZNet.instance == null || sender == 0) return false;
            if (sender == ZNet.GetUID()) return true;
            return ZNet.instance.GetPeers().Count(p => p.m_uid == sender && p.IsReady() && p.m_rpc != null && p.m_rpc.IsConnected()) == 1;
        }
        internal static ZDO LivePortal(ZDOID id)
        {
            if (ZDOMan.instance == null || id.IsNone()) return null;
            return ZDOMan.instance.GetPortalList().FirstOrDefault(z => z != null && z.IsValid() && z.m_uid == id);
        }
        internal static KnownPortal Canonical(KnownPortal requested, ZDO zdo)
        {
            PlusPortalMetadata.Validate(requested);
            if (zdo == null || zdo.m_uid != requested.Id) throw new InvalidDataException("Portal is unavailable.");
            if (requested.Target == requested.Id || requested.Target != ZDOID.None && LivePortal(requested.Target) == null)
                throw new InvalidDataException("Selected destination is unavailable.");
            var result = new KnownPortal(zdo.m_uid, zdo.GetPosition())
            { Name = requested.Name, PreviousId = zdo.GetZDOID(XPortal.Key_PreviousId), Target = requested.Target, Icon = requested.Icon };
            if (result.CreatedUtcTicks == 0 && requested.CreatedUtcTicks > 0
                && !KnownPortalsManager.Instance.ContainsId(requested.Id)
                && Math.Abs(DateTime.UtcNow.Ticks - requested.CreatedUtcTicks) < TimeSpan.FromMinutes(10).Ticks)
                result.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            return result;
        }
    }
}
