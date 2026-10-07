using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PartyPrison
{
    public sealed class PrisonPlayerRow
    {
        public string AccountId, Name;
        public double RemainingSeconds;
        public bool Sentenced, Online;
    }

    public sealed class PrisonUiBindings
    {
        public Func<bool> IsHost, CanUse, CanFight;
        public Func<List<PrisonPlayerRow>> Players;
        public Func<SentenceState> LocalSentence;
        public Action<string, double, string> Impose;
        public Action<string> Release, ForceRelease;
        public Action PrepareBuild, Build, Kit;
        public Action<int> Wave;
        public Action<int, int> Choice;
        public Func<int> CombatFamily, CombatDifficulty;
        public Action<bool> Move;
        public Func<string> Notice, CustodyStatus;
        public Func<string, string, string> Translate;
        public Action<Exception> Error;
    }

    // The panel only paints server snapshots. It never advances a sentence or
    // accepts a local clock as proof that the player may leave.
    public sealed class PrisonWindow
    {
        private const int PageSize = 8;
        private readonly PrisonUiBindings bindings;
        private readonly Button[] rows = new Button[PageSize];
        private readonly Button[] tiers = new Button[3];
        private readonly Button[] families = new Button[6];
        private readonly List<PrisonPlayerRow> players = new List<PrisonPlayerRow>();
        private GameObject overlay, panel;
        private Player owner;
        private ZNet network;
        private Text title, subtitle, selection, pagination, status, buildHint, sentence, reasonText, custodyText, foodText;
        private InputField minutes, reason;
        private Button previous, next, impose, release, forceRelease, build, kit, cell, arena;
        private string selectedAccount, localNotice = "";
        private bool inputOwned, hostPanel, buildArmed;
        private int page, generation;
        private float refreshAt;
        private int foodFamily = -1, foodDifficulty = -1;
        private string foodLanguage = "";

        public PrisonWindow(PrisonUiBindings bindings)
        {
            if (bindings == null) throw new ArgumentNullException("bindings");
            this.bindings = bindings;
        }

        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        private string T(string ru, string en) { return bindings.Translate == null ? ru : bindings.Translate(ru, en); }
        private bool Host() { return bindings.IsHost != null && bindings.IsHost(); }
        private bool Allowed() { return bindings.CanUse != null && bindings.CanUse(); }
        private void Report(Exception error) { if (bindings.Error != null) bindings.Error(error); }

        public void Show()
        {
            Hide(); owner = Player.m_localPlayer; network = ZNet.instance;
            if (!Valid() || GUIManager.CustomGUIFront == null) return;
            try
            {
                hostPanel = Host(); BuildVisuals();
                inputOwned = true; GUIManager.BlockInput(true);
                overlay.transform.SetAsLastSibling(); Repaint(); Scale();
                if (EventSystem.current != null && hostPanel && rows[0] != null && rows[0].interactable)
                    EventSystem.current.SetSelectedGameObject(rows[0].gameObject);
            }
            catch (Exception error) { Report(error); Hide(); }
        }

        private bool Valid()
        {
            return Allowed() && owner != null && ReferenceEquals(owner, Player.m_localPlayer)
                && network != null && ReferenceEquals(network, ZNet.instance)
                && !owner.IsDead() && !owner.IsTeleporting() && !owner.IsSleeping() && !owner.InCutscene()
                && !ZInput.s_IsRebindActive && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible()
                && !StoreGui.IsVisible() && !Hud.IsPieceSelectionVisible() && !PlayerCustomizaton.IsBarberGuiVisible()
                && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }

        public void Tick()
        {
            if (overlay == null && !inputOwned) return;
            try
            {
                if (!IsVisible || !Valid() || Host() != hostPanel) { Hide(); return; }
                if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    if (buildArmed) { buildArmed = false; Repaint(); } else Hide();
                    return;
                }
                if (Time.unscaledTime >= refreshAt)
                { refreshAt = Time.unscaledTime + .5f; Repaint(); Scale(); }
            }
            catch (Exception error) { Report(error); Hide(); }
        }

        public void HandleInputReset()
        {
            // GUIManager.ResetInputBlock already removed this increment. A
            // decrement here could steal the lease of another open panel.
            inputOwned = false; Hide();
        }

        public void Hide()
        {
            ++generation;
            try
            {
                if (overlay != null)
                {
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform))
                        EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            finally
            {
                overlay = panel = null; owner = null; network = null;
                title = subtitle = selection = pagination = status = buildHint = sentence = reasonText = custodyText = foodText = null;
                minutes = reason = null; previous = next = impose = release = forceRelease = build = kit = cell = arena = null;
                for (int i = 0; i < rows.Length; ++i) rows[i] = null;
                for (int i = 0; i < tiers.Length; ++i) tiers[i] = null;
                for (int i = 0; i < families.Length; ++i) families[i] = null;
                players.Clear(); selectedAccount = null; page = 0; buildArmed = false; localNotice = "";
                foodFamily = foodDifficulty = -1; foodLanguage = "";
                if (inputOwned) { inputOwned = false; GUIManager.BlockInput(false); }
            }
        }

        private void BuildVisuals()
        {
            var center = new Vector2(.5f, .5f);
            overlay = new GameObject("PartyPrison.Modal", typeof(RectTransform), typeof(Image));
            overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
            var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; overlay.GetComponent<Image>().color = new Color(0, 0, 0, .6f);
            panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, hostPanel ? 1080 : 820, hostPanel ? 790 : 920, false);
            panel.name = hostPanel ? "PartyPrison.HostPanel" : "PartyPrison.PrisonerPanel";
            var group = panel.AddComponent<CanvasGroup>(); group.interactable = true; group.blocksRaycasts = true;
            title = Label(T("Тюрьма", "Prison"), 0, hostPanel ? 337 : 398, hostPanel ? 910 : 660, 44, 29, true);
            ButtonAt("X", hostPanel ? 487 : 359, hostPanel ? 339 : 399, 40, 36, Hide);
            subtitle = Label("", 0, hostPanel ? 291 : 350, hostPanel ? 985 : 730, 38, 17, false);
            if (hostPanel) BuildHost(); else BuildPrisoner();
        }

        private void BuildHost()
        {
            Label(T("Игроки и приговоры", "Players and sentences"), -282, 242, 455, 32, 22, true);
            for (int i = 0; i < PageSize; ++i)
            {
                int slot = i;
                rows[i] = ButtonAt("", -282, 197 - i * 45, 455, 39, () => Select(slot));
                Text text = rows[i].GetComponentInChildren<Text>(); text.alignment = TextAnchor.MiddleLeft;
                text.rectTransform.offsetMin = new Vector2(12, 2); text.rectTransform.offsetMax = new Vector2(-8, -2);
            }
            previous = ButtonAt("<", -458, -171, 44, 34, () => { if (page > 0) --page; buildArmed = false; Repaint(); });
            pagination = Label("", -282, -171, 244, 32, 17, false);
            next = ButtonAt(">", -106, -171, 44, 34, () => { ++page; buildArmed = false; Repaint(); });
            selection = Label("", 255, 224, 440, 58, 20, true);
            Label(T("Срок в минутах", "Sentence in minutes"), 134, 168, 220, 30, 18, false);
            minutes = Field("10", 353, 168, 185, 39, InputField.ContentType.DecimalNumber, 8);
            Label(T("Причина", "Reason"), 255, 119, 440, 28, 18, false);
            reason = Field("", 255, 77, 440, 46, InputField.ContentType.Standard, 160);
            var reasonPlaceholder = reason.placeholder as Text;
            if (reasonPlaceholder != null) reasonPlaceholder.text = T("Нарушение правил пати", "Party rule violation");
            impose = ButtonAt(T("Посадить", "Imprison"), 134, 14, 212, 44, Impose);
            release = ButtonAt(T("Освободить", "Release"), 376, 14, 212, 44, Release);
            forceRelease = ButtonAt(T("Принудительно освободить", "Force release"), 255, -39, 440, 40, ForceRelease);
            Label(T("Сложность следующих волн", "Difficulty of future waves"), 255, -87, 440, 30, 20, true);
            WaveButtons(255, -127, 140, 40, 150);
            Label(T("Волны появляются сами, когда заключённый на арене. Добычу с мобов он сохраняет.",
                "Waves spawn automatically while the prisoner is in the arena. The prisoner keeps enemy loot."), 255, -183, 440, 49, 16, false);
            build = ButtonAt(T("Построить тюрьму возле меня", "Build prison near me"), 0, -235, 580, 45, BuildPrison);
            buildHint = Label("", 0, -283, 984, 48, 16, false);
            status = Label("", 0, -345, 984, 56, 17, false);
            int created = generation;
            minutes.onValueChanged.AddListener(value => { if (created == generation && IsVisible) RepaintActions(); });
        }

        private void BuildPrisoner()
        {
            sentence = Label("", 0, 298, 710, 57, 29, true);
            reasonText = Label("", 0, 244, 710, 43, 18, false);
            Label(T("Открывайте этот пульт клавишей взаимодействия в камере.\nОтдыхайте на лавочке или кровати. Перед сменой мобов закройте сундук.",
                "Use the interaction key on the cell's arena console.\nRest on the bench or bed. Close the chest before changing enemies."), 0, 186, 710, 62, 18, false);
            kit = ButtonAt(T("Сундук снаряжения — подсказка", "Equipment chest — help"), 0, 120, 638, 42, Kit);
            cell = ButtonAt(T("Вернуться в камеру", "Return to cell"), -168, 64, 300, 42, () => Move(false));
            arena = ButtonAt(T("Перейти на арену", "Go to arena"), 168, 64, 300, 42, () => Move(true));
            Label(T("Противники", "Enemies"), 0, 14, 710, 30, 21, true);
            for (int i = 0; i < families.Length; ++i) {
                int family = i;
                families[i] = ButtonAt(T(CombatCatalog.Name(i, true), CombatCatalog.Name(i, false)), (i % 3 - 1) * 233, -29 - (i / 3) * 49, 216, 40, () => SelectFamily(family));
            }
            Label(T("Сложность следующих волн", "Difficulty of future waves"), 0, -124, 710, 30, 21, true);
            WaveButtons(0, -169, 216, 40, 233);
            foodText = Label("", 0, -226, 710, 64, 16, false);
            custodyText = Label("", 0, -307, 710, 84, 17, false);
            status = Label("", 0, -393, 710, 65, 16, false);
        }

        private void WaveButtons(float centerX, float y, float width, float height, float spacing)
        {
            tiers[0] = ButtonAt(T("Слабые", "Weak"), centerX - spacing, y, width, height, () => Wave(0));
            tiers[1] = ButtonAt(T("Средние", "Medium"), centerX, y, width, height, () => Wave(1));
            tiers[2] = ButtonAt(T("Сильные", "Strong"), centerX + spacing, y, width, height, () => Wave(2));
        }

        private void Repaint()
        {
            if (panel == null) return;
            subtitle.text = hostPanel
                ? T("Выберите игрока и срок. Отсчёт идёт только во время игры на сервере.", "Select a player and duration. Time counts only while playing on this server.")
                : T("Приговор действует до освобождения хостом или окончания срока.", "Your sentence lasts until the host releases you or the time is served.");
            if (hostPanel)
            {
                players.Clear();
                List<PrisonPlayerRow> snapshot = bindings.Players == null ? null : bindings.Players();
                if (snapshot != null) foreach (PrisonPlayerRow player in snapshot)
                    if (player != null && !String.IsNullOrEmpty(player.AccountId) && (player.Online || player.Sentenced)) players.Add(player);
                int pageCount = Math.Max(1, (players.Count + PageSize - 1) / PageSize); page = Math.Max(0, Math.Min(page, pageCount - 1));
                if (Selected() == null) selectedAccount = null;
                for (int i = 0; i < rows.Length; ++i)
                {
                    int index = page * PageSize + i; bool populated = index < players.Count;
                    rows[i].interactable = populated;
                    string caption = "";
                    if (populated)
                    {
                        PrisonPlayerRow player = players[index];
                        caption = (String.Equals(player.AccountId, selectedAccount, StringComparison.Ordinal) ? "› " : "") + Safe(player.Name, 56)
                            + (player.Online ? "" : T(" · офлайн", " · offline"))
                            + (player.Sentenced ? " · " + Duration(player.RemainingSeconds) : "");
                    }
                    rows[i].GetComponentInChildren<Text>().text = caption;
                }
                pagination.text = (page + 1).ToString(CultureInfo.InvariantCulture) + " / " + pageCount.ToString(CultureInfo.InvariantCulture);
                previous.interactable = page > 0; next.interactable = page + 1 < pageCount;
                PrisonPlayerRow selected = Selected();
                selection.text = selected == null ? T("Выберите игрока слева", "Select a player on the left") : Safe(selected.Name, 72);
                build.GetComponentInChildren<Text>().text = buildArmed ? T("Подтвердить постройку", "Confirm construction") : T("Построить тюрьму возле меня", "Build prison near me");
                buildHint.text = buildArmed
                    ? T("Перед вами будут удалены препятствия и постройки, земля выровнена. Вещи и питомцы переместятся наружу. Esc — отмена.",
                        "Obstacles and buildings in front of you will be removed; ground levelled. Belongings and pets move outside. Esc cancels.")
                    : T("Центр тюрьмы — в ", "Prison center: ") + PrisonPlacementPlan.CenterDistance.ToString("0", CultureInfo.InvariantCulture)
                        + T(" м перед вами по направлению взгляда. Вход обращён к вам. Пустую тюрьму можно перестроить.",
                            " m ahead in your look direction; entrance faces you. An empty prison can be rebuilt.");
                RepaintActions();
            }
            else
            {
                SentenceState state = bindings.LocalSentence == null ? null : bindings.LocalSentence();
                bool ready = CanFight(state);
                string custody = bindings.CustodyStatus == null ? "" : bindings.CustodyStatus();
                sentence.text = state == null ? String.IsNullOrEmpty(custody)
                    ? T("Ожидаю состояние приговора…", "Waiting for sentence status…") : T("Хранение ваших вещей", "Your belongings in custody")
                    : state.EmergencyRelease ? T("Принудительное освобождение", "Emergency release")
                    : state.PendingRelease ? T("Срок завершён", "Sentence complete")
                    : !ready ? T("Подготовка заключения…", "Preparing imprisonment…")
                    : T("Осталось: ", "Remaining: ") + Duration(state.RemainingSeconds);
                reasonText.text = state == null ? "" : T("Причина: ", "Reason: ") + Safe(state.Reason, 180);
                custodyText.text = String.IsNullOrEmpty(custody)
                    ? state != null && state.PendingRelease
                        ? T("После открытия решётки взаимодействуйте с сундуками, чтобы забрать вещи.\nДобыча сохраняется; тюремное снаряжение и несъеденные пайки удаляются.",
                            "Once the gate opens, interact with the chests to retrieve your belongings.\nLoot is kept; loan equipment and uneaten rations are removed.")
                        : !ready
                            ? T("Ваши вещи сохраняются в четырёх железных сундуках. Дождитесь окончания подготовки перед боем.",
                                "Your belongings are being secured in four iron chests. Wait for preparation to finish before fighting.")
                            : T("Ваши вещи находятся в четырёх обычных железных сундуках в прихожей. Снаряжение — в сундуке камеры.\nПри поражении вы проснётесь в камере и сохраните добычу с арены.",
                                "Your belongings are in four normal iron chests in the foyer. Equipment is in the cell chest.\nOn defeat you wake in the cell and keep your arena loot.")
                    : Safe(custody, 440);
                if (kit != null) kit.interactable = ready && bindings.Kit != null;
                if (cell != null) cell.interactable = ready && bindings.Move != null;
                if (arena != null) arena.interactable = ready && bindings.Move != null;
                foreach (Button tier in tiers) if (tier != null) tier.interactable = ready && (bindings.Wave != null || bindings.Choice != null);
                int selectedFamily = bindings.CombatFamily == null ? 0 : bindings.CombatFamily();
                RepaintFood(selectedFamily, bindings.CombatDifficulty == null ? 0 : bindings.CombatDifficulty());
                for (int i = 0; i < families.Length; ++i) if (families[i] != null) {
                    families[i].interactable = ready && bindings.Choice != null;
                    families[i].GetComponentInChildren<Text>().text = (i == selectedFamily ? "› " : "") + T(CombatCatalog.Name(i, true), CombatCatalog.Name(i, false));
                }
            }
            int selectedTier = bindings.CombatDifficulty == null ? 0 : bindings.CombatDifficulty();
            string[] tierRu = { "Слабые", "Средние", "Сильные" }, tierEn = { "Weak", "Medium", "Strong" };
            for (int i = 0; i < tiers.Length; ++i) if (tiers[i] != null) tiers[i].GetComponentInChildren<Text>().text = (i == selectedTier ? "› " : "") + T(tierRu[i], tierEn[i]);
            string serviceNotice = bindings.Notice == null ? "" : bindings.Notice();
            status.text = Safe(String.IsNullOrEmpty(localNotice) ? serviceNotice : localNotice, 380);
        }

        private void RepaintFood(int family, int difficulty)
        {
            if (foodText == null) return;
            string language = Localization.instance == null ? "" : Localization.instance.GetSelectedLanguage();
            if (family == foodFamily && difficulty == foodDifficulty && language == foodLanguage) return;
            PrisonCombatLoadout choice = CombatCatalog.Get(family, difficulty);
            string[] names = new string[choice.FoodSources.Length];
            for (int i = 0; i < names.Length; ++i) {
                GameObject prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(choice.FoodSources[i]);
                ItemDrop item = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                string name = item == null ? choice.FoodSources[i] : item.m_itemData.m_shared.m_name;
                names[i] = Localization.instance == null ? name : Localization.instance.Localize(name);
            }
            foodText.text = T("Здоровье: ", "Health: ") + names[0] + ", " + names[1]
                + "\n" + T("Выносливость: ", "Stamina: ") + names[2] + ", " + names[3]
                + T(". Порций каждого: ", ". Portions each: ") + choice.FoodServings
                + "\n" + T("Одновременно — 3 блюда. Съеденная еда действует обычное время.", "Three foods at once. Eaten food keeps its normal duration.");
            foodFamily = family; foodDifficulty = difficulty; foodLanguage = language;
        }

        private void RepaintActions()
        {
            if (impose == null || release == null) return;
            PrisonPlayerRow selected = Selected(); double duration;
            bool validDuration = TryMinutes(out duration);
            impose.interactable = selected != null && selected.Online && validDuration && bindings.Impose != null;
            release.interactable = selected != null && selected.Sentenced && bindings.Release != null;
            if (forceRelease != null) forceRelease.interactable = selected != null && selected.Sentenced && bindings.ForceRelease != null;
            build.interactable = bindings.Build != null;
        }

        private bool CanFight(SentenceState state)
        { return state != null && !state.PendingRelease && (bindings.CanFight == null || bindings.CanFight()); }

        private PrisonPlayerRow Selected()
        {
            foreach (PrisonPlayerRow player in players)
                if (String.Equals(player.AccountId, selectedAccount, StringComparison.Ordinal)) return player;
            return null;
        }

        private void Select(int slot)
        {
            int index = page * PageSize + slot;
            if (index < 0 || index >= players.Count) return;
            selectedAccount = players[index].AccountId; buildArmed = false; localNotice = ""; Repaint();
        }

        private bool TryMinutes(out double duration)
        {
            duration = 0;
            return minutes != null && Double.TryParse((minutes.text ?? "").Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out duration) && !Double.IsNaN(duration) && !Double.IsInfinity(duration)
                && duration * 60 >= 1 && duration * 60 <= SentencePolicy.MaximumDurationSeconds;
        }

        private void Impose()
        {
            PrisonPlayerRow selected = Selected(); double duration;
            if (selected == null || !selected.Online || !TryMinutes(out duration) || bindings.Impose == null) return;
            string explanation = reason == null ? "" : Safe(reason.text, 160).Trim();
            if (String.IsNullOrEmpty(explanation)) explanation = T("Нарушение правил пати", "Party rule violation");
            buildArmed = false; localNotice = ""; bindings.Impose(selected.AccountId, duration, explanation); Repaint();
        }

        private void Release()
        {
            PrisonPlayerRow selected = Selected();
            if (selected == null || !selected.Sentenced || bindings.Release == null) return;
            buildArmed = false; localNotice = ""; bindings.Release(selected.AccountId); Repaint();
        }

        private void ForceRelease()
        {
            PrisonPlayerRow selected = Selected();
            if (!hostPanel || selected == null || !selected.Sentenced || bindings.ForceRelease == null) return;
            buildArmed = false; localNotice = ""; bindings.ForceRelease(selected.AccountId); Repaint();
        }

        private void SelectFamily(int family)
        {
            if (bindings.Choice == null || !CanFight(bindings.LocalSentence == null ? null : bindings.LocalSentence())) return;
            localNotice = ""; bindings.Choice(family, bindings.CombatDifficulty == null ? 0 : bindings.CombatDifficulty()); Repaint();
        }

        private void BuildPrison()
        {
            if (!hostPanel || bindings.Build == null) return;
            if (!buildArmed) { if (bindings.PrepareBuild != null) bindings.PrepareBuild(); buildArmed = true; localNotice = ""; Repaint(); return; }
            buildArmed = false; localNotice = ""; bindings.Build(); Repaint();
        }

        private void Move(bool arena)
        {
            if (bindings.Move == null) return;
            if (!hostPanel && !CanFight(bindings.LocalSentence == null ? null : bindings.LocalSentence())) return;
            buildArmed = false; localNotice = ""; bindings.Move(arena); Hide();
        }

        private void Kit()
        {
            if (bindings.Kit == null) return;
            SentenceState state = bindings.LocalSentence == null ? null : bindings.LocalSentence();
            if (!CanFight(state)) return;
            buildArmed = false; localNotice = ""; bindings.Kit(); Repaint();
        }

        private void Wave(int tier)
        {
            if (bindings.Wave == null && bindings.Choice == null) return;
            if (!hostPanel && !CanFight(bindings.LocalSentence == null ? null : bindings.LocalSentence())) return;
            buildArmed = false; localNotice = "";
            if (bindings.Choice != null) bindings.Choice(bindings.CombatFamily == null ? 0 : bindings.CombatFamily(), tier); else bindings.Wave(tier);
            Repaint();
        }

        private void Invoke(int created, Action action)
        {
            if (created != generation || !IsVisible) return;
            try { if (!Valid()) { Hide(); return; } action(); }
            catch (Exception error) { Report(error); localNotice = Safe(error.Message, 300); if (IsVisible) Repaint(); }
        }

        private Text Label(string value, float x, float y, float width, float height, int size, bool heading)
        {
            var gui = GUIManager.Instance; var center = new Vector2(.5f, .5f);
            Text text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y), heading ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, heading ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = size; return text;
        }

        private Button ButtonAt(string caption, float x, float y, float width, float height, Action action)
        {
            int created = generation; var center = new Vector2(.5f, .5f);
            Button button = GUIManager.Instance.CreateButton(caption, panel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            Text text = button.GetComponentInChildren<Text>(); text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = 18;
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            button.onClick.AddListener(() => Invoke(created, action)); return button;
        }

        private InputField Field(string value, float x, float y, float width, float height, InputField.ContentType contentType, int limit)
        {
            var center = new Vector2(.5f, .5f);
            InputField input = GUIManager.Instance.CreateInputField(panel.transform, center, center, new Vector2(x, y), contentType, value, 19, width, height).GetComponent<InputField>();
            input.characterLimit = limit; input.textComponent.supportRichText = false;
            var placeholder = input.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
            input.text = value; return input;
        }

        private void Scale()
        {
            if (overlay == null || panel == null) return;
            var rect = overlay.GetComponent<RectTransform>();
            float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / (hostPanel ? 1110 : 850), rect.rect.height / (hostPanel ? 820 : 950)));
            if (scale > .01f) panel.transform.localScale = Vector3.one * scale;
        }

        private static string Safe(string value, int maximum)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var clean = new List<char>(Math.Min(value.Length, maximum));
            foreach (char c in value) { if (clean.Count >= maximum) break; if (!Char.IsControl(c)) clean.Add(c); }
            return new String(clean.ToArray());
        }

        private static string Duration(double value)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value <= 0) return "00:00";
            int seconds = (int)Math.Min(Int32.MaxValue, Math.Ceiling(value));
            return (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
