using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimModPack.WorldCharacters;

// Standalone runner: compile separately from the original synchronous Tests.cs.
internal static class SessionAsyncTests
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
        catch (IOException) { ++passed; return; }
        catch (OverflowException) { ++passed; return; }
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
    private static byte[] WorldData()
    {
        using (var s = new MemoryStream())
        using (var w = new BinaryWriter(s))
        {
            w.Write(1);
            for (int i = 0; i < 4; ++i)
            {
                if (i < 3) w.Write((byte)0);
                for (int j = 0; j < 3; ++j) w.Write((float)(i + j));
            }
            w.Write(3); w.Write(new byte[] { 8, 9, 10 }); w.Flush(); return s.ToArray();
        }
    }
    private static CharacterState State(long revision)
    {
        return new CharacterState { World = 11, Character = 44, Owner = "Steam_test", Name = "Викинг",
            Build = "test", Revision = revision, Player = PlayerData(10), WorldData = WorldData() };
    }
    private static CharacterSession Loaded()
    {
        var s = new CharacterSession(State(7)); s.MarkLoaded(s.Token); return s;
    }
    private static void ManyPending()
    {
        var session = Loaded(); var pending = new List<CharacterState>();
        for (int i = 1; i <= 32; ++i)
        {
            var update = State(0); update.Player = PlayerData(i); update.Name = "client rename";
            pending.Add(session.Stage(session.Token, i, update, false));
            Check(session.State.Revision == 7 && session.LastSequence == 0, "accepted snapshots are not durable " + i);
            Check(session.AcceptedSequence == i && session.AcceptedState.Revision == 7 + i, "staged cursor advances independently " + i);
            Check(pending[i - 1].Name == "Викинг", "server name preserved " + i);
        }
        var originalAcceptedPlayer = session.AcceptedState.Player;
        for (int i = 1; i <= pending.Count; ++i)
        {
            session.Committed(pending[i - 1], i, false);
            Check(session.State.Revision == 7 + i && session.LastSequence == i, "FIFO write completion is durable " + i);
            Check(session.AcceptedSequence == 32 && session.AcceptedState.Revision == 39, "earlier completion cannot roll back staged cursor " + i);
        }
        Check(session.AcceptedState.Player.SequenceEqual(originalAcceptedPlayer), "latest accepted inventory unchanged by older completions");
        Check(session.State.Player.SequenceEqual(pending[31].Player), "last committed inventory matches latest snapshot");
        var next = session.Stage(session.Token, 33, State(0), false);
        Check(next.Revision == 40, "staging continues after queue drained");
    }
    private static void OwnedSnapshots()
    {
        var initial = State(7); var session = new CharacterSession(initial); session.MarkLoaded(session.Token);
        initial.Player[0] = 99; initial.WorldData[0] = 99; initial.Name = "mutated";
        Check(session.AcceptedState.Player[0] == 33 && session.State.WorldData[0] == 1, "constructor isolates external arrays");
        var update = State(0); var staged = session.Stage(session.Token, 1, update, false);
        update.Player[0] = 99; update.WorldData[0] = 99;
        Check(staged.Player[0] == 33 && staged.WorldData[0] == 1, "input arrays are not worker snapshot arrays");
        var view = session.AcceptedState; view.Player[0] = 99; view.WorldData[0] = 99; view.Owner = "other";
        Check(session.AcceptedState.Player[0] == 33 && session.AcceptedState.WorldData[0] == 1 && session.AcceptedState.Owner == "Steam_test", "accepted getter cannot mutate internal state");
        staged.Player[0] = 99; staged.WorldData[0] = 99;
        Check(session.AcceptedState.Player[0] == 33 && session.AcceptedState.WorldData[0] == 1, "worker-owned arrays cannot mutate accepted cursor");
        var commit = session.AcceptedState; session.Committed(commit, 1, false);
        commit.Player[0] = 99; commit.WorldData[0] = 99;
        Check(session.State.Player[0] == 33 && session.State.WorldData[0] == 1, "durable state takes its own snapshot");
    }
    private static void Rejections()
    {
        var session = new CharacterSession(State(7));
        Refuse(() => session.Stage(session.Token, 1, State(0), false), "stage before load verification");
        Refuse(() => session.MarkLoaded("wrong"), "load token mismatch"); session.MarkLoaded(session.Token);
        Refuse(() => session.Stage("wrong", 1, State(0), false), "stage token mismatch");
        Refuse(() => session.Stage(session.Token, 2, State(0), false), "stage sequence gap");
        Refuse(() => session.Stage(session.Token, 0, State(0), false), "stage old sequence");
        Refuse(() => session.Stage(session.Token, 1, null, false), "null snapshot");
        var bad = State(0); bad.World = 12; Refuse(() => session.Stage(session.Token, 1, bad, false), "cross-world update");
        bad = State(0); bad.Character = 45; Refuse(() => session.Stage(session.Token, 1, bad, false), "cross-character update");
        bad = State(0); bad.Owner = "other"; Refuse(() => session.Stage(session.Token, 1, bad, false), "cross-account update");
        bad = State(0); bad.Player[0] = 34; Refuse(() => session.Stage(session.Token, 1, bad, true), "invalid player format cannot mark final");
        bad = State(0); bad.WorldData = new byte[] { 1, 2, 3 }; Refuse(() => session.Stage(session.Token, 1, bad, true), "invalid world data cannot mark final");
        bad = State(0); bad.Player = null; Refuse(() => session.Stage(session.Token, 1, bad, true), "null player data cannot mark final");
        bad = State(0); bad.WorldData = null; Refuse(() => session.Stage(session.Token, 1, bad, true), "null world data cannot mark final");
        Check(session.AcceptedSequence == 0 && session.AcceptedState.Revision == 7 && !session.AcceptedFinal, "rejected stages do not advance any accepted cursor");
        var first = session.Stage(session.Token, 1, State(0), false);
        Refuse(() => session.Stage(session.Token, 1, State(0), false), "accepted snapshot replay");
        var second = session.Stage(session.Token, 2, State(0), false);
        Refuse(() => session.Committed(second, 2, false), "out-of-order durability");
        Refuse(() => session.Committed(first, 1, true), "premature final cannot close pending newer snapshot");
        Check(session.LastSequence == 0 && session.State.Revision == 7 && !session.Closed, "rejected commit does not advance durable cursor");
        session.Committed(first, 1, false);
        Refuse(() => session.Committed(first, 1, false), "stale durable completion");
        var wrongRevision = second.Copy(); wrongRevision.Revision++;
        Refuse(() => session.Committed(wrongRevision, 2, false), "durable revision gap");
        session.Committed(second, 2, false);
        Check(session.LastSequence == 2 && session.State.Revision == 9, "valid commits still work after rejected completions");
        var overflow = new CharacterSession(State(Int64.MaxValue)); overflow.MarkLoaded(overflow.Token);
        Refuse(() => overflow.Stage(overflow.Token, 1, State(0), false), "revision overflow");
        Check(overflow.AcceptedSequence == 0 && overflow.AcceptedState.Revision == Int64.MaxValue, "overflow does not reserve a sequence");
    }
    private static void AcceptedMapOwnership()
    {
        var session = Loaded();
        var newMap = State(0); newMap.WorldData[59] = 40; newMap.WorldData[60] = 41; newMap.WorldData[61] = 42;
        session.Stage(session.Token, 1, newMap, false);
        Check(session.State.WorldData[59] == 8 && session.LastSequence == 0, "accepted map fixture is ahead of durable revision");
        byte[] positions = StateCodec.WithoutMap(WorldData()); positions[5] = 12;
        byte[] merged = session.RetainAcceptedMap(positions);
        Check(merged[59] == 40 && merged[60] == 41 && merged[61] == 42, "position-only snapshot inherits accepted but not yet durable map");
        Check(merged[5] == 12 && session.AcceptedState.WorldData[5] != 12, "new snapshot position is independent of accepted position");
        merged[59] = 99; merged[5] = 99; positions[5] = 99;
        byte[] nextPositions = StateCodec.WithoutMap(WorldData());
        byte[] repeated = session.RetainAcceptedMap(nextPositions);
        Check(repeated[59] == 40 && repeated[5] == nextPositions[5], "caller mutation cannot change accepted map or subsequent merges");
        Check(session.AcceptedSequence == 1 && session.AcceptedState.Revision == 8 && session.State.WorldData[59] == 8, "map merge does not advance cursors or replace durable map");
        Refuse(() => session.RetainAcceptedMap(WorldData()), "map-bearing input cannot silently replace accepted map");
    }
    private static void FinalAndLegacy()
    {
        var session = Loaded(); var first = session.Stage(session.Token, 1, State(0), false);
        var last = session.Stage(session.Token, 2, State(0), true);
        Check(session.AcceptedFinal && !session.Closed, "accepted final is not yet durable closed");
        Refuse(() => session.Stage(session.Token, 3, State(0), false), "no new stage after accepted final");
        session.Committed(first, 1, false);
        Check(session.AcceptedFinal && !session.Closed && session.LastSequence == 1, "prior pending commit remains legal before final");
        session.Committed(last, 2, true);
        Check(session.Closed && session.LastSequence == 2 && session.AcceptedSequence == 2, "final closes only after final durable completion");
        Refuse(() => session.Stage(session.Token, 3, State(0), false), "closed session stage");
        Refuse(() => session.Next(session.Token, 3, State(0)), "closed session synchronous update");
        Refuse(() => session.MarkLoaded(session.Token), "closed session cannot reload");
        var legacy = Loaded(); var next = legacy.Next(legacy.Token, 1, State(0));
        Check(legacy.AcceptedSequence == 0 && legacy.LastSequence == 0, "legacy Next alone does not reserve progress");
        legacy.Committed(next, 1, false);
        Check(legacy.AcceptedSequence == 1 && legacy.AcceptedState.Revision == 8, "legacy commit keeps accepted cursor coherent");
        var staged = legacy.Stage(legacy.Token, 2, State(0), false); legacy.Committed(staged, 2, false);
        Check(legacy.LastSequence == 2 && legacy.AcceptedSequence == 2, "staging can follow a legacy committed update");
        next = legacy.Next(legacy.Token, 3, State(0)); legacy.Committed(next, 3, true);
        Check(legacy.AcceptedFinal && legacy.Closed && legacy.AcceptedSequence == 3, "legacy final advances accepted cursor and final flag");
        var reconnect = new CharacterSession(legacy.State); reconnect.MarkLoaded(reconnect.Token);
        Check(reconnect.AcceptedSequence == 0 && reconnect.LastSequence == 0 && !reconnect.AcceptedFinal, "reconnection resets session sequence and final state");
        Refuse(() => reconnect.Stage(legacy.Token, 1, State(0), false), "prior session token cannot write after reconnect");
        Check(reconnect.Stage(reconnect.Token, 1, State(0), false).Revision == 11, "reconnect revision follows durable state");
    }
    private static int Main()
    {
        try
        {
            ManyPending(); OwnedSnapshots(); Rejections(); AcceptedMapOwnership(); FinalAndLegacy();
            Console.WriteLine("PASS: " + passed + " async session assertions (staging, FIFO durability, ownership, validation, final barriers and legacy compatibility).");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
