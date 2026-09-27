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
    public sealed class SearchReport
    {
        public readonly List<SearchResult> Results = new List<SearchResult>();
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
            var report = new SearchReport();
            string[] terms = SearchText.Terms(query);
            if (terms.Length == 0 || player == null) return report;
            names.EnsureLoaded();
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
                var found = new SortedDictionary<string, long>(StringComparer.CurrentCultureIgnoreCase);
                long quantity = 0;
                foreach (var item in snapshot.Items)
                {
                    if (item == null || item.m_stack <= 0 || !names.Matches(terms, item)) continue;
                    string title = names.Display(item.m_shared.m_name);
                    long total; found.TryGetValue(title, out total);
                    found[title] = total + item.m_stack;
                    quantity += item.m_stack;
                }
                if (quantity == 0) continue;
                var summaries = new List<string>();
                foreach (var pair in found)
                {
                    if (summaries.Count == 3) { summaries.Add("…"); break; }
                    summaries.Add(pair.Key + " × " + pair.Value);
                }
                report.Results.Add(new SearchResult { Snapshot = snapshot, ChestName = names.Display(container.m_name),
                    ItemSummary = String.Join(", ", summaries.ToArray()), Quantity = quantity, Kinds = found.Count });
                report.Quantity += quantity;
                if (report.Results.Count >= 256) { report.Limited = true; break; }
            }
            return report;
        }
        private static float Distance(Container container, Player player)
        {
            return container == null ? Single.PositiveInfinity : (container.transform.position - player.transform.position).sqrMagnitude;
        }
    }
}
