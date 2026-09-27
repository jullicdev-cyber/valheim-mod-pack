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
            Reset();
            var ironIcon = new Sprite { name = "Iron icon" };
            var woodIcon = new Sprite { name = "Wood icon" };
            first = new Container(); first.transform.position = new Vector3(10, 0, 0);
            var iron = Item("$item_iron", "Iron", 10); iron.m_shared.m_icons = new[] { ironIcon };
            first.Inventory.Items.Add(iron); first.Inventory.Items.Add(Item("$item_iron", "Iron", 4));
            var wood = Item("$item_wood", "Wood", 50); wood.m_shared.m_icons = new[] { woodIcon };
            first.Inventory.Items.Add(wood);
            near = new Container(); near.transform.position = new Vector3(3, 0, 0);
            near.Inventory.Items.Add(Item("$item_iron", "Iron", 20));
            // Two distinct prefabs deliberately share the exact same localized name.
            near.Inventory.Items.Add(Item("$item_iron", "ModdedIron", 5));
            secret = new Container { Access = false }; secret.Inventory.Items.Add(Item("$item_iron", "Iron", 999));
            var busy = new Container { InUse = true }; busy.Inventory.Items.Add(Item("$item_iron", "Iron", 999));
            var stale = new Container(); stale.SetRevision(0); stale.Inventory.Items.Add(Item("$item_iron", "Iron", 999));
            search = new SearchService(new NativeChestReader(), new NameIndex(message => { throw new Exception(message); }));
            result = search.Search(Player.m_localPlayer, "Iron", 30);
            Check(result.Items.Count == 2 && result.Quantity == 39, "Distinct prefab identities survive identical translated names");
            var ironGroup = result.Items.Find(item => item.Key == "Iron");
            var moddedGroup = result.Items.Find(item => item.Key == "ModdedIron");
            Check(ironGroup.Name == "Железо" && System.Object.ReferenceEquals(ironGroup.Icon, ironIcon), "Item group uses localized name and real item icon");
            Check(ironGroup.Quantity == 34 && ironGroup.ChestCount == 2 && ironGroup.Chests.Count == 2, "Repeated stacks aggregate once per chest");
            Check(ironGroup.NearestDistance == 3 && ironGroup.Chests[0].Snapshot.Container == near, "Item nearest distance and chest list remain distance ordered");
            Check(ironGroup.Chests[1].Quantity == 14 && ironGroup.Chests[1].Kinds == 1, "Per-item chest result excludes unrelated matching items");
            Check(moddedGroup.Quantity == 5 && moddedGroup.ChestCount == 1 && moddedGroup.Icon == null, "Missing icon does not abort counts or merge identities");
            Check(result.Results[0].Quantity == 25 && result.Results[0].Kinds == 2 && result.Results[0].ItemSummary.IndexOf("× 5", StringComparison.Ordinal) >= 0,
                "Compatibility chest result includes separate same-label prefab kinds");
            Check(result.Pending == 2 && secret.Inventory.Reads == 0 && busy.Inventory.Reads == 0 && stale.Inventory.Reads == 0,
                "Item groups do not leak denied, busy or stale quantities");
            var all = search.Search(Player.m_localPlayer, " \t", 30, true);
            Check(all.Items.Count == 3 && all.Quantity == 89 && all.Checked == 2, "Explicit empty-input mode lists every accessible item group");
            Check(search.Search(Player.m_localPlayer, "", 30, false).Items.Count == 0, "Explicit false keeps old empty-input behavior");
            Check(search.Search(Player.m_localPlayer, "Wood", 30, true).Items.Count == 1, "Show-without-input does not disable a nonempty filter");
            Check(search.Search(Player.m_localPlayer, "", Single.NaN, true).Checked == 0 && search.Search(Player.m_localPlayer, "", 81, true).Checked == 0,
                "Show-all validates finite bounded radius before scanning");
            var missingPrefab = Item("$item_iron", "Unknown", 500); missingPrefab.m_dropPrefab = null;
            var missingShared = Item("$item_iron", "Broken", 500); missingShared.m_shared = null;
            first.Inventory.Items.Add(missingPrefab); first.Inventory.Items.Add(missingShared); first.Inventory.Items.Add(null);
            first.Inventory.Items.Add(Item("$item_iron", "Iron", 0)); first.Inventory.Items.Add(Item("$item_iron", "Iron", -5));
            Check(search.Search(Player.m_localPlayer, "Iron", 30).Quantity == 39, "Malformed and nonpositive stacks do not enter groups");
            Check(SearchService.ItemKey(missingPrefab) == "" && SearchService.ItemKey(missingShared) == "" && SearchService.ItemKey(iron) == "Iron",
                "Item identity never falls back to a localized name");
            var snapshot = ironGroup.Chests[1].Snapshot;
            Check(SearchService.ContainsItem(snapshot, "Iron") && !SearchService.ContainsItem(snapshot, "iron") && !SearchService.ContainsItem(snapshot, "ModdedIron"),
                "Marker item membership uses exact prefab identity");
            iron.m_stack = 0; first.Inventory.Items[1].m_stack = 0;
            Check(!SearchService.ContainsItem(snapshot, "Iron") && SearchService.ContainsItem(snapshot, "Wood"), "Marker stops after selected item leaves even when chest still contains other items");
            Check(!SearchService.ContainsItem(snapshot, "") && !SearchService.ContainsItem(null, "Iron"), "Marker membership rejects absent selection and stale snapshot");

            var sorted = new List<ItemSearchResult> {
                new ItemSearchResult { Key = "B", Name = "Bravo", Quantity = 20, ChestCount = 2, NearestDistance = 5 },
                new ItemSearchResult { Key = "C", Name = "Charlie", Quantity = 30, ChestCount = 1, NearestDistance = 2 },
                new ItemSearchResult { Key = "A", Name = "Alpha", Quantity = 10, ChestCount = 3, NearestDistance = 9 }
            };
            SearchService.Sort(sorted, ItemSort.Name, false); Check(sorted[0].Key == "A" && sorted[2].Key == "C", "Name ascending");
            SearchService.Sort(sorted, ItemSort.Name, true); Check(sorted[0].Key == "C" && sorted[2].Key == "A", "Name descending");
            SearchService.Sort(sorted, ItemSort.Quantity, false); Check(sorted[0].Key == "A" && sorted[2].Key == "C", "Quantity ascending");
            SearchService.Sort(sorted, ItemSort.Quantity, true); Check(sorted[0].Key == "C" && sorted[2].Key == "A", "Quantity descending");
            SearchService.Sort(sorted, ItemSort.Distance, false); Check(sorted[0].Key == "C" && sorted[2].Key == "A", "Distance ascending");
            SearchService.Sort(sorted, ItemSort.Distance, true); Check(sorted[0].Key == "A" && sorted[2].Key == "C", "Distance descending");
            SearchService.Sort(sorted, ItemSort.ChestCount, false); Check(sorted[0].Key == "C" && sorted[2].Key == "A", "Chest count ascending");
            SearchService.Sort(sorted, ItemSort.ChestCount, true); Check(sorted[0].Key == "A" && sorted[2].Key == "C", "Chest count descending");
            sorted = new List<ItemSearchResult> {
                new ItemSearchResult { Key = "B", Name = "Same", Quantity = Int64.MaxValue },
                new ItemSearchResult { Key = "A", Name = "Same", Quantity = Int64.MaxValue },
                new ItemSearchResult { Key = "Z", Name = "Other", Quantity = 1 }
            };
            SearchService.Sort(sorted, ItemSort.Quantity, true);
            Check(sorted[0].Key == "A" && sorted[1].Key == "B" && sorted[2].Key == "Z", "Quantity comparator does not overflow and ties use deterministic prefab key");
            SearchService.Sort(sorted, ItemSort.Name, false);
            Check(sorted[0].Key == "Z" && sorted[1].Key == "A", "Name tie order remains deterministic");
            Check(first.Inventory.Items[2] == wood && wood.m_stack == 50, "Search and result sorting never reorder or mutate underlying inventory");

            Reset(); var enormous = new Container();
            for (int i = 0; i < 4096; i++) enormous.Inventory.Items.Add(Item("$item_iron", "Unique" + i, 1));
            var extra = new Container(); extra.transform.position = new Vector3(1, 0, 0); extra.Inventory.Items.Add(Item("$item_iron", "Overflow", 5));
            result = search.Search(Player.m_localPlayer, "", 30, true);
            Check(result.Limited && result.Items.Count == 4096 && result.Quantity == 4096, "Distinct item cap bounds malformed inventory result size and signals truncation");
            Reset();
            for (int i = 0; i < 260; i++) { var c = new Container(); c.transform.position = new Vector3(i / 10f, 0, 0); c.Inventory.Items.Add(Item("$item_iron", "Iron", 1)); }
            result = search.Search(Player.m_localPlayer, "", 30, true);
            Check(result.Limited && result.Results.Count == 256 && result.Items[0].ChestCount == 256 && result.Items[0].Quantity == 256,
                "Chest result limit applies identically to aggregate totals and marker destinations");
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
