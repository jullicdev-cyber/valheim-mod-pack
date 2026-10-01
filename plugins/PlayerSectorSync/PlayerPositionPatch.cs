using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PlayerSectorSync
{
    // Native SetSector notifies peers before InternalSetPosition stores m_position.
    // Notify again after that store, only for current server player characters.
    [HarmonyPatch(typeof(ZDO), "InternalSetPosition")]
    internal static class PlayerPositionPatch
    {
        internal static Action<string> Warning;
        private static bool warningIssued;

        internal struct MoveState
        {
            internal bool Active;
            internal ZoneSystem.SectorIndex PreviousSector;
            internal ZNet Network;
            internal ZDOMan Manager;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        internal static void Prefix(ZDO __instance, Vector3 __0, out MoveState __state)
        {
            __state = new MoveState();
            try
            {
                ZNet net = ZNet.instance;
                ZDOMan manager = ZDOMan.instance;
                if (__instance == null || net == null || manager == null || !net.IsServer()) return;
                List<ZNetPeer> peers = net.GetPeers();
                if (peers == null || peers.Count == 0 || !Finite(__0) || !Finite(__instance.GetPosition())) return;

                ZoneSystem.SectorIndex previous = __instance.GetSectorIndex();
                if (previous == ZoneSystem.GetSectorIndex(__0)) return;

                __state.Active = true;
                __state.PreviousSector = previous;
                __state.Network = net;
                __state.Manager = manager;
            }
            catch (Exception error) { WarnOnce(error); }
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        internal static void Postfix(ZDO __instance, MoveState __state)
        {
            if (!__state.Active || __instance == null) return;
            try
            {
                ZNet net = ZNet.instance;
                ZDOMan manager = ZDOMan.instance;
                if (net == null || manager == null || !object.ReferenceEquals(net, __state.Network) ||
                    !object.ReferenceEquals(manager, __state.Manager) || !net.IsServer()) return;
                if (!Finite(__instance.GetPosition()) || __instance.GetSectorIndex() == __state.PreviousSector) return;

                List<ZNetPeer> peers = net.GetPeers();
                if (peers == null || peers.Count == 0 || !CurrentCharacter(net, peers, __instance.m_uid)) return;

                // This queues native sector invalidation; it does not send an extra RPC,
                // move an object, destroy a ZDO, or alter a character's inventory.
                manager.ZDOSectorInvalidated(__instance);
            }
            catch (Exception error) { WarnOnce(error); }
        }

        private static bool CurrentCharacter(ZNet net, List<ZNetPeer> peers, ZDOID id)
        {
            if (id.IsNone()) return false;
            if (id == net.LocalPlayerCharacterID) return true;
            for (int i = 0; i < peers.Count; ++i)
            {
                ZNetPeer peer = peers[i];
                if (peer != null && peer.IsReady() && id == peer.m_characterID) return true;
            }
            return false;
        }

        private static bool Finite(Vector3 position)
        {
            return !float.IsNaN(position.x) && !float.IsInfinity(position.x) &&
                   !float.IsNaN(position.y) && !float.IsInfinity(position.y) &&
                   !float.IsNaN(position.z) && !float.IsInfinity(position.z);
        }

        private static void WarnOnce(Exception error)
        {
            if (warningIssued) return;
            warningIssued = true;
            try
            {
                Action<string> warning = Warning;
                if (warning != null) warning("Player Sector Sync could not queue a sector correction: " + error.GetType().Name);
            }
            catch { }
        }
    }
}
