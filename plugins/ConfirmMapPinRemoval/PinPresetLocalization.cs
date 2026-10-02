using System;
using System.Collections.Generic;
using System.Text;

namespace ValheimModPack.PinRemoval
{
    public sealed class PinPresetLocalization
    {
        private sealed class Phrase
        {
            internal readonly string English, Russian, Token;
            internal Phrase(string english, string russian, string token)
            { English = english; Russian = russian; Token = token; }
        }
        // Tokens verified against installed Valheim 1.0.16 localization assets.
        // Native labels use the game's selected-language translation; additional
        // generic preset names have their own language table. Other mod UI
        // phrases have Russian/English translations and English fallback.
        private static readonly Dictionary<string, Phrase> phrases = CreatePhrases();
        private static readonly Dictionary<string, string[]> genericLabels = CreateGenericLabels();
        private readonly Func<string> language;
        private readonly Func<string, string> native;

        public PinPresetLocalization() : this(CurrentLanguage, LocalizeNative) { }
        public PinPresetLocalization(Func<string> selectedLanguage, Func<string, string> localizeNative)
        {
            if (selectedLanguage == null || localizeNative == null) throw new ArgumentNullException();
            language = selectedLanguage; native = localizeNative;
        }
        // No cached labels or global event subscription: language changes apply
        // on the next call. The UI may poll this property to refresh open panels.
        public string Language
        { get { string value = language(); return String.IsNullOrEmpty(value) ? "English" : value; } }
        public string Get(string key)
        {
            if (String.IsNullOrEmpty(key)) return "";
            Phrase phrase;
            if (phrases.TryGetValue(key, out phrase))
            {
                string translated;
                if (!String.IsNullOrEmpty(phrase.Token) && TryNative(phrase.Token, out translated)) return translated;
                int index = GenericIndex(phrase.English);
                string[] extra;
                if (index >= 0 && genericLabels.TryGetValue(Language, out extra)) return extra[index];
                return Language == "Russian" ? phrase.Russian : phrase.English;
            }
            // Allows the UI to reuse other stock game labels without treating
            // custom preset names as translation tokens.
            if (key[0] == '$') { string translated; if (TryNative(key, out translated)) return translated; }
            return Safe(key, 160);
        }
        public string Name(PinPreset preset)
        {
            if (preset == null) return "";
            if (!String.IsNullOrEmpty(preset.LocalizationKey) && phrases.ContainsKey(preset.LocalizationKey))
                return Safe(Get(preset.LocalizationKey), 96);
            return Safe(preset.Name, 96);
        }
        public string SearchText(PinPreset preset)
        {
            if (preset == null) return "";
            var result = new StringBuilder(Name(preset));
            result.Append(' ').Append(Safe(preset.Name, 96));
            Phrase phrase;
            if (!String.IsNullOrEmpty(preset.LocalizationKey) && phrases.TryGetValue(preset.LocalizationKey, out phrase))
                result.Append(' ').Append(phrase.English).Append(' ').Append(phrase.Russian)
                    .Append(' ').Append(preset.LocalizationKey);
            return result.ToString();
        }
        public string SearchAliases(PinPreset preset) { return SearchText(preset); }
        public bool Matches(PinPreset preset, string query)
        {
            if (preset == null) return false;
            string haystack = Normalize(SearchText(preset));
            foreach (string term in Normalize(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (haystack.IndexOf(term, StringComparison.Ordinal) < 0) return false;
            return true;
        }
        public static string Normalize(string text)
        { return Safe(text, 2048).ToLowerInvariant().Replace('\u0451', '\u0435'); }

        private bool TryNative(string token, out string translated)
        {
            translated = native(token);
            if (String.IsNullOrWhiteSpace(translated) || translated == token || translated == token.TrimStart('$')
                || translated == "[" + token.TrimStart('$') + "]" || translated.IndexOf('$') >= 0) return false;
            translated = Safe(translated, 160);
            return translated.Length > 0;
        }
        private static string CurrentLanguage()
        { return Localization.instance == null ? "English" : Localization.instance.GetSelectedLanguage(); }
        private static string LocalizeNative(string token)
        { return Localization.instance == null ? token : Localization.instance.Localize(token); }
        private static string Safe(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var text = new StringBuilder(); bool space = false;
            foreach (char c in value)
            {
                if (Char.IsWhiteSpace(c)) { space = text.Length > 0; continue; }
                if (Char.IsControl(c) || c == '\u202a' || c == '\u202b' || c == '\u202c' || c == '\u202d'
                    || c == '\u202e' || c == '\u2066' || c == '\u2067' || c == '\u2068' || c == '\u2069') continue;
                if (space) { text.Append(' '); space = false; }
                text.Append(c); if (text.Length >= limit) break;
            }
            if (text.Length > limit) text.Length = limit;
            if (text.Length > 0 && Char.IsHighSurrogate(text[text.Length - 1])) text.Length--;
            return text.ToString().Trim();
        }
        private static Dictionary<string, Phrase> CreatePhrases()
        {
            var result = new Dictionary<string, Phrase>(StringComparer.Ordinal);
            Add(result, "TrollCave", "Troll Cave", "Пещера тролля", "$location_forestcave");
            Add(result, "BearDen", "Bear Cave", "Медвежья берлога", "$location_bearcave");
            Add(result, "Raspberries", "Raspberries", "Малина", "$item_raspberries");
            Add(result, "Blueberries", "Blueberries", "Черника", "$item_blueberries");
            Add(result, "Mushrooms", "Mushroom", "Грибы", "$item_mushroomcommon");
            Add(result, "Dungeon", "Dungeon", "Подземелье", null);
            Add(result, "SurtlingSpawn", "Surtling Spawn", "Спавн суртлингов", null);
            Add(result, "Copper", "Copper Deposit", "Медная руда", "$piece_deposit_copper");
            Add(result, "Tin", "Tin Deposit", "Залежи олова", "$piece_deposit_tin");
            Add(result, "Iron", "Iron", "Железо", "$item_iron");
            Add(result, "Silver", "Silver Vein", "Серебряная жила", "$piece_deposit_silvervein");
            Add(result, "Cloudberries", "Cloudberries", "Морошка", "$item_cloudberries");
            Add(result, "Portal", "Portal", "Портал", "$piece_portal");
            Add(result, "Base", "Base", "База", null);
            Add(result, "BurialChambers", "Burial Chambers", "Погребальные комнаты", "$location_forestcrypt");
            Add(result, "Crypt", "Sunken Crypts", "Затонувшие склепы", "$location_sunkencrypt");
            Add(result, "MountainCave", "Frost Caves", "Ледяные пещеры", "$location_mountaincave");
            Add(result, "InfestedMine", "Infested Mine", "Заражённый рудник", "$location_dvergrtown");
            Add(result, "TarPit", "Tar Pit", "Дегтярная яма", null);
            Add(result, "Haldor", "Haldor", "Хальдор", "$npc_haldor");
            Add(result, "Hildir", "Hildir", "Хильдир", "$npc_hildir");
            Add(result, "FulingVillage", "Fuling Village", "Деревня гоблинов", null);
            Add(result, "Boars", "Boar", "Кабаны", "$enemy_boar");
            Add(result, "DragonEgg", "Dragon Egg", "Яйцо дракона", "$item_dragonegg");
            Add(result, "Flax", "Flax", "Лён", "$item_flax");

            Add(result, "presets_title", "Quick map pins", "Быстрые метки", null);
            Add(result, "presets_search", "Search presets…", "Поиск шаблонов…", null);
            Add(result, "presets_empty", "No matching presets", "Подходящих шаблонов нет", null);
            Add(result, "presets_close", "Close", "Закрыть", "$menu_close");
            Add(result, "presets_add", "Add", "Добавить", "$piece_smelter_add");
            Add(result, "presets_edit", "Edit", "Изменить", null);
            Add(result, "presets_delete", "Delete", "Удалить", null);
            Add(result, "presets_choose_point", "Choose a map position", "Выбрать точку на карте", null);
            Add(result, "presets_place_here", "Place here", "Поставить здесь", null);
            Add(result, "presets_page", "{0} / {1}", "{0} / {1}", null);
            Add(result, "presets_new_title", "New pin preset", "Новый шаблон метки", null);
            Add(result, "presets_edit_title", "Edit pin preset", "Изменить шаблон метки", null);
            Add(result, "presets_name", "Name", "Название", "$menu_name");
            Add(result, "presets_icon", "Icon", "Значок", null);
            Add(result, "presets_save", "Save", "Сохранить", "$menu_manualsave");
            Add(result, "apply", "Apply", "Применить", "$settings_apply");
            Add(result, "suggestion_place", "Place marker", "Поставить метку", null);
            Add(result, "suggestion_next", "Next", "Другая", null);
            Add(result, "suggestion_hide", "Hide", "Скрыть", null);
            Add(result, "presets_cancel", "Cancel", "Отмена", "$menu_cancel");
            Add(result, "presets_name_required", "Enter a name", "Введите название", null);
            Add(result, "presets_icon_required", "Choose an icon", "Выберите значок", null);
            Add(result, "presets_rename_title", "Rename map pin", "Переименовать метку", null);
            Add(result, "presets_builtin", "Default", "Стандартный", null);
            Add(result, "presets_custom", "Custom", "Свой", null);
            Add(result, "presets_selection_hint", "Select a preset to place a map pin", "Выберите шаблон, чтобы поставить метку", null);
            Add(result, "presets_icon_fire", "Campfire", "Костёр", "$piece_firepit");
            Add(result, "presets_icon_house", "House", "Дом", null);
            Add(result, "presets_icon_hammer", "Hammer", "Молот", "$item_hammer");
            Add(result, "presets_icon_pin", "Point", "Точка", null);
            Add(result, "presets_icon_portal", "Portal", "Портал", "$piece_portal");
            Add(result, "place_point", "Place at selected point", "Поставить в выбранной точке", null);
            Add(result, "place_here", "Place at my position", "Поставить на моей позиции", null);
            Add(result, "quick_pins", "Quick map pins", "Быстрые метки", null);
            Add(result, "icon", "Icon", "Значок", null);
            Add(result, "read_only", "Presets could not be loaded. The file has been preserved; editing is disabled.",
                "Не удалось загрузить шаблоны. Файл сохранён без изменений; редактирование отключено.", null);
            Add(result, "save_failed", "Could not save changes. See the BepInEx log.", "Не удалось сохранить изменения. Подробности в журнале BepInEx.", null);
            Add(result, "delete_preset", "Delete preset?", "Удалить шаблон?", null);
            Add(result, "delete_preset_body", "Delete the preset “{0}”? Existing map pins will remain.", "Удалить шаблон «{0}»? Уже поставленные метки останутся.", null);
            Add(result, "delete", "Delete", "Удалить", null);
            Add(result, "choose_point_hint", "Left-click the map to place the pin. Esc: cancel.", "Нажмите левой кнопкой мыши на карте, чтобы поставить метку. Esc: отмена.", null);
            Add(result, "pin_created", "Map pin created", "Метка поставлена", null);
            // Archives bind a placed built-in pin to its stable default ID.
            // This lookup is independent of the editable personal preset store:
            // deleting or renaming a template never removes an existing binding.
            foreach (PinPreset preset in PinPresetCatalog.Defaults())
                result.Add(preset.Id, result[preset.LocalizationKey]);
            return result;
        }
        private static void Add(Dictionary<string, Phrase> target, string key, string english, string russian, string token)
        { target.Add(key, new Phrase(english, russian, token)); }
        private static int GenericIndex(string english)
        {
            switch (english)
            {
                case "Dungeon": return 0;
                case "Base": return 1;
                case "Surtling Spawn": return 2;
                case "Tar Pit": return 3;
                case "Fuling Village": return 4;
                default: return -1;
            }
        }
        private static Dictionary<string, string[]> CreateGenericLabels()
        {
            // Game language identifiers, not OS locale codes. Abenaki and future
            // unknown languages use English until a reliable translation exists.
            return new Dictionary<string, string[]>(StringComparer.Ordinal) {
                { "Swedish", new[] { "Fängelsehåla", "Bas", "Surtlingarnas spawnplats", "Tjärgrop", "Fulingby" } },
                { "French", new[] { "Donjon", "Base", "Apparition de Surtlings", "Fosse à goudron", "Village de Fulings" } },
                { "Italian", new[] { "Sotterraneo", "Base", "Punto di comparsa dei Surtling", "Fossa di catrame", "Villaggio dei Fuling" } },
                { "German", new[] { "Verlies", "Basis", "Surtling-Spawnpunkt", "Teergrube", "Fuling-Dorf" } },
                { "Spanish", new[] { "Mazmorra", "Base", "Aparición de Surtlings", "Pozo de alquitrán", "Aldea de Fulings" } },
                { "Romanian", new[] { "Temniță", "Bază", "Loc de apariție Surtling", "Groapă de gudron", "Sat de Fuling" } },
                { "Bulgarian", new[] { "Подземие", "База", "Място за поява на суртлинги", "Катранена яма", "Село на фулинги" } },
                { "Macedonian", new[] { "Подземје", "База", "Место за појава на суртлинзи", "Јама со катран", "Село на фулинзи" } },
                { "Finnish", new[] { "Luolasto", "Tukikohta", "Surtlingien syntypaikka", "Tervakuoppa", "Fuling-kylä" } },
                { "Danish", new[] { "Fangehul", "Base", "Surtling-spawnsted", "Tjæregrube", "Fuling-landsby" } },
                { "Norwegian", new[] { "Fangehull", "Base", "Surtling-spawnpunkt", "Tjæregrop", "Fuling-landsby" } },
                { "Icelandic", new[] { "Dýflissa", "Bækistöð", "Surtlingabæli", "Tjörugryfja", "Fuling-þorp" } },
                { "Turkish", new[] { "Zindan", "Üs", "Surtling doğma noktası", "Katran çukuru", "Fuling köyü" } },
                { "Lithuanian", new[] { "Požemis", "Bazė", "Surtlingų atsiradimo vieta", "Dervos duobė", "Fulingų kaimas" } },
                { "Czech", new[] { "Podzemí", "Základna", "Místo výskytu surtlingů", "Dehtová jáma", "Vesnice fulingů" } },
                { "Hungarian", new[] { "Kazamata", "Bázis", "Surtlingek megjelenési helye", "Kátránygödör", "Fulingfalu" } },
                { "Slovak", new[] { "Podzemie", "Základňa", "Miesto výskytu surtlingov", "Dechtová jama", "Dedina fulingov" } },
                { "Polish", new[] { "Loch", "Baza", "Miejsce pojawiania się surtlingów", "Dół ze smołą", "Wioska fulingów" } },
                { "Dutch", new[] { "Kerker", "Basis", "Surtling-spawnplek", "Teerput", "Fuling-dorp" } },
                { "Portuguese_European", new[] { "Masmorra", "Base", "Local de aparecimento de Surtlings", "Poço de alcatrão", "Aldeia de Fulings" } },
                { "Portuguese_Brazilian", new[] { "Masmorra", "Base", "Local de surgimento de Surtlings", "Poço de alcatrão", "Vila de Fulings" } },
                { "Chinese", new[] { "地下城", "基地", "焰灵刷新点", "焦油坑", "丑地精村庄" } },
                { "Chinese_Trad", new[] { "地下城", "基地", "焰靈重生點", "焦油坑", "醜地精村莊" } },
                { "Japanese", new[] { "ダンジョン", "拠点", "スルトリングの出現地点", "タールの穴", "フューリングの村" } },
                { "Korean", new[] { "던전", "기지", "서틀링 생성 지점", "타르 구덩이", "풀링 마을" } },
                { "Hindi", new[] { "कालकोठरी", "आधार शिविर", "सर्टलिंग प्रकट होने की जगह", "तारकोल का गड्ढा", "फुलिंग गाँव" } },
                { "Thai", new[] { "ดันเจี้ยน", "ฐาน", "จุดเกิดเซิร์ตลิง", "บ่อน้ำมันดิน", "หมู่บ้านฟูลิง" } },
                { "Croatian", new[] { "Tamnica", "Baza", "Mjesto pojavljivanja surtlinga", "Katranova jama", "Selo fulinga" } },
                { "Georgian", new[] { "საპყრობილე", "ბაზა", "სურტლინგების გამოჩენის ადგილი", "კუპრის ორმო", "ფულინგების სოფელი" } },
                { "Greek", new[] { "Μπουντρούμι", "Βάση", "Σημείο εμφάνισης Surtling", "Λάκκος πίσσας", "Χωριό Fuling" } },
                { "Serbian", new[] { "Тамница", "База", "Место појављивања суртлинга", "Катранска јама", "Село фулинга" } },
                { "Ukrainian", new[] { "Підземелля", "База", "Місце появи суртлінгів", "Дьогтьова яма", "Село фулінгів" } },
                { "Latvian", new[] { "Pazeme", "Bāze", "Surtlingu parādīšanās vieta", "Darvas bedre", "Fuling ciems" } }
            };
        }
    }
}
