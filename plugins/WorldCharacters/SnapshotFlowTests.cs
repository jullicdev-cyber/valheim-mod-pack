using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ValheimModPack.WorldCharacters;

// Real disk integration runner; compile separately from the other test mains.
internal static class SnapshotFlowTests
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        ++passed;
    }
    private static void Refuse(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { ++passed; return; }
        throw new Exception("Did not refuse: " + name);
    }
    private static byte[] PlayerData(int amount)
    {
        using (var s = new MemoryStream())
        using (var w = new BinaryWriter(s))
        {
            w.Write(33); for (int i = 0; i < 4; ++i) w.Write(25f); w.Write("GP_Eikthyr"); w.Write(0f);
            w.Write(109); w.Write((ushort)1);
            w.Write(500); w.Write((byte)1); w.Write((byte)6); w.Write((byte)0); w.Write((byte)(8 | 64 | 128));
            w.Write((ushort)amount); w.Write(123456); w.Write((byte)2);
            w.Write("EpicLoot"); w.Write("retained enchantment"); w.Write("eaqs_slot"); w.Write("head"); w.Write((byte)0);
            w.Write(0); w.Flush(); return s.ToArray();
        }
    }
    private static byte[] WorldData(byte mapMarker, float position)
    {
        using (var s = new MemoryStream())
        using (var w = new BinaryWriter(s))
        {
            w.Write(1);
            for (int i = 0; i < 4; ++i)
            {
                if (i < 3) w.Write((byte)0);
                for (int j = 0; j < 3; ++j) w.Write(position + i + j);
            }
            w.Write(3); w.Write(new byte[] { mapMarker, (byte)(mapMarker + 1), (byte)(mapMarker + 2) });
            w.Flush(); return s.ToArray();
        }
    }
    private static CharacterState State(int amount, byte mapMarker, float position)
    {
        return new CharacterState { World = 11, Character = 44, Owner = "Steam_test", Name = "Викинг",
            Build = "test", Revision = 1, Player = PlayerData(amount), WorldData = WorldData(mapMarker, position) };
    }
    private static long Retained(CharacterState state)
    {
        return 512L + 4L * (state.Player.Length + state.WorldData.Length);
    }
    private static CharacterSession Load(StateStore store)
    {
        var s = new CharacterSession(store.Read(11, "Steam_test", 44)); s.MarkLoaded(s.Token); return s;
    }
    private static void BlockedAdmissionMapAndFinal(string root)
    {
        using (var store = new StateStore(root))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var writer = new SnapshotWriter(2))
        {
            store.Save(State(10, 8, 0), 0);
            var session = Load(store); var acknowledgements = new List<long>();
            int ownerThread = Thread.CurrentThread.ManagedThreadId, workerThread = 0;
            var firstUpdate = State(20, 20, 10);
            Check(writer.CanEnqueue(Retained(firstUpdate)), "capacity checked before first sequence reservation");
            var first = session.Stage(session.Token, 1, firstUpdate, false);
            Check(writer.TryEnqueue(Retained(first), () =>
            {
                workerThread = Thread.CurrentThread.ManagedThreadId; entered.Set();
                if (!release.WaitOne(10000)) throw new TimeoutException("Blocked I/O fixture was not released.");
                store.Save(first, first.Revision - 1); return first;
            }, (result, error, elapsed) =>
            {
                if (error != null) throw new Exception("First real save failed.", error);
                Check(Thread.CurrentThread.ManagedThreadId == ownerThread, "first durability callback runs on creating thread");
                session.Committed((CharacterState)result, 1, false); acknowledgements.Add(1);
            }), "first snapshot admitted");
            Check(entered.WaitOne(5000), "worker reaches blocked I/O");
            Check(workerThread != ownerThread, "disk work runs off admission thread");
            Check(acknowledgements.Count == 0 && session.LastSequence == 0, "no ACK before disk write completes");
            var secondUpdate = State(30, 99, 30);
            byte[] positions = StateCodec.WithoutMap(secondUpdate.WorldData);
            secondUpdate.WorldData = session.RetainAcceptedMap(positions);
            Check(secondUpdate.WorldData[59] == 20 && session.State.WorldData[59] == 8, "position-only pending update retains new accepted map rather than old durable map");
            var admissionClock = Stopwatch.StartNew();
            Check(writer.CanEnqueue(Retained(secondUpdate)), "second pending snapshot fits queue");
            var second = session.Stage(session.Token, 2, secondUpdate, false);
            Check(writer.TryEnqueue(Retained(second), () =>
            {
                store.Save(second, second.Revision - 1); return second;
            }, (result, error, elapsed) =>
            {
                if (error != null) throw new Exception("Second real save failed.", error);
                session.Committed((CharacterState)result, 2, false); acknowledgements.Add(2);
            }), "second snapshot admitted while first I/O remains blocked");
            admissionClock.Stop();
            Check(admissionClock.ElapsedMilliseconds < 1000 && !release.WaitOne(0), "main admission returns without waiting for blocked I/O");
            Check(writer.PendingCount == 2 && !writer.CanEnqueue(512), "queue is bounded while worker is blocked");
            Check(session.AcceptedSequence == 2 && session.LastSequence == 0, "full queue check does not consume another staged sequence");
            Refuse(() => session.Committed(second, 2, false), "out-of-order completion cannot advance durability");
            Check(acknowledgements.Count == 0 && session.LastSequence == 0, "rejected completion cannot ACK");
            release.Set(); writer.Barrier();
            Check(acknowledgements.Count == 0 && session.LastSequence == 0, "disk barrier alone does not dispatch completion or ACK");
            Check(writer.PendingCount == 2 && !writer.CanEnqueue(512), "completed undrained results stay charged against bounded capacity");
            var beforeDrain = store.Read(11, "Steam_test", 44);
            Check(writer.Drain() == 2, "both ready writes dispatched on main thread");
            Check(beforeDrain.Revision == 3 && beforeDrain.Player.SequenceEqual(second.Player), "real durable file holds latest FIFO snapshot before callback");
            Check(acknowledgements.SequenceEqual(new long[] { 1, 2 }), "ACK order follows real durable FIFO writes");
            Check(session.State.Revision == 3 && session.LastSequence == 2 && writer.IsIdle, "session converges to durable disk after drain");
            Check(writer.CanEnqueue(512), "drain releases queue capacity");
            var finalUpdate = second.Copy(); finalUpdate.Player = PlayerData(40);
            var final = session.Stage(session.Token, 3, finalUpdate, true);
            Check(writer.TryEnqueue(Retained(final), () =>
            {
                store.Save(final, final.Revision - 1); return final;
            }, (result, error, elapsed) =>
            {
                if (error != null) throw new Exception("Final real save failed.", error);
                session.Committed((CharacterState)result, 3, true); acknowledgements.Add(3);
            }), "final snapshot admitted after backpressure cleared");
            Check(session.AcceptedFinal && !session.Closed, "accepted final cannot close session before durability callback");
            Refuse(() => session.Stage(session.Token, 4, finalUpdate, false), "new stage refused behind queued final");
            writer.Barrier(); writer.Drain();
            Check(session.Closed && acknowledgements.SequenceEqual(new long[] { 1, 2, 3 }), "final closes only after prior writes and final ACK");
            var durable = store.Read(11, "Steam_test", 44);
            Check(durable.Revision == 4 && durable.Player.SequenceEqual(final.Player), "final inventory survives actual state file replacement");
            Check(durable.WorldData.Skip(59).SequenceEqual(new byte[] { 20, 21, 22 }), "new map survives position-only and final writes");
            Check(durable.WorldData.Take(55).SequenceEqual(positions.Take(55)), "new position survives map retention");
        }
    }
    private static void DisconnectReconnect(string root)
    {
        using (var store = new StateStore(root))
        using (var writer = new SnapshotWriter())
        {
            store.Save(State(10, 8, 0), 0); var oldSession = Load(store);
            var next = oldSession.Stage(oldSession.Token, 1, State(55, 20, 30), false);
            bool leased = true, connected = false; int acknowledgements = 0;
            Check(writer.TryEnqueue(Retained(next), () =>
            {
                store.Save(next, next.Revision - 1); return next;
            }, (result, error, elapsed) =>
            {
                if (error != null) throw new Exception("Disconnect save failed.", error);
                oldSession.Committed((CharacterState)result, 1, false);
                if (connected) ++acknowledgements;
            }), "disconnect fixture accepts outstanding save");
            // Removal must flush while the lease still prevents a new handshake.
            writer.Barrier(); Check(leased, "lease retained through disk barrier"); writer.Drain(); leased = false;
            var restored = store.Read(11, "Steam_test", 44); var newSession = new CharacterSession(restored);
            newSession.MarkLoaded(newSession.Token);
            Check(!leased && acknowledgements == 0 && oldSession.LastSequence == 1, "disconnected peer progress commits without emitting stale ACK");
            Check(newSession.State.Revision == 2 && newSession.State.Player.SequenceEqual(next.Player), "reconnect reads latest flushed inventory");
            Check(newSession.LastSequence == 0 && newSession.AcceptedSequence == 0, "reconnect sequence belongs to a new session");
            Refuse(() => newSession.Stage(oldSession.Token, 1, State(99, 20, 30), false), "old token cannot overwrite reconnected character");
        }
    }
    private static void FailedWriteAndDependentRevision(string root)
    {
        using (var store = new StateStore(root))
        using (var writer = new SnapshotWriter())
        {
            var original = State(10, 8, 0); store.Save(original, 0); var session = Load(store);
            var first = session.Stage(session.Token, 1, State(20, 20, 10), false);
            var second = session.Stage(session.Token, 2, State(30, 30, 20), false);
            var errors = new List<Exception>(); int acknowledgements = 0;
            Check(writer.TryEnqueue(Retained(first), () =>
            {
                // Deterministic fault immediately before the first real disk write.
                throw new IOException("Injected disk failure before state replacement.");
            }, (result, error, elapsed) =>
            {
                if (error != null) errors.Add(error);
                else { session.Committed((CharacterState)result, 1, false); ++acknowledgements; }
            }), "failing write admitted");
            Check(writer.TryEnqueue(Retained(second), () =>
            {
                store.Save(second, second.Revision - 1); return second;
            }, (result, error, elapsed) =>
            {
                if (error != null) errors.Add(error);
                else { session.Committed((CharacterState)result, 2, false); ++acknowledgements; }
            }), "dependent later revision admitted");
            writer.Barrier(); writer.Drain();
            Check(errors.Count == 2 && errors[0] is IOException && errors[1] is InvalidDataException, "later real write refuses stale expected revision after earlier I/O failure");
            Check(acknowledgements == 0 && session.LastSequence == 0 && session.State.Revision == 1, "failed and dependent snapshots never become durable ACKs");
            var retained = store.Read(11, "Steam_test", 44);
            Check(StateCodec.Encode(retained).SequenceEqual(StateCodec.Encode(original)), "original file remains byte-equivalent after failed dependent sequence");
            Check(writer.IsIdle, "error completions release queue capacity");
            var reconnect = Load(store); var retry = reconnect.Stage(reconnect.Token, 1, State(40, 40, 40), true);
            Check(writer.TryEnqueue(Retained(retry), () =>
            {
                store.Save(retry, retry.Revision - 1); return retry;
            }, (result, error, elapsed) =>
            {
                if (error != null) throw new Exception("Retry disk save failed.", error);
                reconnect.Committed((CharacterState)result, 1, true); ++acknowledgements;
            }), "fresh session retries from last durable revision");
            writer.Barrier(); writer.Drain();
            Check(reconnect.Closed && acknowledgements == 1 && store.Read(11, "Steam_test", 44).Revision == 2, "failed session cannot skip revision but reconnect can save correctly");
        }
    }
    private static void StaleGeneration(string root)
    {
        using (var store = new StateStore(root))
        using (var writer = new SnapshotWriter())
        {
            store.Save(State(10, 8, 0), 0); var oldSession = Load(store);
            var next = oldSession.Stage(oldSession.Token, 1, State(50, 20, 20), false);
            int generation = 1, capturedGeneration = generation, acknowledgements = 0;
            Check(writer.TryEnqueue(Retained(next), () =>
            {
                store.Save(next, next.Revision - 1); return next;
            }, (result, error, elapsed) =>
            {
                if (capturedGeneration != generation) return;
                if (error != null) throw new Exception("Generation disk save failed.", error);
                oldSession.Committed((CharacterState)result, 1, false); ++acknowledgements;
            }), "old-generation outstanding write accepted");
            // Emulate an already detached session: disk data is still preserved,
            // while callbacks must not send to the new network generation.
            ++generation; writer.Barrier(); writer.Drain();
            Check(acknowledgements == 0 && oldSession.LastSequence == 0, "old-generation callback cannot ACK or mutate a new session");
            Check(store.Read(11, "Steam_test", 44).Revision == 2 && writer.IsIdle, "stale callback does not discard admitted durable save");
            var reconnect = Load(store);
            Check(reconnect.State.Player.SequenceEqual(next.Player), "new generation can recover old admitted progress from disk");
        }
    }
    private static void GuestProducedAndSent(string root)
    {
        using (var store = new StateStore(root))
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var writer = new SnapshotWriter())
        {
            long produced = 0, sent = 0, localDurable = 0, acknowledged = 0;
            bool finalProduced = false; var packets = new List<byte[]>();
            Func<CharacterState, bool, bool> produce = (state, final) =>
            {
                if (!writer.CanEnqueue(Retained(state))) return false;
                long sequence = produced + 1;
                bool accepted = writer.TryEnqueue(Retained(state), () =>
                {
                    if (sequence == 1)
                    {
                        entered.Set();
                        if (!release.WaitOne(10000)) throw new TimeoutException("Guest recovery fixture was not released.");
                    }
                    store.Archive("client-recovery", state); return StateCodec.Encode(state);
                }, (result, error, elapsed) =>
                {
                    if (error != null) throw new Exception("Guest recovery save failed.", error);
                    localDurable = sequence; packets.Add((byte[])result); sent = sequence;
                });
                if (accepted) { produced = sequence; if (final) finalProduced = true; }
                return accepted;
            };
            Check(produce(State(20, 20, 20), false), "guest normal recovery admitted");
            Check(entered.WaitOne(5000), "guest recovery reaches blocked I/O");
            Check(produce(State(30, 30, 30), true), "guest final produced while normal recovery is pending");
            Check(finalProduced && produced == 2 && sent == 0 && localDurable == 0, "guest production is separate from local durability and transmission");
            Check(!(localDurable >= produced && acknowledged >= produced), "empty sent cursor cannot permit early final logout");
            release.Set(); writer.Barrier();
            Check(sent == 0 && packets.Count == 0, "worker cannot transmit recovery bytes before main-thread completion");
            writer.Drain();
            Check(localDurable == 2 && sent == 2 && packets.Count == 2, "guest transmits FIFO snapshots only after local recovery is durable");
            Check(StateCodec.Decode(packets[1]).Player.SequenceEqual(PlayerData(30)), "final network packet retains full final inventory");
            acknowledged = 1;
            Check(!(localDurable >= produced && acknowledged >= produced), "normal ACK does not close logout with a newer produced final");
            acknowledged = 2;
            Check(localDurable >= produced && acknowledged >= produced, "final logout permitted only after local durability and final host ACK");
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Environment.CurrentDirectory,
                ".cache", "snapshot-flow-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            BlockedAdmissionMapAndFinal(Path.Combine(root, "fifo"));
            DisconnectReconnect(Path.Combine(root, "disconnect"));
            FailedWriteAndDependentRevision(Path.Combine(root, "failure"));
            StaleGeneration(Path.Combine(root, "generation"));
            GuestProducedAndSent(Path.Combine(root, "guest"));
            Console.WriteLine("PASS: " + passed + " snapshot integration assertions (real disk FIFO, backpressure, map retention, final durability, reconnect, failure and guest production).");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
