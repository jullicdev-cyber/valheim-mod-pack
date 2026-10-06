using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.WorldCharacters
{
    public sealed class AdministrationUiBindings
    {
        public Func<bool> CanUse;
        public Func<long> WorldId;
        public Func<string> ShortcutLabel, StatusText;
        public Func<string, string, string> Translate;
        public Func<AdministrationView> Snapshot;
        public Func<int, string> ItemName;
        public Func<int, Sprite> ItemIcon;
        public Action Refresh, OnClosed;
        public Action<string> Inspect;
        public Action<string, string, AdministrationDecision> Decide;
        public Action<Exception> Error;
    }

    internal sealed class AdministrationInputLease
    {
        private readonly Action<bool> change;
        internal bool Held { get; private set; }
        internal AdministrationInputLease(Action<bool> change) { this.change = change; }
        internal void Acquire()
        {
            if (Held) return;
            Held = true;
            try { change(true); } catch { try { Release(); } catch { } throw; }
        }
        internal void Release() { if (Held) { Held = false; change(false); } }
        internal void ForgetAfterGlobalReset() { Held = false; }
    }

    // Rendering reads an immutable memory snapshot. Disk access belongs to the
    // administration service, and every action returns its complete request ID.
    public sealed class AdministrationWindow
    {
        private const int PageSize = 6;
        private static readonly FieldInfo InputCountField = typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly AdministrationUiBindings bindings;
        private readonly AdministrationInputLease inputLease = new AdministrationInputLease(GUIManager.BlockInput);
        private readonly Button[] requestRows = new Button[PageSize];
        private readonly Image[] itemIcons = new Image[PageSize];
        private readonly Text[] itemRows = new Text[PageSize];
        private readonly List<AdministrationRequest> filtered = new List<AdministrationRequest>();
        private readonly List<Selectable> controls = new List<Selectable>();
        private GameObject overlay, panel, confirmation;
        private Player player;
        private ZNet network;
        private long world, confirmationWorld;
        private int generation, requestPage, itemPage, confirmationGeneration;
        private float nextPaint;
        private bool statusMode;
        private string selectedId = "", notice = "", confirmationId, confirmationFingerprint;
        private AdministrationView view;
        private AdministrationDecision confirmationDecision;
        private InputField search;
        private Text title, subtitle, requestHeading, itemHeading, requestPages, itemPages, identity, hint, status, diagnostics;
        private Text confirmationCaption;
        private Button previousRequest, nextRequest, previousItem, nextItem, approve, reject, fresh, refresh, stateTab, confirmYes, confirmNo;
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }

        public AdministrationWindow(AdministrationUiBindings bindings)
        { if (bindings == null) throw new ArgumentNullException("bindings"); this.bindings = bindings; }
        private string T(string ru, string en) { return bindings.Translate == null ? en : bindings.Translate(ru, en); }
        private bool Allowed() { return bindings.CanUse != null && bindings.CanUse(); }
        private long World() { return bindings.WorldId == null ? 0 : bindings.WorldId(); }
        private void Report(Exception error) { if (bindings.Error != null) bindings.Error(error); }
        private string ServiceText(string value)
        {
            switch (value)
            {
                case "Request rejected; the player can submit again by reconnecting.":
                    return T("Заявка отклонена. Игрок может изменить персонажа и отправить новую заявку, подключившись повторно.", value);
                case "Approved with a new character; the player can reconnect.":
                    return T("Новый старт одобрен. Игрок может подключиться повторно.", value);
                case "Approved with the reviewed progress; the player can reconnect.":
                    return T("Проверенный прогресс одобрен. Игрок может подключиться повторно.", value);
                case "Inspect the current request before making a decision.":
                    return T("Перед решением проверьте актуальное содержимое заявки.", value);
                case "The host session changed; open the window again.":
                    return T("Сессия хоста изменилась. Откройте окно заново.", value);
                case "Character already exists; approval cannot overwrite progress.":
                    return T("Персонаж уже принят. Одобрение не может перезаписать его прогресс.", value);
                default: return value ?? "";
            }
        }
        private static string Safe(string value, int limit)
        {
            if (String.IsNullOrEmpty(value)) return "";
            var chars = new List<char>(Math.Min(value.Length, limit));
            foreach (char c in value) { if (chars.Count == limit) break; if (!Char.IsControl(c)) chars.Add(c); }
            return new string(chars.ToArray());
        }
        private bool ValidContext()
        {
            return Allowed() && world != 0 && World() == world && player != null && ReferenceEquals(player, Player.m_localPlayer)
                && network != null && ReferenceEquals(network, ZNet.instance) && network.GetWorldUID() == world
                && !player.IsDead() && !player.IsTeleporting() && !player.IsSleeping() && !player.InCutscene()
                && !ZInput.s_IsRebindActive && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible()
                && !StoreGui.IsVisible() && !Hud.IsPieceSelectionVisible() && !PlayerCustomizaton.IsBarberGuiVisible()
                && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large)
                && (InputCountField == null || (int)InputCountField.GetValue(null) == (inputLease.Held ? 1 : 0));
        }
        public void Show()
        {
            Hide(); player = Player.m_localPlayer; network = ZNet.instance; world = World();
            if (!ValidContext() || GUIManager.CustomGUIFront == null) return;
            try
            {
                BuildVisuals(); inputLease.Acquire(); overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(refresh.gameObject);
                Refresh();
            }
            catch (Exception error) { Report(error); Hide(); }
        }
        // The menu-only native fixture calls this without a real player or lease.
        private void BuildVisuals()
        {
            if (world == 0) world = World();
            Vector2 center = new Vector2(.5f, .5f);
            overlay = new GameObject("WorldCharacters.AdministrationModal", typeof(RectTransform), typeof(Image));
            overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
            var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; overlay.GetComponent<Image>().color = new Color(0, 0, 0, .6f);
            panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 1130, 820, false);
            panel.name = "WorldCharacters.AdministrationPanel";
            var group = panel.AddComponent<CanvasGroup>(); group.interactable = true; group.blocksRaycasts = true;
            title = Label("", 0, 350, 970, 42, 29, true);
            ButtonAt("X", 512, 351, 42, 36, Hide);
            subtitle = Label("", 0, 305, 1030, 34, 16, false);
            requestHeading = Label("", -390, 253, 310, 34, 22, true);
            itemHeading = Label("", 61, 253, 468, 34, 22, true);
            stateTab = ButtonAt("", 418, 254, 190, 36, () => { statusMode = !statusMode; CancelConfirmation(); Repaint(); });
            stateTab.gameObject.name = "WorldCharacters.StatusTab";
            search = GUIManager.Instance.CreateInputField(panel.transform, center, center, new Vector2(-390, 207),
                InputField.ContentType.Standard, "", 18, 310, 38).GetComponent<InputField>();
            search.gameObject.name = "WorldCharacters.RequestSearch"; search.characterLimit = 80; search.textComponent.supportRichText = false;
            var placeholder = search.placeholder as Text; if (placeholder != null) placeholder.supportRichText = false;
            controls.Add(search); int created = generation;
            search.onValueChanged.AddListener(value =>
            {
                if (created != generation || !IsVisible || confirmation != null) return;
                requestPage = itemPage = 0; selectedId = ""; CancelConfirmation(); Repaint();
            });
            for (int i = 0; i < PageSize; i++)
            {
                int slot = i;
                requestRows[i] = ButtonAt("", -390, 153 - i * 53, 310, 47, () => SelectRequest(slot));
                requestRows[i].gameObject.name = "WorldCharacters.RequestRow" + i;
                requestRows[i].GetComponentInChildren<Text>().alignment = TextAnchor.MiddleLeft;
                var textRect = requestRows[i].GetComponentInChildren<Text>().rectTransform;
                textRect.offsetMin = new Vector2(9, 2); textRect.offsetMax = new Vector2(-8, -2);
                itemRows[i] = Label("", 190, 199 - i * 52, 640, 46, 16, false);
                itemRows[i].gameObject.name = "WorldCharacters.ItemRow" + i; itemRows[i].alignment = TextAnchor.MiddleLeft;
                var iconObject = new GameObject("WorldCharacters.ItemIcon" + i, typeof(RectTransform), typeof(Image));
                iconObject.layer = GUIManager.UILayer; iconObject.transform.SetParent(panel.transform, false);
                var iconRect = iconObject.GetComponent<RectTransform>(); iconRect.anchorMin = iconRect.anchorMax = center;
                iconRect.sizeDelta = new Vector2(38, 38); iconRect.anchoredPosition = new Vector2(-154, 199 - i * 52);
                itemIcons[i] = iconObject.GetComponent<Image>(); itemIcons[i].preserveAspect = true; itemIcons[i].raycastTarget = false;
            }
            previousRequest = ButtonAt("<", -508, -170, 40, 32, () => { requestPage--; Repaint(); });
            requestPages = Label("", -390, -170, 175, 32, 16, false);
            nextRequest = ButtonAt(">", -272, -170, 40, 32, () => { requestPage++; Repaint(); });
            previousItem = ButtonAt("<", -154, -167, 40, 32, () => { itemPage--; Repaint(); });
            itemPages = Label("", 170, -167, 530, 32, 16, false);
            nextItem = ButtonAt(">", 494, -167, 40, 32, () => { itemPage++; Repaint(); });
            identity = Label("", -390, -253, 310, 112, 15, false);
            hint = Label("", 170, -282, 690, 65, 16, false);
            approve = ButtonAt("", -58, -226, 218, 45, () => Decide(AdministrationDecision.Approve)); approve.gameObject.name = "WorldCharacters.Approve";
            reject = ButtonAt("", 171, -226, 218, 45, () => Decide(AdministrationDecision.Reject)); reject.gameObject.name = "WorldCharacters.Reject";
            fresh = ButtonAt("", 400, -226, 218, 45, () => Decide(AdministrationDecision.Fresh)); fresh.gameObject.name = "WorldCharacters.StartFresh";
            refresh = ButtonAt("", -390, -347, 310, 40, Refresh); refresh.gameObject.name = "WorldCharacters.Refresh";
            status = Label("", 170, -353, 690, 65, 16, false);
            diagnostics = Label("", 170, 15, 690, 430, 18, false); diagnostics.gameObject.name = "WorldCharacters.Diagnostics";
            Repaint(); Scale();
        }
        public void Tick()
        {
            if (overlay == null && !inputLease.Held) return;
            try
            {
                if (!IsVisible || !ValidContext()) { Hide(); return; }
                if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    if (confirmation != null) CancelConfirmation(); else Hide(); return;
                }
                if (Time.unscaledTime >= nextPaint) { nextPaint = Time.unscaledTime + .25f; Repaint(); Scale(); }
            }
            catch (Exception error) { Report(error); Hide(); }
        }
        private AdministrationRequest Selected()
        {
            if (view != null && view.World == world) foreach (var request in view.Requests)
                if (request != null && request.Id == selectedId) return request;
            return null;
        }
        private bool Actionable()
        {
            var selected = Selected();
            return Allowed() && World() == world && view != null && !view.Busy && String.IsNullOrEmpty(view.Error)
                && selected != null && selected.IsActionable && selected.World == world && view.SelectedId == selectedId && bindings.Decide != null;
        }
        private void SelectRequest(int slot)
        {
            int index = requestPage * PageSize + slot;
            if (!Allowed() || view == null || view.Busy || index < 0 || index >= filtered.Count || !filtered[index].IsActionable) return;
            selectedId = filtered[index].Id; itemPage = 0; CancelConfirmation(); notice = T("Читаю содержимое заявки…", "Reading request contents…");
            if (bindings.Inspect != null) bindings.Inspect(selectedId); Repaint();
        }
        private void Refresh()
        {
            if (!Allowed() || World() != world || view != null && view.Busy) return;
            CancelConfirmation(); notice = T("Обновляю заявки…", "Refreshing requests…");
            if (bindings.Refresh != null) bindings.Refresh(); Repaint();
        }
        private void Decide(AdministrationDecision decision)
        {
            Repaint(); if (statusMode || !Actionable() || confirmation != null) return;
            if (decision == AdministrationDecision.Fresh) { ConfirmFresh(); return; }
            var selected = Selected(); bindings.Decide(selected.Id, selected.Fingerprint, decision);
            notice = decision == AdministrationDecision.Approve
                ? T("Одобрение запрошено. После завершения игрок должен подключиться повторно.", "Approval requested. The player must reconnect after completion.")
                : T("Отклонение запрошено. Игрок сможет изменить персонажа и отправить новую заявку.", "Rejection requested. The player can change the character and submit another request.");
            Repaint();
        }
        private void ConfirmFresh()
        {
            var selected = Selected();
            confirmationId = selected.Id; confirmationFingerprint = selected.Fingerprint; confirmationWorld = world;
            confirmationGeneration = generation; confirmationDecision = AdministrationDecision.Fresh;
            var center = new Vector2(.5f, .5f);
            confirmation = new GameObject("WorldCharacters.FreshConfirmation", typeof(RectTransform), typeof(Image));
            confirmation.layer = GUIManager.UILayer; confirmation.transform.SetParent(overlay.transform, false);
            var rect = confirmation.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; confirmation.GetComponent<Image>().color = new Color(0, 0, 0, .7f);
            var wood = GUIManager.Instance.CreateWoodpanel(confirmation.transform, center, center, Vector2.zero, 740, 354, false);
            confirmationCaption = GUIManager.Instance.CreateText("", wood.transform, center, center, new Vector2(0, 38), GUIManager.Instance.AveriaSerif,
                21, GUIManager.Instance.ValheimBeige, true, Color.black, 680, 210, false).GetComponent<Text>();
            confirmationCaption.supportRichText = false; confirmationCaption.alignment = TextAnchor.MiddleCenter;
            confirmationCaption.resizeTextForBestFit = true; confirmationCaption.resizeTextMinSize = 15; confirmationCaption.resizeTextMaxSize = 21;
            confirmNo = ConfirmationButton(wood, -174, CancelConfirmation);
            confirmYes = ConfirmationButton(wood, 174, ApplyConfirmation);
            confirmation.transform.SetAsLastSibling(); PaintConfirmation(); Navigation();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(confirmNo.gameObject);
        }
        private Button ConfirmationButton(GameObject wood, float x, Action action)
        {
            int created = generation; GameObject dialog = confirmation;
            var button = GUIManager.Instance.CreateButton("", wood.transform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(x, -121), 320, 43).GetComponent<Button>();
            Style(button); button.onClick.AddListener(() =>
            {
                if (!ReferenceEquals(confirmation, dialog) || dialog == null || !dialog.activeInHierarchy) return;
                Invoke(created, action, true);
            });
            return button;
        }
        private void PaintConfirmation()
        {
            if (confirmation == null) return;
            var selected = Selected();
            confirmationCaption.text = T("Начать персонажа заново в этом мире?", "Start this character fresh in this world?") + "\n\n"
                + Safe(selected == null ? "" : selected.Name, 60) + "\n"
                + T("Будут использованы стартовые предметы и навыки. Весь прогресс из заявки, вещи и содержимое рюкзаков не будут импортированы; внешность может стать стандартной. Исходный .fch сохранится.",
                    "Default items and skills will be used. The proposed progress, inventory and backpack contents will not be imported; appearance may be reset. The original .fch remains intact.");
            confirmNo.GetComponentInChildren<Text>().text = T("Отмена", "Cancel");
            confirmYes.GetComponentInChildren<Text>().text = T("Начать заново", "Start fresh");
        }
        private void ApplyConfirmation()
        {
            Repaint();
            var selected = Selected();
            if (confirmation == null || confirmationGeneration != generation || confirmationWorld != world || World() != world
                || !Actionable() || selected.Id != confirmationId || selected.Fingerprint != confirmationFingerprint)
            { CancelConfirmation(); notice = T("Заявка изменилась. Выберите и проверьте её заново.", "The request changed. Select and inspect it again."); Repaint(); return; }
            string id = confirmationId, fingerprint = confirmationFingerprint; AdministrationDecision decision = confirmationDecision;
            CancelConfirmation(); bindings.Decide(id, fingerprint, decision);
            notice = T("Новый старт запрошен. После завершения игрок должен подключиться повторно.", "Fresh start requested. The player must reconnect after completion."); Repaint();
        }
        private void CancelConfirmation()
        {
            bool restoreFocus = false;
            if (confirmation != null)
            {
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                    && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(confirmation.transform)) restoreFocus = true;
                confirmation.SetActive(false); UnityEngine.Object.Destroy(confirmation);
            }
            confirmation = null; confirmationCaption = null; confirmNo = confirmYes = null;
            confirmationId = confirmationFingerprint = null;
            Navigation();
            if (restoreFocus && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(IsVisible && refresh != null ? refresh.gameObject : null);
        }
        private void Repaint()
        {
            if (panel == null) return;
            view = bindings.Snapshot == null ? null : bindings.Snapshot();
            if (Selected() == null && selectedId.Length != 0) { selectedId = ""; itemPage = 0; CancelConfirmation(); }
            string shortcut = bindings.ShortcutLabel == null ? "" : bindings.ShortcutLabel();
            title.text = T("World Characters — заявки персонажей", "World Characters — character requests");
            subtitle.text = T("Открыть / закрыть: ", "Open / close: ") + (String.IsNullOrEmpty(shortcut) ? T("Клавиша не назначена", "Unbound") : shortcut)
                + T("  •  Управление доступно только хосту этого мира.", "  •  Management is available only to this world's host.");
            requestHeading.text = T("Заявки на вход", "Join requests");
            var placeholder = search.placeholder as Text; if (placeholder != null) placeholder.text = T("Поиск: имя / Steam ID / заявка", "Search: name / Steam ID / request");
            filtered.Clear(); string query = (search.text ?? "").Trim();
            if (view != null && view.World == world) foreach (var request in view.Requests)
            {
                if (request == null || request.World != world && !(request.World == 0 && !String.IsNullOrEmpty(request.Error))) continue;
                if (query.Length == 0 || (request.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (request.Owner ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || request.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) filtered.Add(request);
            }
            bool busy = view != null && view.Busy;
            int requestCount = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize); requestPage = Math.Max(0, Math.Min(requestPage, requestCount - 1));
            requestPages.text = (requestPage + 1) + " / " + requestCount + "  •  " + filtered.Count;
            for (int i = 0; i < PageSize; i++)
            {
                int index = requestPage * PageSize + i; var request = index < filtered.Count ? filtered[index] : null;
                requestRows[i].interactable = Allowed() && !busy && request != null && request.IsActionable;
                requestRows[i].GetComponentInChildren<Text>().text = request == null ? "" : (request.Id == selectedId ? "› " : "")
                    + Safe(request.Name, 40) + "\n" + (request.IsActionable ? Safe(request.Owner, 24) + " • " + request.ItemsCount + T(" предметов", " items")
                        : request.Approved ? T("Уже одобрено", "Already approved") : T("Ошибка: ", "Error: ") + Safe(request.Error, 80));
            }
            previousRequest.interactable = !busy && requestPage > 0; nextRequest.interactable = !busy && requestPage + 1 < requestCount;
            search.interactable = !busy && confirmation == null;
            var selected = Selected();
            identity.text = selected == null ? (busy ? T("Читаю заявки…", "Reading requests…") : filtered.Count == 0
                ? T("Заявок нет. При первом входе гостя здесь появится его заявка.", "No requests. A guest's first connection creates a request here.")
                : T("Выберите заявку, чтобы проверить персонажа.", "Select a request to inspect the character."))
                : Safe(selected.Name, 45) + "\nSteam: " + Safe(selected.Owner, 24) + "\n" + T("Персонаж: ", "Character: ") + selected.Character
                    + "\n" + T("Заявка: ", "Request: ") + Safe(selected.Id, 16);
            bool inspected = selected != null && view != null && view.SelectedId == selectedId;
            int itemCount = inspected ? view.Items.Count : 0, pages = Math.Max(1, (itemCount + PageSize - 1) / PageSize);
            itemPage = Math.Max(0, Math.Min(itemPage, pages - 1));
            itemHeading.text = statusMode ? T("Состояние сохранений", "Save status") : selected == null ? T("Содержимое заявки", "Request contents") : Safe(selected.Name, 45);
            itemPages.text = (itemPage + 1) + " / " + pages + T("  •  Предметов: ", "  •  Items: ") + itemCount;
            for (int i = 0; i < PageSize; i++)
            {
                int index = itemPage * PageSize + i; var item = inspected && index < itemCount ? view.Items[index] : null;
                string name = item == null ? "" : bindings.ItemName == null ? item.Prefab.ToString() : bindings.ItemName(item.Prefab);
                itemRows[i].text = item == null ? "" : Safe(name, 66) + " × " + item.Count + T(" • качество ", " • quality ") + item.Quality
                    + "\n" + (String.IsNullOrEmpty(item.BackpackPath) ? T("Инвентарь", "Inventory") : T("В рюкзаке: ", "In backpack: ") + Safe(item.BackpackPath, 44))
                    + (item.Equipment ? T(" • экипировка", " • equipment") : "") + (String.IsNullOrEmpty(item.Details) ? "" : " • " + Safe(item.Details, 75));
                itemRows[i].gameObject.SetActive(!statusMode);
                itemIcons[i].sprite = item == null || bindings.ItemIcon == null ? null : bindings.ItemIcon(item.Prefab);
                itemIcons[i].enabled = !statusMode && itemIcons[i].sprite != null; itemIcons[i].gameObject.SetActive(!statusMode);
            }
            previousItem.gameObject.SetActive(!statusMode); nextItem.gameObject.SetActive(!statusMode); itemPages.gameObject.SetActive(!statusMode);
            previousItem.interactable = !busy && itemPage > 0; nextItem.interactable = !busy && itemPage + 1 < pages;
            stateTab.GetComponentInChildren<Text>().text = statusMode ? T("Содержимое", "Contents") : T("Состояние", "Status");
            diagnostics.gameObject.SetActive(statusMode);
            diagnostics.text = bindings.StatusText == null ? T("Показатели пока недоступны.", "Status is not available yet.") : bindings.StatusText();
            approve.GetComponentInChildren<Text>().text = T("Одобрить прогресс", "Approve progress");
            reject.GetComponentInChildren<Text>().text = T("Отклонить", "Reject");
            fresh.GetComponentInChildren<Text>().text = T("Начать заново…", "Start fresh…");
            foreach (var button in new[] { approve, reject, fresh }) { button.gameObject.SetActive(!statusMode); button.interactable = !statusMode && Actionable(); }
            hint.gameObject.SetActive(!statusMode);
            hint.text = T("Одобрение импортирует весь прогресс: предметы, рюкзаки, навыки и данные карты. После решения игрок должен подключиться повторно.",
                "Approval imports all progress: inventory, backpacks, skills and map data. The player must reconnect after a decision.");
            refresh.GetComponentInChildren<Text>().text = T("Обновить заявки", "Refresh requests"); refresh.interactable = Allowed() && !busy;
            string operationNotice = Safe(ServiceText(view == null ? "" : view.Notice), 180);
            string operationError = Safe(ServiceText(view == null ? "" : view.Error), 180);
            status.text = busy ? T("Операция выполняется…", "Operation in progress…") : operationNotice.Length != 0
                ? operationNotice + (operationError.Length == 0 ? "" : "\n" + operationError)
                : operationError.Length != 0 ? operationError : notice;
            PaintConfirmation(); Navigation();
        }
        private Text Label(string caption, float x, float y, float width, float height, int size, bool headingStyle)
        {
            var gui = GUIManager.Instance; var center = new Vector2(.5f, .5f);
            Text text = gui.CreateText(caption, panel.transform, center, center, new Vector2(x, y), headingStyle ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, headingStyle ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = size; return text;
        }
        private Button ButtonAt(string caption, float x, float y, float width, float height, Action action)
        {
            int created = generation;
            var button = GUIManager.Instance.CreateButton(caption, panel.transform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(x, y), width, height).GetComponent<Button>();
            Style(button); button.onClick.AddListener(() => Invoke(created, action, false)); controls.Add(button); return button;
        }
        private static void Style(Button button)
        {
            var text = button.GetComponentInChildren<Text>(); text.supportRichText = false;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = 18;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
        }
        private void Navigation()
        {
            var active = new List<Selectable>();
            if (confirmation != null)
            {
                if (confirmNo != null && confirmNo.IsInteractable()) active.Add(confirmNo);
                if (confirmYes != null && confirmYes.IsInteractable()) active.Add(confirmYes);
            }
            else foreach (var control in controls) if (control != null && control.gameObject.activeInHierarchy && control.IsInteractable()) active.Add(control);
            for (int i = 0; i < active.Count; i++) active[i].navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.Explicit,
                selectOnUp = active[(i + active.Count - 1) % active.Count], selectOnLeft = active[(i + active.Count - 1) % active.Count],
                selectOnDown = active[(i + 1) % active.Count], selectOnRight = active[(i + 1) % active.Count] };
        }
        private void Invoke(int created, Action action, bool isConfirmation)
        {
            if (created != generation || !IsVisible || confirmation != null && !isConfirmation) return;
            try { if (!ValidContext()) { Hide(); return; } action(); } catch (Exception error) { Report(error); Hide(); }
        }
        private void Scale()
        {
            if (panel == null || overlay == null) return;
            var rect = overlay.GetComponent<RectTransform>();
            float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 1160, rect.rect.height / 850));
            if (scale > .01f) panel.transform.localScale = Vector3.one * scale;
        }
        public void HandleInputReset() { inputLease.ForgetAfterGlobalReset(); Hide(); }
        public void Hide()
        {
            bool wasVisible = IsVisible; generation++;
            try
            {
                CancelConfirmation(); if (search != null) search.DeactivateInputField();
                if (overlay != null)
                {
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform)) EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            catch (Exception error) { Report(error); }
            finally
            {
                overlay = panel = null; player = null; network = null; world = 0; view = null; search = null;
                title = subtitle = requestHeading = itemHeading = requestPages = itemPages = identity = hint = status = diagnostics = null;
                previousRequest = nextRequest = previousItem = nextItem = approve = reject = fresh = refresh = stateTab = null;
                selectedId = notice = ""; statusMode = false; requestPage = itemPage = 0; filtered.Clear(); controls.Clear();
                Array.Clear(requestRows, 0, requestRows.Length); Array.Clear(itemRows, 0, itemRows.Length); Array.Clear(itemIcons, 0, itemIcons.Length);
                try { inputLease.Release(); } catch (Exception error) { Report(error); }
                if (wasVisible && bindings.OnClosed != null) { try { bindings.OnClosed(); } catch (Exception error) { Report(error); } }
            }
        }
    }
}
