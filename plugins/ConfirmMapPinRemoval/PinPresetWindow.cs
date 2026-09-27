using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    // UI snapshots only: persistence, world ownership and pin placement belong to the controller.
    public sealed class PinPresetEntryView
    {
        public string Id, Name, SearchText;
        public int IconType;
        public Sprite Icon;
        public bool BuiltIn;
    }

    public sealed class PinPresetIconOption
    {
        public int Type;
        public Sprite Icon;
        public string Label;
    }

    public sealed class PinPresetEdit
    {
        public string Id, Name;
        public int IconType;
        public bool NameChanged;
    }

    public sealed class PinPresetWindow
    {
        private enum View { Picker, Editor, Rename, Confirm }
        private const int PageSize = 8, IconPageSize = 12, NameLimit = 96;
        private static readonly Vector2 Center = new Vector2(.5f, .5f);
        private readonly Func<string, string> localize;
        private readonly Action<Exception> report;
        private readonly List<PinPresetEntryView> entries = new List<PinPresetEntryView>();
        private readonly List<PinPresetIconOption> icons = new List<PinPresetIconOption>();
        private readonly List<PinPresetEntryView> filtered = new List<PinPresetEntryView>();
        private GameObject overlay, panel, rows, iconRows;
        private InputField search, name;
        private Text pageText, statusText;
        private Button previous, next, place, choose, edit, delete;
        private bool ownsInputBlock;
        private int generation, rowsGeneration, page, iconPage, openedFrame;
        private View view;
        private string query = "", selectedId, placeLabel, status = "";
        private string draftId, draftName, initialName;
        private int draftIcon;
        private string confirmTitle, confirmBody, confirmLabel;
        private Action<string> placeHere, chooseOnMap, deletePreset, renameSave;
        private Action<PinPresetEdit> savePreset;
        private Action close, cancel, confirm;

        public PinPresetWindow(Func<string, string> localize, Action<Exception> report)
        {
            if (localize == null) throw new ArgumentNullException("localize");
            this.localize = localize; this.report = report;
        }

        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        public bool IsEditing { get { return IsVisible && view != View.Picker; } }
        public string Query
        {
            get { return query; }
            set
            {
                query = value ?? ""; page = 0;
                if (search != null && search.text != query) search.text = query;
                else if (IsVisible && view == View.Picker) RenderRows();
            }
        }

        public void Show(IList<PinPresetEntryView> presets, IList<PinPresetIconOption> iconOptions,
            Action<string> placeHere, Action<string> chooseOnMap, Action<PinPresetEdit> save,
            Action<string> delete, Action close, string placeLabel)
        {
            Hide();
            this.placeHere = placeHere; this.chooseOnMap = chooseOnMap; savePreset = save;
            deletePreset = delete; this.close = close; this.placeLabel = placeLabel;
            CopyEntries(presets); icons.Clear();
            if (iconOptions != null) foreach (var option in iconOptions)
            {
                if (option == null) continue;
                icons.Add(new PinPresetIconOption { Type = option.Type, Icon = option.Icon, Label = option.Label });
            }
            status = ""; view = View.Picker; page = 0;
            Open();
        }

        public void ShowRename(string initial, Action<string> save, Action cancel)
        {
            Hide(); view = View.Rename; draftName = initial ?? ""; initialName = draftName;
            renameSave = save; this.cancel = cancel; status = ""; Open();
        }

        public void ShowConfirm(string title, string body, string confirmText, Action onConfirm, Action onCancel)
        {
            Hide(); view = View.Confirm; confirmTitle = title ?? ""; confirmBody = body ?? "";
            confirmLabel = confirmText ?? ""; confirm = onConfirm; cancel = onCancel; status = ""; Open();
        }

        // A successful edit returns here; failed saves leave the draft intact via SetStatus.
        public void RefreshEntries(IList<PinPresetEntryView> presets, bool returnToPicker = true)
        {
            CopyEntries(presets);
            if (!IsVisible) return;
            if (returnToPicker) status = "";
            if (returnToPicker && (view == View.Editor || view == View.Confirm)) { view = View.Picker; RenderView(true); }
            else if (view == View.Picker) RenderRows();
        }

        public void RefreshLabels(string newPlaceLabel)
        {
            placeLabel = newPlaceLabel;
            if (!IsVisible) return;
            CaptureDraft(); RenderView(true);
        }

        public void SetStatus(string message)
        {
            status = message ?? "";
            if (statusText != null) statusText.text = status;
        }

        public void Tick()
        {
            if (overlay == null)
            {
                // Unity can destroy the canvas during a scene change without calling our Hide.
                if (ownsInputBlock) Hide();
                return;
            }
            if (!overlay.activeInHierarchy) { Hide(); return; }
            FitPanel();
            if (Time.frameCount == openedFrame) return;
            bool controllerBack = ZInput.GetButtonDown("JoyButtonB");
            if (!Input.GetKeyDown(KeyCode.Escape) && !controllerBack) return;
            if (controllerBack) ZInput.ResetButtonStatus("JoyButtonB");
            Safe(delegate
            {
                if (view == View.Editor) BackToPicker();
                else if (view == View.Rename || view == View.Confirm) { var action = cancel; Hide(); if (action != null) action(); }
                else ClosePicker();
            });
        }

        private void CopyEntries(IList<PinPresetEntryView> source)
        {
            entries.Clear();
            if (source == null) return;
            foreach (var entry in source)
            {
                if (entry == null || String.IsNullOrEmpty(entry.Id)) continue;
                entries.Add(new PinPresetEntryView { Id = entry.Id, Name = entry.Name ?? "", SearchText = entry.SearchText ?? "",
                    IconType = entry.IconType, Icon = entry.Icon, BuiltIn = entry.BuiltIn });
            }
        }

        private string T(string key) { return localize(key) ?? key; }

        private void Open()
        {
            var front = GUIManager.CustomGUIFront;
            if (front == null) throw new InvalidOperationException("Jotunn canvas is not ready");
            try
            {
                overlay = new GameObject("ConfirmMapPinRemoval.Presets", typeof(RectTransform), typeof(Image));
                overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(front.transform, false);
                var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>(); dim.color = new Color(0, 0, 0, .6f); dim.raycastTarget = true;
                RenderView(true);
                ownsInputBlock = true; GUIManager.BlockInput(true);
                overlay.transform.SetAsLastSibling(); openedFrame = Time.frameCount;
            }
            catch { Hide(); throw; }
        }

        private void RenderView(bool focus)
        {
            ++generation; ++rowsGeneration;
            if (panel != null) { panel.SetActive(false); UnityEngine.Object.Destroy(panel); }
            search = name = null; rows = iconRows = null; statusText = pageText = null;
            place = choose = edit = delete = previous = next = null;
            bool picker = view == View.Picker;
            float width = picker ? 880 : 700, height = picker ? 700 : view == View.Editor ? 490 : 310;
            panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, Center, Center, Vector2.zero, width, height, false);
            panel.name = "ConfirmMapPinRemoval.Preset" + view;
            if (picker) RenderPicker();
            else if (view == View.Editor) RenderEditor();
            else if (view == View.Rename) RenderRename();
            else RenderConfirm();
            FitPanel();
            if (focus) Focus(picker ? search : name);
        }

        private void RenderPicker()
        {
            Label(panel.transform, T("presets_title"), 0, 298, 780, 46, 30, true);
            search = InputAt(panel.transform, query, T("presets_search"), 0, 242, 760, 40, 128);
            int epoch = generation;
            search.onValueChanged.AddListener(delegate(string value)
            {
                if (!Current(epoch) || view != View.Picker) return;
                query = value ?? ""; page = 0; SetStatus(""); Safe(RenderRows);
            });
            rows = Container(panel.transform, "PresetRows", 0, 42, 770, 332);
            previous = ButtonAt(panel.transform, "‹", -340, -151, 68, 36, delegate { --page; RenderRows(); });
            next = ButtonAt(panel.transform, "›", 340, -151, 68, 36, delegate { ++page; RenderRows(); });
            pageText = Label(panel.transform, "", 0, -151, 560, 36, 18, false);
            ButtonAt(panel.transform, T("presets_add"), -259, -198, 240, 38, delegate { BeginEdit(null); });
            edit = ButtonAt(panel.transform, T("presets_edit"), 0, -198, 240, 38, delegate { BeginEdit(Selected()); });
            delete = ButtonAt(panel.transform, T("presets_delete"), 259, -198, 240, 38, delegate
            {
                var chosen = Selected(); if (chosen != null && deletePreset != null) deletePreset(chosen.Id);
            });
            place = ButtonAt(panel.transform, String.IsNullOrEmpty(placeLabel) ? T("presets_place_here") : placeLabel,
                -197, -247, 365, 42, delegate { var chosen = Selected(); if (chosen != null && placeHere != null) placeHere(chosen.Id); });
            choose = ButtonAt(panel.transform, T("presets_choose_point"), 197, -247, 365, 42,
                delegate { var chosen = Selected(); if (chosen != null && chooseOnMap != null) chooseOnMap(chosen.Id); });
            statusText = Label(panel.transform, status, 0, -285, 770, 30, 16, false);
            ButtonAt(panel.transform, T("presets_close"), 0, -322, 220, 38, ClosePicker);
            RenderRows();
        }

        private void RenderRows()
        {
            if (!IsVisible || rows == null || view != View.Picker) return;
            ++rowsGeneration; int rowEpoch = rowsGeneration;
            ClearChildren(rows.transform); filtered.Clear();
            string[] terms = query.Trim().Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in entries)
            {
                string haystack = entry.Name + " " + entry.SearchText; bool matches = true;
                foreach (string term in terms) if (haystack.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) < 0) { matches = false; break; }
                if (matches) filtered.Add(entry);
            }
            if (!filtered.Exists(delegate(PinPresetEntryView item) { return item.Id == selectedId; })) selectedId = null;
            int pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            previous.interactable = page > 0; next.interactable = page + 1 < pages;
            pageText.text = String.Format(CultureInfo.CurrentCulture, T("presets_page"), page + 1, pages);
            if (filtered.Count == 0) Label(rows.transform, T("presets_empty"), 0, 0, 700, 140, 23, false);
            for (int slot = 0; slot < PageSize && page * PageSize + slot < filtered.Count; ++slot)
            {
                PinPresetEntryView entry = filtered[page * PageSize + slot];
                float x = slot % 2 == 0 ? -197 : 197, y = 126 - (slot / 2) * 83;
                var button = ButtonAt(rows.transform, "", x, y, 365, 72, delegate
                {
                    if (rowEpoch != rowsGeneration) return;
                    selectedId = entry.Id; SetStatus(""); RenderRows();
                });
                if (entry.Id == selectedId)
                {
                    var colors = button.colors; colors.normalColor = new Color(1f, .8f, .48f, 1f); button.colors = colors;
                }
                Icon(button.transform, entry.Icon, -145, 0, 44);
                var title = Label(button.transform, entry.Name, 25, 11, 265, 30, 21, true); title.alignment = TextAnchor.MiddleLeft;
                var subtitle = Label(button.transform, T(entry.BuiltIn ? "presets_builtin" : "presets_custom"), 25, -17, 265, 22, 15, false);
                subtitle.alignment = TextAnchor.MiddleLeft;
            }
            bool selected = Selected() != null;
            place.interactable = selected && placeHere != null; choose.interactable = selected && chooseOnMap != null;
            edit.interactable = selected && savePreset != null; delete.interactable = selected && deletePreset != null;
            if (statusText != null) statusText.text = !selected && String.IsNullOrEmpty(status) ? T("presets_selection_hint") : status;
        }

        private PinPresetEntryView Selected()
        {
            return entries.Find(delegate(PinPresetEntryView entry) { return entry.Id == selectedId; });
        }

        private void BeginEdit(PinPresetEntryView entry)
        {
            if (savePreset == null) return;
            draftId = entry == null ? null : entry.Id; initialName = entry == null ? "" : entry.Name;
            draftName = initialName; draftIcon = entry == null ? (icons.Count == 0 ? -1 : icons[0].Type) : entry.IconType;
            iconPage = 0;
            for (int i = 0; i < icons.Count; ++i) if (icons[i].Type == draftIcon) iconPage = i / IconPageSize;
            status = ""; view = View.Editor; RenderView(true);
        }

        private void RenderEditor()
        {
            Label(panel.transform, T(String.IsNullOrEmpty(draftId) ? "presets_new_title" : "presets_edit_title"), 0, 194, 600, 45, 29, true);
            Label(panel.transform, T("presets_name"), 0, 148, 600, 28, 18, false);
            name = InputAt(panel.transform, draftName, T("presets_name"), 0, 111, 592, 42, NameLimit);
            Label(panel.transform, T("presets_icon"), 0, 66, 600, 30, 19, false);
            iconRows = Container(panel.transform, "NativePinIcons", 0, -14, 610, 126);
            RenderIcons();
            if (icons.Count > IconPageSize)
            {
                ButtonAt(panel.transform, "‹", -240, -107, 60, 32, delegate { --iconPage; RenderIcons(); });
                ButtonAt(panel.transform, "›", 240, -107, 60, 32, delegate { ++iconPage; RenderIcons(); });
            }
            statusText = Label(panel.transform, status, 0, -151, 600, 45, 17, false);
            ButtonAt(panel.transform, T("presets_cancel"), -155, -204, 260, 42, BackToPicker);
            ButtonAt(panel.transform, T("presets_save"), 155, -204, 260, 42, SaveDraft);
        }

        private void RenderIcons()
        {
            ClearChildren(iconRows.transform); int pages = Math.Max(1, (icons.Count + IconPageSize - 1) / IconPageSize);
            iconPage = Math.Max(0, Math.Min(iconPage, pages - 1));
            int iconEpoch = ++rowsGeneration;
            for (int slot = 0; slot < IconPageSize && iconPage * IconPageSize + slot < icons.Count; ++slot)
            {
                var option = icons[iconPage * IconPageSize + slot];
                float x = -250 + (slot % 6) * 100, y = 34 - (slot / 6) * 74;
                var button = ButtonAt(iconRows.transform, "", x, y, 88, 66, delegate
                {
                    if (iconEpoch != rowsGeneration) return;
                    draftIcon = option.Type; SetStatus(""); RenderIcons();
                });
                if (option.Type == draftIcon)
                {
                    var colors = button.colors; colors.normalColor = new Color(1f, .8f, .48f, 1f); button.colors = colors;
                }
                Icon(button.transform, option.Icon, 0, 6, 34);
                Label(button.transform, option.Label ?? option.Type.ToString(CultureInfo.InvariantCulture), 0, -21, 80, 20, 12, false);
            }
        }

        private void SaveDraft()
        {
            CaptureDraft(); string trimmed = (draftName ?? "").Trim();
            if (trimmed.Length == 0) { SetStatus(T("presets_name_required")); Focus(name); return; }
            if (!icons.Exists(delegate(PinPresetIconOption icon) { return icon.Type == draftIcon; }))
            { SetStatus(T("presets_icon_required")); return; }
            if (savePreset != null) savePreset(new PinPresetEdit { Id = draftId, Name = trimmed, IconType = draftIcon,
                NameChanged = String.IsNullOrEmpty(draftId) || !String.Equals(draftName, initialName, StringComparison.Ordinal) });
        }

        private void RenderRename()
        {
            Label(panel.transform, T("presets_rename_title"), 0, 108, 590, 46, 28, true);
            name = InputAt(panel.transform, draftName, T("presets_name"), 0, 43, 592, 44, NameLimit);
            statusText = Label(panel.transform, status, 0, -18, 600, 46, 17, false);
            ButtonAt(panel.transform, T("presets_cancel"), -155, -99, 260, 42, CancelModal);
            ButtonAt(panel.transform, T("presets_save"), 155, -99, 260, 42, delegate
            {
                CaptureDraft(); string trimmed = (draftName ?? "").Trim();
                if (trimmed.Length == 0) { SetStatus(T("presets_name_required")); Focus(name); return; }
                if (renameSave != null) renameSave(trimmed);
            });
        }

        private void RenderConfirm()
        {
            Label(panel.transform, confirmTitle, 0, 103, 600, 46, 28, true);
            Label(panel.transform, confirmBody, 0, 16, 600, 118, 22, false);
            statusText = Label(panel.transform, status, 0, -62, 610, 29, 16, false);
            var cancelButton = ButtonAt(panel.transform, T("presets_cancel"), -155, -109, 260, 42, CancelModal);
            ButtonAt(panel.transform, confirmLabel, 155, -109, 260, 42, delegate { if (confirm != null) confirm(); });
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(cancelButton.gameObject);
        }

        private void BackToPicker() { view = View.Picker; status = ""; RenderView(true); }
        private void CaptureDraft() { if (name != null) draftName = name.text; }
        private void ClosePicker() { var action = close; Hide(); if (action != null) action(); }
        private void CancelModal() { var action = cancel; Hide(); if (action != null) action(); }
        private bool Current(int epoch) { return generation == epoch && IsVisible; }
        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                if (report != null) report(error);
                if (IsVisible) SetStatus(error.Message);
            }
        }

        private Button ButtonAt(Transform parent, string label, float x, float y, float width, float height, Action action)
        {
            var button = GUIManager.Instance.CreateButton(label, parent, Center, Center, new Vector2(x, y), width, height).GetComponent<Button>();
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            foreach (var text in button.GetComponentsInChildren<Text>(true)) { text.supportRichText = false; text.raycastTarget = false; }
            int epoch = generation;
            button.onClick.AddListener(delegate { if (Current(epoch) && button != null && button.interactable) Safe(action); });
            return button;
        }

        private static Text Label(Transform parent, string value, float x, float y, float width, float height, int size, bool title)
        {
            var gui = GUIManager.Instance;
            var text = gui.CreateText(value ?? "", parent, Center, Center, new Vector2(x, y), title ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, title ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate; return text;
        }

        private static InputField InputAt(Transform parent, string value, string placeholder, float x, float y, float width, float height, int limit)
        {
            var field = GUIManager.Instance.CreateInputField(parent, Center, Center, new Vector2(x, y), InputField.ContentType.Standard,
                placeholder, 22, width, height).GetComponent<InputField>();
            field.characterLimit = limit; field.lineType = InputField.LineType.SingleLine;
            if (field.textComponent != null) field.textComponent.supportRichText = false;
            var placeholderText = field.placeholder as Text; if (placeholderText != null) placeholderText.supportRichText = false;
            field.text = value ?? ""; return field;
        }

        private static GameObject Container(Transform parent, string name, float x, float y, float width, float height)
        {
            var result = new GameObject(name, typeof(RectTransform)); result.layer = GUIManager.UILayer;
            result.transform.SetParent(parent, false); var rect = result.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Center; rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height);
            return result;
        }

        private static void Icon(Transform parent, Sprite sprite, float x, float y, float size)
        {
            if (sprite == null) return;
            var holder = Container(parent, "PinIcon", x, y, size, size);
            var image = holder.AddComponent<Image>(); image.sprite = sprite; image.preserveAspect = true; image.raycastTarget = false;
        }

        private static void ClearChildren(Transform parent)
        {
            foreach (Transform child in parent) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); }
        }

        private static void Focus(InputField field)
        {
            if (field == null) return;
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(field.gameObject);
            field.ActivateInputField();
        }

        private void FitPanel()
        {
            if (panel == null || overlay == null) return;
            var available = overlay.GetComponent<RectTransform>().rect; var size = panel.GetComponent<RectTransform>().rect;
            float scale = Math.Min(1f, Math.Min(available.width / (size.width + 32), available.height / (size.height + 32)));
            if (scale > 0) panel.transform.localScale = new Vector3(scale, scale, 1);
        }

        public void Hide()
        {
            ++generation; ++rowsGeneration;
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
                overlay = panel = rows = iconRows = null; search = name = null; statusText = pageText = null;
                place = choose = edit = delete = previous = next = null;
                if (ownsInputBlock) { ownsInputBlock = false; GUIManager.BlockInput(false); }
            }
        }
    }
}
