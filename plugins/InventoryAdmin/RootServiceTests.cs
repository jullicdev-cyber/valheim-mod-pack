using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using WC = ValheimModPack.WorldCharacters.Plugin;
using BepInEx.Configuration;

namespace ValheimModPack.InventoryAdmin
{
    internal static class RootServiceTests
    {
        private const string Bob = "Steam_76561198000000002", Alice = "Steam_76561198000000001";
        private const long BobPeer = 82, AlicePeer = 81;
        private static int checks;
        private static void Check(bool okay, string text) { ++checks; if (!okay) throw new Exception("FAIL: " + text); }
        private static object Get(object target, string field) { return target.GetType().GetField(field, BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).GetValue(target); }
        private static object Call(object target, string method, params object[] args)
        {
            try { return target.GetType().GetMethod(method, BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(target, args); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }
        private static bool StaticBool(string method, params object[] args)
        { return (bool)typeof(Plugin).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args); }
        private static void Reject(Action action, string text)
        {
            ++checks; try { action(); } catch (UnauthorizedAccessException) { return; } catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; } catch (ArgumentException) { return; } catch (IOException) { return; } throw new Exception("FAIL: accepted " + text);
        }
        private static byte[] Packet(int kind, Action<BinaryWriter> fields, bool trailing)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { writer.Write(1); writer.Write(999L); writer.Write(kind); fields(writer); if (trailing) writer.Write((byte)99); writer.Flush(); return stream.ToArray(); }
        }
        private static void Text(BinaryWriter writer, string text) { PolicyBinary.WriteString(writer, text, 2048); }
        private static void Bytes(BinaryWriter writer, byte[] bytes) { PolicyBinary.WriteBytes(writer, bytes, InventoryCodec.MaximumItemBytes); }
        private static void Dispatch(Plugin plugin, long actor, int kind, Action<BinaryWriter> fields)
        { Call(plugin, "Dispatch", actor, Packet(kind, fields, false)); }
        private static string Id() { return Guid.NewGuid().ToString("N"); }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1) throw new ArgumentException("Expected isolated state directory.");
                Directory.CreateDirectory(args[0]); BepInEx.Paths.BepInExRootPath = Path.Combine(args[0], "BepInEx");
                ZNet.instance = new ZNet(); Player.m_localPlayer = new Player(); Time.realtimeSinceStartup = 10;
                ZNet.instance.Peers.Add(new ZNetPeer { m_uid = AlicePeer, m_playerID = 101, m_socket = new ZSteamSocket { Owner = Alice.Substring(6) } });
                ZNet.instance.Peers.Add(new ZNetPeer { m_uid = BobPeer, m_playerID = 102, m_socket = new ZSteamSocket { Owner = Bob.Substring(6) } });
                WC.Durable[ZNet.Uid] = WC.Durable[AlicePeer] = WC.Durable[BobPeer] = 50;
                var plugin = new Plugin(); Call(plugin, "Awake"); Call(plugin, "EnsureSession");
                try { Permissions(plugin); Packets(plugin); ClientPacketValidation(plugin); Operations(plugin); CheckpointFailures(plugin); RecoveryCommands(plugin); SourcePayloadValidation(plugin); Transport(); Shortcuts(plugin); }
                finally { Call(plugin, "OnDestroy"); }
                System.Console.WriteLine("InventoryAdmin actual service checks passed: " + checks); return 0;
            }
            catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
        }
        private static void Permissions(Plugin plugin)
        {
            var store = (PermissionStore)Get(plugin, "permissions");
            Check((bool)Call(plugin, "Allowed", ZNet.Uid), "authenticated local host allowed");
            Check(!(bool)Call(plugin, "Allowed", AlicePeer), "ordinary peer denied");
            Reject(delegate { Dispatch(plugin, AlicePeer, 2, w => Text(w, Id())); }, "ordinary roster access");
            Reject(delegate { Dispatch(plugin, AlicePeer, 18, w => { w.Write(BobPeer); w.Write(true); }); }, "ordinary role assignment");
            Check(!store.IsAdministrator(999, Bob), "unauthorized role assignment has no effect");
            Dispatch(plugin, ZNet.Uid, 18, w => { w.Write(AlicePeer); w.Write(true); });
            Check(store.IsAdministrator(999, Alice) && (bool)Call(plugin, "Allowed", AlicePeer), "host assigns authenticated Steam account");
            Reject(delegate { Dispatch(plugin, AlicePeer, 18, w => { w.Write(BobPeer); w.Write(true); }); }, "delegated administrator cannot grant others");
            Check(!store.IsAdministrator(999, Bob), "delegated assignment has no effect");
            Dispatch(plugin, ZNet.Uid, 18, w => { w.Write(AlicePeer); w.Write(false); });
            Check(!(bool)Call(plugin, "Allowed", AlicePeer), "revocation immediate");
            WC.Durable.Remove(AlicePeer); store.SetAdministrator(999, true, Alice, true);
            Check(!(bool)Call(plugin, "Allowed", AlicePeer), "administrator not ready for this character denied"); WC.Durable[AlicePeer] = 50;
        }
        private static void Packets(Plugin plugin)
        {
            var store = (PermissionStore)Get(plugin, "permissions");
            byte[] malicious = Packet(18, w => { w.Write(BobPeer); w.Write(true); }, true);
            Reject(delegate { Call(plugin, "Dispatch", ZNet.Uid, malicious); }, "trailing data on role command");
            Check(!store.IsAdministrator(999, Bob), "trailing malformed command cannot grant role before validation");
            byte[] badBool = Packet(18, w => { w.Write(BobPeer); w.Write((byte)2); }, false);
            Reject(delegate { Call(plugin, "Dispatch", ZNet.Uid, badBool); }, "noncanonical command boolean");
            Check(!store.IsAdministrator(999, Bob), "noncanonical role boolean cannot grant role");
            byte[] wrongWorld = Packet(18, w => { w.Write(BobPeer); w.Write(true); }, false); wrongWorld[4] = 1;
            Reject(delegate { Call(plugin, "Dispatch", ZNet.Uid, wrongWorld); }, "different world command");
            Check(!store.IsAdministrator(999, Bob), "wrong world cannot alter roles");
            int pending = ((System.Collections.IDictionary)Get(plugin,"reads")).Count;
            string id = Id(); byte[] trailingViewRequest = Packet(4,w=>{Text(w,id);w.Write(BobPeer);},true);
            Reject(delegate { Call(plugin,"Dispatch",ZNet.Uid,trailingViewRequest); }, "trailing bytes on view request");
            Check(((System.Collections.IDictionary)Get(plugin,"reads")).Count == pending, "malformed view request does not reserve request or send target command");
        }
        private static void ClientPacketValidation(Plugin plugin)
        {
            int captures = ((System.Collections.IDictionary)Get(plugin,"captures")).Count;
            int local = ((System.Collections.IDictionary)Get(plugin,"localOperations")).Count;
            string request = Id();
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(1,w=>{w.Write(true);Text(w,Alice);},true)); }, "trailing authorization welcome");
            Check(!(bool)Get(plugin,"authorized"), "malformed welcome does not change client permission");
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(5,w=>Text(w,request),true)); }, "trailing inventory capture command");
            Check(((System.Collections.IDictionary)Get(plugin,"captures")).Count == captures, "malformed capture command cannot sample or cache inventory");
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(11,w=>{Text(w,request);Bytes(w,new byte[]{1,2,3});},true)); }, "trailing destination check");
            Check(((System.Collections.IDictionary)Get(plugin,"localOperations")).Count == local, "malformed destination check cannot reserve an operation");
            int removed = NativeAdapter.Removed, added = NativeAdapter.Added;
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(13,w=>Text(w,request),true)); }, "trailing removal command");
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(15,w=>{Text(w,request);Bytes(w,new byte[]{1,2,3});},true)); }, "trailing insertion command");
            Check(NativeAdapter.Removed == removed && NativeAdapter.Added == added, "malformed mutation commands cannot change any item");
            var window = (AdminWindow)Get(plugin,"window"); window.SetBusy(true); string status = window.Status;
            Reject(delegate { Call(plugin,"Dispatch",0L,Packet(17,w=>{Text(w,request);w.Write(true);Text(w,"fake completed");},true)); }, "trailing operation result");
            Check(window.Busy && window.Status == status, "malformed result cannot clear busy state or claim completion"); window.SetBusy(false);
        }
        private static void Operations(Plugin plugin)
        {
            var journal = (TransactionJournal)Get(plugin, "journal"); string viewId = Id(), slotId = Id();
            var view = new InventoryView { RequestId = viewId, TargetPeerId = BobPeer, TargetOwner = Bob, TargetCharacter = 102, Revision = 7,
                Items = new List<ItemInfo> { new ItemInfo { SlotId = slotId, Fingerprint = InventoryCodec.Fingerprint(new byte[] { 1,2,3 }), Prefab = "Wood", Name = "Wood", Stack = 5, Quality = 1 } } };
            Time.realtimeSinceStartup += 1;
            Dispatch(plugin, ZNet.Uid, 4, w => { Text(w, viewId); w.Write(BobPeer); });
            byte[] encoded = InventoryCodec.EncodeInventoryView(view);
            Dispatch(plugin, AlicePeer, 6, w => { Text(w, viewId); w.Write(true); Text(w, ""); Bytes(w, encoded); });
            Check(((System.Collections.IDictionary)Get(plugin, "served")).Count == 0, "other peer cannot answer target view request");
            Dispatch(plugin, BobPeer, 6, w => { Text(w, viewId); w.Write(true); Text(w, ""); Bytes(w, encoded); });
            Check(((System.Collections.IDictionary)Get(plugin, "served")).Contains(viewId), "authenticated source view accepted");
            var mutation = new MutationRequest { RequestId = Id(), ViewRequestId = viewId, SlotId = slotId, Fingerprint = view.Items[0].Fingerprint,
                TargetPeerId = BobPeer, TargetCharacter = 102, Revision = 7, Count = 2, Operation = InventoryOperation.Take };
            Time.realtimeSinceStartup += 1;
            Dispatch(plugin, ZNet.Uid, 8, w => Bytes(w, InventoryCodec.EncodeMutationRequest(mutation)));
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Submitted, "authorized mutation begins submitted");
            byte[] blob = { 1,2,3 };
            Dispatch(plugin, AlicePeer, 10, w => { Text(w, mutation.RequestId); w.Write(true); Text(w, ""); Bytes(w, blob); });
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Submitted, "other peer cannot prepare target item");
            Dispatch(plugin, BobPeer, 10, w => { Text(w, mutation.RequestId); w.Write(true); Text(w, ""); Bytes(w, blob); });
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Prepared, "source preparation persists escrow");
            int queued = ((System.Collections.ICollection)Get(Get(plugin, "transport"), "queue")).Count;
            Dispatch(plugin, BobPeer, 10, w => { Text(w, mutation.RequestId); w.Write(true); Text(w, ""); Bytes(w, blob); });
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Prepared, "duplicate preparation cannot abort accepted transaction");
            Check(((System.Collections.ICollection)Get(Get(plugin, "transport"), "queue")).Count == queued, "duplicate source preparation cannot enqueue removal twice");
            Dispatch(plugin, ZNet.Uid, 12, w => { Text(w, mutation.RequestId); w.Write(false); Text(w, "replayed negative check"); });
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Prepared, "late duplicate destination check cannot abort removal in flight");
            WC.Durable[BobPeer] = 51;
            Dispatch(plugin, BobPeer, 14, w => { Text(w, mutation.RequestId); w.Write(true); w.Write(false); w.Write(51L); Text(w, ""); });
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.SourceRemoved && NativeAdapter.Added == 1, "verified source checkpoint delivers once");
            Dispatch(plugin, BobPeer, 14, w => { Text(w, mutation.RequestId); w.Write(true); w.Write(false); w.Write(51L); Text(w, ""); });
            Check(NativeAdapter.Added == 1, "duplicate source checkpoint cannot apply destination twice");
            WC.Durable[ZNet.Uid] = WC.Next;
            Call(plugin, "UpdateLocalOperations");
            Check(journal.Get(mutation.RequestId).Stage == InventoryTransactionStage.Committed && Get(plugin, "operation") == null, "destination durable checkpoint commits receipt");
            Dispatch(plugin, BobPeer, 14, w => { Text(w, mutation.RequestId); w.Write(true); w.Write(false); w.Write(51L); Text(w, ""); });
            Check(NativeAdapter.Added == 1, "late terminal checkpoint ignored");
        }
        private static MutationRequest BeginPrepared(Plugin plugin, InventoryOperation kind, bool prepare = true)
        {
            string viewId = Id(), slotId = Id();
            var view = new InventoryView { RequestId = viewId, TargetPeerId = BobPeer, TargetOwner = Bob, TargetCharacter = 102, Revision = 7,
                Items = new List<ItemInfo> { new ItemInfo { SlotId = slotId, Fingerprint = InventoryCodec.Fingerprint(new byte[] { 1,2,3 }), Prefab = "Wood", Name = "Wood", Stack = 5, Quality = 1 } } };
            Time.realtimeSinceStartup += 1;
            Dispatch(plugin, ZNet.Uid, 4, w => { Text(w, viewId); w.Write(BobPeer); });
            Dispatch(plugin, BobPeer, 6, w => { Text(w, viewId); w.Write(true); Text(w, ""); Bytes(w, InventoryCodec.EncodeInventoryView(view)); });
            var request = new MutationRequest { RequestId = Id(), ViewRequestId = viewId, SlotId = slotId, Fingerprint = view.Items[0].Fingerprint,
                TargetPeerId = BobPeer, TargetCharacter = 102, Revision = 7, Count = 2, Operation = kind };
            Time.realtimeSinceStartup += 1;
            Dispatch(plugin, ZNet.Uid, 8, w => Bytes(w, InventoryCodec.EncodeMutationRequest(request)));
            if (prepare) Dispatch(plugin, BobPeer, 10, w => { Text(w, request.RequestId); w.Write(true); Text(w, ""); Bytes(w, new byte[] { 1,2,3 }); });
            return request;
        }
        private static void CheckpointFailures(Plugin plugin)
        {
            var journal = (TransactionJournal)Get(plugin, "journal"); int before = NativeAdapter.Added;
            MutationRequest stale = BeginPrepared(plugin, InventoryOperation.Take); long old = WC.Durable[BobPeer];
            Dispatch(plugin, BobPeer, 14, w => { Text(w, stale.RequestId); w.Write(true); w.Write(false); w.Write(old); Text(w, ""); });
            Check(journal.Get(stale.RequestId).Stage == InventoryTransactionStage.NeedsRecovery && Get(plugin, "operation") == null, "old durable checkpoint cannot prove a new mutation");
            Check(NativeAdapter.Added == before, "old checkpoint does not apply destination item");
            MutationRequest undurable = BeginPrepared(plugin, InventoryOperation.Take);
            Dispatch(plugin, BobPeer, 14, w => { Text(w, undurable.RequestId); w.Write(true); w.Write(false); w.Write(WC.Durable[BobPeer] + 1); Text(w, ""); });
            Check(journal.Get(undurable.RequestId).Stage == InventoryTransactionStage.NeedsRecovery, "future uncommitted checkpoint rejected");
            Check(NativeAdapter.Added == before, "uncommitted checkpoint does not apply destination item");
            MutationRequest switched = BeginPrepared(plugin, InventoryOperation.Take);
            WC.Durable[BobPeer]++;
            ZNet.instance.Peers[1].m_playerID = 999;
            Dispatch(plugin, BobPeer, 14, w => { Text(w, switched.RequestId); w.Write(true); w.Write(false); w.Write(WC.Durable[BobPeer]); Text(w, ""); });
            Check(journal.Get(switched.RequestId).Stage == InventoryTransactionStage.NeedsRecovery, "changed character cannot acknowledge prior character mutation");
            Check(NativeAdapter.Added == before, "changed character checkpoint does not deliver"); ZNet.instance.Peers[1].m_playerID = 102;
            MutationRequest denied = BeginPrepared(plugin, InventoryOperation.Delete);
            Dispatch(plugin, BobPeer, 14, w => { Text(w, denied.RequestId); w.Write(false); w.Write(false); w.Write(0L); Text(w, "item changed before removal"); });
            Check(journal.Get(denied.RequestId).Stage == InventoryTransactionStage.Aborted, "explicit pre-effect source refusal may safely abort");
            MutationRequest uncertain = BeginPrepared(plugin, InventoryOperation.Delete);
            Dispatch(plugin, BobPeer, 14, w => { Text(w, uncertain.RequestId); w.Write(false); w.Write(true); w.Write(0L); Text(w, "save failed after removal"); });
            Check(journal.Get(uncertain.RequestId).Stage == InventoryTransactionStage.NeedsRecovery, "failure after potential removal freezes escrow");
            MutationRequest removed = BeginPrepared(plugin, InventoryOperation.Delete);
            WC.Durable[BobPeer]++;
            Dispatch(plugin, BobPeer, 14, w => { Text(w, removed.RequestId); w.Write(true); w.Write(false); w.Write(WC.Durable[BobPeer]); Text(w, ""); });
            Check(journal.Get(removed.RequestId).Stage == InventoryTransactionStage.Committed, "delete needs only its own confirmed durable checkpoint");
            Check(NativeAdapter.Added == before, "delete never delivers a copy");
        }
        private static void RecoveryCommands(Plugin plugin)
        {
            var journal = (TransactionJournal)Get(plugin, "journal");
            var request = new MutationRequest { RequestId = Id(), ViewRequestId = Id(), SlotId = Id(), Fingerprint = InventoryCodec.Fingerprint(new byte[] {1,2,3}),
                TargetPeerId = BobPeer, TargetCharacter = 102, Revision = 1, Count = 2, Operation = InventoryOperation.Take };
            journal.Begin(999, "local-host", Bob, 102, "local-host", 100, request); journal.SetPrepared(request.RequestId, new byte[] {1,2,3}); journal.MarkSourceRemoved(request.RequestId);
            NativeAdapter.CanReceive = false;
            var args = new Terminal.ConsoleEventArgs { Values = new[] {"ia", "retry", request.RequestId} }; Call(plugin, "Command", args);
            Check(journal.Get(request.RequestId).Stage == InventoryTransactionStage.SourceRemoved && Get(plugin,"operation") == null, "known recipient refusal leaves safely retryable source escrow");
            NativeAdapter.CanReceive = true;
            int before = NativeAdapter.Added; Call(plugin, "Command", args);
            Check(journal.Get(request.RequestId).Stage == InventoryTransactionStage.SourceRemoved && NativeAdapter.Added == before + 1, "retry sends escrow without removing source again");
            WC.Durable[ZNet.Uid] = WC.Next; Call(plugin, "UpdateLocalOperations");
            Check(journal.Get(request.RequestId).Stage == InventoryTransactionStage.Committed, "retried escrow commits only after fresh destination checkpoint");
            Call(plugin, "Command", args); Check(NativeAdapter.Added == before + 1, "repeating committed retry cannot duplicate destination");
            var otherWorld = InventoryCodec.DecodeMutationRequest(InventoryCodec.EncodeMutationRequest(request)); otherWorld.RequestId = Id();
            journal.Begin(888, "local-host", Bob, 102, "local-host", 100, otherWorld); journal.SetPrepared(otherWorld.RequestId, new byte[] {1,2,3}); journal.MarkSourceRemoved(otherWorld.RequestId); journal.RequireRecovery(otherWorld.RequestId, "disconnect");
            Call(plugin, "Command", new Terminal.ConsoleEventArgs { Values = new[] {"ia", "recover", otherWorld.RequestId, "CONFIRM-NOT-DELIVERED"} });
            Check(journal.Get(otherWorld.RequestId).Stage == InventoryTransactionStage.NeedsRecovery, "recovery command cannot release escrow from a different world");
            Check(NativeAdapter.Added == before + 1, "wrong world recovery cannot insert an item");
        }
        private static ZPackage Frame(string id, int total, int offset, byte[] bytes)
        { var package = new ZPackage(); package.Write(1); package.Write(id); package.Write(total); package.Write(offset); package.Write(bytes); return new ZPackage(package.GetArray()); }
        private static void Transport()
        {
            var sender = new WireTransport(); var receiver = new WireTransport(); var rpc = new ZRpc(); byte[] bytes = new byte[100007];
            for (int i=0;i<bytes.Length;++i) bytes[i] = (byte)(i % 251);
            sender.Clear(); sender.Send(rpc, bytes); Time.realtimeSinceStartup += .3f; sender.Tick();
            Check(rpc.Sent.Count == 4, "transport sends at most four bounded chunks per frame");
            sender.Tick(); Check(rpc.Sent.Count == 4, "transport cannot exceed available bandwidth without elapsed time");
            Time.realtimeSinceStartup += .3f; sender.Tick(); Check(rpc.Sent.Count == 7, "transport completes paced payload after time advances");
            byte[] result = null;
            for (int i=0;i<rpc.Sent.Count;++i)
            {
                Check(rpc.Sent[i].Size() <= 16384 + 128, "network chunk size bound " + i);
                byte[] partial = receiver.Receive(rpc, new ZPackage(rpc.Sent[i].GetArray()));
                Check(i == rpc.Sent.Count - 1 ? partial != null : partial == null, "payload delivered only on last ordered chunk " + i);
                if (partial != null) result = partial;
            }
            Check(result != null && result.Length == bytes.Length, "full payload size preserved");
            Check(InventoryCodec.Fingerprint(result) == InventoryCodec.Fingerprint(bytes), "segmented transfer preserves every byte");
            Check((long)Get(receiver,"incomingBytes") == 0, "completed transfer releases receive byte budget");
            string id = Id(); var one = new ZRpc();
            Reject(delegate { receiver.Receive(one, Frame(id, 4, 1, new byte[] {1})); }, "chunk without prefix");
            receiver.Receive(one, Frame(id, 4, 0, new byte[] {1}));
            Reject(delegate { receiver.Receive(one, Frame(id, 4, 0, new byte[] {1})); }, "overlapping concurrent message");
            Reject(delegate { receiver.Receive(one, Frame(id, 4, 2, new byte[] {1})); }, "out of order chunk");
            Reject(delegate { receiver.Receive(one, Frame(Id(), 4, 1, new byte[] {1})); }, "changed stream identifier");
            receiver.Remove(one);
            Check(receiver.Receive(one, Frame(id, 2, 0, new byte[] {1,2})).Length == 2, "explicit discard releases per-peer assembly");
            Reject(delegate { receiver.Receive(one, Frame(id, InventoryCodec.MaximumItemBytes + 65537, 0, new byte[] {1})); }, "oversized advertised payload");
            Reject(delegate { receiver.Receive(one, Frame(id, 1, 0, new byte[] {1,2})); }, "chunk exceeds advertised payload");
            receiver.Receive(one, Frame(id, 4, 0, new byte[] {1})); Time.realtimeSinceStartup += 121; receiver.Tick();
            Check(receiver.Receive(one, Frame(id, 2, 0, new byte[] {1,2})).Length == 2, "expired assembly releases capacity");
            var disconnected = new ZRpc { Connected = false }; Reject(delegate { sender.Send(disconnected, new byte[] {1}); }, "disconnected outgoing peer");
            sender.Clear(); for(int i=0;i<64;++i) sender.Send(rpc,new byte[] {1}); Reject(delegate { sender.Send(rpc,new byte[] {1}); }, "bounded outgoing message count");
            sender.Clear(); Check((long)Get(sender,"queuedBytes") == 0, "session clear releases queued byte budget");
            var largePeers = new List<ZRpc>();
            for(int i=0;i<4;++i)
            {
                var connection = new ZRpc(); largePeers.Add(connection);
                receiver.Receive(connection,Frame(Id(),InventoryCodec.MaximumItemBytes,0,new byte[]{1}));
            }
            Check((long)Get(receiver,"incomingBytes") == 16*1024*1024, "aggregate receive budget permits exactly bounded payloads");
            var excess = new ZRpc();
            Reject(delegate { receiver.Receive(excess,Frame(Id(),2,0,new byte[]{1})); }, "aggregate receive capacity rejects before allocating another payload");
            Check((long)Get(receiver,"incomingBytes") == 16*1024*1024, "rejected payload cannot reserve byte budget");
            receiver.Remove(largePeers[0]);
            Check((long)Get(receiver,"incomingBytes") == 12*1024*1024, "disconnect releases whole reserved payload");
            receiver.Receive(excess,Frame(Id(),2,0,new byte[]{1}));
            Check((long)Get(receiver,"incomingBytes") == 12*1024*1024+2, "released receive capacity supports new peer");
            Time.realtimeSinceStartup += 121; receiver.Tick();
            Check((long)Get(receiver,"incomingBytes") == 0, "all expired streams release aggregate capacity");
            receiver.Receive(excess,Frame(Id(),4,0,new byte[]{1})); receiver.Clear();
            Check((long)Get(receiver,"incomingBytes") == 0 && ((System.Collections.IDictionary)Get(receiver,"incoming")).Count == 0,
                "session clear discards unfinished streams and receive reservations");
        }
        private static void SourcePayloadValidation(Plugin plugin)
        {
            var journal = (TransactionJournal)Get(plugin,"journal"); int before = NativeAdapter.Added;
            MutationRequest empty = BeginPrepared(plugin, InventoryOperation.Take, false);
            var window = (AdminWindow)Get(plugin,"window"); window.SetBusy(true);
            Dispatch(plugin,BobPeer,10,w=>{Text(w,empty.RequestId);w.Write(true);Text(w,"");Bytes(w,new byte[0]);});
            Check(journal.Get(empty.RequestId).Stage == InventoryTransactionStage.Aborted && Get(plugin,"operation")==null,
                "empty native blob aborts immediately before any removal request");
            Check(!window.Busy && window.Status.IndexOf("Missing item blob", StringComparison.Ordinal) >= 0,
                "invalid native blob reports the error and releases moderator busy state immediately");
            Check(NativeAdapter.Added == before && journal.Get(empty.RequestId).ItemBytes.Length==0,
                "empty native blob cannot deliver an item or retain fictional escrow");
            for(int field=0;field<5;++field)
            {
                MutationRequest request = BeginPrepared(plugin, InventoryOperation.Take, false);
                NativeAdapter.BlobStack = field==0 ? 3 : 2; NativeAdapter.BlobPrefab = field==1 ? "SwordSilver" : "Wood";
                NativeAdapter.BlobQuality = field==2 ? 2 : 1; NativeAdapter.BlobVariant = field==3 ? 1 : 0;
                NativeAdapter.BlobDurability = field==4 ? 99 : 0;
                try
                {
                    try { Dispatch(plugin,BobPeer,10,w=>{Text(w,request.RequestId);w.Write(true);Text(w,"");Bytes(w,new byte[]{1,2,3});}); }
                    catch(InvalidDataException) { }
                    Check(journal.Get(request.RequestId).Stage == InventoryTransactionStage.Aborted && Get(plugin,"operation")==null,
                        "mismatching prepared native item field safely aborts before removal " + field);
                    Check(NativeAdapter.Added == before,"invalid prepared payload does not deliver " + field);
                }
                finally { NativeAdapter.BlobStack=2;NativeAdapter.BlobPrefab="Wood";NativeAdapter.BlobQuality=1;NativeAdapter.BlobVariant=0;NativeAdapter.BlobDurability=0; }
            }
        }
        private static void Keys(KeyCode[] held, params KeyCode[] down)
        { ZInput.Held.Clear();ZInput.Down.Clear();foreach(KeyCode key in held) ZInput.Held.Add(key);foreach(KeyCode key in down) ZInput.Down.Add(key); }
        private static void Shortcuts(Plugin plugin)
        {
            var normal = new KeyboardShortcut(KeyCode.F9,KeyCode.LeftControl);
            Keys(new[]{KeyCode.F9,KeyCode.LeftControl},KeyCode.F9);
            Check(StaticBool("ShortcutHeld",normal) && StaticBool("ShortcutDown",normal),"default Ctrl+F9 reads native ZInput boundary");
            Keys(new[]{KeyCode.F9,KeyCode.RightControl},KeyCode.F9);
            Check(StaticBool("ShortcutDown",normal),"right Ctrl triggers left-Ctrl-family default");
            Keys(new[]{KeyCode.F9,KeyCode.LeftControl});
            Check(StaticBool("ShortcutHeld",normal) && !StaticBool("ShortcutDown",normal),"held chord does not retrigger without key-down edge");
            Keys(new[]{KeyCode.LeftControl},KeyCode.F9);
            Check(!StaticBool("ShortcutDown",normal),"inconsistent released main key cannot open window");
            Keys(new[]{KeyCode.F9},KeyCode.F9);
            Check(!StaticBool("ShortcutDown",normal),"missing modifier rejects default shortcut");
            foreach(KeyCode extra in new[]{KeyCode.LeftShift,KeyCode.RightShift,KeyCode.LeftAlt,KeyCode.RightAlt,KeyCode.LeftCommand,KeyCode.RightCommand})
            {
                Keys(new[]{KeyCode.F9,KeyCode.LeftControl,extra},KeyCode.F9);
                Check(!StaticBool("ShortcutDown",normal),"extra modifier rejects shortcut: "+extra);
            }
            var rebound = new KeyboardShortcut(KeyCode.F8,KeyCode.LeftShift,KeyCode.LeftAlt);
            Keys(new[]{KeyCode.F8,KeyCode.RightShift,KeyCode.RightAlt},KeyCode.F8);
            Check(StaticBool("ShortcutDown",rebound),"rebound main key and right modifier families work");
            Keys(new[]{KeyCode.F9,KeyCode.LeftControl},KeyCode.F9);
            Check(!StaticBool("ShortcutDown",rebound),"old binding ceases triggering after rebind");
            Check(!StaticBool("ShortcutDown",new KeyboardShortcut(KeyCode.None,KeyCode.LeftControl)),"unbound shortcut never triggers");
            var entry = (ConfigEntry<KeyboardShortcut>)Get(plugin,"shortcut"); entry.Value = normal;
            var window = (AdminWindow)Get(plugin,"window"); window.Hide();
            Keys(new[]{KeyCode.F9,KeyCode.RightControl},KeyCode.F9);Time.frameCount++;Call(plugin,"Update");
            Check(window.IsVisible && (KeyCode)Get(plugin,"claimed")==KeyCode.F9,"actual update opens moderator window and claims ZInput main key");
            Keys(new[]{KeyCode.F9,KeyCode.RightControl});Time.frameCount++;Call(plugin,"Update");
            Check(window.IsVisible,"holding keys does not toggle window repeatedly");
            Keys(new[]{KeyCode.RightControl});Time.frameCount++;Call(plugin,"Update");
            Check((KeyCode)Get(plugin,"claimed")==KeyCode.F9,"release frame still claims native key-up");
            Time.frameCount++;Call(plugin,"Update");
            Check((KeyCode)Get(plugin,"claimed")==KeyCode.None,"next native ZInput release frame clears claim");
            Keys(new[]{KeyCode.F9,KeyCode.LeftControl},KeyCode.F9);Time.frameCount++;Call(plugin,"Update");
            Check(!window.IsVisible,"second fresh chord closes moderator window");
            Keys(new KeyCode[0]);Time.frameCount++;Call(plugin,"Update");Time.frameCount++;Call(plugin,"Update");
            Check((KeyCode)Get(plugin,"claimed")==KeyCode.None,"closing stroke claim clears after release");
            entry.Value = rebound;
            Keys(new[]{KeyCode.F8,KeyCode.RightShift,KeyCode.RightAlt},KeyCode.F8);Time.frameCount++;Call(plugin,"Update");
            Check(window.IsVisible && (KeyCode)Get(plugin,"claimed")==KeyCode.F8,"actual update immediately honors rebound shortcut");
            Keys(new KeyCode[0]);window.Hide();Time.frameCount++;Call(plugin,"Update");Time.frameCount++;Call(plugin,"Update");entry.Value=normal;
        }
    }
}
