// Menu-only graphical fixture; excluded from the production WorldCharacters DLL.
using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimModPack.WorldCharacters
{
    public static class AdministrationUiNativeChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("World Characters UI: " + message); checks++; }
        private static object Get(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static void Set(object instance, string name, object value)
        { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value); }
        private static void Call(object instance, string name, params object[] arguments)
        { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments); }
        private static int InputCount()
        { return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
        private static bool Held(object lease)
        { return (bool)lease.GetType().GetProperty("Held", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lease, null); }
        // Test-only screenshot preview. This owns no input lease and never opens
        // a real world, administration service, request file or player inventory.
        public static AdministrationWindow Preview()
        {
            if (Player.m_localPlayer != null || GUIManager.CustomGUIFront == null)
                throw new InvalidOperationException("Administration preview requires the isolated graphical menu.");
            string firstId = "1".PadLeft(64, '0');
            var requests = new[]
            {
                new AdministrationRequest(firstId, "Игрок 1", "76561198000000001", 1001, 99, 3, "preview-first", "", false),
                new AdministrationRequest("2".PadLeft(64, '0'), "Игрок 2", "76561198000000002", 1002, 99, 8, "preview-second", "", false)
            };
            var items = new[]
            {
                new AdministrationItem(1, 40, 1, 0, 0, false, "", "Ячейка 1, 1"),
                new AdministrationItem(2, 12, 1, 1, 0, false, "Рюкзак@3,0/внутренний@0,0", "Ячейка 2, 1"),
                new AdministrationItem(3, 1, 2, 0, 6, true, "", "Прочность сохранена")
            };
            var snapshot = new AdministrationView(99, 1, requests, firstId, items, false, "",
                "Игрок должен подключиться повторно после решения.");
            var bindings = new AdministrationUiBindings
            {
                CanUse = () => true, WorldId = () => 99, ShortcutLabel = () => "Ctrl+F4", Translate = (ru, en) => ru,
                Snapshot = () => snapshot, ItemName = hash => hash == 1 ? "Древесина" : hash == 2 ? "Железо" : "Кожаный шлем",
                ItemIcon = hash =>
                {
                    string name = hash == 1 ? "Wood" : hash == 2 ? "Iron" : "HelmetLeather";
                    var prefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab(name);
                    var drop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
                    return drop == null ? null : drop.m_itemData.GetIcon();
                },
                Refresh = () => { }, Inspect = id => { }, Decide = (id, fingerprint, decision) => { },
                StatusText = () => "Состояние загружено.\nОжидают решения: 2\nНеподтверждённых сохранений: 0"
            };
            var window = new AdministrationWindow(bindings);
            try { Call(window, "BuildVisuals"); Call(window, "SelectRequest", 0); return window; }
            catch { window.Hide(); throw; }
        }
        public static string Run()
        {
            if (Player.m_localPlayer != null) return "SKIP: World Characters UI fixture requires isolated menu.";
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null)
                return "SKIP: World Characters UI fixture requires graphical Jotunn canvas.";
            checks = 0; CheckLease();
            bool allowed = true, russian = true; long world = 99;
            string shortcut = "Ctrl+F4", inspected = "", decidedId = "", decidedFingerprint = "";
            int inspectCalls = 0, refreshCalls = 0, decisions = 0, snapshots = 0;
            AdministrationDecision lastDecision = AdministrationDecision.Reject;
            var requests = new List<AdministrationRequest>();
            for (int i = 0; i < 8; i++) requests.Add(new AdministrationRequest((i + 1).ToString("x").PadLeft(64, '0'),
                "Guest <" + i + ">", (76561198000000000UL + (ulong)i).ToString(), 100 + i, 99, 9, "fingerprint-" + i, "", false));
            requests.Add(new AdministrationRequest("bad".PadLeft(64, '0'), "Broken", "", 0, 0, 0, "", "Corrupt <record>", false));
            requests.Add(new AdministrationRequest("other".PadLeft(64, '0'), "Other world", "", 200, 100, 0, "other-fingerprint", "", false));
            var items = new List<AdministrationItem>();
            for (int i = 0; i < 9; i++) items.Add(new AdministrationItem(i + 1, 10 + i, 2, i, 0, i == 0,
                i == 0 ? "Backpack@1,2/nested@0,0" : "", "variant=1; metadata=<fixture>"));
            AdministrationView view = new AdministrationView(99, 1, requests, "", null, false, "", "");
            var iron = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab("Iron");
            var drop = iron == null ? null : iron.GetComponent<ItemDrop>();
            Sprite sprite = drop == null ? null : drop.m_itemData.GetIcon();
            Check(sprite != null, "fixture uses real native item sprite without creating player inventory");
            var bindings = new AdministrationUiBindings
            {
                CanUse = () => allowed, WorldId = () => world, ShortcutLabel = () => shortcut,
                Translate = (ru, en) => russian ? ru : en,
                Snapshot = () => { snapshots++; return view; }, ItemName = hash => "Item <b>" + hash + "</b>", ItemIcon = hash => sprite,
                StatusText = () => "Queue <1>\nCapture=2 ms",
                Refresh = () => refreshCalls++,
                Inspect = id => { inspectCalls++; inspected = id; view = new AdministrationView(99, 1, requests, id, items, false, "", ""); },
                Decide = (id, fingerprint, decision) => { decisions++; decidedId = id; decidedFingerprint = fingerprint; lastDecision = decision; },
                Error = error => { throw new InvalidOperationException("Unexpected World Characters UI fixture error", error); }
            };
            var window = new AdministrationWindow(bindings); int baseline = InputCount();
            try
            {
                Call(window, "BuildVisuals");
                var panel = (GameObject)Get(window, "panel"); var rect = panel.GetComponent<RectTransform>();
                Check(window.IsVisible && panel.name == "WorldCharacters.AdministrationPanel" && rect.rect.width >= 1129 && rect.rect.height >= 819,
                    "actual native wood panel has expected administration dimensions");
                var viewport = ((GameObject)Get(window, "overlay")).GetComponent<RectTransform>();
                viewport.anchorMin = viewport.anchorMax = new Vector2(.5f, .5f);
                viewport.sizeDelta = new Vector2(1280, 720); viewport.anchoredPosition = Vector2.zero;
                Call(window, "Scale");
                float panelScale = panel.transform.localScale.x;
                Check(panelScale > 0 && panelScale <= 1 && rect.rect.width * panelScale <= viewport.rect.width
                    && rect.rect.height * panelScale <= viewport.rect.height,
                    "native panel scales inside an isolated 1280 by 720 viewport without resizing shared canvas");
                foreach (var name in new[] { "title", "status", "identity" })
                {
                    var labelRect = ((Text)Get(window, name)).rectTransform;
                    Check((Math.Abs(labelRect.anchoredPosition.y) + labelRect.rect.height / 2) * panelScale <= viewport.rect.height / 2,
                        "responsive viewport contains header, identity and status footer");
                }
                viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
                viewport.offsetMin = viewport.offsetMax = Vector2.zero; Call(window, "Scale");
                Check(((Text)Get(window, "subtitle")).text.Contains("Ctrl+F4"), "header shows current shortcut");
                var rows = (Button[])Get(window, "requestRows"); var itemRows = (Text[])Get(window, "itemRows");
                // Hide clears and BuildVisuals repopulates the same row array.
                // Retain the button itself to exercise the old generation guard.
                Button oldRequestRow = rows[0];
                var icons = (Image[])Get(window, "itemIcons");
                var approve = (Button)Get(window, "approve"); var reject = (Button)Get(window, "reject"); var fresh = (Button)Get(window, "fresh");
                var search = (InputField)Get(window, "search");
                Check(((Text)Get(window, "requestPages")).text.EndsWith("9"), "request list includes damaged entries and excludes other world");
                Check(rows[0].gameObject.name == "WorldCharacters.RequestRow0" && !rows[0].GetComponentInChildren<Text>().supportRichText
                    && rows[0].GetComponentInChildren<Text>().text.Contains("<0>"), "request identity renders literal rich-text characters");
                Check(!approve.interactable && !reject.interactable && !fresh.interactable, "no whole-character decision before inspection");
                Call(window, "SelectRequest", 0);
                Check(inspectCalls == 1 && inspected == requests[0].Id && approve.interactable && reject.interactable && fresh.interactable,
                    "selecting row inspects its complete ID and enables reviewed decisions");
                Check(((Text)Get(window, "identity")).text.Contains(requests[0].Owner) && !((Text)Get(window, "identity")).supportRichText,
                    "reviewed character identity and Steam account are plain text");
                Check(itemRows[0].text.Contains("× 10") && itemRows[0].text.Contains("качество 2") && itemRows[0].text.Contains("В рюкзаке")
                    && itemRows[0].text.Contains("nested@0,0") && itemRows[0].text.Contains("<b>") && !itemRows[0].supportRichText,
                    "actual item rows show quantity, quality, nested backpack path and plain metadata");
                for (int i = 0; i < icons.Length; i++) Check(icons[i].enabled && icons[i].sprite == sprite && icons[i].preserveAspect,
                    "native request item row shows real item sprite");
                Set(window, "itemPage", 1); Call(window, "Repaint");
                Check(icons[2].enabled && !icons[3].enabled && icons[3].sprite == null && itemRows[3].text == "", "final item page clears prior icons and text");
                Set(window, "requestPage", 1); Call(window, "Repaint");
                Check(rows[0].interactable && rows[1].interactable && !rows[2].interactable && rows[2].GetComponentInChildren<Text>().text.Contains("Corrupt <record>"),
                    "corrupt request is visible with disabled selection");
                search.text = requests[7].Owner;
                Check(((Text)Get(window, "requestPages")).text.EndsWith("1") && !approve.interactable && rows[0].GetComponentInChildren<Text>().text.Contains("<7>"),
                    "search filters Steam account and clears action selection");
                Call(window, "SelectRequest", 0); Check(inspected == requests[7].Id, "filtered row forwards actual matching request ID");
                search.text = ""; Call(window, "SelectRequest", 0);
                int beforeReads = inspectCalls, beforeRefresh = refreshCalls;
                for (int i = 0; i < 5; i++) Call(window, "Repaint");
                Check(snapshots > 5 && inspectCalls == beforeReads && refreshCalls == beforeRefresh, "repaint polls only immutable memory snapshot without scheduling disk reads");
                Call(window, "Refresh"); Check(refreshCalls == beforeRefresh + 1, "manual refresh schedules exactly one request-list operation");
                Call(window, "Decide", AdministrationDecision.Approve);
                Check(decisions == 1 && decidedId == requests[0].Id && decidedFingerprint == requests[0].Fingerprint && lastDecision == AdministrationDecision.Approve,
                    "approval returns exact inspected ID and fingerprint for full-progress decision");
                Call(window, "Decide", AdministrationDecision.Reject);
                Check(decisions == 2 && lastDecision == AdministrationDecision.Reject, "rejection is an explicit separate decision");
                bool popupBefore = UnifiedPopup.IsVisible(); Call(window, "Decide", AdministrationDecision.Fresh);
                var dialog = (GameObject)Get(window, "confirmation");
                Check(dialog != null && dialog.activeInHierarchy && dialog.transform.IsChildOf(((GameObject)Get(window, "overlay")).transform)
                    && decisions == 2 && UnifiedPopup.IsVisible() == popupBefore, "fresh opens only an owned modal and makes no immediate decision");
                Check(((Text)Get(window, "confirmationCaption")).text.Contains("Весь прогресс") && !((Text)Get(window, "confirmationCaption")).supportRichText,
                    "fresh confirmation explains whole progress and backpack consequence");
                var yes = (Button)Get(window, "confirmYes"); var no = (Button)Get(window, "confirmNo");
                Check(no.navigation.selectOnDown == yes && yes.navigation.selectOnDown == no,
                    "fresh confirmation gamepad navigation cycles only between its own two controls");
                if (EventSystem.current != null) Check(EventSystem.current.currentSelectedGameObject == no.gameObject, "fresh confirmation initially focuses cancel");
                var oldButtons = dialog.GetComponentsInChildren<Button>(); Call(window, "CancelConfirmation"); Call(window, "CancelConfirmation");
                if (EventSystem.current != null) Check(EventSystem.current.currentSelectedGameObject == ((Button)Get(window, "refresh")).gameObject,
                    "closing owned confirmation restores focus to refresh");
                Call(window, "Decide", AdministrationDecision.Fresh); var replacement = Get(window, "confirmation");
                foreach (var button in oldButtons) button.onClick.Invoke();
                Check(ReferenceEquals(Get(window, "confirmation"), replacement) && window.IsVisible && decisions == 2,
                    "callbacks from replaced confirmation cannot close current dialog or decide");
                requests[0] = new AdministrationRequest(requests[0].Id, requests[0].Name, requests[0].Owner, requests[0].Character, 99, 9, "changed-fingerprint", "", false);
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "", ""); Call(window, "ApplyConfirmation");
                Check(Get(window, "confirmation") == null && decisions == 2, "changed request fingerprint invalidates destructive confirmation");
                Call(window, "Decide", AdministrationDecision.Fresh); world = 100; Call(window, "ApplyConfirmation");
                Check(Get(window, "confirmation") == null && decisions == 2, "world identity change invalidates destructive confirmation");
                world = 99; Call(window, "Decide", AdministrationDecision.Fresh); Call(window, "ApplyConfirmation");
                Check(Get(window, "confirmation") == null && decisions == 3 && lastDecision == AdministrationDecision.Fresh
                    && decidedFingerprint == "changed-fingerprint", "confirmed fresh forwards currently inspected fingerprint exactly once");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "", "Approved with the reviewed progress; the player can reconnect.");
                Call(window, "Repaint"); Check(((Text)Get(window, "status")).text.Contains("может подключиться повторно"), "successful service decision notice localizes reconnect instruction");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "Post-commit refresh failed <details>", "Approved with the reviewed progress; the player can reconnect.");
                Call(window, "Repaint"); string committedStatus = ((Text)Get(window, "status")).text;
                Check(committedStatus.Contains("прогресс одобрен") && committedStatus.Contains("Post-commit refresh failed <details>")
                    && committedStatus.IndexOf("прогресс одобрен", StringComparison.Ordinal) < committedStatus.IndexOf("Post-commit refresh", StringComparison.Ordinal)
                    && !approve.interactable, "durable decision success remains visible before post-commit refresh error and cannot be repeated");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, true, "", ""); Call(window, "Repaint");
                Call(window, "Decide", AdministrationDecision.Approve); Call(window, "Refresh");
                Check(!approve.interactable && !fresh.interactable && decisions == 3 && refreshCalls == beforeRefresh + 1, "busy service blocks repeated decisions and refresh");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "Read failure <details>", ""); Call(window, "Repaint");
                Check(!approve.interactable && ((Text)Get(window, "status")).text.Contains("Read failure <details>"), "service error appears as plain text and blocks decisions");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "", ""); allowed = false;
                Call(window, "Decide", AdministrationDecision.Reject); Call(window, "Refresh"); Call(window, "SelectRequest", 1);
                Check(decisions == 3 && inspectCalls == beforeReads && refreshCalls == beforeRefresh + 1 && !reject.interactable,
                    "lost host eligibility blocks private action handlers before next Tick");
                allowed = true; shortcut = "Alt+F12"; russian = false; Call(window, "Repaint");
                Check(((Text)Get(window, "subtitle")).text.Contains("Alt+F12") && approve.GetComponentInChildren<Text>().text == "Approve progress"
                    && fresh.GetComponentInChildren<Text>().text == "Start fresh…", "language and rebound shortcut update existing native controls");
                shortcut = ""; Set(window, "statusMode", true); Call(window, "Repaint");
                Check(((Text)Get(window, "subtitle")).text.Contains("Unbound") && !approve.gameObject.activeSelf
                    && ((Text)Get(window, "diagnostics")).text.Contains("Queue <1>") && !((Text)Get(window, "diagnostics")).supportRichText,
                    "status tab shows plain diagnostics and hides admission controls");
                Call(window, "Decide", AdministrationDecision.Approve); Check(decisions == 3, "hidden admission controls cannot decide from status tab");
                Set(window, "statusMode", false); view = new AdministrationView(99, 1, new AdministrationRequest[0], "", null, false, "", ""); Call(window, "Repaint");
                Check(!approve.interactable && ((Text)Get(window, "identity")).text.Contains("No requests"), "removed selected request clears action state and explains empty list");
                object lease = Get(window, "inputLease"); Call(lease, "Acquire"); Check(Held(lease), "window records its own input lease in isolated menu");
                window.HandleInputReset(); Check(!Held(lease) && !window.IsVisible && InputCount() == baseline,
                    "global reset forgets ownership and closes only this panel without decrementing another lease");
                view = new AdministrationView(99, 1, requests, requests[0].Id, items, false, "", ""); Call(window, "BuildVisuals");
                oldRequestRow.onClick.Invoke();
                Check(window.IsVisible && inspectCalls == beforeReads, "old request-row callback cannot affect newly created window");
                Call(window, "SelectRequest", 0); ((Button)Get(window, "approve")).onClick.Invoke();
                Check(!window.IsVisible && decisions == 3, "actual native action callback requires real player and network context");
                window.Hide(); window.Hide(); Check(InputCount() == baseline, "native UI cleanup preserves shared input counter");
                return "WorldCharacters administration native UI PASS; " + checks + " checks (isolated menu; no requests or player saves modified).";
            }
            finally { window.Hide(); }
        }
        private static void CheckLease()
        {
            int count = 3;
            Type type = typeof(AdministrationWindow).Assembly.GetType("ValheimModPack.WorldCharacters.AdministrationInputLease", true);
            object lease = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Action<bool>) }, null)
                .Invoke(new object[] { new Action<bool>(value => count += value ? 1 : -1) });
            Call(lease, "Acquire"); Call(lease, "Acquire"); Check(count == 4 && Held(lease), "administration input lease acquires only one increment");
            Call(lease, "Release"); Call(lease, "Release"); Check(count == 3 && !Held(lease), "administration input lease releases only its own increment");
            Call(lease, "Acquire"); count = 0; Call(lease, "ForgetAfterGlobalReset"); count = 1; Call(lease, "Release");
            Check(count == 1, "post-reset administration cleanup preserves another window's new input lease");
        }
    }
}
