using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.PortalFinder
{
    // Reads persistent world records; it never loads scenes or edits portal data.
    internal sealed class PortalRegistry : IDisposable
    {
        private static PortalRegistry active;
        private readonly Action<Exception> report;
        private PropertyInfo managerInstance, portalId, portalName, portalLocation;
        private MethodInfo getList;
        private bool patchAttempted, xportalAvailable, ready, disposed, warned;
        private ZNet readyNet;
        private long readyWorld;

        internal PortalRegistry() : this(null) { }
        internal PortalRegistry(Action<Exception> reportError) { report = reportError; }

        internal void Patch(Harmony harmony)
        {
            if (disposed || patchAttempted) return;
            patchAttempted = true;
            active = this;
            MethodInfo resync = null, reset = null;
            MethodInfo resyncPostfix = AccessTools.Method(typeof(PortalRegistry), "AfterResync");
            MethodInfo resetPostfix = AccessTools.Method(typeof(PortalRegistry), "AfterReset");
            try
            {
                Type manager = AccessTools.TypeByName("XPortal.KnownPortalsManager");
                if (manager == null) return;
                Type portal = AccessTools.TypeByName("XPortal.KnownPortal");
                managerInstance = AccessTools.Property(manager, "Instance");
                getList = AccessTools.Method(manager, "GetList", Type.EmptyTypes);
                portalId = portal == null ? null : AccessTools.Property(portal, "Id");
                portalName = portal == null ? null : AccessTools.Property(portal, "Name");
                portalLocation = portal == null ? null : AccessTools.Property(portal, "Location");
                resync = AccessTools.Method(manager, "UpdateFromResyncPackage", new[] { typeof(ZPackage) });
                reset = AccessTools.Method(manager, "Reset", Type.EmptyTypes);
                if (managerInstance == null || getList == null || portalId == null || portalName == null
                    || portalLocation == null || resync == null || reset == null
                    || !typeof(IEnumerable).IsAssignableFrom(getList.ReturnType)
                    || portalId.PropertyType != typeof(ZDOID) || portalName.PropertyType != typeof(string)
                    || portalLocation.PropertyType != typeof(Vector3))
                    throw new MissingMemberException("XPortal world registry API changed");
                harmony.Patch(resync, postfix: new HarmonyMethod(resyncPostfix));
                harmony.Patch(reset, postfix: new HarmonyMethod(resetPostfix));
                xportalAvailable = true;
            }
            catch (Exception error)
            {
                xportalAvailable = false;
                Reset();
                // A failed second patch must not leave a partly active adapter.
                if (harmony != null)
                {
                    try
                    {
                        if (resync != null) harmony.Unpatch(resync, resyncPostfix);
                        if (reset != null) harmony.Unpatch(reset, resetPostfix);
                    }
                    catch (Exception cleanupError) { Warn(cleanupError); }
                }
                Warn(error);
            }
        }

        internal bool TryRead(out List<PortalRecord> portals, out string reason)
        {
            portals = new List<PortalRecord>();
            reason = "unavailable";
            if (disposed) return false;
            try
            {
                ZNet net = ZNet.instance;
                ZDOMan zdos = ZDOMan.instance;
                if (net == null || zdos == null) { Reset(); return false; }
                long world = net.GetWorldUID();
                if (world == 0) { Reset(); return false; }
                if (net.IsServer())
                {
                    // This registry includes distant wood and stone portals.
                    List<ZDO> source = zdos.GetPortalList();
                    if (source == null) return false;
                    foreach (ZDO zdo in source)
                    {
                        if (zdo == null || !zdo.IsValid() || zdo.m_uid == ZDOID.None) continue;
                        Vector3 position = zdo.GetPosition();
                        portals.Add(Record(zdo.m_uid, zdo.GetString("tag", ""), position));
                    }
                }
                else
                {
                    if (!xportalAvailable) return false;
                    if (!ready || !ReferenceEquals(readyNet, net) || readyWorld != world)
                    {
                        Reset();
                        reason = "sync";
                        return false;
                    }
                    object manager = managerInstance.GetValue(null, null);
                    if (manager == null) throw new InvalidOperationException("XPortal registry is unavailable");
                    IEnumerable source = getList.Invoke(manager, null) as IEnumerable;
                    if (source == null) throw new InvalidOperationException("XPortal registry returned no snapshot");
                    foreach (object portal in source)
                    {
                        if (portal == null) continue;
                        ZDOID id = (ZDOID)portalId.GetValue(portal, null);
                        if (id == ZDOID.None) continue;
                        string name = (string)portalName.GetValue(portal, null);
                        Vector3 position = (Vector3)portalLocation.GetValue(portal, null);
                        portals.Add(Record(id, name, position));
                    }
                }
                if (!ReferenceEquals(net, ZNet.instance) || world != net.GetWorldUID())
                {
                    Reset(); portals.Clear(); reason = "sync"; return false;
                }
                reason = "";
                return true;
            }
            catch (Exception error)
            {
                portals.Clear();
                Warn(error);
                return false;
            }
        }

        private static PortalRecord Record(ZDOID id, string name, Vector3 position)
        {
            return new PortalRecord { Id = id.ToString(), Name = name ?? "",
                X = position.x, Y = position.y, Z = position.z };
        }

        private static void AfterResync()
        {
            PortalRegistry self = active;
            if (self == null || self.disposed || !self.xportalAvailable) return;
            try
            {
                ZNet net = ZNet.instance;
                if (net == null || net.IsServer()) return;
                long world = net.GetWorldUID();
                if (world == 0) return;
                self.readyNet = net;
                self.readyWorld = world;
                self.ready = true;
            }
            catch (Exception error) { self.Reset(); self.Warn(error); }
        }

        private static void AfterReset()
        { if (active != null) active.Reset(); }

        internal void Reset()
        { ready = false; readyNet = null; readyWorld = 0; }

        private void Warn(Exception error)
        {
            if (warned || report == null) return;
            warned = true;
            try { report(error); } catch { }
        }

        public void Dispose()
        {
            disposed = true;
            Reset();
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
