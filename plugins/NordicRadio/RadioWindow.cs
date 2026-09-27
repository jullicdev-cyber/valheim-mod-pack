using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.NordicRadio
{
    // One modal owns one input-block request. Network updates only repaint text;
    // they never recreate the window or move the user's current selection.
    public sealed class RadioWindow
    {
        private const int PageSize = 6;
        private readonly Plugin plugin;
        private readonly List<Button> buttons = new List<Button>();
        private readonly Button[] trackButtons = new Button[PageSize];
        private readonly string[] visibleTrackIds = new string[PageSize];
        private GameObject overlay;
        private GameObject panel;
        private RadioPiece piece;
        private Player player;
        private ZNet network;
        private Text currentTitle;
        private Text statusLabel;
        private Text sharedVolume;
        private Text personalVolume;
        private Text pageLabel;
        private Button playButton;
        private Button repeatButton;
        private Button shuffleButton;
        private Button previousPage;
        private Button nextPage;
        private Button refreshButton;
        private bool russian;
        private bool ownsInputBlock;
        private int generation;
        private int page;
        private float nextRepaint;

        public RadioWindow(Plugin plugin)
        {
            if (plugin == null) throw new ArgumentNullException("plugin");
            this.plugin = plugin;
        }

        public bool IsVisible { get { return overlay != null && overlay.activeInHierarchy; } }

        public void Show(RadioPiece target)
        {
            if (IsVisible && ReferenceEquals(piece, target)) return;
            Hide();
            piece = target;
            player = Player.m_localPlayer;
            network = ZNet.instance;
            if (!ValidContext()) { piece = null; return; }
            var front = GUIManager.CustomGUIFront;
            if (front == null) return;
            russian = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            page = 0;
            try
            {
                overlay = new GameObject("NordicRadio.Modal", typeof(RectTransform), typeof(Image));
                overlay.transform.SetParent(front.transform, false);
                overlay.layer = GUIManager.UILayer;
                var rect = overlay.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                var dim = overlay.GetComponent<Image>();
                dim.color = new Color(0, 0, 0, 0.55f);
                dim.raycastTarget = true;

                var center = new Vector2(0.5f, 0.5f);
                panel = GUIManager.Instance.CreateWoodpanel(overlay.transform, center, center, Vector2.zero, 640, 680, false);
                panel.name = "NordicRadio.WoodPanel";
                var group = panel.AddComponent<CanvasGroup>();
                group.interactable = true; group.blocksRaycasts = true;
                Label(T("Рог скальда", "Skald's Horn"), 0, 302, 490, 40, 30, true);
                ButtonAt("X", 278, 302, 42, 38, Hide);
                currentTitle = Label("", 0, 253, 568, 40, 23, false);
                statusLabel = Label("", 0, 218, 568, 30, 17, false);

                ButtonAt(T("Предыдущая", "Previous"), -200, 173, 174, 44,
                    () => Send("previous", "", 0));
                playButton = ButtonAt(T("Включить", "Play"), 0, 173, 174, 44, () =>
                {
                    var state = plugin.Service.GetState(piece.Id);
                    Send(state != null && state.Playing ? "pause" : "play", "", 0);
                });
                ButtonAt(T("Следующая", "Next"), 200, 173, 174, 44,
                    () => Send("next", "", 0));
                repeatButton = ButtonAt("", -150, 124, 270, 38, () =>
                {
                    var state = plugin.Service.GetState(piece.Id);
                    Send("repeat", "", state != null && state.Repeat ? 0 : 1);
                });
                shuffleButton = ButtonAt("", 150, 124, 270, 38, () =>
                {
                    var state = plugin.Service.GetState(piece.Id);
                    Send("shuffle", "", state != null && state.Shuffle ? 0 : 1);
                });

                sharedVolume = Label("", -155, 80, 285, 28, 18, false);
                personalVolume = Label("", 155, 80, 285, 28, 18, false);
                ButtonAt("-", -250, 43, 66, 32, () => ChangeSharedVolume(-0.1f));
                ButtonAt("+", -60, 43, 66, 32, () => ChangeSharedVolume(0.1f));
                Label(T("Для всех", "Everyone"), -155, 43, 118, 28, 16, false);
                ButtonAt("-", 60, 43, 66, 32, () => ChangePersonalVolume(-0.1f));
                ButtonAt("+", 250, 43, 66, 32, () => ChangePersonalVolume(0.1f));
                Label(T("Только мне", "Only me"), 155, 43, 118, 28, 16, false);
                Label(T("Композиции", "Tracks"), -120, -4, 324, 32, 22, true);
                refreshButton = ButtonAt(T("Обновить", "Rescan"), 206, -4, 150, 32,
                    () => { if (ZNet.instance != null && ZNet.instance.IsServer()) plugin.Service.RefreshLibrary(); });
                for (int i = 0; i < PageSize; ++i)
                {
                    int slot = i;
                    trackButtons[i] = ButtonAt("", 0, -49 - i * 40, 572, 36, () =>
                    {
                        string id = visibleTrackIds[slot];
                        if (!String.IsNullOrEmpty(id)) Send("select", id, 0);
                    });
                    var text = trackButtons[i].GetComponentInChildren<Text>();
                    if (text != null) { text.fontSize = 18; text.alignment = TextAnchor.MiddleCenter; }
                }
                previousPage = ButtonAt("<", -225, -301, 116, 34, () => { if (page > 0) --page; });
                pageLabel = Label("", 0, -301, 300, 30, 17, false);
                nextPage = ButtonAt(">", 225, -301, 116, 34, () => { ++page; });

                Refresh();
                // Jotunn increments its counter before manipulating the game camera.
                // Mark ownership first so a failure is still balanced by Hide().
                ownsInputBlock = true;
                GUIManager.BlockInput(true);
                overlay.transform.SetAsLastSibling();
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(playButton.gameObject);
            }
            catch (Exception error) { Fail(error); }
        }

        public void Tick()
        {
            if (overlay == null && !ownsInputBlock) return;
            try
            {
                if (!IsVisible || !ValidContext() || Input.GetKeyDown(KeyCode.Escape)
                    || ZInput.GetButtonDown("JoyButtonB"))
                {
                    if (ZInput.GetButtonDown("JoyButtonB")) ZInput.ResetButtonStatus("JoyButtonB");
                    Hide();
                    return;
                }
                if (Time.unscaledTime >= nextRepaint)
                {
                    Refresh();
                    nextRepaint = Time.unscaledTime + 0.2f;
                }
            }
            catch (Exception error) { Fail(error); }
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
                    overlay.SetActive(false);
                    UnityEngine.Object.Destroy(overlay);
                }
            }
            catch (Exception error) { Debug.LogError("[NordicRadio] Closing window: " + error); }
            finally
            {
                overlay = null; panel = null; piece = null; player = null; network = null;
                buttons.Clear();
                Array.Clear(visibleTrackIds, 0, visibleTrackIds.Length);
                Array.Clear(trackButtons, 0, trackButtons.Length);
                if (ownsInputBlock)
                {
                    ownsInputBlock = false;
                    try { GUIManager.BlockInput(false); }
                    catch (Exception error) { Debug.LogError("[NordicRadio] Releasing UI input: " + error); }
                }
            }
        }

        private bool ValidContext()
        {
            return piece != null && piece.IsReady && player != null && ReferenceEquals(player, Player.m_localPlayer)
                && network != null && ReferenceEquals(network, ZNet.instance) && plugin.Service != null
                && !player.IsDead() && !player.IsTeleporting() && !player.InCutscene() && !player.IsSleeping()
                && Vector3.Distance(player.transform.position, piece.transform.position) <= 5f
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !InventoryGui.IsVisible()
                && (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large);
        }

        private void Send(string command, string trackId, float value)
        {
            plugin.Service.Command(piece.Id, command, trackId, value);
        }

        private void ChangeSharedVolume(float change)
        {
            var state = plugin.Service.GetState(piece.Id);
            Send("volume", "", Mathf.Clamp01((state == null ? 0.7f : state.Volume) + change));
        }

        private void ChangePersonalVolume(float change)
        {
            plugin.PersonalVolume = Mathf.Clamp01(plugin.PersonalVolume + change);
        }

        private void Refresh()
        {
            var state = plugin.Service.GetState(piece.Id);
            IList<TrackInfo> tracks = plugin.Service.Tracks;
            int count = tracks == null ? 0 : tracks.Count;
            int pages = Math.Max(1, (count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            TrackInfo current = null;
            for (int i = 0; i < count; ++i)
                if (tracks[i] != null && state != null && tracks[i].Id == state.TrackId) { current = tracks[i]; break; }
            SetText(currentTitle, current == null ? T("Выберите композицию", "Select a track") : SafeTitle(current.Title, 58));
            string detail = state == null || String.IsNullOrEmpty(state.TrackId) ? "" : plugin.Service.Status(state.TrackId);
            double elapsed = state == null ? 0 : state.Offset;
            if (state != null && state.Playing && ZNet.instance != null)
                elapsed += Math.Max(0, ZNet.instance.GetTimeSeconds() - state.StartedAt);
            double duration = current == null ? 0 : Convert.ToDouble(current.Duration, CultureInfo.InvariantCulture);
            if (duration > 0) elapsed = Math.Min(elapsed, duration);
            string clock = Clock(elapsed) + " / " + (duration > 0 ? Clock(duration) : "--:--");
            string libraryStatus = plugin.Service.LibraryStatus ?? "";
            bool scanning = libraryStatus.StartsWith("Scanning MP3", StringComparison.Ordinal);
            bool scanFailed = libraryStatus.StartsWith("MP3 scan failed:", StringComparison.Ordinal);
            if (count == 0)
            {
                if (scanning || scanFailed) detail = LocalStatus(libraryStatus, russian);
                else detail = ZNet.instance.IsServer() ? T("Добавьте MP3 в NordicRadio/Music и нажмите «Обновить»", "Add MP3 files to NordicRadio/Music, then Rescan")
                    : T("Ожидание списка музыки от хоста", "Waiting for the host's music library");
            }
            else if (scanFailed) detail = LocalStatus(libraryStatus, russian);
            else if (String.IsNullOrEmpty(detail))
                detail = state != null && state.Playing ? T("Воспроизведение", "Playing") : T("Остановлено", "Paused");
            else detail = LocalStatus(detail, russian);
            SetText(statusLabel, count == 0 ? SafeTitle(detail, 100) : clock + "   " + SafeTitle(detail, 62));
            SetButtonText(playButton, state != null && state.Playing ? T("Пауза", "Pause") : T("Включить", "Play"));
            SetButtonText(repeatButton, T("Повтор: ", "Repeat: ") + OnOff(state != null && state.Repeat));
            SetButtonText(shuffleButton, T("Случайно: ", "Shuffle: ") + OnOff(state != null && state.Shuffle));
            SetText(sharedVolume, T("Громкость радио: ", "Radio volume: ") + Percent(state == null ? 0.7f : state.Volume));
            SetText(personalVolume, T("Личная громкость: ", "Personal volume: ") + Percent(plugin.PersonalVolume));
            refreshButton.interactable = ZNet.instance.IsServer();
            playButton.interactable = count > 0;
            for (int slot = 0; slot < PageSize; ++slot)
            {
                int index = page * PageSize + slot;
                TrackInfo track = index < count ? tracks[index] : null;
                visibleTrackIds[slot] = track == null ? null : track.Id;
                trackButtons[slot].interactable = track != null;
                SetButtonText(trackButtons[slot], track == null ? "" :
                    (state != null && track.Id == state.TrackId ? "> " : "") + SafeTitle(track.Title, 56));
            }
            previousPage.interactable = page > 0;
            nextPage.interactable = page + 1 < pages;
            SetText(pageLabel, (page + 1).ToString(CultureInfo.InvariantCulture) + " / " + pages.ToString(CultureInfo.InvariantCulture)
                + "   (" + count.ToString(CultureInfo.InvariantCulture) + T(" композиций)", " tracks)"));
            ConfigureNavigation();
            var parent = overlay.GetComponent<RectTransform>();
            float scale = Mathf.Min(1f, Mathf.Min(parent.rect.width / 660f, parent.rect.height / 700f));
            // On the first frame the canvas may not have been laid out yet.
            if (scale > 0.01f) panel.transform.localScale = Vector3.one * scale;
        }

        private Text Label(string value, float x, float y, float width, float height, int size, bool heading)
        {
            var gui = GUIManager.Instance;
            var center = new Vector2(0.5f, 0.5f);
            var text = gui.CreateText(value, panel.transform, center, center, new Vector2(x, y),
                heading ? gui.AveriaSerifBold : gui.AveriaSerif, size, heading ? gui.ValheimOrange : gui.ValheimBeige,
                true, Color.black, width, height, false).GetComponent<Text>();
            text.supportRichText = false;
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = Math.Min(size, 14);
            text.resizeTextMaxSize = size;
            return text;
        }

        private Button ButtonAt(string title, float x, float y, float width, float height, Action action)
        {
            int createdGeneration = generation;
            var center = new Vector2(0.5f, 0.5f);
            var button = GUIManager.Instance.CreateButton(title, panel.transform, center, center,
                new Vector2(x, y), width, height).GetComponent<Button>();
            var text = button.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.supportRichText = false;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 14;
                text.resizeTextMaxSize = 20;
            }
            var sound = button.GetComponent<ButtonSfx>();
            if (sound != null) sound.m_selectSfxPrefab = null;
            button.onClick.AddListener(() =>
            {
                if (createdGeneration != generation || !IsVisible) return;
                try
                {
                    if (!ValidContext()) { Hide(); return; }
                    action();
                    if (IsVisible) Refresh();
                }
                catch (Exception error) { Fail(error); }
            });
            buttons.Add(button);
            return button;
        }

        private void ConfigureNavigation()
        {
            var active = new List<Button>();
            foreach (var button in buttons) if (button != null && button.interactable) active.Add(button);
            for (int i = 0; i < active.Count; ++i)
            {
                Button previous = active[(i + active.Count - 1) % active.Count];
                Button next = active[(i + 1) % active.Count];
                active[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnLeft = previous, selectOnUp = previous, selectOnRight = next, selectOnDown = next };
            }
        }

        private void Fail(Exception error)
        {
            Debug.LogError("[NordicRadio] Radio window: " + error);
            Hide();
        }

        private string T(string ru, string en) { return russian ? ru : en; }
        private string OnOff(bool enabled) { return enabled ? T("вкл", "on") : T("выкл", "off"); }
        private static string Percent(float value) { return Mathf.RoundToInt(Mathf.Clamp01(value) * 100).ToString(CultureInfo.InvariantCulture) + "%"; }
        private static void SetText(Text text, string value) { if (text != null && text.text != value) text.text = value; }
        private static void SetButtonText(Button button, string value) { SetText(button.GetComponentInChildren<Text>(), value); }

        internal static string LocalStatus(string value, bool russian)
        {
            if (String.IsNullOrEmpty(value)) return "";
            if (!russian) return value;
            if (value == "Ready") return "Готово";
            if (value == "Queued") return "В очереди";
            if (value == "Checking cache") return "Проверка кэша";
            if (value == "Waiting for music") return "Ожидание музыки";
            if (value.StartsWith("Scanning MP3", StringComparison.Ordinal)) return "Сканирование MP3...";
            if (value.StartsWith("Downloading ", StringComparison.Ordinal)) return "Загрузка " + value.Substring("Downloading ".Length);
            if (value.StartsWith("Download failed: ", StringComparison.Ordinal)) return "Ошибка загрузки: " + value.Substring("Download failed: ".Length);
            if (value.StartsWith("MP3 scan failed: ", StringComparison.Ordinal)) return "Ошибка чтения музыки: " + value.Substring("MP3 scan failed: ".Length);
            return value;
        }

        internal static string Clock(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds)) return "--:--";
            int total = (int)Math.Min(Int32.MaxValue, Math.Max(0, seconds));
            return (total / 60).ToString(CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        internal static string SafeTitle(string value, int maxElements)
        {
            if (String.IsNullOrEmpty(value) || maxElements < 1) return "";
            var clean = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (Char.IsControl(c)) { if (Char.IsWhiteSpace(c)) clean.Append(' '); }
                else if (Char.GetUnicodeCategory(c) != UnicodeCategory.Format) clean.Append(c);
            }
            string result = clean.ToString().Trim();
            int[] elements = StringInfo.ParseCombiningCharacters(result);
            return elements.Length > maxElements ? result.Substring(0, elements[maxElements]) + "…" : result;
        }
    }
}
