// Optional menu-only checks; excluded from the released plugin.
using System;
using System.Collections.Generic;
using System.Reflection;
using Jotunn.Managers;
using UnityEngine;
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
        public static string Run()
        {
            if (Player.m_localPlayer != null) return "SKIP: Inventory Admin UI probe requires isolated menu.";
            if (GUIManager.CustomGUIFront == null || GUIManager.Instance.AveriaSerif == null)
                return "SKIP: Inventory Admin native UI requires a graphical Jotunn canvas.";
            checks = 0; CheckLease();
            bool host = true, allowed = true, russian = true;
            string shortcut = "Ctrl+F9";
            long requested = 0; int requestPlayers = 0, takeCalls = 0, deleteCalls = 0, roles = 0;
            string takenSnapshot = null, takenItem = null; int takenQuantity = 0;
            var bindings = new AdminUiBindings
            {
                CanUse = () => allowed, IsHost = () => host, LocalPeerId = () => 1,
                ShortcutLabel = () => shortcut, Translate = (ru, en) => russian ? ru : en,
                RequestPlayers = () => requestPlayers++, RequestInventory = peer => requested = peer,
                Take = (version, item, count) => { takeCalls++; takenSnapshot = version; takenItem = item; takenQuantity = count; },
                Delete = (version, item, count) => deleteCalls++, SetAdmin = (peer, value) => roles++,
                Error = error => { throw new InvalidOperationException("Unexpected Inventory Admin fixture error", error); }
            };
            var window = new AdminWindow(bindings);
            int baseline = (int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            try
            {
                Call(window, "BuildVisuals");
                Check(window.IsVisible, "isolated native wood panel is visible");
                var panel = (GameObject)Get(window, "panel");
                var rect = panel.GetComponent<RectTransform>();
                Check(rect.rect.width >= 1129 && rect.rect.height >= 779, "native modal has requested dimensions");
                Check(((Text)Get(window, "subtitle")).text.Contains("Ctrl+F9"), "actual shortcut is visible");
                var online = new List<AdminPlayerView>();
                for (int i = 0; i < 9; i++) online.Add(new AdminPlayerView { PeerId = i + 1, Name = "Player <" + i + ">", IsAdmin = i == 2 });
                window.SetPlayers(online); Call(window, "SelectPlayer", 1);
                Check(requested == 2, "selecting online player requests that exact peer");
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
                host = false; Call(window, "Repaint");
                Check(!((Button)Get(window, "grant")).gameObject.activeSelf && !((Button)Get(window, "revoke")).gameObject.activeSelf, "delegated admin has no role management buttons");
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
                window.SetBusy(true); Call(window, "Refresh"); Check(requestPlayers == 0, "refresh cannot overlap active transaction"); window.SetBusy(false);
                allowed = false; Call(window, "UpdateActions"); Check(!take.interactable && !delete.interactable, "lost permission disables mutations before next Tick");
                Check(((Button)Get(window, "take")).GetComponent<AdminItemDrop>() != null, "receiving button exposes real Unity drop handler");
                Check(rows[0].GetComponent<AdminItemDrag>() != null, "inventory row exposes real Unity drag handlers");
                Check(deleteCalls == 0, "UI construction and refresh never delete anything");
                window.Hide(); window.Hide(); Check(!window.IsVisible, "close is idempotent");
                Check((int)typeof(GUIManager).GetField("InputBlockRequests", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == baseline,
                    "isolated construction and cleanup leave shared input counter unchanged");
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
