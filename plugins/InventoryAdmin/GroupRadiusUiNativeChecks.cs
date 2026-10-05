// Optional menu-only checks; excluded from the released plugin.
using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimModPack.InventoryAdmin
{
    public static class GroupRadiusUiNativeChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Group radius UI: " + message); checks++; }
        private static object Get(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static void Call(object instance, string method, params object[] arguments)
        { instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, arguments); }
        private static int InputCount()
        { return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
        public static string Run()
        {
            if (Player.m_localPlayer != null) return "SKIP: Group radius UI probe requires isolated menu.";
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null)
                return "SKIP: Group radius native UI requires a graphical Jotunn canvas.";
            checks = 0;
            bool allowed = true, russian = true;
            string shortcut = "Ctrl+F11";
            var view = new GroupRadiusAdminView { Enabled = false, Radius = 500, Revision = 30, LeaderPeerId = 2, LeaderName = "Center <2>" };
            view.ExemptPeers.Add(3);
            int settings = 0, leaders = 0, exemptions = 0, inventories = 0, playerRequests = 0;
            long suppliedRevision = -1, suppliedPeer = 0; float suppliedRadius = 0; bool suppliedEnabled = false, suppliedExempt = false;
            var bindings = new AdminUiBindings
            {
                CanUse = () => allowed, IsHost = () => false, LocalPeerId = () => 1,
                Translate = (ru, en) => russian ? ru : en,
                GetGroupRadius = () => view, GroupRadiusShortcutLabel = () => shortcut,
                RequestPlayers = () => playerRequests++, RequestInventory = peer => inventories++,
                UpdateGroupRadius = (revision, enabled, radius) => { settings++; suppliedRevision = revision; suppliedEnabled = enabled; suppliedRadius = radius; },
                SetGroupRadiusLeader = (revision, peer) => { leaders++; suppliedRevision = revision; suppliedPeer = peer; },
                SetGroupRadiusExemption = (revision, peer, exempt) => { exemptions++; suppliedRevision = revision; suppliedPeer = peer; suppliedExempt = exempt; },
                Error = error => { throw new InvalidOperationException("Unexpected group radius UI fixture error", error); }
            };
            var window = new AdminWindow(bindings); int baseline = InputCount();
            try
            {
                Call(window, "BuildVisuals");
                var inventoryPanel = (GameObject)Get(window, "panel");
                var launcher = (Button)Get(window, "openGroupRadius");
                Check(launcher.interactable && launcher.GetComponentInChildren<Text>().text == "Радиус группы", "administrator inventory launcher is localized and enabled");
                var online = new List<AdminPlayerView>();
                for (int i = 0; i < 9; i++) online.Add(new AdminPlayerView { PeerId = i + 1, Name = "Player <" + (i + 1) + ">", IsAdmin = i == 0 });
                window.SetPlayers(online); Call(window, "OpenGroupRadius");
                var modal = (GameObject)Get(window, "groupRadiusModal");
                var panel = (GameObject)Get(window, "groupRadiusPanel");
                var radius = (InputField)Get(window, "groupRadiusValue");
                var enabled = (Toggle)Get(window, "groupRadiusEnabled");
                var exempt = (Toggle)Get(window, "groupRadiusExempt");
                var apply = (Button)Get(window, "groupRadiusApply");
                var leader = (Button)Get(window, "groupRadiusLeader");
                var exemptionApply = (Button)Get(window, "groupRadiusExemptionApply");
                var rows = (Button[])Get(window, "groupRadiusPlayers");
                Check(window.IsVisible && modal.activeInHierarchy && !inventoryPanel.activeSelf, "separate group panel reuses the current modal");
                Check(panel.GetComponent<RectTransform>().rect.width >= 959 && panel.GetComponent<RectTransform>().rect.height >= 749, "native wood panel has the expected group layout");
                Check(radius.text == "500" && !enabled.isOn && apply.interactable, "server snapshot populates mode and radius drafts");
                Check(((Text)Get(window, "groupRadiusSubtitle")).text.Contains("Ctrl+F11"), "configured shortcut appears in the native panel");
                Check(settings == 0 && leaders == 0 && exemptions == 0, "construction performs no mutation");
                foreach (var control in panel.GetComponentsInChildren<Selectable>())
                {
                    var rect = control.GetComponent<RectTransform>();
                    if (rect.transform.parent != panel.transform) continue;
                    Check(rect.anchoredPosition.y - rect.rect.height * rect.pivot.y >= -375
                        && rect.anchoredPosition.y + rect.rect.height * (1 - rect.pivot.y) <= 375,
                        "native group control remains vertically inside the wood panel: " + control.name);
                }
                foreach (var toggle in new[] { enabled, exempt })
                {
                    var rect = toggle.transform.Find("Background").GetComponent<RectTransform>();
                    Check(rect.rect.width >= 27 && rect.rect.width <= 29 && rect.rect.height >= 27 && rect.rect.height <= 29,
                        "group checkbox retains readable native checkmark geometry");
                }
                Call(window, "Repaint"); Call(window, "Repaint");
                Check(settings == 0 && exemptions == 0, "repaint never submits toggle changes");
                Check(!leader.interactable && !exempt.interactable && !exemptionApply.interactable, "peer mutations require an online selection");
                Call(window, "SelectGroupRadiusPlayer", 2);
                Check((long)Get(window, "selectedPeer") == 3 && exempt.isOn && leader.interactable && inventories == 0,
                    "group selection loads that online peer's manual exemption without reading inventory");
                Check(!exemptionApply.interactable, "unchanged manual exemption has no Apply action");
                exempt.isOn = false; enabled.isOn = true; radius.text = "650.5";
                Check(exemptionApply.interactable && settings == 0 && exemptions == 0, "edited toggles and radius remain local drafts");
                Call(window, "ApplyGroupRadiusSettings");
                Check(settings == 1 && suppliedRevision == 30 && suppliedEnabled && suppliedRadius == 650.5f,
                    "settings callback receives displayed revision, enabled mode, and invariant radius");
                Call(window, "ApplyGroupRadiusLeader");
                Check(leaders == 1 && suppliedRevision == 30 && suppliedPeer == 3, "central player callback receives exactly the selected online peer");
                Call(window, "ApplyGroupRadiusExemption");
                Check(exemptions == 1 && suppliedRevision == 30 && suppliedPeer == 3 && !suppliedExempt,
                    "exemption callback receives selected peer and explicit off value");
                foreach (string invalid in new[] { "", "0", "49.99", "10000.1", "NaN", "Infinity", "1e100", "500,5", "500 meters" })
                {
                    radius.text = invalid; Call(window, "ApplyGroupRadiusSettings");
                    Check(settings == 1 && window.IsVisible && Get(window, "groupRadiusModal") != null,
                        "invalid radius is rejected with the panel open: " + invalid);
                    Check(!String.IsNullOrEmpty(((Text)Get(window, "groupRadiusStatus")).text), "invalid radius leaves a visible explanation");
                }
                radius.text = "50"; Call(window, "ApplyGroupRadiusSettings");
                Check(settings == 2 && suppliedRadius == 50, "lower radius boundary is accepted");
                radius.text = "10000"; Call(window, "ApplyGroupRadiusSettings");
                Check(settings == 3 && suppliedRadius == 10000, "upper radius boundary is accepted");
                radius.text = "700"; view.Revision++;
                Call(window, "Repaint");
                Check(radius.text == "700" && !apply.interactable && !leader.interactable && !exemptionApply.interactable,
                    "a newer server revision preserves edits and disables stale mutations");
                Call(window, "ApplyGroupRadiusSettings"); Call(window, "ApplyGroupRadiusLeader"); Call(window, "ApplyGroupRadiusExemption");
                Check(settings == 3 && leaders == 1 && exemptions == 1, "direct stale handlers cannot submit old revision");
                Call(window, "ReloadGroupRadius");
                Check(radius.text == "500" && exempt.isOn && apply.interactable && playerRequests == 1,
                    "explicit refresh reloads current revision and drafts and requests online peers");
                window.SetBusy(true); Call(window, "ApplyGroupRadiusSettings"); Call(window, "ApplyGroupRadiusLeader"); Call(window, "ApplyGroupRadiusExemption");
                Check(!radius.interactable && !enabled.interactable && !exempt.interactable && !rows[0].interactable
                    && settings == 3 && leaders == 1 && exemptions == 1, "busy transaction disables controls and rejects duplicate mutations");
                window.SetBusy(false); view.ReadOnly = true; view.Notice = "Файл настроек повреждён. Данные не перезаписываются."; Call(window, "Repaint");
                Call(window, "ApplyGroupRadiusSettings"); Call(window, "ApplyGroupRadiusLeader"); Call(window, "ApplyGroupRadiusExemption");
                Check(!apply.interactable && !leader.interactable && !exemptionApply.interactable && !radius.interactable
                    && settings == 3 && leaders == 1 && exemptions == 1, "read-only server snapshot disables and rejects all mutations");
                Check(((Text)Get(window, "groupRadiusStatus")).text == view.Notice, "read-only reason appears in the native panel");
                view.ReadOnly = false; allowed = false; Call(window, "Repaint");
                Call(window, "ApplyGroupRadiusSettings"); Call(window, "ApplyGroupRadiusLeader"); Call(window, "ApplyGroupRadiusExemption");
                Check(!apply.interactable && !leader.interactable && !exempt.interactable && settings == 3 && leaders == 1 && exemptions == 1,
                    "revoked administrator access disables and rejects all mutations");
                allowed = true; var saved = view; view = null; Call(window, "Repaint");
                Check(!apply.interactable && !leader.interactable && !exempt.interactable && window.IsVisible,
                    "missing or expired private snapshot keeps panel open and disables mutations");
                view = saved; view.LeaderPeerId = 0; view.LeaderName = "Offline center"; Call(window, "Repaint");
                Check(((Text)Get(window, "groupRadiusCurrent")).text.Contains("Offline center"), "offline center keeps its saved name visible");
                online.RemoveAll(p => p.PeerId == 3); window.SetPlayers(online);
                Check((long)Get(window, "selectedPeer") == 0 && !leader.interactable && !exemptionApply.interactable,
                    "disconnect invalidates the selected online peer");
                shortcut = "Alt+F11"; Call(window, "Repaint");
                Check(((Text)Get(window, "groupRadiusSubtitle")).text.Contains("Alt+F11")
                    && !((Text)Get(window, "groupRadiusSubtitle")).text.Contains("Ctrl+F11"), "shortcut rebind replaces native group hint");
                russian = false; shortcut = ""; Call(window, "Repaint");
                Check(((Text)Get(window, "groupRadiusHeading")).text == "Group radius"
                    && ((Text)Get(window, "groupRadiusSubtitle")).text.Contains("Unbound"), "language switch and unbound shortcut localize");
                Check(((Text)Get(window, "groupRadiusHint")).text.Contains("central player")
                    && !((Text)Get(window, "groupRadiusHint")).text.Contains("Administrators"), "automatic exemption describes only the central player");
                Call(window, "SelectGroupRadiusPlayer", 1);
                Check((long)Get(window, "selectedPeer") == 2, "mixed-draft fixture selects a currently online peer");
                radius.text = "725"; enabled.isOn = true; exempt.isOn = true;
                Call(window, "Repaint"); Call(window, "Repaint");
                Check(radius.text == "725" && enabled.isOn && exempt.isOn && settings == 3 && exemptions == 1,
                    "periodic snapshots at the same revision retain every local draft");
                Call(window, "ApplyGroupRadiusLeader"); view.Revision++; Call(window, "Repaint");
                Check(leaders == 2 && radius.text == "725" && enabled.isOn && exempt.isOn && !apply.interactable,
                    "applying the center preserves unapplied settings and exemption drafts when its new revision arrives");
                Call(window, "ApplyGroupRadiusSettings"); Call(window, "ApplyGroupRadiusExemption");
                Check(settings == 3 && exemptions == 1, "mixed drafts still reject their old expected revision");
                Call(window, "ReloadGroupRadius");
                radius.text = "800"; exempt.isOn = true;
                Call(window, "ApplyGroupRadiusSettings"); view.Revision++; Call(window, "Repaint");
                Check(settings == 4 && exempt.isOn && !exemptionApply.interactable,
                    "applying settings preserves an unapplied exemption draft across the refreshed revision");
                Call(window, "ReloadGroupRadius");
                radius.text = "850"; enabled.isOn = true; exempt.isOn = true;
                Call(window, "ApplyGroupRadiusExemption"); view.Revision++; Call(window, "Repaint");
                Check(exemptions == 2 && radius.text == "850" && enabled.isOn && !apply.interactable,
                    "applying exemption preserves unapplied mode and radius drafts across the refreshed revision");
                Call(window, "ReloadGroupRadius");
                radius.text = "875"; exempt.isOn = true;
                Call(window, "SelectGroupRadiusPlayer", 0); Call(window, "Repaint");
                Check(radius.text == "875" && !exempt.isOn,
                    "selecting another online peer replaces its exemption draft while preserving server settings edits");
                int finalSettings = settings, finalLeaders = leaders, finalExemptions = exemptions;
                var oldButtons = modal.GetComponentsInChildren<Button>();
                Call(window, "BackFromGroupRadius");
                Check(Get(window, "groupRadiusModal") == null && inventoryPanel.activeSelf && window.IsVisible, "Back restores inventory without closing its modal");
                Call(window, "OpenGroupRadius"); var currentModal = Get(window, "groupRadiusModal");
                foreach (var button in oldButtons) button.onClick.Invoke();
                Check(ReferenceEquals(Get(window, "groupRadiusModal"), currentModal) && settings == finalSettings && leaders == finalLeaders && exemptions == finalExemptions,
                    "destroyed group callbacks cannot close or mutate a newer group panel");
                window.Hide(); window.Hide();
                Check(!window.IsVisible && Get(window, "groupRadiusModal") == null && InputCount() == baseline,
                    "idempotent cleanup destroys both panels and preserves shared input counter");
                return "Group radius native UI PASS; " + checks + " checks (isolated menu, no world settings modified).";
            }
            finally { window.Hide(); }
        }
    }
}
