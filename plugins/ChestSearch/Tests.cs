using System;
using System.Collections.Generic;
using ValheimModPack.ChestSearch;
using UnityEngine;

internal static class ChestSearchTests
{
    private static int checks;
    private static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    private static void Reset()
    {
        UnityEngine.Object.Containers.Clear();
        PrivateArea.Allowed = true; Player.m_localPlayer = new Player();
        ZNetScene.instance = new ZNetScene();
        ZNetScene.instance.Prefabs[1] = new GameObject { name = "piece_chest_wood" };
        ZNetScene.instance.Prefabs[2] = new GameObject { name = "piece_chest_private" };
        ZNetScene.instance.Prefabs[3] = new GameObject { name = "piece_chest_modded" };
        Localization.instance = new Localization();
        Localization.instance.Current["$item_iron"] = "Железо";
        Localization.instance.Current["$item_wood"] = "Древесина";
        Localization.instance.Current["$piece_chest"] = "Сундук";
        Localization.SetCsv("KEY,English,Russian\r\nitem_iron,Iron,Железо\r\nitem_wood,Wood,Древесина\r\nitem_finewood,Fine wood,Качественная древесина\r\n");
    }
    private static ItemDrop.ItemData Item(string key, string prefab, int count)
    {
        return new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_name = key }, m_dropPrefab = new GameObject { name = prefab }, m_stack = count };
    }
    private static void Reject(Action<Container> configure, string name)
    {
        Reset(); var chest = new Container(); configure(chest);
        ChestSnapshot snapshot; ReadFailure failure;
        Check(!new NativeChestReader().TryRead(chest, Player.m_localPlayer, 30, out snapshot, out failure), name);
        Check(chest.Inventory.Reads == 0, name + " must not read private inventory contents");
    }
    private static int Main()
    {
        try
        {
            Check(SearchText.Matches(SearchText.Terms(" чЁРНый металл "), "Чёрный металл", "Black metal", "BlackMetal"), "Russian case and yo normalization");
            Check(SearchText.Matches(SearchText.Terms("fine wood"), "FineWood"), "Multiple terms match prefab alias");
            Check(!SearchText.Matches(SearchText.Terms("iron wood"), "Iron", "Железо"), "All terms required");
            Check(!SearchText.Matches(SearchText.Terms(" "), "Iron"), "Empty query does not expose all chest items");
            Check(SearchText.Safe("<color=red>Железо</color>\n\u202E", 40) == "Железо", "Remove markup and bidi controls");
            Check(SearchText.Safe("a\u0301bc", 1) == "a\u0301…", "Do not split combining character");
            Check(SearchText.Safe("\ud83c\udfb5music", 1) == "\ud83c\udfb5…", "Do not split supplementary Unicode");
            Check(SearchText.StandardChest("piece_chest_private") && !SearchText.StandardChest("piece_chest_modded"), "Native chest allowlist");
            var aliases = new Dictionary<string, string[]>();
            LocalizationCsv.Read("\ufeffKEY,English,Russian\r\nitem_test,\"Fine, wood\",\"Качественная\nдревесина\"\r\nitem_quote,\"A \"\"quoted\"\" item\",Предмет", aliases);
            Check(aliases["item_test"][0] == "Fine, wood", "Quoted comma CSV");
            Check(aliases["item_test"][1] == "Качественная\nдревесина", "Quoted multiline CSV");
            Check(aliases["item_quote"][0] == "A \"quoted\" item", "Quoted escape and last CSV row");
            bool invalidCsv = false; try { LocalizationCsv.Read("KEY,English\nitem,\"bad", new Dictionary<string, string[]>()); } catch (FormatException) { invalidCsv = true; }
            Check(invalidCsv, "Malformed quote fails cleanly");

            Reset(); var valid = new Container(); valid.Inventory.Items.Add(Item("$item_iron", "Iron", 20));
            ChestSnapshot read; ReadFailure fail;
            Check(new NativeChestReader().TryRead(valid, Player.m_localPlayer, 30, out read, out fail) && read.Items.Length == 1 && read.Items[0].m_stack == 20, "Fresh accessible chest is readable");
            Check(valid.Inventory.Items.Count == 1 && valid.Inventory.Items[0].m_stack == 20, "Read preserves stacks and original inventory");
            Reject(c => c.Access = false, "Denied personal chest");
            Reject(c => PrivateArea.Allowed = false, "Denied ward");
            Reject(c => c.ThrowAccess = true, "Failed permission check fails closed");
            Reject(c => c.InUse = true, "Locally busy chest");
            Reject(c => c.View.Data.InUse = 1, "Remotely busy chest");
            Reject(c => c.SetRevision(0), "Stale replicated inventory");
            Reject(c => { c.SetRevision(0); c.View.Data.DataRevision = 0; }, "Never-loaded zero revision");
            Reject(c => c.SetLoading(true), "Inventory mid-load");
            Reject(c => c.View.Valid = false, "Invalid network view");
            Reject(c => c.transform.position = new Vector3(31, 0, 0), "Outside configured radius");
            Reject(c => c.transform.position = new Vector3(Single.NaN, 0, 0), "Malformed position cannot bypass radius");
            Reject(c => c.Components[typeof(Piece)] = new Piece { Built = false }, "Generated dungeon chest");
            Reject(c => c.Components[typeof(TombStone)] = new TombStone(), "Tombstone");
            Reject(c => c.Components[typeof(Character)] = new Character(), "Player or character inventory");
            Reject(c => c.Components[typeof(ItemDrop)] = new ItemDrop(), "Dropped backpack inventory");
            Reject(c => c.Components[typeof(Ship)] = new Ship(), "Ship inventory");
            Reject(c => c.m_wagon = new Vagon(), "Wagon inventory");
            Reject(c => c.m_rootObjectOverride = new ZNetView(), "Root-overridden special inventory");
            Reject(c => c.View.Data.Prefab = 3, "Unknown custom chest");
            Reset(); valid = new Container();
            var reader = new NativeChestReader();
            Check(!reader.TryRead(valid, Player.m_localPlayer, Single.NaN, out read, out fail), "NaN radius rejected");
            Check(!reader.TryRead(valid, Player.m_localPlayer, Single.PositiveInfinity, out read, out fail), "Infinite radius rejected");
            Check(!reader.TryRead(valid, Player.m_localPlayer, 81, out read, out fail), "Overmaximum radius rejected");
            Check(!reader.TryRead(valid, new Player(), 30, out read, out fail), "Another player cannot be impersonated");

            Reset(); var first = new Container(); first.transform.position = new Vector3(10, 0, 0);
            first.Inventory.Items.Add(Item("$item_iron", "Iron", 10)); first.Inventory.Items.Add(Item("$item_iron", "Iron", 4)); first.Inventory.Items.Add(Item("$item_wood", "Wood", 50));
            var near = new Container(); near.transform.position = new Vector3(3, 0, 0); near.Inventory.Items.Add(Item("$item_iron", "Iron", 20));
            var secret = new Container { Access = false }; secret.Inventory.Items.Add(Item("$item_iron", "Iron", 999));
            var index = new NameIndex(message => { throw new Exception(message); });
            var search = new SearchService(new NativeChestReader(), index);
            var result = search.Search(Player.m_localPlayer, "IRON", 30);
            Check(result.Quantity == 34 && result.Results.Count == 2, "English alias matches Russian game and excludes private quantities");
            Check(result.Results[0].Snapshot.Container == near && result.Results[1].Quantity == 14, "Distance order and multiple stack aggregation");
            Check(search.Search(Player.m_localPlayer, "железо", 30).Quantity == 34, "Russian query");
            Check(search.Search(Player.m_localPlayer, "Wood", 30).Quantity == 50, "Prefab English query");
            Check(secret.Inventory.Reads == 0, "Search never reads denied inventory");
            near.SetRevision(0);
            result = search.Search(Player.m_localPlayer, "Iron", 30);
            Check(result.Quantity == 14 && result.Pending == 1, "Stale inventory omitted instead of reporting unreliable counts");
            near.SetRevision(1); first.Inventory.Items[0].m_stack = Int32.MaxValue; first.Inventory.Items[1].m_stack = Int32.MaxValue;
            Check(search.Search(Player.m_localPlayer, "Iron", 30).Quantity == 2L * Int32.MaxValue + 20, "Quantities do not overflow Int32");
            Check(search.Search(Player.m_localPlayer, "", 30).Checked == 0, "Empty input does not scan inventories");
            Check(ForbiddenWrites.Count == 0, "Zero load, save, mutation or ownership calls");

            int blocks = 2, acquired = 0, released = 0;
            var lease = new InputBlockLease(on => { if (on) { blocks++; acquired++; } else { blocks--; released++; } });
            lease.Acquire(); lease.Acquire();
            Check(blocks == 3 && acquired == 1 && lease.Held, "Duplicate Show owns only one input block");
            lease.Release(); lease.Dispose(); lease.Release();
            Check(blocks == 2 && released == 1 && !lease.Held, "Repeated close preserves other mods' blocks");
            lease.Acquire(); lease.Dispose();
            Check(blocks == 2 && acquired == 2 && released == 2, "Reopen/close balanced");
            var failing = new InputBlockLease(on => { if (on) { blocks++; throw new Exception("Camera failed after increment"); } blocks--; });
            bool failed = false; try { failing.Acquire(); } catch { failed = true; }
            failing.Release();
            Check(failed && blocks == 2 && !failing.Held, "Failed input acquisition balances increment exactly once");
            int releaseAttempts = 0;
            var badRelease = new InputBlockLease(on => { if (!on) { releaseAttempts++; throw new Exception("Camera unavailable after decrement"); } });
            badRelease.Acquire(); try { badRelease.Release(); } catch { } badRelease.Release();
            Check(releaseAttempts == 1 && !badRelease.Held, "Failed release never decrements another mod's block on retry");
            Console.WriteLine("PASS " + checks + " ChestSearch checks: actual reader, search aggregation, CSV/Unicode and input lease; native Unity UI not exercised.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
