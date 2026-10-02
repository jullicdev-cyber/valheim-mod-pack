using System;
using System.Collections.Generic;

// Compile the real AdministrativeCheckpoint.cs against small native/session
// doubles. No Unity objects, Steam calls, saves or game process are involved.
public sealed class Player
{
    public static Player m_localPlayer;
    public bool Dead;
    public long Character = 11;
    public bool IsDead() { return Dead; }
    public long GetPlayerID() { return Character; }
}
public interface ICheckpointSocket { string GetHostName(); }
public sealed class WrappedCheckpointSocket : ICheckpointSocket
{
    public int Reads;
    public string GetHostName() { ++Reads; throw new InvalidOperationException("Wrapped socket identity must not be read."); }
}
public sealed class ClaimedCheckpointSocket : ICheckpointSocket
{
    public int Reads;
    public string GetHostName() { ++Reads; return "76561198000000999"; }
}
public sealed class ZRpc
{
    public bool Connected = true;
    public bool IsConnected() { return Connected; }
}
public sealed class ZNetPeer
{
    public ZRpc m_rpc = new ZRpc();
    public long m_uid = 2;
    public bool Ready = true;
    public ICheckpointSocket m_socket;
    public bool IsReady() { return Ready; }
}
public sealed class ZNet
{
    public static ZNet instance;
    public static long Uid = 1;
    public bool Server = true;
    public long World = 99;
    public static long GetUID() { return Uid; }
    public bool IsServer() { return Server; }
    public long GetWorldUID() { return World; }
}
namespace ValheimModPack.WorldCharacters
{
    public sealed class CharacterState
    {
        public long World = 99, Character = 22;
        public string Owner = "Steam_76561198000000002";
    }
    public sealed class CharacterSession
    {
        public long AcceptedSequence = 8, LastSequence = 7;
        public bool Loaded = true, Closed;
        public CharacterState State = new CharacterState();
    }
    public sealed partial class Plugin
    {
        internal static Plugin Instance;
        internal bool Managed = true, ready = true, failed, closing, saving;
        internal long produced = 8, acknowledged = 7;
        internal CharacterSession localHost = new CharacterSession();
        internal readonly Dictionary<ZRpc, Link> links = new Dictionary<ZRpc, Link>();
        internal int PublishCalls, PublishAdvance = 1;
        internal bool PublishedFinal, PublishedMap, PublishedMandatory;
        internal sealed class Link
        {
            internal ZNetPeer Peer;
            internal CharacterSession Session = new CharacterSession();
            internal bool Rejected;
        }
        // Match the existing production connection gate, including rejection.
        private static bool Connected(Link link)
        { return link != null && !link.Rejected && link.Peer != null && link.Peer.m_rpc != null && link.Peer.m_rpc.IsConnected(); }
        private void Publish(bool final, bool includesMap, bool mandatory)
        {
            ++PublishCalls;
            PublishedFinal = final; PublishedMap = includesMap; PublishedMandatory = mandatory;
            if (localHost != null) localHost.AcceptedSequence += PublishAdvance;
            else produced += PublishAdvance;
        }
    }
    internal static class AdministrativeCheckpointTests
    {
        private static int checks;
        private static Plugin plugin;
        private static ZNetPeer peer;
        private static Plugin.Link link;
        private static WrappedCheckpointSocket socket;
        private static void Check(bool condition, string label)
        { ++checks; if (!condition) throw new Exception("FAIL: " + label); }
        private static void Reset()
        {
            ZNet.instance = new ZNet(); ZNet.Uid = 1; Player.m_localPlayer = new Player();
            plugin = new Plugin(); Plugin.Instance = plugin;
            socket = new WrappedCheckpointSocket();
            peer = new ZNetPeer { m_socket = socket };
            link = new Plugin.Link { Peer = peer };
            plugin.links.Add(peer.m_rpc, link);
        }
        private static void EmptyOwner(string label)
        { Check(Plugin.GetAdministrativeOwner(peer) == String.Empty, label); Check(socket.Reads == 0, label + " does not inspect a socket claim"); }
        private static void RefuseSave(string label)
        {
            bool refused = false;
            try { Plugin.RequestAdministrativeSave(); } catch (InvalidOperationException) { refused = true; }
            Check(refused, label); Check(plugin.PublishCalls == 0, label + " captures nothing");
        }
        private static void OwnerChecks()
        {
            Reset();
            string originalOwner = link.Session.State.Owner;
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "exact approved peer survives a wrapped socket");
            Check(socket.Reads == 0, "throwing GetHostName is never called");
            Check(link.Session.State.Owner == originalOwner, "reading owner does not change approved identity");
            peer.m_socket = null;
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "approved identity does not depend on a replaced socket field");
            var claimed = new ClaimedCheckpointSocket(); peer.m_socket = claimed;
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "a different socket claim cannot replace approved identity");
            Check(claimed.Reads == 0, "plausible socket claim is not read");
            Check(link.Session.State.Owner == originalOwner, "plausible socket claim leaves the stored owner unchanged");
            Check(Plugin.GetAdministrativeOwner(null) == String.Empty, "null peer has no approved identity");

            Reset(); Plugin.Instance = null; EmptyOwner("missing plugin refuses identity");
            Reset(); plugin.failed = true; EmptyOwner("failed protection refuses identity");
            Reset(); plugin.closing = true; EmptyOwner("closing protection refuses identity");
            Reset(); ZNet.instance = null; EmptyOwner("missing network refuses identity");
            Reset(); ZNet.instance.Server = false; EmptyOwner("a client cannot query host-approved owners");
            Reset(); peer.m_rpc = null; EmptyOwner("missing RPC refuses identity");
            Reset(); peer.m_uid = 0; EmptyOwner("unassigned UID refuses identity");
            Reset(); peer.Ready = false; EmptyOwner("native peer authentication must complete");
            Reset(); peer.m_rpc.Connected = false; EmptyOwner("disconnected RPC refuses identity");
            Reset(); plugin.links.Clear(); EmptyOwner("unknown connection refuses identity");
            Reset(); peer.m_rpc = new ZRpc(); EmptyOwner("another RPC cannot borrow this peer's approval");
            Reset(); link.Rejected = true; EmptyOwner("rejected connection refuses identity");
            Reset(); link.Session = null; EmptyOwner("a connected peer without approval refuses identity");
            Reset(); link.Session.Loaded = false; EmptyOwner("unloaded approved character refuses identity");
            Reset(); link.Session.Closed = true; EmptyOwner("closed approved character refuses identity");
            Reset(); link.Session.State.World = 100; EmptyOwner("approval from another world refuses identity");
            Reset(); ZNet.instance.World = 100; EmptyOwner("a changed host world invalidates previous approval");
            Reset();
            var impersonator = new ZNetPeer { m_uid = peer.m_uid, m_rpc = peer.m_rpc, m_socket = new ClaimedCheckpointSocket() };
            Check(Plugin.GetAdministrativeOwner(impersonator) == String.Empty, "another peer with the same UID and RPC cannot borrow approval");
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "a rejected duplicate does not damage original approval");
            Check(((ClaimedCheckpointSocket)impersonator.m_socket).Reads == 0, "duplicate peer claim is never inspected");
            Reset();
            impersonator = new ZNetPeer { m_uid = peer.m_uid, m_socket = new ClaimedCheckpointSocket() };
            Check(Plugin.GetAdministrativeOwner(impersonator) == String.Empty, "same UID on another RPC is not approved");
            Reset();
            var secondPeer = new ZNetPeer { m_uid = 3, m_socket = new WrappedCheckpointSocket() };
            var secondLink = new Plugin.Link { Peer = secondPeer };
            secondLink.Session.State.Owner = "Steam_76561198000000003";
            plugin.links.Add(secondPeer.m_rpc, secondLink);
            Check(Plugin.GetAdministrativeOwner(secondPeer) == secondLink.Session.State.Owner, "second approved RPC resolves its own owner");
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "second approved RPC cannot change first owner");
            Check(((WrappedCheckpointSocket)secondPeer.m_socket).Reads == 0, "second wrapper is not inspected");
            Reset(); plugin.Managed = false; plugin.ready = false; Player.m_localPlayer = null;
            Check(Plugin.GetAdministrativeOwner(peer) == originalOwner, "dedicated host needs no local character to expose an approved remote owner");
            Reset();
            plugin.links.Clear(); peer.m_socket = new ClaimedCheckpointSocket();
            Check(Plugin.GetAdministrativeOwner(peer) == String.Empty, "plausible Steam string without an approved session grants no identity");
            Check(((ClaimedCheckpointSocket)peer.m_socket).Reads == 0, "unapproved Steam claim is not inspected");
        }
        private static void ReadyAndSaveChecks()
        {
            Reset(); Check(Plugin.AdministrativeReady, "protected living character is ready");
            Plugin.Instance = null; Check(!Plugin.AdministrativeReady, "missing plugin is not ready");
            Reset(); plugin.Managed = false; Check(!Plugin.AdministrativeReady, "unprotected character is not ready"); RefuseSave("unprotected character cannot capture");
            Reset(); plugin.ready = false; Check(!Plugin.AdministrativeReady, "unloaded local character is not ready"); RefuseSave("unloaded local character cannot capture");
            Reset(); plugin.failed = true; Check(!Plugin.AdministrativeReady, "failed protection is not ready"); RefuseSave("failed protection cannot capture");
            Reset(); plugin.closing = true; Check(!Plugin.AdministrativeReady, "closing character is not ready"); RefuseSave("closing character cannot capture");
            Reset(); Player.m_localPlayer = null; Check(!Plugin.AdministrativeReady, "absent local player is not ready"); RefuseSave("absent local player cannot capture");
            Reset(); Player.m_localPlayer.Dead = true; Check(!Plugin.AdministrativeReady, "dead local player is not ready"); RefuseSave("dead local player cannot capture");
            Reset(); plugin.saving = true; RefuseSave("capture during another capture is refused");
            Reset();
            Check(Plugin.RequestAdministrativeSave() == 9, "host mandatory capture returns next accepted sequence");
            Check(plugin.localHost.AcceptedSequence == 9 && plugin.localHost.LastSequence == 7, "host capture does not advance durability before disk ACK");
            Check(plugin.PublishCalls == 1 && !plugin.PublishedFinal && !plugin.PublishedMap && plugin.PublishedMandatory, "host capture reuses a mandatory non-final map-free publish");
            Check(!Plugin.IsAdministrativeSaveDurable(9), "captured host save is not prematurely durable");
            plugin.localHost.LastSequence = 9; Check(Plugin.IsAdministrativeSaveDurable(9), "host disk ACK makes capture durable");
            Reset(); plugin.localHost = null;
            Check(Plugin.RequestAdministrativeSave() == 9, "remote mandatory capture returns next produced sequence");
            Check(plugin.produced == 9 && plugin.acknowledged == 7, "remote capture does not manufacture a host ACK");
            Check(plugin.PublishCalls == 1 && !plugin.PublishedFinal && !plugin.PublishedMap && plugin.PublishedMandatory, "remote capture uses the normal mandatory publish");
            Check(!Plugin.IsAdministrativeSaveDurable(9), "remote capture waits for host disk ACK");
            plugin.acknowledged = 9; Check(Plugin.IsAdministrativeSaveDurable(9), "remote host ACK makes capture durable");
            Reset(); plugin.PublishAdvance = 0;
            bool refused = false; try { Plugin.RequestAdministrativeSave(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && plugin.PublishCalls == 1, "a publish without a new sequence is refused");
            Reset(); plugin.PublishAdvance = 2; refused = false;
            try { Plugin.RequestAdministrativeSave(); } catch (InvalidOperationException) { refused = true; }
            Check(refused && plugin.PublishCalls == 1, "a publish that skips a sequence is refused");
        }
        private static void DurabilityAndCharacterChecks()
        {
            Reset();
            Check(Plugin.IsAdministrativeSaveDurable(7), "existing durable host sequence is visible");
            Check(!Plugin.IsAdministrativeSaveDurable(8), "future host sequence is not durable");
            Check(!Plugin.IsAdministrativeSaveDurable(0), "zero sequence is not a durable administrative save");
            Check(!Plugin.IsAdministrativeSaveDurable(-1), "negative sequence is not a durable administrative save");
            plugin.failed = true; Check(!Plugin.IsAdministrativeSaveDurable(7), "failed protection cannot promise durability");
            Reset(); plugin.Managed = false; Check(!Plugin.IsAdministrativeSaveDurable(7), "unmanaged local character cannot promise durability");
            Reset(); Plugin.Instance = null; Check(!Plugin.IsAdministrativeSaveDurable(7), "missing plugin cannot promise durability");
            Reset();
            Check(Plugin.GetAdministrativeDurableSequence(1) == 7, "host sequence comes from local protected session");
            Check(Plugin.GetAdministrativeDurableSequence(2) == 7, "remote sequence comes from approved protected session");
            Check(Plugin.IsAdministrativePeerReady(2), "remote approved loaded session is ready");
            Check(Plugin.GetAdministrativeCharacter(1) == 11, "host character comes from loaded player");
            Check(Plugin.GetAdministrativeCharacter(2) == 22, "remote character comes from approved session");
            Check(Plugin.GetAdministrativeDurableSequence(777) == -1, "unknown peer has no durable sequence");
            Check(!Plugin.IsAdministrativePeerReady(777), "unknown peer is not administrative-ready");
            Check(Plugin.GetAdministrativeCharacter(777) == 0, "unknown peer has no approved character");
            link.Session.LastSequence = 0;
            Check(Plugin.IsAdministrativePeerReady(2), "newly loaded approved session needs no first periodic snapshot");
            Reset(); ZNet.instance.Server = false;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && !Plugin.IsAdministrativePeerReady(2), "a client cannot query another peer's host durability");
            Check(Plugin.GetAdministrativeCharacter(2) == 0, "a client cannot query another peer's host character approval");
            Reset(); ZNet.instance = null;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "missing network returns no host-side approval");
            Reset(); Plugin.Instance = null;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "missing plugin returns no host-side approval");
            Reset(); link.Rejected = true;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "rejected connection has no host-side approval");
            Reset(); peer.m_rpc.Connected = false;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "disconnected connection has no host-side approval");
            Reset(); link.Session = null;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "session-less peer has no host-side approval");
            Reset(); link.Session.Loaded = false;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "unloaded session has no host-side approval");
            Reset(); link.Session.Closed = true;
            Check(Plugin.GetAdministrativeDurableSequence(2) == -1 && Plugin.GetAdministrativeCharacter(2) == 0, "closed session has no host-side approval");
            Reset(); plugin.localHost = null;
            Check(Plugin.GetAdministrativeDurableSequence(1) == -1, "host without protected local session has no durable cursor");
            Reset(); plugin.ready = false;
            Check(Plugin.GetAdministrativeDurableSequence(1) == -1 && Plugin.GetAdministrativeCharacter(1) == 0, "unloaded host exposes no local cursor or character");
            Reset(); plugin.failed = true;
            Check(Plugin.GetAdministrativeDurableSequence(1) == -1 && Plugin.GetAdministrativeCharacter(1) == 0, "failed host exposes no local cursor or character");
            Reset(); Player.m_localPlayer.Dead = true;
            Check(Plugin.GetAdministrativeCharacter(1) == 0, "dead host exposes no live administrative character");
        }
        private static int Main()
        {
            try
            {
                OwnerChecks(); ReadyAndSaveChecks(); DurabilityAndCharacterChecks();
                System.Console.WriteLine("PASS: " + checks + " real administrative checkpoint API checks (exact connection ownership, readiness and durable-save gates). No game process started.");
                return 0;
            }
            catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        }
    }
}
