using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimModPack.ChestSearch
{
    public sealed class SearchResult
    {
        public ChestSnapshot Snapshot;
        public string ChestName;
        public string ItemSummary;
        public long Quantity;
        public int Kinds;
    }
    public enum ItemSort { Name, Quantity, Distance, ChestCount }
    public sealed class ItemSearchResult
    {
        public string Key;
        public string Name;
        public Sprite Icon;
        public long Quantity;
        public int ChestCount;
        public float NearestDistance;
        public readonly List<SearchResult> Chests = new List<SearchResult>();
    }
    public sealed class SearchReport
    {
        public readonly List<SearchResult> Results = new List<SearchResult>();
        public readonly List<ItemSearchResult> Items = new List<ItemSearchResult>();
        public int Checked;
        public int Pending;
        public long Quantity;
        public bool Limited;
    }
    public sealed class SearchService
    {
        private readonly NativeChestReader reader;
        private readonly NameIndex names;
        public SearchService(NativeChestReader reader, NameIndex names) { this.reader = reader; this.names = names; }
        public SearchReport Search(Player player, string query, float radius)
        {
            return Search(player, query, radius, false);
        }
        public SearchReport Search(Player player, string query, float radius, bool showWithoutInput)
        {
            var report = new SearchReport();
            string[] terms = SearchText.Terms(query);
            if ((terms.Length == 0 && !showWithoutInput) || player == null
                || Single.IsNaN(radius) || Single.IsInfinity(radius) || radius <= 0 || radius > 80) return report;
            names.EnsureLoaded();
            var grouped = new Dictionary<string, ItemSearchResult>(StringComparer.Ordinal);
            Container[] containers = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None);
            Array.Sort(containers, (a, b) => Distance(a, player).CompareTo(Distance(b, player)));
            foreach (Container container in containers)
            {
                if (container == null || Distance(container, player) > radius * radius) continue;
                if (report.Checked >= 512) { report.Limited = true; break; }
                ChestSnapshot snapshot; ReadFailure failure;
                if (!reader.TryRead(container, player, radius, out snapshot, out failure))
                {
                    if (failure == ReadFailure.Busy || failure == ReadFailure.Stale) report.Pending++;
                    continue;
                }
                report.Checked++;
                var found = new Dictionary<string, long>(StringComparer.Ordinal);
                long quantity = 0;
                foreach (var item in snapshot.Items)
                {
                    string key = ItemKey(item);
                    if (key.Length == 0 || item.m_stack <= 0 || (terms.Length > 0 && !names.Matches(terms, item))) continue;
                    ItemSearchResult group;
                    if (!grouped.TryGetValue(key, out group))
                    {
                        // Bound malformed/modded inventories without combining unrelated prefab identities.
                        if (grouped.Count >= 4096) { report.Limited = true; continue; }
                        string title = names.Display(item.m_shared.m_name);
                        group = new ItemSearchResult { Key = key, Name = title.Length == 0 ? SearchText.Safe(key, 100) : title,
                            NearestDistance = snapshot.Distance };
                        grouped.Add(key, group); report.Items.Add(group);
                    }
                    // GetIcon indexes a variant array in vanilla; one malformed item must not abort the search.
                    if (group.Icon == null) { try { group.Icon = item.GetIcon(); } catch { } }
                    long total; found.TryGetValue(key, out total);
                    found[key] = total + item.m_stack;
                    quantity += item.m_stack;
                }
                if (quantity == 0) continue;
                var summaries = new List<string>();
                var inChest = new List<ItemSearchResult>();
                foreach (string key in found.Keys) inChest.Add(grouped[key]);
                Sort(inChest, ItemSort.Name, false);
                string chestName = names.Display(container.m_name);
                foreach (ItemSearchResult group in inChest)
                {
                    long count = found[group.Key];
                    string summary = group.Name + " × " + count;
                    if (summaries.Count < 3) summaries.Add(summary);
                    else if (summaries.Count == 3) summaries.Add("…");
                    group.Quantity += count; group.ChestCount++;
                    group.NearestDistance = Math.Min(group.NearestDistance, snapshot.Distance);
                    group.Chests.Add(new SearchResult { Snapshot = snapshot, ChestName = chestName,
                        ItemSummary = summary, Quantity = count, Kinds = 1 });
                }
                report.Results.Add(new SearchResult { Snapshot = snapshot, ChestName = chestName,
                    ItemSummary = String.Join(", ", summaries.ToArray()), Quantity = quantity, Kinds = found.Count });
                report.Quantity += quantity;
                if (report.Results.Count >= 256) { report.Limited = true; break; }
            }
            Sort(report.Items, ItemSort.Name, false);
            return report;
        }
        public static string ItemKey(ItemDrop.ItemData item)
        {
            // A shared display name is not an identity. Missing prefabs are omitted instead of guessed.
            if (item == null || item.m_shared == null || item.m_dropPrefab == null) return "";
            string key = item.m_dropPrefab.name;
            return String.IsNullOrEmpty(key) || key.Length > 256 ? "" : key;
        }
        public static bool ContainsItem(ChestSnapshot snapshot, string itemKey)
        {
            if (snapshot == null || snapshot.Items == null || String.IsNullOrEmpty(itemKey)) return false;
            foreach (ItemDrop.ItemData item in snapshot.Items)
                if (item != null && item.m_stack > 0 && String.Equals(ItemKey(item), itemKey, StringComparison.Ordinal)) return true;
            return false;
        }
        public static void Sort(List<ItemSearchResult> items, ItemSort order, bool descending)
        {
            if (items == null) return;
            items.Sort(delegate(ItemSearchResult a, ItemSearchResult b)
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int result;
                switch (order)
                {
                    case ItemSort.Quantity: result = a.Quantity.CompareTo(b.Quantity); break;
                    case ItemSort.Distance: result = a.NearestDistance.CompareTo(b.NearestDistance); break;
                    case ItemSort.ChestCount: result = a.ChestCount.CompareTo(b.ChestCount); break;
                    default: result = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name); break;
                }
                if (result != 0) return descending ? (result > 0 ? -1 : 1) : result;
                result = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                return result != 0 ? result : StringComparer.Ordinal.Compare(a.Key, b.Key);
            });
        }
        private static float Distance(Container container, Player player)
        {
            return container == null ? Single.PositiveInfinity : (container.transform.position - player.transform.position).sqrMagnitude;
        }
    }
}
