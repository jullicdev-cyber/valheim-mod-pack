using System;
using System.Text;
using UnityEngine;

namespace ValheimModPack.ChestSearch
{
    // Optional entry point for the pack's supervised native probe. It only reads
    // APIs, localizations and already loaded containers; it never opens the UI.
    public static class NativeChecks
    {
        public static string Run()
        {
            var reader = new NativeChestReader();
            var warnings = new StringBuilder();
            var names = new NameIndex(message => warnings.Append(message).Append("; "));
            names.EnsureLoaded();
            int matched = 0;
            if (ObjectDB.instance != null)
            {
                var iron = ObjectDB.instance.GetItemPrefab("Iron");
                ItemDrop item = iron == null ? null : iron.GetComponent<ItemDrop>();
                if (item != null)
                {
                    if (!names.Matches(SearchText.Terms("Iron"), item.m_itemData)) throw new InvalidOperationException("English Iron query failed");
                    if (!names.Matches(SearchText.Terms("железо"), item.m_itemData)) throw new InvalidOperationException("Russian Iron query failed");
                    matched = 2;
                }
            }
            int readable = 0;
            if (Player.m_localPlayer != null)
            {
                foreach (Container chest in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
                {
                    ChestSnapshot snapshot; ReadFailure failure;
                    if (reader.TryRead(chest, Player.m_localPlayer, 30, out snapshot, out failure)) readable++;
                }
            }
            return "ChestSearch API PASS; aliases=" + names.AliasCount + "; native item queries=" + matched
                + "; accessible fresh loaded chests=" + readable + "; warnings=" + warnings;
        }
    }
}
