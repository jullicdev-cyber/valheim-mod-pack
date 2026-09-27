using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.ExpeditionLoadouts
{
    public sealed class LoadoutWindow
    {
        private const int PageSize = 6;
        private readonly Plugin plugin;
        private GameObject overlay, panel;
        private Player owner;
        private ZNet network;
        private bool inputOwned, russian;
        private int generation, presetPage, itemPage;
        private float nextRefresh, deleteUntil;
        private LoadoutPreset draft;
        private InputField name;
        private Text status, pageLabel, presetsPageLabel;
        private readonly Button[] presets = new Button[PageSize];
        private readonly Text[] items = new Text[PageSize];
        private readonly Button[] minus = new Button[PageSize], plus = new Button[PageSize], remove = new Button[PageSize];
        private Button create, save, refill, delete, merge;
        private string notice = "", deleteId;
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        public LoadoutWindow(Plugin plugin) { this.plugin = plugin; }

        public void Show()
        {
            Hide(); owner = Player.m_localPlayer; network = ZNet.instance;
            if (!Valid() || GUIManager.CustomGUIFront == null) return;
            russian = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            presetPage = itemPage = 0; draft = null; notice = "";
            try
            {
                overlay = new GameObject("ExpeditionLoadouts.Modal", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(GUIManager.CustomGUIFront.transform, false); overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                overlay.GetComponent<Image>().color = new Color(0, 0, 0, 0.6f);
                Vector2 center = new Vector2(0.5f, 0.5f);
                panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 880, 720, false);
                Label(T("Походные комплекты", "Expedition loadouts"), 0, 278, 740, 45, 30, true);
                ButtonAt("X", 391, 282, 40, 36, Hide);
                Label(T("Наборы", "Presets"), -290, 222, 244, 30, 23, true);
                for (int i = 0; i < PageSize; i++)
                {
                    int slot = i;
                    presets[i] = ButtonAt("", -290, 174 - i * 49, 244, 43, () => Select(presetPage * PageSize + slot));
                }
                ButtonAt("<", -386, -129, 40, 30, () => { if (presetPage > 0) --presetPage; });
                presetsPageLabel = Label("", -290, -129, 135, 30, 17, false);
                ButtonAt(">", -194, -129, 40, 30, () => { if ((presetPage + 1) * PageSize < plugin.Store.Presets.Count) ++presetPage; });
                create = ButtonAt(T("Создать из инвентаря", "Create from inventory"), -290, -182, 244, 43, Create);
                delete = ButtonAt(T("Удалить набор", "Delete preset"), -290, -233, 244, 38, Delete);

                name = GUIManager.Instance.CreateInputField(panel.transform, center, center, new Vector2(90, 222),
                    InputField.ContentType.Standard, T("Название набора", "Preset name"), 20, 446, 38).GetComponent<InputField>();
                name.characterLimit = 60; name.textComponent.supportRichText = false;
                Label(T("Предмет — есть / нужно     (" + plugin.LargeStepLabel + ": шаг 10)", "Item — held / target     (" + plugin.LargeStepLabel + ": step 10)"), 135, 178, 550, 28, 17, false);
                for (int i = 0; i < PageSize; i++)
                {
                    int slot = i; float y = 134 - i * 42;
                    items[i] = Label("", 14, y, 295, 38, 18, false);
                    items[i].alignment = TextAnchor.MiddleLeft;
                    minus[i] = ButtonAt("−", 197, y, 46, 34, () => ChangeCount(slot, -1));
                    plus[i] = ButtonAt("+", 250, y, 46, 34, () => ChangeCount(slot, 1));
                    remove[i] = ButtonAt("×", 310, y, 46, 34, () => RemoveItem(slot));
                }
                ButtonAt("<", -90, -129, 42, 30, () => { if (itemPage > 0) --itemPage; });
                pageLabel = Label("", 100, -129, 294, 30, 17, false);
                ButtonAt(">", 308, -129, 42, 30, () => { if (draft != null && (itemPage + 1) * PageSize < draft.Items.Count) ++itemPage; });
                merge = ButtonAt(T("Добавить из инвентаря", "Add from inventory"), 13, -182, 278, 40, Merge);
                save = ButtonAt(T("Сохранить", "Save"), 265, -182, 170, 40, Persist);
                refill = ButtonAt(T("Пополнить из сундуков", "Restock from chests"), 83, -233, 419, 42, Restock);
                ButtonAt(T("Стоп", "Stop"), 338, -233, 70, 42, () => plugin.Service.Cancel());
                Label(T("Снимок обычных предметов: инвентарь, экипировка и быстрые слоты.\nПополняет основной инвентарь; не переодевает персонажа.",
                    "Captures ordinary items from inventory, equipment and quick slots.\nRestocks the main inventory; does not change worn equipment."), 0, -279, 795, 44, 16, false);
                status = Label("", 0, -330, 795, 42, 17, false);
                if (plugin.Store.Presets.Count > 0) SetDraft(plugin.Store.Presets[0]);
                Refresh(); inputOwned = true; GUIManager.BlockInput(true); overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(create.gameObject);
            }
            catch (Exception error) { plugin.Error(error); Hide(); }
        }

        private bool Valid()
        {
            return Plugin.ValidPlayer(owner) && ReferenceEquals(network, ZNet.instance) && plugin.Store != null
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible() && !global::Console.IsVisible()
                && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }
        public void Tick()
        {
            if (overlay == null && !inputOwned) return;
            try
            {
                if (!IsVisible || !Valid() || Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    Hide(); return;
                }
                if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.25f; Refresh(); }
            }
            catch (Exception error) { plugin.Error(error); Hide(); }
        }
        public void Hide()
        {
            ++generation;
            try { if (plugin.Service != null) plugin.Service.Cancel(); }
            catch (Exception error) { plugin.Error(error); }
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
                overlay = panel = null; owner = null; network = null; draft = null; name = null; deleteId = null;
                if (inputOwned) { inputOwned = false; GUIManager.BlockInput(false); }
            }
        }

        private void SetDraft(LoadoutPreset preset)
        { draft = preset.Copy(); name.text = draft.Name; itemPage = 0; deleteId = null; notice = ""; }
        private void Select(int index)
        {
            if (plugin.Service.IsBusy || index < 0 || index >= plugin.Store.Presets.Count) return;
            if (draft != null && !plugin.Store.ReadOnly) Persist(); SetDraft(plugin.Store.Presets[index]);
        }
        private void Create()
        {
            if (draft != null) Persist();
            var capture = ChestService.CaptureInventory(owner);
            var captured = capture.Items;
            if (captured.Count == 0) { notice = CaptureNotice(capture); return; }
            var preset = new LoadoutPreset { Id = Guid.NewGuid().ToString("N"), Name = T("Набор ", "Preset ") + (plugin.Store.Presets.Count + 1) };
            foreach (var target in captured) if (preset.Items.Count < PresetStore.MaxItems)
                preset.Items.Add(new PresetItem { Prefab = target.Prefab, Quality = target.Quality, Variant = target.Variant,
                    WorldLevel = target.WorldLevel, Count = Math.Min(PresetStore.MaxCount, target.Count) });
            plugin.Store.Save(preset); SetDraft(preset); presetPage = (plugin.Store.Presets.Count - 1) / PageSize;
            notice = CaptureNotice(capture);
        }
        private void Persist()
        {
            if (draft == null) return;
            string title = PresetStore.SafeName(name.text);
            if (title.Length == 0) { name.text = draft.Name; title = draft.Name; }
            draft.Name = title; plugin.Store.Save(draft); name.text = title;
            deleteId = null; notice = T("Набор сохранён.", "Preset saved.");
        }
        private void ChangeCount(int slot, int change)
        {
            int index = itemPage * PageSize + slot;
            if (draft == null || index >= draft.Items.Count) return;
            if (plugin.LargeStepHeld) change *= 10;
            draft.Items[index].Count = Mathf.Clamp(draft.Items[index].Count + change, 1, PresetStore.MaxCount); Persist();
        }
        private void RemoveItem(int slot)
        {
            int index = itemPage * PageSize + slot;
            if (draft == null || index >= draft.Items.Count) return;
            draft.Items.RemoveAt(index); Persist();
        }
        private void Merge()
        {
            if (draft == null) return;
            var capture = ChestService.CaptureInventory(owner);
            foreach (var target in capture.Items)
            {
                if (draft.Items.Exists(x => x.Prefab == target.Prefab && x.Quality == target.Quality
                    && x.Variant == target.Variant && x.WorldLevel == target.WorldLevel)) continue;
                if (draft.Items.Count >= PresetStore.MaxItems) { ++capture.SkippedLimit; continue; }
                draft.Items.Add(new PresetItem { Prefab = target.Prefab, Quality = target.Quality,
                    Variant = target.Variant, WorldLevel = target.WorldLevel, Count = Math.Min(PresetStore.MaxCount, target.Count) });
            }
            Persist();
            notice = CaptureNotice(capture);
        }
        private void Delete()
        {
            if (draft == null) return;
            if (deleteId != draft.Id || Time.unscaledTime > deleteUntil) { deleteId = draft.Id; deleteUntil = Time.unscaledTime + 5; notice = T("Нажмите «Удалить набор» ещё раз для подтверждения.", "Press Delete preset again to confirm."); return; }
            plugin.Store.Remove(draft.Id); draft = null; name.text = ""; deleteId = null;
            if (plugin.Store.Presets.Count > 0) SetDraft(plugin.Store.Presets[0]);
        }
        private void Restock()
        {
            if (draft == null || draft.Items.Count == 0 || plugin.Service.IsBusy) return;
            Persist(); var targets = new List<SupplyTarget>();
            foreach (var item in draft.Items) targets.Add(new SupplyTarget { Prefab = item.Prefab, Quality = item.Quality,
                Variant = item.Variant, WorldLevel = item.WorldLevel, Count = item.Count });
            plugin.Service.Begin(owner, targets, plugin.Radius); notice = "";
        }
        private void Refresh()
        {
            bool busy = plugin.Service.IsBusy, writable = !plugin.Store.ReadOnly;
            presetPage = Math.Min(presetPage, Math.Max(0, (plugin.Store.Presets.Count - 1) / PageSize));
            itemPage = Math.Min(itemPage, Math.Max(0, ((draft == null ? 0 : draft.Items.Count) - 1) / PageSize));
            for (int i = 0; i < PageSize; i++)
            {
                int index = presetPage * PageSize + i;
                presets[i].interactable = !busy && index < plugin.Store.Presets.Count;
                SetButton(presets[i], index < plugin.Store.Presets.Count ? ((draft != null && draft.Id == plugin.Store.Presets[index].Id) ? "• " : "") + plugin.Store.Presets[index].Name : "—");
                int itemIndex = itemPage * PageSize + i;
                bool has = draft != null && itemIndex < draft.Items.Count;
                if (has)
                {
                    var item = draft.Items[itemIndex];
                    int owned = ChestService.CountOwned(owner, new SupplyTarget { Prefab = item.Prefab, Quality = item.Quality,
                        Variant = item.Variant, WorldLevel = item.WorldLevel, Count = item.Count });
                    items[i].text = ItemName(item) + " — " + owned + " / " + item.Count;
                }
                else items[i].text = "";
                minus[i].interactable = plus[i].interactable = remove[i].interactable = has && !busy && writable;
            }
            create.interactable = !busy && writable && plugin.Store.Presets.Count < PresetStore.MaxPresets;
            save.interactable = delete.interactable = merge.interactable = !busy && writable && draft != null;
            refill.interactable = !busy && writable && draft != null && draft.Items.Count > 0;
            name.interactable = !busy && writable && draft != null;
            pageLabel.text = (itemPage + 1) + " / " + Math.Max(1, ((draft == null ? 0 : draft.Items.Count) + PageSize - 1) / PageSize);
            presetsPageLabel.text = (presetPage + 1) + " / " + Math.Max(1, (plugin.Store.Presets.Count + PageSize - 1) / PageSize);
            status.text = plugin.Store.ReadOnly ? T("Файл наборов не прочитан и сохранён без изменений. Подробности в журнале.", "Preset file could not be read and was preserved. See the log.")
                : busy || notice.Length == 0 ? plugin.Service.Status : notice;
            if (String.IsNullOrEmpty(status.text)) status.text = T("Сундуки в радиусе ", "Chests within ") + plugin.Radius.ToString("0", CultureInfo.InvariantCulture) + T(" м. Закрытие окна останавливает пополнение.", " m. Closing this window stops restocking.");
            var rect = overlay.GetComponent<RectTransform>();
            float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 900, rect.rect.height / 740));
            if (scale > 0.01f) panel.transform.localScale = Vector3.one * scale;
        }
        private static string ItemName(PresetItem item)
        {
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item.Prefab) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            string label = drop != null && Localization.instance != null ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : item.Prefab;
            bool ru = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            return PresetStore.SafeName(label) + (item.Quality > 1 ? " (" + item.Quality + ")" : "")
                + (item.Variant > 0 ? (ru ? " · вариант " : " · variant ") + item.Variant : "")
                + (item.WorldLevel > 0 ? (ru ? " · мир " : " · world ") + item.WorldLevel : "");
        }
        private string CaptureNotice(InventoryCapture capture)
        {
            string message = T("Снимок: ", "Captured: ") + capture.Items.Count + T(" видов из ", " types from ")
                + capture.IncludedSlots + T(" ячеек.", " slots.");
            if (capture.SkippedCustomData > 0) message += T(" Особые данные (в т. ч. Epic Loot) пропущены: ", "Custom-data items (including Epic Loot) skipped: ") + capture.SkippedCustomData + ".";
            if (capture.SkippedUnsupported > 0) message += T(" Неподдерживаемые: ", "Unsupported: ") + capture.SkippedUnsupported + ".";
            if (capture.SkippedInvalid > 0) message += T(" Некорректные ячейки/предметы: ", "Invalid items/cells: ") + capture.SkippedInvalid + ".";
            if (capture.SkippedLimit > 0) message += T(" Ограничены лимитом набора: ", "Limited by preset capacity: ") + capture.SkippedLimit + ".";
            return message;
        }
        private string T(string ru, string en) { return russian ? ru : en; }
        private Text Label(string value, float x, float y, float width, float height, int size, bool heading)
        {
            var gui = GUIManager.Instance; var center = new Vector2(0.5f, 0.5f);
            var text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y), heading ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, heading ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = size; return text;
        }
        private Button ButtonAt(string title, float x, float y, float width, float height, Action action)
        {
            int created = generation; var center = new Vector2(0.5f, 0.5f);
            var button = GUIManager.Instance.CreateButton(title, panel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            var text = button.GetComponentInChildren<Text>(); text.supportRichText = false;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 13; text.resizeTextMaxSize = 19;
            var sfx = button.GetComponent<ButtonSfx>(); if (sfx != null) sfx.m_selectSfxPrefab = null;
            button.onClick.AddListener(() =>
            {
                if (created != generation || !IsVisible) return;
                try { if (!Valid()) { Hide(); return; } action(); if (IsVisible) Refresh(); }
                catch (Exception error) { plugin.Error(error); notice = T("Действие не выполнено. Подробности в журнале.", "Action failed. See the log."); }
            });
            return button;
        }
        private static void SetButton(Button button, string text) { button.GetComponentInChildren<Text>().text = text; }
    }
}
