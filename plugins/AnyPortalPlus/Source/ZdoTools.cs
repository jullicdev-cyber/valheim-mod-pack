// AnyPortal+ fork changes, 2026-10-06. Original XPortal by SpikeHimself; GPL-3.0.
namespace XPortal
{
    internal static class ZdoTools
    {
        public static string GetName(ZDO portalZdo)
        {
            return portalZdo.GetString("tag");
        }

        public static void SetName (ZDO portalZdo, string name)
        {
            portalZdo.Set("tag", name);
        }

        public static void SetOwner(ZDO portalZdo)
        {
            portalZdo.SetOwner(ZDOMan.GetSessionID());
        }

        public static void SetPreviousId(ZDO portalZdo)
        {
            portalZdo.Set(XPortal.Key_PreviousId, portalZdo.m_uid);
        }

        public static void SetTarget(ZDO portalZdo, ZDOID targetId)
        {
            portalZdo.Set(XPortal.Key_TargetId, targetId);
            portalZdo.SetConnection(ZDOExtraData.ConnectionType.Portal, targetId);
        }

        public static void UpdateFromKnownPortal(bool delayed = false, object state = null)
        {
            if (delayed)
            {
                QueuedAction.Queue(UpdateFromKnownPortal, delay: 1, state: state);
                return;
            }

            KnownPortal portal = (KnownPortal)state;
            ZDO portalZdo = ZDOMan.instance.GetZDO(portal.Id);

            if (portalZdo == null)
            {
                Log.Warning("Portal ZDO is no longer available; update cancelled.");
                return;
            }

            SetOwner(portalZdo);
            SetName(portalZdo, portal.Name);
            SetPreviousId(portalZdo);
            SetTarget(portalZdo, portal.Target);
            Plus.PlusPortalMetadata.WriteInto(portalZdo, portal);
        }
    }
}
