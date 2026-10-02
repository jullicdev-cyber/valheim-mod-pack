using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.PinRemoval
{
    public sealed class DeathPinController : IDisposable
    {
        private readonly FieldInfo pins;
        private readonly PinHistoryController history;
        private readonly Action<Exception> report;
        private readonly WoodDialogView view;
        private readonly RemovalDialog<List<Minimap.PinData>> dialog;
        private GameObject launcher;
        private MapActionContext context;
        private List<Minimap.PinData> requestedPins;
        private HashSet<Minimap.PinData> requestedIndex;
        private bool allowed, disposed;
        public Func<bool> OpenShortcut;
        public Func<string> ShortcutLabel;
        public bool IsOpen { get { return dialog.IsOpen; } }

        public DeathPinController(FieldInfo pins, PinHistoryController history, Action<Exception> report)
        {
            if (pins == null || history == null || report == null) throw new ArgumentNullException();
            this.pins = pins; this.history = history; this.report = report;
            view = new WoodDialogView(() => T("Удаление меток смерти", "Remove death pins"),
                count => String.Format(CultureInfo.InvariantCulture, T("Удалить все метки смерти ({0}) с вашей карты?\nМогилы и вещи останутся в мире.",
                    "Remove all death pins ({0}) from your map?\nTombstones and items will remain in the world."), count),
                () => T("Удалить все", "Delete all"));
            dialog = new RemovalDialog<List<Minimap.PinData>>(view, Report);
        }
        public void Tick(bool canOpen)
        {
            if (disposed) return;
            try
            {
                allowed = canOpen && MapActionContext.BaseContext(Minimap.instance, Player.m_localPlayer);
                if (dialog.IsOpen)
                {
                    if (!allowed || !view.IsVisible || Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) Close();
                    else dialog.ValidateContext();
                }
                SetLauncher(allowed && !dialog.IsOpen && !TextInput.IsVisible());
                if (allowed && !dialog.IsOpen && !TextInput.IsVisible() && OpenShortcut != null && OpenShortcut()) Open();
            }
            catch (Exception error) { Close(); Report(error); }
        }
        private List<Minimap.PinData> CurrentPins(Minimap map)
        { return (List<Minimap.PinData>)pins.GetValue(map); }
        private static bool Death(Minimap.PinData pin)
        { return pin != null && pin.m_save && pin.m_type == Minimap.PinType.Death; }
        public void Open()
        {
            if (disposed || !allowed || dialog.IsOpen || TextInput.IsVisible()) return;
            try
            {
                var requested = MapActionContext.Capture(Minimap.instance);
                if (requested == null) return;
                var targets = new List<Minimap.PinData>();
                foreach (var pin in CurrentPins(requested.Map)) if (Death(pin)) targets.Add(pin);
                if (targets.Count == 0)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, T("На карте нет меток смерти.", "There are no death pins on your map."), 0, null);
                    return;
                }
                context = requested; requestedPins = targets; requestedIndex = new HashSet<Minimap.PinData>(targets);
                dialog.Open(targets, targets.Count.ToString(CultureInfo.InvariantCulture),
                    candidate => Valid(requested, candidate),
                    candidate => history.RemoveMany(requested.Map, candidate));
                SetLauncher(false);
            }
            catch (Exception error) { Close(); Report(error); }
        }
        private bool Valid(MapActionContext requested, List<Minimap.PinData> targets)
        {
            if (!ReferenceEquals(context, requested) || !ReferenceEquals(targets, requestedPins) || !requested.Current()
                || targets == null || targets.Count == 0 || requestedIndex == null || requestedIndex.Count != targets.Count) return false;
            var current = CurrentPins(requested.Map);
            foreach (var pin in targets) if (!Death(pin)) return false;
            // Validate live membership in linear time, without allocating a new
            // set for every frame while the confirmation remains open.
            int present = 0;
            foreach (var pin in current) if (requestedIndex.Contains(pin)) present++;
            return present == targets.Count;
        }
        private void SetLauncher(bool visible)
        {
            if (!visible) { if (launcher != null) launcher.SetActive(false); return; }
            if (launcher == null)
            {
                if (GUIManager.CustomGUIFront == null) return;
                launcher = GUIManager.Instance.CreateButton("", GUIManager.CustomGUIFront.transform,
                    new Vector2(1, 1), new Vector2(1, 1), new Vector2(-220, -405), 420, 50);
                launcher.name = "ConfirmMapPinRemoval.DeathLauncher";
                var sound = launcher.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
                launcher.GetComponent<Button>().onClick.AddListener(Open);
            }
            string hotkey = ShortcutLabel == null ? "" : ShortcutLabel();
            PinLauncherLabel.Apply(launcher, T("Удалить все метки смерти", "Remove all death pins"), hotkey,
                T("Клавиша не назначена", "Unbound"));
            launcher.SetActive(true);
        }
        private static string T(string russian, string english)
        { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian" ? russian : english; }
        private void Report(Exception error) { try { report(error); } catch { } }
        public void Close()
        {
            try { dialog.Cancel(); view.Hide(); }
            catch (Exception error) { Report(error); }
            finally { context = null; requestedPins = null; requestedIndex = null; allowed = false; SetLauncher(false); }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try { Close(); }
            finally { if (launcher != null) { launcher.SetActive(false); UnityEngine.Object.Destroy(launcher); launcher = null; } }
        }
    }
}
