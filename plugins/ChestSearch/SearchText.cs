using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimModPack.ChestSearch
{
    public static class SearchText
    {
        public static bool StandardChest(string prefab)
        {
            return prefab == "piece_chest_wood" || prefab == "piece_chest" || prefab == "piece_chest_private" || prefab == "piece_chest_blackmetal";
        }
        public static string Safe(string value, int limit)
        {
            if (String.IsNullOrEmpty(value) || limit < 1) return "";
            var clean = new StringBuilder(Math.Min(value.Length, 1024));
            bool tag = false;
            for (int i = 0; i < value.Length && clean.Length < 4096; i++)
            {
                char c = value[i];
                if (c == '<' && value.IndexOf('>', i + 1) >= 0) { tag = true; continue; }
                if (tag) { if (c == '>') tag = false; continue; }
                if (Char.IsWhiteSpace(c)) { clean.Append(' '); continue; }
                if (!Char.IsControl(c) && Char.GetUnicodeCategory(c) != UnicodeCategory.Format) clean.Append(c);
            }
            string result = clean.ToString().Trim();
            int[] boundaries = StringInfo.ParseCombiningCharacters(result);
            return boundaries.Length > limit ? result.Substring(0, boundaries[limit]) + "…" : result;
        }

        public static string Normalize(string value)
        {
            string clean = Safe(value, 256).ToLowerInvariant().Replace('ё', 'е');
            try { return clean.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { return clean; }
        }

        public static string[] Terms(string query)
        {
            return Normalize(Safe(query, 64)).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool Matches(string[] terms, params string[] names)
        {
            if (terms == null || terms.Length == 0) return false;
            var normalized = new string[names == null ? 0 : names.Length];
            for (int i = 0; i < normalized.Length; i++) normalized[i] = Normalize(names[i]);
            foreach (string term in terms)
            {
                bool found = false;
                foreach (string name in normalized)
                    if (name.IndexOf(term, StringComparison.Ordinal) >= 0) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
    }

    // Parse the game's CSV text into our own index. This never calls SetLanguage
    // or changes Localization.instance, and supports quoted multiline cells.
    public static class LocalizationCsv
    {
        public static void Read(string csv, Dictionary<string, string[]> aliases)
        {
            if (String.IsNullOrEmpty(csv) || csv.Length > 32 * 1024 * 1024) return;
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            bool header = true;
            int english = -1, russian = -1;
            for (int i = 0; i <= csv.Length; i++)
            {
                char c = i == csv.Length ? '\n' : csv[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = !quoted;
                    continue;
                }
                if (!quoted && (c == ',' || c == '\n' || c == '\r'))
                {
                    row.Add(cell.ToString()); cell.Length = 0;
                    if (row.Count > 256) throw new FormatException("Too many localization CSV columns");
                    if (c == ',') continue;
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    if (header)
                    {
                        for (int k = 0; k < row.Count; k++)
                        {
                            string name = row[k].Trim().Trim('\ufeff');
                            if (name == "English") english = k;
                            if (name == "Russian") russian = k;
                        }
                        header = false;
                    }
                    else if (row.Count > 1 && !String.IsNullOrWhiteSpace(row[0]))
                    {
                        if (aliases.Count > 100000) throw new FormatException("Localization index too large");
                        string key = row[0].Trim().TrimStart('$');
                        string en = english >= 0 && english < row.Count ? row[english] : "";
                        string ru = russian >= 0 && russian < row.Count ? row[russian] : "";
                        aliases[key] = new[] { en, ru };
                    }
                    row.Clear();
                }
                else
                {
                    if (cell.Length >= 65536) throw new FormatException("Localization cell too large");
                    cell.Append(c);
                }
            }
            if (quoted) throw new FormatException("Unterminated localization CSV quote");
        }
    }
}
