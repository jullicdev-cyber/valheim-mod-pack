using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
namespace ValheimModPack
{
    // Azu 3.1.6 reads QuickStackStore_player_ID.dat but keeps a second cache and writes
    // only TWO serialized lists. Quick Stack writes THREE, including trash flags.
    // Keep one live state and one canonical writer; never overwrite the legacy backup files.
    internal sealed class FavoriteStateLink
    {
        private readonly MethodInfo quickGet, azuGet, quickSave;
        private readonly FieldInfo quickSlots, quickItems, azuSlots, azuItems;
        private readonly string directory;
        private readonly Dictionary<long, bool> originallyPresent = new Dictionary<long, bool>();
        private readonly HashSet<long> binding = new HashSet<long>(), imported = new HashSet<long>();
        private readonly Dictionary<object, object> quickByAzu = new Dictionary<object, object>();
        private readonly Dictionary<object, object> azuByQuick = new Dictionary<object, object>();
        internal FavoriteStateLink(string directory, MethodInfo quickGet, MethodInfo azuGet, MethodInfo quickSave,
            FieldInfo quickSlots, FieldInfo quickItems, FieldInfo azuSlots, FieldInfo azuItems)
        {
            this.directory = directory; this.quickGet = quickGet; this.azuGet = azuGet; this.quickSave = quickSave;
            this.quickSlots = quickSlots; this.quickItems = quickItems; this.azuSlots = azuSlots; this.azuItems = azuItems;
        }
        internal void ObserveBeforeGet(long id)
        {
            if (id == 0) throw new InvalidOperationException("Favorite data requested without player identity");
            if (!originallyPresent.ContainsKey(id))
                originallyPresent[id] = File.Exists(Path.Combine(directory, "QuickStackStore_player_" + id.ToString(CultureInfo.InvariantCulture) + ".dat"));
        }
        internal void Bind(long id, object quick, object azu)
        {
            if (binding.Contains(id)) return;
            if (!originallyPresent.ContainsKey(id)) throw new InvalidOperationException("Favorite primary-file state was not observed before loading");
            binding.Add(id);
            try
            {
                if (quick == null) quick = quickGet.Invoke(null, new object[] { id });
                if (azu == null) azu = azuGet.Invoke(null, new object[] { id });
                if (quick == null || azu == null) throw new InvalidOperationException("Favorite configuration unavailable");
                var slots = quickSlots.GetValue(quick) as HashSet<Vector2i>;
                var items = quickItems.GetValue(quick) as HashSet<string>;
                var oldSlots = azuSlots.GetValue(azu) as HashSet<Vector2i>;
                var oldItems = azuItems.GetValue(azu) as HashSet<string>;
                if (slots == null || items == null || oldSlots == null || oldItems == null)
                    throw new InvalidOperationException("Favorite collections unavailable");
                bool migrate = !originallyPresent[id] && !imported.Contains(id);
                if (migrate) { slots.UnionWith(oldSlots); items.UnionWith(oldItems); }
                // Existing primary, even an empty file/set, always wins over stale mirrors.
                azuSlots.SetValue(azu, slots); azuItems.SetValue(azu, items);
                quickByAzu[azu] = quick; azuByQuick[quick] = azu;
                if (migrate)
                {
                    quickSave.Invoke(quick, null); // Native canonical format retains trashFlaggedItems.
                    imported.Add(id); originallyPresent[id] = true;
                }
            }
            finally { binding.Remove(id); }
        }
        internal void SaveAzu(object azu)
        {
            object quick;
            if (!quickByAzu.TryGetValue(azu, out quick)) throw new InvalidOperationException("Detached Azu favorite cache; refusing to overwrite canonical file");
            if (!ReferenceEquals(quickSlots.GetValue(quick), azuSlots.GetValue(azu)) || !ReferenceEquals(quickItems.GetValue(quick), azuItems.GetValue(azu)))
                throw new InvalidOperationException("Favorite cache references detached; refusing to overwrite canonical file");
            quickSave.Invoke(quick, null);
        }
        internal void AfterQuickSave(object quick)
        {
            object azu;
            if (!azuByQuick.TryGetValue(quick, out azu)) return;
            // Quick Stack's ResetAllFavoriting replaces both HashSets before calling Save.
            var slots = quickSlots.GetValue(quick) as HashSet<Vector2i>;
            var items = quickItems.GetValue(quick) as HashSet<string>;
            if (slots == null || items == null) throw new InvalidOperationException("Quick Stack reset produced invalid favorite data");
            azuSlots.SetValue(azu, slots); azuItems.SetValue(azu, items);
        }
    }
}
