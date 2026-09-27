using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.ChestSearch
{
    public sealed class NameIndex
    {
        private readonly Dictionary<string, string[]> aliases = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly Action<string> log;
        private bool loaded;
        public int AliasCount { get { return aliases.Count; } }
        public NameIndex(Action<string> log) { this.log = log; }
        public void Clear() { loaded = false; aliases.Clear(); }
        public void EnsureLoaded()
        {
            if (loaded || Localization.instance == null) return;
            loaded = true;
            try
            {
                var field = typeof(Localization).GetField("m_localizationSettings", BindingFlags.NonPublic | BindingFlags.Static);
                object settings = field == null ? null : field.GetValue(null);
                var property = settings == null ? null : settings.GetType().GetProperty("Localizations");
                IEnumerable assets = property == null ? null : property.GetValue(settings, null) as IEnumerable;
                if (assets == null) throw new MissingMemberException("Localization CSV assets unavailable");
                foreach (object item in assets)
                {
                    TextAsset asset = item as TextAsset;
                    if (asset != null) LocalizationCsv.Read(asset.text, aliases);
                }
            }
            catch (Exception error) { if (log != null) log("Extra RU/EN aliases unavailable; current language and prefab names remain searchable: " + error.Message); }
        }
        public string Display(string token)
        {
            return SearchText.Safe(Localization.instance == null ? token : Localization.instance.Localize(token ?? ""), 100);
        }
        public bool Matches(string[] terms, ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            string key = (item.m_shared.m_name ?? "").TrimStart('$');
            string[] extra;
            if (!aliases.TryGetValue(key, out extra)) extra = new[] { "", "" };
            return SearchText.Matches(terms, Display(item.m_shared.m_name), extra[0], extra[1], key,
                item.m_dropPrefab == null ? "" : item.m_dropPrefab.name);
        }
    }
}
