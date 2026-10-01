using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimModPack.PortalFinder;
public static class PortalRegistryTests
{
    private static int count;
    private static void Check(bool passed, string name) { count++; if(!passed) throw new Exception(name); }
    private static void Callback(string name)
    { typeof(PortalRegistry).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null); }
    public static void Main()
    {
        int logs=0;
        var registry=new PortalRegistry(error => logs++);
        var harmony=new Harmony(); registry.Patch(harmony); registry.Patch(harmony);
        Check(harmony.Patches==2, "only two XPortal patches, repeated Patch is idempotent");
        ZNet.instance=new ZNet { Server=true };
        ZDOMan.instance.Portals.Add(new ZDO { m_uid=new ZDOID(7), Name="<b>raw</b>", Position=new UnityEngine.Vector3(50000,300,-40000) });
        ZDOMan.instance.Portals.Add(new ZDO { m_uid=new ZDOID(8), Valid=false });
        ZDOMan.instance.Portals.Add(new ZDO());
        ZDOMan.instance.Portals.Add(null);
        List<PortalRecord> records; string reason;
        Check(registry.TryRead(out records,out reason) && records.Count==1 && reason=="", "host receives persistent registry");
        Check(records[0].Id=="id:7" && records[0].Name=="<b>raw</b>" && records[0].X==50000 && records[0].Z==-40000, "ID, raw name and distant position copied");
        records.Clear(); Check(registry.TryRead(out records,out reason) && records.Count==1, "returned snapshot does not mutate registry");
        var remote=new ZNet { Server=false, World=42 }; ZNet.instance=remote;
        int nativeReads=ZDOMan.instance.Reads;
        Check(!registry.TryRead(out records,out reason) && reason=="sync" && records.Count==0, "remote waits for complete resync");
        Callback("AfterResync");
        Check(registry.TryRead(out records,out reason) && records.Count==0, "synced empty world is valid");
        XPortal.KnownPortalsManager.Instance.Portals.Add(new XPortal.KnownPortal { Id=new ZDOID(99), Name="far stone portal", Location=new UnityEngine.Vector3(90000,5,90000) });
        Check(registry.TryRead(out records,out reason) && records.Count==1 && records[0].Id=="id:99", "remote reads complete XPortal snapshot");
        PortalMatch match=PortalSearch.Find(records,89997,90004);
        Check(match!=null && match.Portal.Id=="id:99" && match.Distance==5, "production search consumes distant remote records and map point coordinates");
        Check(nativeReads==ZDOMan.instance.Reads, "remote never falls back to partial native list");
        Callback("AfterReset"); Check(!registry.TryRead(out records,out reason) && reason=="sync", "XPortal reset clears readiness");
        Callback("AfterResync"); remote.World=43;
        Check(!registry.TryRead(out records,out reason) && reason=="sync", "world UID change rejects stale registry");
        Callback("AfterResync"); ZNet.instance=new ZNet { World=43 };
        Check(!registry.TryRead(out records,out reason) && reason=="sync", "new ZNet instance rejects previous join readiness");
        Callback("AfterResync"); XPortal.KnownPortalsManager.Instance.Throw=true;
        Check(!registry.TryRead(out records,out reason) && reason=="unavailable" && records.Count==0 && logs==1, "reflection failure is graceful");
        registry.TryRead(out records,out reason); Check(logs==1, "failure logger is bounded");
        XPortal.KnownPortalsManager.Instance.Throw=false;
        Check(registry.TryRead(out records,out reason) && records.Count==1, "read recovers after transient reflection failure");
        ZNet.instance=null; Check(!registry.TryRead(out records,out reason) && reason=="unavailable", "logout rejects registry");
        ZNet.instance=remote; Check(!registry.TryRead(out records,out reason) && reason=="sync", "logout cleared readiness");
        registry.Dispose(); Callback("AfterResync");
        Check(!registry.TryRead(out records,out reason) && reason=="unavailable", "disposed registry cannot become ready");
        AccessTools.XPortalMissing=true;
        var missing=new PortalRegistry(); missing.Patch(new Harmony());
        Check(!missing.TryRead(out records,out reason) && reason=="unavailable", "remote without XPortal is explicitly unavailable");
        ZNet.instance.Server=true;
        Check(missing.TryRead(out records,out reason) && records.Count==1, "native host remains supported without XPortal");
        missing.Dispose(); AccessTools.XPortalMissing=false; ZNet.instance.Server=false;
        int patchLogs=0;
        var failed=new PortalRegistry(error => patchLogs++); var badHarmony=new Harmony { FailAt=2 };
        failed.Patch(badHarmony);
        Check(badHarmony.Unpatches==2 && patchLogs==1, "partial XPortal patch failure cleans up owned callbacks");
        Callback("AfterResync");
        Check(!failed.TryRead(out records,out reason) && reason=="unavailable", "failed adapter cannot claim remote readiness");
        failed.Dispose();
        Console.WriteLine("PASS: "+count+" production PortalRegistry host assertions; no Unity execution.");
    }
}
