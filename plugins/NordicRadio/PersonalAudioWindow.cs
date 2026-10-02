using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.NordicRadio
{
    // This window changes only local audio settings. It has no target, playlist,
    // command or networking dependency, so it also works when no horn is nearby.
    public sealed class PersonalAudioWindow
    {
        private readonly Plugin plugin;
        private readonly List<Selectable> controls = new List<Selectable>();
        private GameObject overlay, panel;
        private Player owner;
        private ZNet network;
        private Text title, hint, percent, explanation;
        private Slider slider;
        private Button mute;
        private bool ownsInput, repainting;
        private int generation;
        private float nextRefresh;
        public PersonalAudioWindow(Plugin plugin)
        { if (plugin == null) throw new ArgumentNullException("plugin"); this.plugin = plugin; }
        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }
        private string T(string ru, string en)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? ru : en; }
        public void Show()
        {
            Hide(); owner = Player.m_localPlayer; network = ZNet.instance;
            if (!Valid() || GUIManager.CustomGUIFront == null) return;
            try
            {
                BuildVisuals(); ownsInput = true; GUIManager.BlockInput(true); overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(slider.gameObject);
            }
            catch (Exception error) { Hide(); plugin.Report(error); }
        }
        private void BuildVisuals()
        {
            var center = new Vector2(.5f, .5f);
            overlay = new GameObject("NordicRadio.PersonalAudio.Modal", typeof(RectTransform), typeof(Image));
            overlay.layer = GUIManager.UILayer; overlay.transform.SetParent(GUIManager.CustomGUIFront.transform, false);
            var rect = overlay.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; overlay.GetComponent<Image>().color = new Color(0, 0, 0, .58f);
            panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 670, 452, false);
            panel.name = "NordicRadio.PersonalAudio.WoodPanel";
            title = Label("", 0, 162, 555, 42, 26, true); ButtonAt("X", 284, 168, 40, 34, Hide);
            hint = Label("", 0, 115, 580, 39, 16, false);
            percent = Label("", 0, 66, 530, 38, 24, true);
            BuildSlider();
            ButtonAt("−", -205, -34, 102, 34, () => ChangeVolume(-.05f));
            Label(T("Шаг 5%", "5% step"), 0, -34, 232, 34, 17, false);
            ButtonAt("+", 205, -34, 102, 34, () => ChangeVolume(.05f));
            mute = ButtonAt("", 0, -87, 512, 42, () => { plugin.PersonalMuted = !plugin.PersonalMuted; Refresh(); });
            explanation = Label("", 0, -153, 584, 65, 16, false);
            Refresh(); Scale();
        }
        private void BuildSlider()
        {
            var holder = new GameObject("NordicRadio.PersonalVolumeSlider", typeof(RectTransform), typeof(Slider));
            holder.layer = GUIManager.UILayer; holder.transform.SetParent(panel.transform, false);
            var rect = holder.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, 15); rect.sizeDelta = new Vector2(516, 30);
            var track = ImageRect("Track", holder.transform, new Color(.22f, .18f, .13f, 1), new Vector2(0, .36f), new Vector2(1, .64f));
            track.raycastTarget = true;
            var fill = ImageRect("Fill", holder.transform, GUIManager.Instance.ValheimOrange, new Vector2(0, .36f), new Vector2(1, .64f)); fill.raycastTarget = false;
            var handle = ImageRect("Handle", holder.transform, GUIManager.Instance.ValheimBeige, new Vector2(0, .5f), new Vector2(0, .5f));
            handle.rectTransform.sizeDelta = new Vector2(20, 29); handle.raycastTarget = true;
            slider = holder.GetComponent<Slider>(); slider.minValue = 0; slider.maxValue = 100; slider.wholeNumbers = true;
            slider.direction = Slider.Direction.LeftToRight; slider.fillRect = fill.rectTransform; slider.handleRect = handle.rectTransform; slider.targetGraphic = handle;
            int created = generation;
            slider.onValueChanged.AddListener(value =>
            {
                if (repainting || created != generation || !IsVisible) return;
                try { if (!Valid()) { Hide(); return; } plugin.PersonalVolume = value / 100f; Refresh(); }
                catch (Exception error) { Hide(); plugin.Report(error); }
            });
            controls.Add(slider);
        }
        private static Image ImageRect(string name, Transform parent, Color color, Vector2 anchorMin, Vector2 anchorMax)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image)); obj.layer = GUIManager.UILayer; obj.transform.SetParent(parent, false);
            var rect = obj.GetComponent<RectTransform>(); rect.anchorMin = anchorMin; rect.anchorMax = anchorMax; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = obj.GetComponent<Image>(); image.color = color; return image;
        }
        private void ChangeVolume(float step)
        { plugin.PersonalVolume = Mathf.Clamp01(plugin.PersonalVolumeSetting + step); Refresh(); }
        private void Refresh()
        {
            if (title == null) return;
            repainting = true;
            try
            {
                title.text = T("Личная громкость радио", "Personal radio volume");
                string shortcut = plugin.PersonalAudioShortcutLabel;
                hint.text = T("Открыть / закрыть: ", "Open / close: ") + (String.IsNullOrEmpty(shortcut) ? T("Клавиша не назначена", "Unbound") : shortcut);
                int value = Mathf.RoundToInt(plugin.PersonalVolumeSetting * 100);
                percent.text = value.ToString(CultureInfo.InvariantCulture) + "%" + (plugin.PersonalMuted ? T(" — звук выключен", " — muted") : "");
                slider.value = value;
                mute.GetComponentInChildren<Text>().text = plugin.PersonalMuted ? T("Включить звук только мне", "Unmute only for me") : T("Выключить звук только мне", "Mute only for me");
                explanation.text = T("Настройка сохраняется на вашем ПК и действует на все радио.\nДругие игроки продолжают слышать музыку. При включении звука прежняя громкость сохраняется.",
                    "Saved on this PC and applied to every radio.\nOther players keep hearing the music. Unmuting preserves your previous volume.");
                for (int i = 0; i < controls.Count; i++) controls[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnUp = controls[(i + controls.Count - 1) % controls.Count], selectOnLeft = controls[(i + controls.Count - 1) % controls.Count],
                    selectOnDown = controls[(i + 1) % controls.Count], selectOnRight = controls[(i + 1) % controls.Count] };
            }
            finally { repainting = false; }
        }
        private bool Valid()
        {
            return owner != null && ReferenceEquals(owner, Player.m_localPlayer) && network != null && ReferenceEquals(network, ZNet.instance)
                && !owner.IsDead() && !owner.IsTeleporting() && !owner.IsSleeping() && !owner.InCutscene()
                && !ZInput.s_IsRebindActive && !Menu.IsVisible() && !InventoryGui.IsVisible() && !UnifiedPopup.IsVisible()
                && !StoreGui.IsVisible() && !Hud.IsPieceSelectionVisible() && !PlayerCustomizaton.IsBarberGuiVisible()
                && !global::Console.IsVisible() && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy)
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }
        public void Tick()
        {
            if (overlay == null && !ownsInput) return;
            try
            {
                if (!IsVisible || !Valid() || Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
                { if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB"); Hide(); return; }
                if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .2f; Refresh(); Scale(); }
            }
            catch (Exception error) { Hide(); plugin.Report(error); }
        }
        private Text Label(string value, float x, float y, float width, float height, int size, bool heading)
        {
            var gui = GUIManager.Instance; var center = new Vector2(.5f, .5f);
            var text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y), heading ? gui.AveriaSerifBold : gui.AveriaSerif,
                size, heading ? gui.ValheimOrange : gui.ValheimBeige, true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true; text.resizeTextMinSize = 14; text.resizeTextMaxSize = size; return text;
        }
        private Button ButtonAt(string caption, float x, float y, float width, float height, Action action)
        {
            int created = generation; var center = new Vector2(.5f, .5f);
            var button = GUIManager.Instance.CreateButton(caption, panel.transform, center, center, new Vector2(x, y), width, height).GetComponent<Button>();
            var text = button.GetComponentInChildren<Text>(); text.supportRichText = false; text.resizeTextForBestFit = true; text.resizeTextMinSize = 14; text.resizeTextMaxSize = 18;
            var sound = button.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
            button.onClick.AddListener(() =>
            {
                if (created != generation || !IsVisible) return;
                try { if (!Valid()) { Hide(); return; } action(); }
                catch (Exception error) { Hide(); plugin.Report(error); }
            }); controls.Add(button); return button;
        }
        private void Scale()
        {
            if (overlay == null || panel == null) return;
            var rect = overlay.GetComponent<RectTransform>(); float scale = Mathf.Min(1, Mathf.Min(rect.rect.width / 700, rect.rect.height / 480));
            if (scale > .01f) panel.transform.localScale = Vector3.one * scale;
        }
        internal void HandleInputReset()
        { ownsInput = false; Hide(); }
        public void Hide()
        {
            generation++;
            try
            {
                if (overlay != null)
                {
                    if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                        && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(overlay.transform)) EventSystem.current.SetSelectedGameObject(null);
                    overlay.SetActive(false); UnityEngine.Object.Destroy(overlay);
                }
            }
            finally
            {
                overlay = panel = null; owner = null; network = null; title = hint = percent = explanation = null; slider = null; mute = null; controls.Clear();
                if (ownsInput) { ownsInput = false; try { GUIManager.BlockInput(false); } catch (Exception error) { plugin.Report(error); } }
            }
        }
    }
}
