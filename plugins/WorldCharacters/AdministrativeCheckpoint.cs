using System;

namespace ValheimModPack.WorldCharacters
{
    // Inventory administration captures through the normal save chain and waits
    // for its existing disk ACK. It never rewrites the session's sequence cursor.
    public sealed partial class Plugin
    {
        public static bool AdministrativeReady
        {
            get { return Instance != null && Instance.Managed && Instance.ready && !Instance.failed && !Instance.closing
                && Player.m_localPlayer != null && !Player.m_localPlayer.IsDead(); }
        }
        public static long RequestAdministrativeSave()
        {
            if (!AdministrativeReady || Instance.saving) throw new InvalidOperationException("Protected character is not ready for an administrative save.");
            long before = Instance.localHost != null ? Instance.localHost.AcceptedSequence : Instance.produced;
            Instance.Publish(false, false, true);
            long after = Instance.localHost != null ? Instance.localHost.AcceptedSequence : Instance.produced;
            if (after != checked(before + 1)) throw new InvalidOperationException("Administrative snapshot was not captured.");
            return after;
        }
        public static bool IsAdministrativeSaveDurable(long sequence)
        {
            if (Instance == null || !Instance.Managed || Instance.failed || sequence <= 0) return false;
            return Instance.localHost != null ? Instance.localHost.LastSequence >= sequence : Instance.acknowledged >= sequence;
        }
        public static long GetAdministrativeDurableSequence(long peerId)
        {
            if (Instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return -1;
            if (peerId == ZNet.GetUID())
                return Instance.localHost != null && Instance.ready && !Instance.failed ? Instance.localHost.LastSequence : -1;
            foreach (Link link in Instance.links.Values)
                if (link.Peer.m_uid == peerId && Connected(link) && link.Session != null && link.Session.Loaded && !link.Session.Closed)
                    return link.Session.LastSequence;
            return -1;
        }
        public static bool IsAdministrativePeerReady(long peerId)
        { return GetAdministrativeDurableSequence(peerId) >= 0; }
        // ServerSync can replace peer.m_socket after the Steam handshake. Read
        // the identity approved for this exact RPC, never a claimed peer UID.
        public static string GetAdministrativeOwner(ZNetPeer peer)
        {
            if (Instance == null || Instance.failed || Instance.closing || ZNet.instance == null || !ZNet.instance.IsServer()
                || peer == null || peer.m_rpc == null || peer.m_uid == 0 || !peer.IsReady()) return String.Empty;
            Link link;
            if (!Instance.links.TryGetValue(peer.m_rpc, out link) || !ReferenceEquals(link.Peer, peer)
                || !Connected(link) || link.Session == null || !link.Session.Loaded || link.Session.Closed
                || link.Session.State.World != ZNet.instance.GetWorldUID()) return String.Empty;
            return link.Session.State.Owner;
        }
        public static long GetAdministrativeCharacter(long peerId)
        {
            if (Instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return 0;
            if (peerId == ZNet.GetUID()) return AdministrativeReady ? Player.m_localPlayer.GetPlayerID() : 0;
            foreach (Link link in Instance.links.Values)
                if (link.Peer.m_uid == peerId && Connected(link) && link.Session != null && link.Session.Loaded && !link.Session.Closed)
                    return link.Session.State.Character;
            return 0;
        }
    }
}
