using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using ValheimModPack.WorldCharacters;

internal static class Tests
{
    private static int passed;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); ++passed; }
    private static void Refuse(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { ++passed; return; } catch (IOException) { ++passed; return; } catch (ArgumentException) { ++passed; return; }
        catch (InvalidOperationException) { ++passed; return; }
        throw new Exception("Did not refuse: " + name);
    }
    private static byte[] PlayerData(int amount, bool duplicate, string customValue)
    {
        using (var s = new MemoryStream())
        using (var w = new BinaryWriter(s))
        {
            w.Write(33); for (int i = 0; i < 4; ++i) w.Write(25f); w.Write("GP_Eikthyr"); w.Write(0f);
            w.Write(109); w.Write((ushort)(duplicate ? 2 : 1));
            for (int i = 0; i < (duplicate ? 2 : 1); ++i)
            {
                w.Write(500); w.Write((byte)1); w.Write((byte)6); w.Write((byte)0); w.Write((byte)(8 | 64 | 128));
                w.Write((ushort)amount); w.Write(123456); w.Write((byte)2);
                w.Write("EpicLoot"); w.Write(customValue); w.Write("eaqs_slot"); w.Write("head"); w.Write((byte)0);
            }
            w.Write(0); w.Flush(); return s.ToArray();
        }
    }
    private static CharacterState State(long world = 11, string owner = "Steam_76561198000000001", long character = 44)
    {
        return new CharacterState { World = world, Owner = owner, Character = character, Name = "Викинг", Build = "test",
            Player = PlayerData(10, false, "enchanted+backpack data") };
    }
    private static void Codec()
    {
        CharacterState original = State(); byte[] encoded = StateCodec.Encode(original); var decoded = StateCodec.Decode(encoded);
        Check(decoded.Name == original.Name && decoded.Player.SequenceEqual(original.Player), "Unicode and opaque player data round trip");
        encoded[40] ^= 1; Refuse(() => StateCodec.Decode(encoded), "corruption");
        Refuse(() => StateCodec.Decode(new byte[500]), "unknown envelope");
        Refuse(() => StateCodec.Decode(new byte[StateCodec.MaximumBytes + 1]), "oversized state");
        var invalid = State(); invalid.Player[0] = 34; Refuse(() => StateCodec.Encode(invalid), "future player format");
        invalid = State(); invalid.Player = PlayerData(10, true, "x"); Refuse(() => StateCodec.Encode(invalid), "overlapping EAQS slots");
        invalid = State(); invalid.Player = PlayerData(0, false, "x"); Refuse(() => StateCodec.Encode(invalid), "zero item stack");
        invalid = State(); invalid.World = 0; Refuse(() => StateCodec.Encode(invalid), "unknown world");
        invalid = State(); invalid.WorldData = new byte[59]; Refuse(() => StateCodec.Encode(invalid), "malformed world profile");
        byte[] oldWorld = new byte[62]; oldWorld[0]=1; oldWorld[55]=3; oldWorld[59]=8; oldWorld[60]=9; oldWorld[61]=10;
        byte[] positions = StateCodec.WithoutMap(oldWorld); positions[5]=12;
        byte[] merged = StateCodec.RetainMap(positions,oldWorld);
        Check(positions.Length==59 && merged.Length==62 && merged[5]==12 && merged[59]==8 && merged[61]==10,"position-only snapshot retains server map");
        Refuse(()=>StateCodec.RetainMap(oldWorld,oldWorld),"position-only packet cannot replace map implicitly");
        Check(StateCodec.Key(1,"a",2) != StateCodec.Key(2,"a",2), "world isolation");
        Check(StateCodec.Key(1,"a",2) != StateCodec.Key(1,"b",2), "account isolation");
        Check(StateCodec.Key(1,"a",2) != StateCodec.Key(1,"a",3), "character isolation");
        List<StoredItem> items = NativeInventory.ReadPlayer(original.Player);
        Check(items.Count == 1 && items[0].Y == 6 && items[0].Custom["EpicLoot"] == "enchanted+backpack data", "hidden rows and full custom metadata");
        var copy = NativeInventory.ReadPlayer(original.Player); copy[0].X = 7; copy[0].Y = 8; copy[0].Custom["eaqs_slot"] = "other";
        NativeInventory.RequireSameItems(items, copy); ++passed;
        copy[0].Custom["randyknapp.mods.epicloot#EpicLoot.MagicItemComponent"]="";
        NativeInventory.RequireSameItems(items,copy); ++passed;
        copy[0].Custom["randyknapp.mods.epicloot#EpicLoot.MagicItemComponent"]="changed enchantment";
        Refuse(()=>NativeInventory.RequireSameItems(items,copy),"non-empty Epic Loot component is still checked");
        copy[0].Custom.Remove("randyknapp.mods.epicloot#EpicLoot.MagicItemComponent");
        copy[0].Custom["EpicLoot"] = "lost effect";
        Refuse(() => NativeInventory.RequireSameItems(items, copy), "lost Epic Loot or nested backpack data");
        Refuse(() => NativeInventory.RequireSameItems(items, new List<StoredItem>()), "missing prefab drops item");
        copy = NativeInventory.ReadPlayer(original.Player); copy[0].Count--;
        Refuse(() => NativeInventory.RequireSameItems(items, copy), "lost stack quantity");
        var bag = NativeInventory.ReadPlayer(original.Player)[0];
        string bagKey = "vapok.mods.adventurebackpacks#AdventureBackpacks.Components.BackpackComponent";
        byte[] nestedPlayer = PlayerData(12,false,"nested enchantment"); byte[] nested;
        using(var s=new MemoryStream(nestedPlayer)) using(var r=new BinaryReader(s))
        {
            r.ReadInt32(); for(int i=0;i<4;++i) r.ReadSingle(); r.ReadString(); r.ReadSingle(); int offset=(int)s.Position;
            NativeInventory.ReadInventory(r); nested=nestedPlayer.Skip(offset).Take((int)s.Position-offset).ToArray();
        }
        bag.Custom[bagKey]=Convert.ToBase64String(nested);
        Check(NativeInventory.IncludingBackpacks(new[]{bag}).Count()==2,"real backpack inventory format inspected recursively");
        bag.Custom[bagKey]="not base64";
        Refuse(()=>NativeInventory.IncludingBackpacks(new[]{bag}).ToArray(),"corrupted backpack rejected before native loading");
        for (int i = 0; i < original.Player.Length - 4; ++i)
        {
            byte[] truncated = original.Player.Take(i).ToArray();
            Refuse(() => NativeInventory.ReadPlayer(truncated), "truncated item at " + i);
        }
    }
    private static void Store(string root)
    {
        using (var store = new StateStore(root))
        {
            Refuse(() => { using (var second = new StateStore(root)) {} }, "exclusive process lock");
            CharacterState candidate = State(); string id = store.Propose(candidate);
            Check(store.Read(candidate.World,candidate.Owner,candidate.Character) == null, "first join never silently imports a guest");
            var changed = candidate.Copy(); changed.Player = PlayerData(99,false,"foreign"); store.Propose(changed);
            Check(store.Pending(id).Player.SequenceEqual(candidate.Player), "pending request immutable across reconnect");
            store.Approve(id,false); var approved = store.Read(candidate.World,candidate.Owner,candidate.Character);
            Check(approved.Revision == 1 && approved.Player.SequenceEqual(candidate.Player), "existing inventory imported without reset");
            store.Propose(changed);
            Check(store.Read(candidate.World,candidate.Owner,candidate.Character).Player.SequenceEqual(candidate.Player), "foreign-world reconnect candidate cannot replace approved inventory");
            Refuse(() => store.Approve(id,false), "approval cannot overwrite an existing world character");
            var next = approved.Copy(); next.Revision = 2; next.Player = PlayerData(4,false,"same effect"); store.Save(next,1);
            Check(store.Read(next.World,next.Owner,next.Character).Player.SequenceEqual(next.Player), "server progress saved");
            string lockedFile = Path.Combine(root,"characters",id + ".wchar");
            using(var exclusive=new FileStream(lockedFile,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                var blocked=next.Copy(); blocked.Revision=3;
                Refuse(()=>store.Save(blocked,2),"I/O failure cannot commit a new revision");
            }
            Check(store.Read(next.World,next.Owner,next.Character).Revision==2,"previous state survives failed save");
            Refuse(() => store.Save(approved,0), "old profile cannot overwrite progress");
            Check(store.Read(99,next.Owner,next.Character) == null, "worlds do not share inventory");
            Refuse(() => store.Pending("../../bad"), "path traversal");
            var fresh = State(22); string freshId = store.Propose(fresh); store.Approve(freshId,true);
            Check(store.Read(22,fresh.Owner,fresh.Character).Player.Length == 0, "fresh mode requires explicit approval");
            var rejected = State(33); string rejectId = store.Propose(rejected); store.RejectProposal(rejectId);
            Check(!store.PendingIds().Contains(rejectId), "reject preserves evidence but permits a new proposal");
            rejected.Player = PlayerData(2,false,"clean"); store.Propose(rejected);
            Check(store.Pending(rejectId).Player.SequenceEqual(rejected.Player), "resubmit after cleaning imported gear");
            store.Archive("before-restore",candidate); store.Archive("client-recovery",next);
            Check(store.Read(next.World,next.Owner,next.Character).Revision == 2, "recovery archive never becomes authoritative");
            Refuse(() => store.Archive("../../escape",candidate), "archive path boundary");
            byte[] checkpoint = store.CaptureCheckpoint(11); store.CommitCheckpoint(11,checkpoint);
            Check(File.Exists(Path.Combine(root,"checkpoints","11.wcheckpoint")), "world checkpoint written");
            string file = Path.Combine(root,"characters",id + ".wchar"); byte[] bytes = File.ReadAllBytes(file); bytes[30] ^= 1; File.WriteAllBytes(file,bytes);
            Refuse(() => store.Read(11,candidate.Owner,44), "no silent rollback to backup after corruption");
            File.Delete(file); Refuse(() => store.Read(11,candidate.Owner,44), "missing primary with backup is not a new character");
        }
        using (var reopened = new StateStore(root)) Check(reopened.Read(22,State().Owner,44).Revision == 1, "restart keeps approved state");
    }
    private static void Session()
    {
        var state = State(); state.Revision = 1; var session = new CharacterSession(state);
        Refuse(() => session.Next(session.Token,1,state), "save before load verification");
        Refuse(() => session.MarkLoaded("wrong"), "wrong load token"); session.MarkLoaded(session.Token);
        Refuse(() => session.Next("wrong",1,state), "wrong snapshot token");
        Refuse(() => session.Next(session.Token,2,state), "out of order snapshot");
        var wrong = State(22); Refuse(() => session.Next(session.Token,1,wrong), "cross-world packet");
        wrong = State(11,"Steam_other"); Refuse(() => session.Next(session.Token,1,wrong), "cross-account packet");
        wrong = State(11,state.Owner,45); Refuse(() => session.Next(session.Token,1,wrong), "cross-character packet");
        var next = session.Next(session.Token,1,state);
        Check(session.State.Revision == 1 && session.LastSequence == 0, "failed disk write cannot advance session");
        session.Committed(next,1,false); Check(session.State.Revision == 2 && session.LastSequence == 1, "ack only after disk commit");
        Refuse(() => session.Next(session.Token,1,state), "replayed packet");
        next = session.Next(session.Token,2,state); session.Committed(next,2,true);
        Refuse(() => session.Next(session.Token,3,state), "save after final logout");
        var reconnect = new CharacterSession(session.State);
        Refuse(() => reconnect.MarkLoaded(session.Token), "old connection token invalid after reconnect");
    }
    private static int Main(string[] args)
    {
        try
        {
            Codec(); Store(args[0]); Session();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                byte[] real = File.ReadAllBytes(args[1]); var items = NativeInventory.ReadPlayer(real);
                Check(items.Count > 0, "real existing character inventory parsed");
                var original = State(); original.Player = real;
                Check(StateCodec.Decode(StateCodec.Encode(original)).Player.SequenceEqual(real), "real inventory and metadata preserved byte-for-byte");
            }
            Console.WriteLine("PASS: " + passed + " assertions (migration, isolation, corruption, slots, metadata, disk persistence and session replay)."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
