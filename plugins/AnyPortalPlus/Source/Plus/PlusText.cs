// AnyPortal+ additions, 2026-10-06. GPL-3.0; see ../../LICENSE.
using System;

namespace XPortal.Plus
{
    internal static class PlusText
    {
        internal static string Language { get { return Localization.instance == null ? "English" : Localization.instance.GetSelectedLanguage(); } }
        internal static string Get(string russian, string english) { return Language == "Russian" ? russian : english; }
        internal static string Biome(string key)
        {
            string token, russian, english;
            switch (key) {
                case "Meadows": token = "$biome_meadows"; russian = "Луга"; english = "Meadows"; break;
                case "BlackForest": token = "$biome_blackforest"; russian = "Чёрный лес"; english = "Black forest"; break;
                case "Swamp": token = "$biome_swamp"; russian = "Болото"; english = "Swamp"; break;
                case "Mountain": token = "$biome_mountain"; russian = "Горы"; english = "Mountain"; break;
                case "Plains": token = "$biome_plains"; russian = "Равнины"; english = "Plains"; break;
                case "Mistlands": token = "$biome_mistlands"; russian = "Туманные земли"; english = "Mistlands"; break;
                case "AshLands": token = "$biome_ashlands"; russian = "Пепельные земли"; english = "Ashlands"; break;
                case "DeepNorth": token = "$biome_deepnorth"; russian = "Крайний север"; english = "Deep north"; break;
                case "Ocean": token = "$biome_ocean"; russian = "Океан"; english = "Ocean"; break;
                default: return Get("Биом неизвестен", "Biome unknown");
            }
            string translated = Localization.instance == null ? token : Localization.instance.Localize(token);
            return String.IsNullOrEmpty(translated) || translated == token || translated.IndexOf("[", StringComparison.Ordinal) == 0 ? Get(russian, english) : translated;
        }
    }
}
