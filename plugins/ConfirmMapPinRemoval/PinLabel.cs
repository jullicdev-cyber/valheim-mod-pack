using System;
using System.Globalization;
using System.Text.RegularExpressions;
namespace ValheimModPack.PinRemoval
{
    public static class PinLabel
    {
        public static string Format(string name, bool russian)
        {
            string text = Regex.Replace(name ?? "", "<[^>]*>", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (String.IsNullOrWhiteSpace(text)) return russian ? "без названия" : "unnamed";
            var elements = StringInfo.ParseCombiningCharacters(text);
            return elements.Length > 80 ? text.Substring(0, elements[80]) + "…" : text;
        }
    }
}
