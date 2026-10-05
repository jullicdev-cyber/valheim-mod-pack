using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using XPortal;
using XPortal.Plus;
using XPortal.RPC;
using XPortal.RPC.Client;
using XPortal.RPC.Server;

internal static class CoreTests
{
    private static int checks;
    private static int configsApplied;
    private static int SyncCount { get { return SendToClient.Snapshots.Count; } }
    private static readonly ZDOID PortalId = new ZDOID(77, 1), TargetId = new ZDOID(77, 2);

    private static void Main()
    {
        XPortalConfig.Instance.OnServerConfigChanged += () => configsApplied++;
        XPortalConfig.Instance.OnServerConfigChanged += XPortal.XPortal.OnServerConfigChanged;
        Serialization();
        SnapshotValidationAndScope();
        MetadataMigrationAndBiomes();
        ConfigurationSessionLifecycle();
        SubmitPortalChanges();
        SenderAuthority();
        CanonicalServerUpdates();
        PendingPlacements();
        RemovedPortalsAndQueuedActions();
        Console.WriteLine("AnyPortal+ core checks passed: " + checks);
    }

    private static void Reset(bool server = true)
    {
        ZNet.instance = new ZNet { Server = server, Uid = 77, World = 999 };
        ZNet.instance.Peers.Add(new ZNetPeer { m_uid = 88 });
        ZDOMan.instance = new ZDOMan();
        ZRoutedRpc.instance = new ZRoutedRpc { ServerPeerId = 88 };
        WorldGenerator.instance = new WorldGenerator();
        ObjectDB.instance = null;
        Time.realtimeSinceStartup = 10;
        KnownPortalsManager.Instance.Reset();
        ServerEvents.Reset(); QueuedAction.Clear();
        XPortalConfig.Instance.LoadLocalConfig(new ConfigFile());
        XPortalConfig.Instance.BeginSession();
        XPortalConfig.Instance.Local.DefaultPortal.Value = Vector3.zero;
        SendToClient.Updates.Clear(); SendToClient.Snapshots.Clear(); SendToClient.ConfigRequests = 0;
        SendToServer.Updates.Clear();
        configsApplied = 0;
        Log.Warnings.Clear(); Log.Errors.Clear();
    }

    private static KnownPortal Portal(ZDOID id, string name = "База")
    {
        return new KnownPortal(id, new Vector3(100, 10, 200))
        { Name = name, Target = ZDOID.None, PreviousId = ZDOID.None, CreatedUtcTicks = 0, Icon = -1 };
    }

    private static ZDO Live(ZDOID id, string name, Vector3 position, long created = 0, int icon = -1)
    {
        ZDO zdo = new ZDO { m_uid = id, Position = position, m_prefab = "portal_wood".GetStableHashCode() };
        zdo.Set("tag", name); zdo.Set(PlusPortalMetadata.CreatedKey, created); zdo.Set(PlusPortalMetadata.IconKey, icon);
        ZDOMan.instance.m_objectsByID[id] = zdo;
        return zdo;
    }

    private static ZPackage Readable(ZPackage package) { return new ZPackage(package.GetArray()); }
    private static ZPackage Snapshot(params KnownPortal[] portals)
    {
        ZPackage package = new ZPackage(); package.Write(portals.Length);
        foreach (KnownPortal portal in portals) package.Write(portal.Pack());
        return Readable(package);
    }

    private static ZPackage RawPortal(KnownPortal portal, int? protocol, long ticks, int icon, bool trailing = false)
    {
        ZPackage package = new ZPackage();
        package.Write(portal.Id); package.Write(portal.Name); package.Write(portal.Location);
        package.Write(portal.PreviousId); package.Write(portal.Target); package.Write(portal.Colour);
        if (protocol.HasValue) { package.Write(protocol.Value); package.Write(ticks); package.Write(portal.Biome); package.Write(icon); }
        if (trailing) package.Write(12345);
        return Readable(package);
    }

    private static void Serialization()
    {
        Reset();
        KnownPortal original = Portal(PortalId, "Горная база 🏔");
        original.Target = TargetId; original.PreviousId = new ZDOID(77, 3);
        original.CreatedUtcTicks = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc).Ticks;
        original.Biome = (int)Heightmap.Biome.Mountain; original.Icon = 6;
        KnownPortal clone = new KnownPortal(Readable(original.Pack()));
        Check(clone.Id == original.Id && clone.Target == TargetId && clone.PreviousId == original.PreviousId, "portal identities round trip");
        Check(clone.Name == original.Name && clone.Colour == original.Colour && clone.Location == original.Location, "portal display and position round trip");
        Check(clone.CreatedUtcTicks == original.CreatedUtcTicks && clone.Biome == original.Biome && clone.Icon == 6, "metadata round trip");
        ExpectReject(() => new KnownPortal(RawPortal(original, null, 0, -1)), "old XPortal packet is rejected");
        ExpectReject(() => new KnownPortal(RawPortal(original, 2, 0, -1)), "wrong metadata protocol");
        ExpectReject(() => new KnownPortal(RawPortal(original, 1, 0, 4)), "reserved map icon");
        ExpectReject(() => new KnownPortal(RawPortal(original, 1, 0, 5)), "reserved map icon");
        ExpectReject(() => new KnownPortal(RawPortal(original, 1, -1, -1)), "negative timestamp");
        ExpectReject(() => new KnownPortal(RawPortal(original, 1, DateTime.MaxValue.Ticks + 1, -1)), "invalid timestamp range");
        ExpectReject(() => new KnownPortal(RawPortal(original, 1, 0, -1, true)), "trailing portal data");
        ExpectReject(() => new KnownPortal(new ZPackage(new byte[4097])), "single portal packet cap");
        ExpectReject(() => new KnownPortal((ZPackage)null), "null portal packet");
        foreach (int icon in new[] { -1, 0, 1, 2, 3, 6 })
            Check(new KnownPortal(RawPortal(original, 1, 0, icon)).Icon == icon, "supported icon " + icon);

        foreach (string invalid in new[] { "a\nb", "a\0b", "a\tb", "\ud800", "\udc00", "\ud800a", new string('x', 257) })
            ExpectReject(() => PlusPortalMetadata.ValidateName(invalid), "invalid name shape");
        PlusPortalMetadata.ValidateName(new string('я', 256)); checks++;
        PlusPortalMetadata.ValidateName("Корабль \ud83d\ude80"); checks++;
        Check(PlusPortalMetadata.CleanName("a\nb\ud800c\udc00d") == "abcd", "migration strips controls and invalid surrogate halves");
        string truncated = PlusPortalMetadata.CleanName(new string('x', 255) + "\ud83d\ude80");
        Check(truncated.Length == 255 && !Char.IsSurrogate(truncated[254]), "migration does not split Unicode pair at limit");
        KnownPortal malformed = Portal(PortalId); malformed.Location = new Vector3(float.NaN, 0, 0);
        ExpectReject(() => malformed.Pack(), "non-finite position");
        malformed.Location = new Vector3(0, float.PositiveInfinity, 0);
        ExpectReject(() => malformed.Pack(), "infinite position");
        malformed.Location = new Vector3(100001, 0, 0);
        ExpectReject(() => malformed.Pack(), "world coordinate bound");
        malformed.Location = new Vector3(100000, -100000, 100000); malformed.Pack(); checks++;
        malformed.Id = ZDOID.None;
        ExpectReject(() => malformed.Pack(), "missing identity");
    }

    private static void SnapshotValidationAndScope()
    {
        Reset(false);
        KnownPortal first = Portal(PortalId), second = Portal(TargetId, "Лес");
        KnownPortalsManager manager = KnownPortalsManager.Instance;
        manager.AddOrUpdate(first);
        Check(!manager.HasCompleteSnapshot, "partial updates do not authorize stale-marker cleanup");
        manager.UpdateFromResyncPackage(Snapshot(first, second));
        Check(manager.Count == 2 && manager.HasCompleteSnapshot, "complete valid snapshot installs both portals");
        long revision = manager.SnapshotRevision;
        ZPackage badCount = new ZPackage(); badCount.Write(-1);
        ExpectReject(() => manager.UpdateFromResyncPackage(Readable(badCount)), "negative snapshot count");
        ZPackage tooMany = new ZPackage(); tooMany.Write(8193);
        ExpectReject(() => manager.UpdateFromResyncPackage(Readable(tooMany)), "snapshot count cap");
        ExpectReject(() => manager.UpdateFromResyncPackage(Snapshot(first, first)), "duplicate IDs in full snapshot");
        ZPackage mixed = new ZPackage(); mixed.Write(2); mixed.Write(Portal(new ZDOID(77, 99), "Injected").Pack());
        mixed.Write(RawPortal(second, null, 0, -1));
        ExpectReject(() => manager.UpdateFromResyncPackage(Readable(mixed)), "atomic rejection of mixed old/new protocols");
        Check(manager.Count == 2 && !manager.ContainsId(new ZDOID(77, 99)) && manager.ContainsId(PortalId), "malformed snapshot does not partially replace registry");
        ZPackage trailing = new ZPackage(); trailing.Write(1); trailing.Write(first.Pack()); trailing.Write(42);
        ExpectReject(() => manager.UpdateFromResyncPackage(Readable(trailing)), "trailing full-snapshot bytes");
        Check(manager.Count == 2 && manager.HasCompleteSnapshot, "invalid refresh preserves previous complete snapshot");
        Check(manager.SnapshotRevision == revision, "invalid snapshot does not advance revision");
        ExpectReject(() => manager.UpdateFromResyncPackage(new ZPackage(new byte[16 * 1024 * 1024 + 1])), "snapshot byte cap");
        long oldWorld = ZNet.instance.World;
        ZNet.instance.World = oldWorld + 1;
        Check(!manager.HasCompleteSnapshot, "completeness is scoped to world");
        manager.UpdateFromResyncPackage(Snapshot(second));
        Check(manager.HasCompleteSnapshot && manager.Count == 1, "new world snapshot replaces registry");
        Check(manager.SnapshotRevision > revision, "new complete snapshot advances revision");
        ZNet.instance = new ZNet { Server = false, Uid = 77, World = oldWorld + 1 };
        Check(!manager.HasCompleteSnapshot, "same world reconnect needs a new complete snapshot");
        manager.UpdateFromZDOList(new List<ZDO>());
        Check(!manager.HasCompleteSnapshot, "client's local ZDO subset never establishes world completeness");
        manager.Reset(); Check(manager.Count == 0 && !manager.HasCompleteSnapshot && manager.SnapshotRevision == 0, "registry reset clears snapshot authority and revision");
    }

    private static void MetadataMigrationAndBiomes()
    {
        Reset();
        WorldGenerator.instance.Selector = point => point.x > 50 ? Heightmap.Biome.Plains : Heightmap.Biome.Meadows;
        ZDO old = Live(PortalId, "Old", new Vector3(100, 12, 400));
        KnownPortal loaded = new KnownPortal(PortalId, old.Position);
        Check(loaded.CreatedUtcTicks == 0 && loaded.Icon == -1, "old portal keeps unknown creation time and no icon");
        Check(loaded.Biome == (int)Heightmap.Biome.Plains, "constructor reads biome at actual position");
        KnownPortalsManager.Instance.UpdateFromZDOList(ZDOMan.instance.GetPortalList());
        Check(KnownPortalsManager.Instance.GetKnownPortalById(PortalId).Biome == (int)Heightmap.Biome.Plains, "full ZDO migration uses actual biome");
        Check(KnownPortalsManager.Instance.HasCompleteSnapshot, "host authoritative ZDO list is complete");
        loaded.Name = "Edited"; loaded.Icon = 2;
        ZdoTools.UpdateFromKnownPortal(state: loaded);
        Check(old.GetLong(PlusPortalMetadata.CreatedKey) == 0 && old.GetInt(PlusPortalMetadata.IconKey) == 2, "editing migrated portal preserves unknown timestamp and persists icon");
        long existing = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        old.Set(PlusPortalMetadata.CreatedKey, existing); old.Set(PlusPortalMetadata.IconKey, 3);
        KnownPortal actual = new KnownPortal(PortalId, old.Position);
        Check(actual.CreatedUtcTicks == existing && actual.Icon == 3, "metadata loads from native ZDO");
        actual.CreatedUtcTicks = DateTime.UtcNow.Ticks;
        PlusPortalMetadata.WriteInto(old, actual);
        Check(old.GetLong(PlusPortalMetadata.CreatedKey) == existing, "existing creation timestamp cannot be rewritten");
        PlusPortalMetadata.StampCreation(PortalId);
        Check(old.GetLong(PlusPortalMetadata.CreatedKey) == existing, "creation stamp is idempotent");
        ZDO fresh = Live(TargetId, "New", new Vector3(300, 0, 0));
        long before = DateTime.UtcNow.Ticks; PlusPortalMetadata.StampCreation(TargetId); long after = DateTime.UtcNow.Ticks;
        Check(fresh.GetLong(PlusPortalMetadata.CreatedKey) >= before && fresh.GetLong(PlusPortalMetadata.CreatedKey) <= after, "new placement uses server UTC time");
        old.Set(PlusPortalMetadata.CreatedKey, -9L); old.Set(PlusPortalMetadata.IconKey, 123);
        KnownPortal sanitized = new KnownPortal(PortalId, old.Position);
        Check(sanitized.CreatedUtcTicks == 0 && sanitized.Icon == -1, "invalid saved metadata migrates safely");
    }

    private static void SenderAuthority()
    {
        Reset();
        Check(PlusRpcAuthority.IsClientSender(77) && PlusRpcAuthority.IsClientSender(88), "host and one connected ready peer can request portal edits");
        Check(!PlusRpcAuthority.IsClientSender(0) && !PlusRpcAuthority.IsClientSender(999), "zero and unknown senders are rejected");
        ZNetPeer peer = ZNet.instance.Peers[0];
        peer.Ready = false; Check(!PlusRpcAuthority.IsClientSender(88), "unready peer rejected"); peer.Ready = true;
        peer.m_rpc.Connected = false; Check(!PlusRpcAuthority.IsClientSender(88), "disconnected RPC rejected"); peer.m_rpc.Connected = true;
        ZNet.instance.Peers.Add(new ZNetPeer { m_uid = 88 });
        Check(!PlusRpcAuthority.IsClientSender(88), "ambiguous duplicate connected identity rejected");
        ZNet.instance.Peers.RemoveAt(1);
        Check(!PlusRpcAuthority.IsServerSender(88), "host never accepts server-to-client messages");
        ServerEvents.RPC_ConfigRequest(999); Check(SendToClient.ConfigRequests == 0, "unauthorized config request has no response");
        ServerEvents.RPC_ConfigRequest(88); Check(SendToClient.ConfigRequests == 1, "connected config request gets response");
        ServerEvents.RPC_SyncRequest(999, "spoof"); Check(SyncCount == 0, "unauthorized sync does not inspect world");
        ServerEvents.RPC_SyncRequest(88, "valid"); ServerEvents.RPC_SyncRequest(88, "spam");
        Check(SyncCount == 1, "same peer sync requests are rate limited");
        Time.realtimeSinceStartup += 1; ServerEvents.RPC_SyncRequest(88, "valid");
        Check(SyncCount == 2, "rate limiter allows subsequent requests");

        Reset(false);
        Check(PlusRpcAuthority.IsServerSender(88) && !PlusRpcAuthority.IsServerSender(77) && !PlusRpcAuthority.IsServerSender(0), "client trusts exact current server sender only");
        KnownPortal portal = Portal(PortalId);
        ClientEvents.RPC_Resync(99, Snapshot(portal), "spoof");
        ClientEvents.RPC_SyncPortal(99, Readable(portal.Pack()));
        ClientEvents.RPC_Config(99, new ZPackage());
        Check(KnownPortalsManager.Instance.Count == 0 && configsApplied == 0, "forged client updates and config leave state unchanged");
        ClientEvents.RPC_Resync(88, Snapshot(portal), "valid");
        Check(KnownPortalsManager.Instance.ContainsId(PortalId) && KnownPortalsManager.Instance.HasCompleteSnapshot, "authenticated server snapshot is installed");
        ZPackage config = new ZPackage(); config.Write(false); config.Write(false); config.Write(false);
        ClientEvents.RPC_Config(88, Readable(config)); Check(configsApplied == 1, "authenticated server config accepted");
        ZRoutedRpc.instance.ServerPeerId = 90;
        ClientEvents.RPC_SyncPortal(88, Readable(Portal(TargetId).Pack()));
        Check(!KnownPortalsManager.Instance.ContainsId(TargetId), "previous server cannot edit new connection's registry");
    }

    private static void ConfigurationSessionLifecycle()
    {
        Reset(false);
        ZNet.instance = null;
        ConfigFile file = new ConfigFile();
        file.Bind("General", "PingMapDisabled", true, "");
        ConfigEntry<bool> doubleCosts = file.Bind("General", "DoublePortalCosts", true, "");
        file.Bind("General", "HidePortalDistance", true, "");
        XPortalConfig.Instance.LoadLocalConfig(file);
        Check(XPortalConfig.Instance.Local.DoublePortalCosts && !XPortalConfig.Instance.Server.DoublePortalCosts,
            "main-menu config does not assume a host role");
        ZNet.instance = new ZNet();
        ItemDrop wood = new ItemDrop { name = "Wood" };
        Piece.Requirement requirement = new Piece.Requirement { m_resItem = wood, m_amount = 10 };
        Piece piece = new Piece { m_resources = new[] { requirement } };
        GameObject pieceObject = new GameObject { name = "portal_wood" }; pieceObject.Components[typeof(Piece)] = piece;
        ItemDrop hammer = new ItemDrop(); hammer.m_itemData.m_shared.m_buildPieces.m_pieces.Add(pieceObject);
        GameObject hammerObject = new GameObject(); hammerObject.Components[typeof(ItemDrop)] = hammer;
        ObjectDB.instance = new ObjectDB { Hammer = hammerObject };
        int notifications = configsApplied;
        XPortal.XPortal.GameStarted();
        Check(ReferenceEquals(XPortalConfig.Instance.Server, XPortalConfig.Instance.Local) &&
            XPortalConfig.Instance.Server.PingMapDisabled && XPortalConfig.Instance.Server.HidePortalDistance,
            "starting a host session binds the loaded local config as authoritative server config");
        Check(configsApplied > notifications && requirement.m_amount == 20, "host session initialization applies recipe settings");
        int configBroadcasts = SendToClient.ConfigRequests;
        doubleCosts.Value = false;
        Check(!XPortalConfig.Instance.Server.DoublePortalCosts && requirement.m_amount == 10 && SendToClient.ConfigRequests > configBroadcasts,
            "host config change updates the host recipe and sends configuration to clients");

        ZNet.instance.Server = false;
        XPortalConfig.Instance.BeginSession();
        Check(!ReferenceEquals(XPortalConfig.Instance.Server, XPortalConfig.Instance.Local) && !XPortalConfig.Instance.Server.PingMapDisabled,
            "new guest session drops previous world's enforced server settings");
        ZPackage supplied = new ZPackage(); supplied.Write(false); supplied.Write(true); supplied.Write(false);
        XPortalConfig.Instance.ReceiveServerConfig(Readable(supplied));
        Check(XPortalConfig.Instance.Server.DoublePortalCosts && !XPortalConfig.Instance.Local.DoublePortalCosts && requirement.m_amount == 20,
            "guest uses received server recipe setting without overwriting local settings");
        notifications = configsApplied;
        ZPackage shortConfig = new ZPackage(); shortConfig.Write(true); shortConfig.Write(false);
        ExpectReject(() => XPortalConfig.Instance.ReceiveServerConfig(Readable(shortConfig)), "truncated config packet");
        Check(configsApplied == notifications && XPortalConfig.Instance.Server.DoublePortalCosts, "invalid config is rejected before state mutation");
        XPortalConfig.Instance.BeginSession();
        Check(!XPortalConfig.Instance.Server.DoublePortalCosts && requirement.m_amount == 10, "joining a new guest session resets previous recipe modifier");
        ZNet.instance.Server = true;
        XPortalConfig.Instance.BeginSession();
        Check(XPortalConfig.Instance.Server.PingMapDisabled && ReferenceEquals(XPortalConfig.Instance.Server, XPortalConfig.Instance.Local),
            "returning to host restores user's local server settings");
    }

    private static void SubmitPortalChanges()
    {
        Reset();
        ZDO actual = Live(PortalId, "Original", new Vector3(100, 10, 200));
        ZDO target = Live(TargetId, "Destination", new Vector3(300, 10, 200));
        KnownPortalsManager.Instance.UpdateFromZDOList(ZDOMan.instance.GetPortalList());
        KnownPortal original = KnownPortalsManager.Instance.GetKnownPortalById(PortalId);
        XPortal.XPortal.PortalInfoSubmitted(original, "Renamed", TargetId, true, 3);
        Check(SendToServer.Updates.Count == 1 && actual.GetString("tag") == "Renamed" && actual.GetInt(PlusPortalMetadata.IconKey) == 3,
            "real Apply handler can read its detached clone and submit rename/icon changes");
        Check(original.Name == "Original" && original.Target.IsNone() && original.Icon == -1,
            "Apply does not optimistically mutate the previously displayed registry object");
        Check(actual.GetZDOID(XPortal.XPortal.Key_TargetId) == TargetId && target.GetZDOID(XPortal.XPortal.Key_TargetId) == PortalId,
            "real Apply handler submits destination change");
        Check(XPortalConfig.Instance.Local.DefaultPortal.Value == actual.Position, "real Apply handler sets local default portal");
        KnownPortal updated = KnownPortalsManager.Instance.GetKnownPortalById(PortalId);
        XPortal.XPortal.PortalInfoSubmitted(updated, "Renamed", TargetId, true, 3);
        Check(SendToServer.Updates.Count == 1, "unchanged Apply does not resend a portal edit");
        XPortal.XPortal.PortalInfoSubmitted(updated, "Renamed", TargetId, false, 3);
        Check(XPortalConfig.Instance.Local.DefaultPortal.Value == Vector3.zero, "unchecking the selected default clears local default");
        KnownPortalsManager.Instance.Remove(TargetId);
        ExpectReject(() => XPortal.XPortal.PortalInfoSubmitted(updated, "Invalid", TargetId, true, 3), "removed selected destination");
        Check(XPortalConfig.Instance.Local.DefaultPortal.Value == Vector3.zero && actual.GetString("tag") == "Renamed",
            "unavailable destination is rejected before local default or native portal changes");
    }

    private static void CanonicalServerUpdates()
    {
        Reset();
        WorldGenerator.instance.Selector = point => point.x > 50 ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        long existing = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        ZDO actual = Live(PortalId, "Original", new Vector3(500, 40, 700), existing, 1);
        actual.Set(XPortal.XPortal.Key_PreviousId, new ZDOID(1, 50));
        KnownPortal request = Portal(PortalId, "Updated 🏠");
        request.CreatedUtcTicks = DateTime.UtcNow.Ticks; request.PreviousId = new ZDOID(666, 77);
        request.Icon = 6; request.Location = new Vector3(-20, 0, -30); request.Biome = 999;
        ServerEvents.RPC_AddOrUpdateRequest(999, Readable(request.Pack()));
        Check(KnownPortalsManager.Instance.Count == 0 && actual.GetString("tag") == "Original", "unauthorized edit does not mutate world or registry");
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(request.Pack()));
        KnownPortal canonical = KnownPortalsManager.Instance.GetKnownPortalById(PortalId);
        Check(canonical.Location == actual.Position && canonical.Biome == (int)Heightmap.Biome.BlackForest, "server canonical position and biome override client claims");
        Check(canonical.CreatedUtcTicks == existing && actual.GetLong(PlusPortalMetadata.CreatedKey) == existing, "client timestamp does not override native creation time");
        Check(canonical.PreviousId == new ZDOID(1, 50), "server uses real previous identity");
        Check(actual.GetString("tag") == request.Name && actual.GetInt(PlusPortalMetadata.IconKey) == 6 && actual.Owner == 77, "valid name and icon persist in native portal");
        Check(SendToClient.Updates.Count == 1 && !ReferenceEquals(SendToClient.Updates[0], request), "broadcast contains canonical detached portal");
        request.Name = "Changed after send";
        Check(SendToClient.Updates[0].Name == "Updated 🏠", "later caller edits cannot change broadcast payload");

        int broadcasts = SendToClient.Updates.Count;
        KnownPortal missingTarget = Portal(PortalId, "Bad target"); missingTarget.Target = new ZDOID(55, 55);
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(missingTarget.Pack()));
        Check(actual.GetString("tag") == "Updated 🏠" && SendToClient.Updates.Count == broadcasts, "missing destination rejected before any mutation");
        KnownPortal selfTarget = Portal(PortalId, "Self"); selfTarget.Target = PortalId;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(selfTarget.Pack()));
        Check(actual.GetString("tag") == "Updated 🏠", "self destination rejected");
        ZDO target = Live(TargetId, "Target", new Vector3(1000, 0, 1000), existing, 2);
        target.Live = false;
        missingTarget.Target = TargetId;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(missingTarget.Pack()));
        Check(actual.GetString("tag") == "Updated 🏠", "destroyed destination rejected");
        target.Live = true; target.IsPortal = false;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(missingTarget.Pack()));
        Check(actual.GetString("tag") == "Updated 🏠", "non-portal native ZDO is not a destination");
        target.IsPortal = true;
        KnownPortal targetRequest = Portal(PortalId, "Linked"); targetRequest.Target = TargetId;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(targetRequest.Pack()));
        Check(actual.GetZDOID(XPortal.XPortal.Key_TargetId) == TargetId && target.GetZDOID(XPortal.XPortal.Key_TargetId) == PortalId, "valid destination gets native reciprocal link when unlinked");
        Check(KnownPortalsManager.Instance.GetKnownPortalById(TargetId).Icon == 2, "reciprocal update preserves destination icon");
        ZDO legacy = Live(new ZDOID(77, 5), "Legacy", new Vector3(700, 0, 0));
        KnownPortalsManager.Instance.AddOrUpdate(new KnownPortal(legacy.m_uid, legacy.Position) { Name = "Legacy" });
        KnownPortal forgedLegacy = Portal(legacy.m_uid, "Legacy edited"); forgedLegacy.CreatedUtcTicks = DateTime.UtcNow.Ticks;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(forgedLegacy.Pack()));
        Check(legacy.GetLong(PlusPortalMetadata.CreatedKey) == 0, "existing migrated unknown date stays unknown even with client date claim");
    }

    private static int PendingCount()
    {
        return ((IDictionary)typeof(ServerEvents).GetField("pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;
    }
    private static int QueuedCount()
    {
        return ((IDictionary)typeof(QueuedAction).GetField("queuedActions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Count;
    }
    private static void Frames(int count) { for (int i = 0; i < count; i++) QueuedAction.Update(); }

    private static void PendingPlacements()
    {
        Reset();
        KnownPortal fresh = Portal(PortalId, "Placed"); fresh.CreatedUtcTicks = DateTime.UtcNow.Ticks; fresh.Icon = 3;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(fresh.Pack()));
        Check(PendingCount() == 1 && KnownPortalsManager.Instance.Count == 0, "placement may wait for native ZDO arrival");
        for (int i = 0; i < 50; i++) ServerEvents.RPC_AddOrUpdateRequest(88, Readable(fresh.Pack()));
        Check(PendingCount() == 1 && QueuedCount() <= 1, "same pending placement coalesces instead of creating unbounded retry queue");
        ZDO live = Live(PortalId, "Native", new Vector3(900, 0, 900));
        long before = DateTime.UtcNow.Ticks; Frames(17); long after = DateTime.UtcNow.Ticks;
        Check(KnownPortalsManager.Instance.ContainsId(PortalId) && PendingCount() == 0, "available native ZDO resolves pending placement");
        Check(live.GetString("tag") == "Placed" && live.GetInt(PlusPortalMetadata.IconKey) == 3, "pending placement applies requested name and icon");
        Check(live.GetLong(PlusPortalMetadata.CreatedKey) >= before && live.GetLong(PlusPortalMetadata.CreatedKey) <= after, "server assigns real creation time instead of trusting client ticks");

        Reset();
        for (uint i = 1; i <= 33; i++)
        {
            KnownPortal request = Portal(new ZDOID(88, i)); request.CreatedUtcTicks = DateTime.UtcNow.Ticks;
            ServerEvents.RPC_AddOrUpdateRequest(88, Readable(request.Pack()));
        }
        Check(PendingCount() <= 32 && QueuedCount() <= 32, "different missing placements have a bounded queue");
        Frames(500);
        Check(PendingCount() == 0 && QueuedCount() == 0 && KnownPortalsManager.Instance.Count == 0, "missing placements expire after finite retry budget");
        Reset();
        fresh = Portal(PortalId, "Wrong world"); fresh.CreatedUtcTicks = DateTime.UtcNow.Ticks;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(fresh.Pack()));
        ZNet.instance.World++;
        Live(PortalId, "Keep", new Vector3(100, 0, 100)); Frames(17);
        Check(PendingCount() == 0 && KnownPortalsManager.Instance.Count == 0 && ZDOMan.instance.GetZDO(PortalId).GetString("tag") == "Keep", "retry cannot cross a world transition");
        Reset();
        fresh = Portal(PortalId, "Disconnected"); fresh.CreatedUtcTicks = DateTime.UtcNow.Ticks;
        ServerEvents.RPC_AddOrUpdateRequest(88, Readable(fresh.Pack()));
        ZNet.instance.Peers[0].m_rpc.Connected = false;
        Live(PortalId, "Keep", new Vector3(100, 0, 100)); Frames(17);
        Check(PendingCount() == 0 && KnownPortalsManager.Instance.Count == 0, "retry rechecks sender connection");
    }

    private static void RemovedPortalsAndQueuedActions()
    {
        Reset();
        ZDO first = Live(PortalId, "Keep", new Vector3(100, 0, 100));
        ZDO target = Live(TargetId, "Linked", new Vector3(200, 0, 200)); target.Set(XPortal.XPortal.Key_TargetId, PortalId);
        KnownPortalsManager.Instance.UpdateFromZDOList(ZDOMan.instance.GetPortalList());
        ServerEvents.RPC_RemoveRequest(999, PortalId); Frames(5);
        Check(KnownPortalsManager.Instance.ContainsId(PortalId) && SyncCount == 0, "unknown sender cannot force deletion or rescan");
        ServerEvents.RPC_RemoveRequest(88, PortalId); Frames(5);
        Check(KnownPortalsManager.Instance.ContainsId(PortalId) && first.GetString("tag") == "Keep", "remove request cannot delete a still-live portal");
        first.Live = false;
        ServerEvents.RPC_RemoveRequest(88, PortalId); Frames(5);
        Check(!KnownPortalsManager.Instance.ContainsId(PortalId) && KnownPortalsManager.Instance.ContainsId(TargetId), "destroyed portal disappears after authoritative rescan");
        Check(KnownPortalsManager.Instance.GetKnownPortalById(TargetId).Target == ZDOID.None && target.GetZDOID(XPortal.XPortal.Key_TargetId) == ZDOID.None, "dangling links clear in registry and native ZDO");
        int syncs = SyncCount;
        ServerEvents.RPC_RemoveRequest(88, TargetId); ZNet.instance = new ZNet(); Frames(5);
        Check(SyncCount == syncs, "queued removal rescan cannot cross network instance change");

        QueuedAction.Clear();
        int invocations = 0;
        QueuedAction.Queue((delayed, state) => { invocations++; QueuedAction.Queue((d, s) => invocations++, 1); }, 0);
        Frames(3);
        Check(invocations == 2 && QueuedCount() == 0, "queued callback may enqueue another callback safely");
        QueuedAction.Queue((delayed, state) => { throw new InvalidOperationException("test callback"); }, 0);
        QueuedAction.Queue((delayed, state) => invocations++, 0);
        Frames(1);
        Check(invocations == 3 && QueuedCount() == 0, "throwing callback does not interrupt other ready actions");
        QueuedAction.Queue((delayed, state) => invocations++, 1); QueuedAction.Clear(); Frames(3);
        Check(invocations == 3, "clear cancels pending callbacks");
        KnownPortal missing = Portal(new ZDOID(99, 99));
        ZdoTools.UpdateFromKnownPortal(state: missing);
        Check(ZDOMan.instance.GetZDO(missing.Id) == null, "editing absent native ZDO cancels instead of rescheduling forever");
    }

    private static void ExpectReject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { checks++; return; }
        catch (IOException) { checks++; return; }
        catch (ArgumentException) { checks++; return; }
        catch (InvalidOperationException) { checks++; return; }
        throw new Exception("Expected rejection: " + message);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
