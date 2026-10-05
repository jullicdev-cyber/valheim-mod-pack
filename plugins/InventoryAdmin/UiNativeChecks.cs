// Optional menu-only checks; excluded from the released plugin.
using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ValheimModPack.InventoryAdmin
{
    public static class InventoryAdminUiNativeChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Inventory Admin UI: " + message); checks++; }
        private static object Get(object instance, string name)
        { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
        private static void Call(object instance, string method, params object[] arguments)
        { instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, arguments); }
        private static void Set(object instance, string name, object value)
        { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
        private static int InputCount()
        { return (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
        private static bool LeaseHeld(AdminWindow window)
        {
            object lease = Get(window, "inputLease");
            return (bool)lease.GetType().GetProperty("Held", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lease, null);
        }
        private static string InputState(AdminWindow window, int baseline)
        { return " (baseline=" + baseline + ", counter=" + InputCount() + ", Held=" + LeaseHeld(window) + ", scene=" + SceneManager.GetActiveScene().name + ")"; }
        public static string Run()
        {
            if (Player.m_localPlayer != null) return "SKIP: Inventory Admin UI probe requires isolated menu.";
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null)
                return "SKIP: Inventory Admin native UI requires a graphical Jotunn canvas.";
            checks = 0; CheckLease();
            bool host = true, allowed = true, russian = true, tracking = true;
            string shortcut = "Ctrl+F9", mapShortcut = "Ctrl+F10";
            long requested = 0; int requestPlayers = 0, takeCalls = 0, deleteCalls = 0, roles = 0;
            long locatedPeer = 0, queriedMapPeer = 0; int trackingCalls = 0, locateCalls = 0;
            double locationTime = 1;
            var locationCache = new PlayerLocationCache();
            string locationToken = Guid.NewGuid().ToString("N");
            locationCache.SetContext(99, locationToken); locationCache.SetAccess(true, true);
            var locationFrame = new List<PlayerLocation> { new PlayerLocation { PeerId = 2, CharacterId = 102, Name = "Player <1>", X = 10, Y = 20, Z = 30 } };
            string takenSnapshot = null, takenItem = null; int takenQuantity = 0;
            AdminWindow window = null;
            int baseline = InputCount();
            var bindings = new AdminUiBindings
            {
                CanUse = () => allowed, IsHost = () => host, LocalPeerId = () => 1,
                ShortcutLabel = () => shortcut, Translate = (ru, en) => russian ? ru : en,
                IsTrackingPlayers = () => tracking, TrackingShortcutLabel = () => mapShortcut,
                SetTrackingPlayers = value => { trackingCalls++; tracking = value; locationCache.SetAccess(allowed, tracking); },
                CanFindPlayerOnMap = peer =>
                {
                    queriedMapPeer = peer;
                    if (!allowed || !tracking) return false;
                    foreach (var location in locationCache.Snapshot(locationTime)) if (location.PeerId == peer) return true;
                    return false;
                },
                FindPlayerOnMap = peer =>
                {
                    locateCalls++; locatedPeer = peer;
                    Check(!window.IsVisible && Get(window, "snapshot") == null && (long)Get(window, "selectedPeer") == 0,
                        "map action receives the peer only after inventory modal and selection are closed");
                    Check(!LeaseHeld(window) && InputCount() == baseline,
                        "map action starts after the modal has released its input lease" + InputState(window, baseline));
                },
                RequestPlayers = () => requestPlayers++, RequestInventory = peer => requested = peer,
                Take = (version, item, count) => { takeCalls++; takenSnapshot = version; takenItem = item; takenQuantity = count; },
                Delete = (version, item, count) => deleteCalls++, SetAdmin = (peer, value) => roles++,
                Error = error => { throw new InvalidOperationException("Unexpected Inventory Admin fixture error", error); }
            };
            window = new AdminWindow(bindings);
            try
            {
                Call(window, "BuildVisuals");
                Check(window.IsVisible, "isolated native wood panel is visible");
                var panel = (GameObject)Get(window, "panel");
                var rect = panel.GetComponent<RectTransform>();
                Check(rect.rect.width >= 1129 && rect.rect.height >= 859, "native modal includes space for map controls");
                Check(((Text)Get(window, "subtitle")).text.Contains("Ctrl+F9"), "actual shortcut is visible");
                var mapToggle = (Toggle)Get(window, "showPlayersOnMap");
                var mapLabel = (Text)Get(window, "mapTrackingLabel");
                var findOnMap = (Button)Get(window, "findSelectedOnMap");
                Check(mapToggle.gameObject.activeInHierarchy && mapToggle.gameObject.name == "InventoryAdmin.ShowPlayersOnMap"
                    && mapToggle.isOn && mapToggle.interactable, "native tracking checkbox reflects enabled administrator access");
                Check(mapLabel.text.Contains("Показывать игроков на карте") && mapLabel.text.Contains("включая скрытых")
                    && mapLabel.text.Contains("Ctrl+F10") && !mapLabel.supportRichText, "Russian tracking caption shows its actual shortcut as plain text");
                Check(findOnMap.gameObject.activeInHierarchy && findOnMap.gameObject.name == "InventoryAdmin.FindSelectedOnMap"
                    && findOnMap.GetComponentInChildren<Text>().text == "Найти игрока на карте" && !findOnMap.interactable,
                    "native find button is visible and unavailable before a player is selected");
                var checkboxRect = mapToggle.transform.Find("Background").GetComponent<RectTransform>();
                Check(checkboxRect.rect.width >= 27 && checkboxRect.rect.width <= 29 && checkboxRect.rect.height >= 27 && checkboxRect.rect.height <= 29,
                    "native checkbox retains readable checkmark geometry rather than stretching across its label");
                foreach (var control in new[] { mapToggle.GetComponent<RectTransform>(), findOnMap.GetComponent<RectTransform>(), ((Text)Get(window, "status")).rectTransform })
                    Check(control.anchoredPosition.y - control.rect.height / 2 >= -rect.rect.height / 2,
                        "map control or status footer remains inside the native wood panel");
                var online = new List<AdminPlayerView>();
                for (int i = 0; i < 9; i++) online.Add(new AdminPlayerView { PeerId = i + 1, Name = "Player <" + i + ">", IsAdmin = i == 2 });
                window.SetPlayers(online); Call(window, "SelectPlayer", 1);
                Check(requested == 2, "selecting online player requests that exact peer");
                Check(queriedMapPeer == 2 && !findOnMap.interactable, "map lookup checks selected peer and waits for fresh coordinates");
                Call(window, "FindSelectedOnMap");
                Check(locateCalls == 0 && window.IsVisible, "missing coordinates cannot close modal or invoke map action");
                Call(window, "SetMapTracking", false);
                Check(!tracking && !mapToggle.isOn && trackingCalls == 1 && !findOnMap.interactable, "tracking handler forwards off and repaints checkbox state");
                Call(window, "SetMapTracking", true);
                Check(tracking && mapToggle.isOn && trackingCalls == 2 && !findOnMap.interactable, "tracking handler forwards on without inventing a location");
                Check(locationCache.TryReplace(99, locationToken, 1, locationFrame, locationTime), "map UI fixture accepts a real fresh location frame");
                Call(window, "Repaint"); Call(window, "Repaint");
                Check(findOnMap.interactable && queriedMapPeer == 2 && trackingCalls == 2,
                    "fresh selected player enables find and repaint does not trigger native toggle callbacks");
                locationTime = 9; Call(window, "Repaint"); Call(window, "FindSelectedOnMap");
                Check(!findOnMap.interactable && locateCalls == 0 && window.IsVisible, "eight-second location expiry disables find and rejects direct action");
                Check(locationCache.TryReplace(99, locationToken, 2, locationFrame, locationTime), "map UI fixture accepts a newer replacement frame");
                Call(window, "SelectPlayer", 2); Call(window, "FindSelectedOnMap");
                Check(queriedMapPeer == 3 && !findOnMap.interactable && locateCalls == 0, "fresh coordinates for another peer do not enable selected-player find");
                Call(window, "SelectPlayer", 1); Check(findOnMap.interactable, "selecting the peer with current coordinates restores find");
                mapShortcut = "Alt+F11"; Call(window, "Repaint");
                Check(mapLabel.text.Contains("Alt+F11") && !mapLabel.text.Contains("Ctrl+F10"), "tracking shortcut rebind replaces the displayed assignment");
                mapShortcut = ""; Call(window, "Repaint");
                Check(!mapLabel.text.Contains("Alt+F11") && trackingCalls == 2, "unbound tracking shortcut clears obsolete hint without changing tracking");
                var ironPrefab = ObjectDB.instance == null ? null : ObjectDB.instance.GetItemPrefab("Iron");
                var drop = ironPrefab == null ? null : ironPrefab.GetComponent<ItemDrop>();
                Sprite icon = drop == null ? null : drop.m_itemData.GetIcon();
                Check(icon != null, "fixture uses actual native Iron inventory sprite");
                var snapshot = new AdminInventoryView { PeerId = 2, Name = "Player <1>", SnapshotToken = "view-v1" };
                for (int i = 0; i < 9; i++) snapshot.Items.Add(new AdminItemView
                {
                    ItemToken = "item-" + i, Name = "Iron <" + i + ">", Prefab = "Iron", Group = i == 0 ? "equipment" : "main",
                    Count = 10 + i, Quality = 2, SlotX = i % 8, SlotY = i / 8, Equipped = i == 0, Icon = icon, Durability = 45, MaxDurability = 100
                });
                window.SetSnapshot(snapshot);
                var rows = (Button[])Get(window, "itemRows"); var icons = (Image[])Get(window, "icons");
                for (int i = 0; i < rows.Length; i++)
                {
                    Check(rows[i].interactable, "populated item row is selectable");
                    Check(icons[i].enabled && icons[i].sprite == icon && icons[i].preserveAspect, "row shows native inventory icon");
                    var caption = rows[i].GetComponentInChildren<Text>();
                    Check(!caption.supportRichText && caption.text.Contains("<" + i + ">"), "item names render plain text");
                }
                Call(window, "SelectItem", 0);
                var quantity = (InputField)Get(window, "quantity");
                var take = (Button)Get(window, "take"); var delete = (Button)Get(window, "delete");
                Check(quantity.text == "10" && take.interactable && delete.interactable, "selection defaults to stack size and enables actions");
                Check(((Text)Get(window, "details")).text.Contains("Надетый"), "equipped item consequence appears before action");
                foreach (string invalid in new[] { "", "0", "-1", "11", "2147483647" })
                { quantity.text = invalid; Call(window, "UpdateActions"); Check(!take.interactable && !delete.interactable, "invalid quantity disables both actions: " + invalid); }
                quantity.text = "3"; Call(window, "TakeSelected");
                Check(takeCalls == 1 && takenSnapshot == "view-v1" && takenItem == "item-0" && takenQuantity == 3, "take returns exact snapshot/item token and partial count");
                Call(window, "ConfirmDelete");
                var confirmation = (GameObject)Get(window, "confirmation");
                Check(confirmation != null && confirmation.activeInHierarchy && deleteCalls == 0, "delete opens a separate confirmation and performs no immediate mutation");
                Check(confirmation.GetComponentInChildren<Text>().text.Contains("× 3"), "confirmation shows chosen quantity");
                var oldConfirmButtons = confirmation.GetComponentsInChildren<Button>();
                Call(window, "CancelConfirmation"); Call(window, "CancelConfirmation");
                Check(Get(window, "confirmation") == null && deleteCalls == 0, "cancel is idempotent and cannot delete");
                Call(window, "ConfirmDelete"); var newConfirmation = Get(window, "confirmation");
                foreach (var old in oldConfirmButtons) old.onClick.Invoke();
                Check(ReferenceEquals(Get(window, "confirmation"), newConfirmation) && window.IsVisible && deleteCalls == 0,
                    "callbacks from destroyed confirmation cannot affect a newer dialog");
                Call(window, "CancelConfirmation");
                window.SetBusy(true); Check(!take.interactable && !delete.interactable && !rows[0].interactable, "busy transaction disables item and mutation controls");
                Call(window, "TakeSelected"); Check(takeCalls == 1, "direct duplicate take blocked while busy");
                Call(window, "SetMapTracking", false); Call(window, "FindSelectedOnMap");
                Check(!mapToggle.interactable && !findOnMap.interactable && tracking && trackingCalls == 2 && locateCalls == 0,
                    "active transaction disables and rejects tracking changes and map navigation");
                window.SetBusy(false);
                var wrong = new AdminInventoryView { PeerId = 3, SnapshotToken = "wrong" }; window.SetSnapshot(wrong);
                Check(ReferenceEquals(Get(window, "snapshot"), snapshot), "another player's delayed snapshot is ignored");
                var refreshed = new AdminInventoryView { PeerId = 2, Name = "Player <1>", SnapshotToken = "view-v2" };
                refreshed.Items.Add(snapshot.Items[0]); window.SetSnapshot(refreshed);
                Check(Get(window, "selectedItem") == null && !take.interactable && !delete.interactable, "changed revision clears actionable selection");
                Call(window, "SelectItem", 0); Call(window, "ConfirmDelete");
                window.SetSnapshot(new AdminInventoryView { PeerId = 2, Name = "Player <1>", SnapshotToken = "view-v3" });
                Check(Get(window, "confirmation") == null && deleteCalls == 0, "refresh invalidates pending deletion confirmation");
                Set(window, "snapshot", snapshot); Set(window, "itemPage", 1); Call(window, "Repaint");
                Check(rows[2].interactable && !rows[3].interactable && !icons[3].enabled && icons[3].sprite == null, "final page clears obsolete rows and icons");
                shortcut = "Alt+F10"; Call(window, "Repaint"); Check(((Text)Get(window, "subtitle")).text.Contains(shortcut), "shortcut rebind updates visible hint");
                shortcut = ""; Call(window, "Repaint"); Check(((Text)Get(window, "subtitle")).text.Contains("Клавиша не назначена"), "unbound shortcut is explicit");
                russian = false; Call(window, "Repaint"); Check(((Text)Get(window, "heading")).text == "Player inventories", "language switch updates window title");
                Check(((Text)Get(window, "quantityHeading")).text == "Quantity", "language switch updates quantity caption");
                Check(((Text)Get(window, "subtitle")).text.Contains("Unbound"), "English unbound hint localizes");
                Check(mapLabel.text == "Show players on the map, including hidden players"
                    && findOnMap.GetComponentInChildren<Text>().text == "Find player on map", "language switch localizes both native map controls");
                host = false; Call(window, "Repaint");
                Check(!((Button)Get(window, "grant")).gameObject.activeSelf && !((Button)Get(window, "revoke")).gameObject.activeSelf, "delegated admin has no role management buttons");
                Check(mapToggle.gameObject.activeInHierarchy && mapToggle.interactable && findOnMap.interactable,
                    "authorized delegated administrator retains private map controls");
                Call(window, "ChangeRole", true); Check(roles == 0, "delegated admin cannot invoke role assignment");
                host = true; Call(window, "Repaint");
                Check(((Button)Get(window, "grant")).gameObject.activeSelf, "host role buttons restore");
                Call(window, "SelectPlayer", 0); Call(window, "ChangeRole", true); Check(roles == 0, "host cannot assign itself a redundant admin role");
                window.SetSnapshot(new AdminInventoryView { PeerId = 1, Name = "Self", SnapshotToken = "self" });
                ((AdminInventoryView)Get(window, "snapshot")).Items.Add(snapshot.Items[0]); Call(window, "SelectItem", 0);
                Check(!take.interactable && delete.interactable, "self take is disabled but explicit removal remains available");
                Call(window, "SelectPlayer", 1); window.SetSnapshot(snapshot); Call(window, "SelectItem", 0);
                online.RemoveAll(p => p.PeerId == 2); window.SetPlayers(online);
                Check(Get(window, "snapshot") == null && Get(window, "selectedItem") == null && !take.interactable, "disconnect removes stale inventory selection");
                Call(window, "FindSelectedOnMap");
                Check(!findOnMap.interactable && locateCalls == 0, "disconnected selection cannot navigate using cached coordinates");
                window.SetBusy(true); Call(window, "Refresh"); Check(requestPlayers == 0, "refresh cannot overlap active transaction"); window.SetBusy(false);
                allowed = false; Call(window, "UpdateActions"); Check(!take.interactable && !delete.interactable, "lost permission disables mutations before next Tick");
                Call(window, "Repaint"); Call(window, "SetMapTracking", false); Call(window, "FindSelectedOnMap");
                Check(!mapToggle.interactable && !findOnMap.interactable && trackingCalls == 2 && locateCalls == 0,
                    "permission revocation disables map controls and blocks their direct handlers");
                Check(((Button)Get(window, "take")).GetComponent<AdminItemDrop>() != null, "receiving button exposes real Unity drop handler");
                Check(rows[0].GetComponent<AdminItemDrag>() != null, "inventory row exposes real Unity drag handlers");
                Check(deleteCalls == 0, "UI construction and refresh never delete anything");
                allowed = true; online.Add(new AdminPlayerView { PeerId = 2, Name = "Map target" }); window.SetPlayers(online);
                Set(window, "selectedPeer", 2L); Call(window, "Repaint");
                Call(Get(window, "inputLease"), "Acquire");
                // Jotunn intentionally ignores BlockInput in the isolated start
                // scene. The lease still records ownership and must release it
                // before Find; CheckLease covers counted callbacks separately.
                Check(LeaseHeld(window) && InputCount() == baseline,
                    "positive map handler records lease ownership without changing the menu input counter" + InputState(window, baseline));
                Call(window, "FindSelectedOnMap");
                Check(locateCalls == 1 && locatedPeer == 2 && !window.IsVisible, "find action forwards exactly the selected peer and closes inventory modal");
                Call(window, "BuildVisuals");
                mapToggle.onValueChanged.Invoke(false); findOnMap.onClick.Invoke();
                Check(window.IsVisible && tracking && trackingCalls == 2 && locateCalls == 1,
                    "destroyed map controls cannot change tracking or navigate a newer modal");
                var currentMapToggle = (Toggle)Get(window, "showPlayersOnMap");
                currentMapToggle.onValueChanged.Invoke(false);
                Check(!window.IsVisible && trackingCalls == 2 && tracking, "current tracking callback requires an actual player and network context");
                Call(window, "BuildVisuals"); window.SetPlayers(online); Set(window, "selectedPeer", 2L); Call(window, "Repaint");
                var currentFindOnMap = (Button)Get(window, "findSelectedOnMap");
                Check(currentFindOnMap.interactable, "fresh selected peer fixture is ready before missing-context callback test");
                currentFindOnMap.onClick.Invoke();
                Check(!window.IsVisible && locateCalls == 1, "current find callback rejects menu context even when coordinates and selection are valid");
                window.Hide(); window.Hide(); Check(!window.IsVisible, "close is idempotent");
                Check(!LeaseHeld(window) && InputCount() == baseline,
                    "isolated construction and cleanup leave shared input counter unchanged" + InputState(window, baseline));
                return "InventoryAdmin native UI PASS; " + checks + " checks (isolated menu, no world or player inventories modified).";
            }
            finally { window.Hide(); }
        }
        private static void CheckLease()
        {
            int count = 3;
            Type type = typeof(AdminWindow).Assembly.GetType("ValheimModPack.InventoryAdmin.AdminInputLease", true);
            var constructor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Action<bool>) }, null);
            object lease = constructor.Invoke(new object[] { new Action<bool>(value => count += value ? 1 : -1) });
            Call(lease, "Acquire"); Call(lease, "Acquire"); Check(count == 4, "input lease acquires one shared count");
            Call(lease, "Release"); Call(lease, "Release"); Check(count == 3, "input lease releases only its own count");
            Call(lease, "Acquire"); count = 0; Call(lease, "ForgetAfterGlobalReset"); count = 1;
            Call(lease, "Release"); Check(count == 1, "a global reset cannot make late cleanup subtract another window's new request");
            count = 3;
            bool fail = true;
            object failing = constructor.Invoke(new object[] { new Action<bool>(value => { count += value ? 1 : -1; if (value && fail) { fail = false; throw new InvalidOperationException("fixture"); } }) });
            try { Call(failing, "Acquire"); } catch (TargetInvocationException) { }
            Call(failing, "Release"); Check(count == 3, "input acquisition exception releases owned count");
        }
    }
}
