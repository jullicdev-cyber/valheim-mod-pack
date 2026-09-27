using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimModPack.NordicRadio;

internal static class PortableControllerTests
{
    private static int assertions;
    private static readonly MethodInfo use = typeof(PortableController).GetMethod("BeforeUseItem", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo attack = typeof(PortableController).GetMethod("BeforeAttack", BindingFlags.Static | BindingFlags.NonPublic);
    private static void Check(bool success, string label)
    {
        ++assertions;
        if (!success) throw new Exception("FAIL: " + label);
    }
    private static ItemDrop.ItemData Item(string prefab)
    { return new ItemDrop.ItemData { m_dropPrefab = new GameObject(prefab) }; }
    private static string Token(ItemDrop.ItemData item)
    {
        string token;
        return item.m_customData != null && item.m_customData.TryGetValue(PortableController.ItemToken, out token) ? token : "";
    }
    private static bool Use(Humanoid player, Inventory inventory, ItemDrop.ItemData item)
    { return (bool)use.Invoke(null, new object[] { player, inventory, item }); }
    private static bool Attack(Humanoid player, ItemDrop.ItemData item, out bool result)
    {
        object[] arguments = { player, item, true };
        bool runOriginal = (bool)attack.Invoke(null, arguments);
        result = (bool)arguments[2]; return runOriginal;
    }
    private static Player NewPlayer(long user, uint id, bool owned)
    {
        Player player = new Player { View = new ZNetView { State = new ZDO { m_uid = new ZDOID(user, id), Owned = owned } } };
        Player.Players.Add(player); return player;
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Player Player;
        public readonly ItemDrop.ItemData Idol;
        public readonly Plugin Plugin;
        public readonly PortableController Controller;
        public Fixture()
        {
            UnityEngine.Time.frameCount = 100; UnityEngine.Time.unscaledTime = 10f;
            global::Player.Players.Clear();
            InventoryGui.instance = new InventoryGui(); InventoryGui.Visible = false;
            Menu.Visible = UnifiedPopup.Visible = false;
            TextInput.Visible = Console.Visible = UnityEngine.Input.Escape = ZInput.Cancel = false;
            Chat.instance = new Chat();
            Localization.instance = new Localization();
            Player = NewPlayer(1, 10, true); global::Player.m_localPlayer = Player;
            Idol = Item(PortableModel.PrefabName); Player.Inventory.Items.Add(Idol);
            Plugin = new Plugin(); ValheimModPack.NordicRadio.Plugin.Instance = Plugin;
            Controller = new PortableController(Plugin); Plugin.Portable = Controller;
            Controller.Tick();
        }
        public void Activate() { Use(Player, Player.Inventory, Idol); }
        public void Advance(float seconds)
        { UnityEngine.Time.unscaledTime += seconds; ++UnityEngine.Time.frameCount; Controller.Tick(); }
        public string Marker { get { return Player.View.State.GetString(PortableController.Marker, ""); } }
        public ServiceCall LastCall { get { return Plugin.Service.Calls[Plugin.Service.Calls.Count - 1]; } }
        public IRadioTarget Target { get { return Plugin.Attached[0]; } }
        public void Dispose() { Controller.Dispose(); ValheimModPack.NordicRadio.Plugin.Instance = null; global::Player.m_localPlayer = null; }
    }
    private static void OrdinaryItemsRemainVanilla()
    {
        using (Fixture f = new Fixture())
        {
            ItemDrop.ItemData hammer = Item("Hammer"); f.Player.Inventory.Items.Add(hammer);
            Check(Use(f.Player, f.Player.Inventory, hammer), "ordinary item use reaches vanilla");
            Check(Use(f.Player, f.Player.Inventory, null), "null item use reaches vanilla");
            bool result;
            Check(Attack(f.Player, hammer, out result) && result, "ordinary attack reaches vanilla without result mutation");
            Check(f.Plugin.Service.Calls.Count == 0 && f.Player.EquipCalls == 0, "ordinary actions create no radio state");
            Player remote = NewPlayer(2, 20, false); remote.Inventory.Items.Add(f.Idol);
            Check(Use(remote, remote.Inventory, f.Idol), "remote item use never activates local controller");
            Check(Attack(remote, f.Idol, out result) && result, "remote attack is not intercepted");
            Check(f.Marker == "", "remote actions cannot publish local radio");
            Check(!PortableController.IsIdol(new ItemDrop.ItemData()), "missing drop prefab is not an idol");
        }
    }
    private static void ActivationAndSharedWindow()
    {
        using (Fixture f = new Fixture())
        {
            InventoryGui.Visible = true;
            Check(!Use(f.Player, f.Player.Inventory, f.Idol), "idol use is handled without consuming vanilla item");
            Guid token;
            Check(Token(f.Idol).Length == 32 && Guid.TryParseExact(Token(f.Idol), "N", out token), "first use assigns persistent GUID token");
            Check(f.Player.Equipped == f.Idol && f.Player.EquipCalls == 1, "idol is equipped in the hand");
            Check(f.Marker == Token(f.Idol), "owner advertises selected idol token");
            Check(f.LastCall.Id == f.Player.View.State.m_uid && f.LastCall.Token == Token(f.Idol), "service receives player identity and item token");
            Check(!InventoryGui.Visible && InventoryGui.instance.HideCalls == 1, "inventory closes before music window");
            f.Controller.Tick();
            Check(f.Plugin.Opened.Count == 0, "activation frame cannot reopen UI while closing inventory");
            f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 1 && Object.ReferenceEquals(f.Plugin.Opened[0], f.Target), "next frame opens common radio window for the discovered source");
            Check(f.Target.IsReady && f.Target.HasAccess(f.Player), "owner can control active source");
            Check(f.Target.GetHoverName() == "Идол скальда", "source uses item localization");
            Check(f.Plugin.Service.Watched.Contains(f.Target.Id), "new source requests shared radio state");
            int opens = f.Plugin.Opened.Count; f.Advance(0.5f);
            Check(f.Plugin.Opened.Count == opens, "pending window opens exactly once");
            bool result;
            Check(!Attack(f.Player, f.Idol, out result) && !result, "held idol attack opens controls without a weapon attack");
            f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == opens + 1 && f.Player.EquipCalls == 1, "held use reopens controls without re-equipping");
            Check(f.Plugin.Errors.Count == 0, "normal activation has no swallowed errors");
        }
    }
    private static void InventoryAndShipLifecycle()
    {
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f); string token = Token(f.Idol); IRadioTarget source = f.Target;
            f.Player.Equipped = null; f.Advance(1);
            Check(source.IsReady && f.Controller.HasItem(token), "holstering for ship controls keeps source alive");
            Check(f.Marker == token && f.LastCall.Token == token, "holstering does not issue stop");
            f.Player.transform.position = new Vector3(72, 3, -81);
            Vector3 position = source.SoundPosition;
            Check(Math.Abs(position.x - 72) < 0.001 && Math.Abs(position.y - 4.1f) < 0.001 && Math.Abs(position.z + 81) < 0.001,
                "sound follows moving ship passenger with chest-height offset");
            ItemDrop.ItemData cloned = Item(PortableModel.PrefabName);
            cloned.m_customData = new Dictionary<string, string>(f.Idol.m_customData);
            f.Player.Inventory.Items.Clear(); f.Player.Inventory.Items.Add(cloned); f.Advance(0.3f);
            Check(source.IsReady && f.Controller.HasItem(token), "inventory sorting clone preserves playback by stable item token");
            f.Player.Inventory.Items.Clear(); f.Controller.Tick();
            Check(f.Marker == "" && f.LastCall.Token == "", "moving item out of inventory stops and clears ownership");
            Check(!source.IsReady && !source.HasAccess(f.Player), "removed item immediately invalidates source and controls");
            f.Advance(0.3f);
            Check(f.Plugin.Attached.Count == 0 && f.Plugin.Detached.Contains(source), "removed item detaches its audio target");
            f.Player.Inventory.Items.Add(cloned); f.Advance(0.3f);
            Check(f.Plugin.Attached.Count == 0 && f.Marker == "", "returning dropped item cannot resume automatically");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f); IRadioTarget source = f.Target;
            f.Player.Dead = true; f.Advance(0.3f);
            Check(f.Marker == "" && f.LastCall.Token == "", "death sends stop and clears persisted marker");
            Check(!source.IsReady && f.Plugin.Attached.Count == 0, "death removes audible target");
            f.Player.Dead = false; f.Advance(0.3f);
            Check(f.Marker == "" && f.Plugin.Attached.Count == 0, "respawn cannot restart old playback");
        }
    }
    private static void SwitchingAndTransferring()
    {
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f); string oldToken = Token(f.Idol); IRadioTarget old = f.Target;
            ItemDrop.ItemData second = Item(PortableModel.PrefabName); f.Player.Inventory.Items.Add(second);
            int start = f.Plugin.Service.Calls.Count;
            Use(f.Player, f.Player.Inventory, second);
            Check(Token(second) != oldToken && f.Marker == Token(second), "second idol selects a distinct stable identity");
            Check(f.Plugin.Service.Calls[start].Token == "" && f.Plugin.Service.Calls[start + 1].Token == Token(second), "switching first stops previous idol then selects next");
            Check(!old.IsReady && !f.Controller.HasItem(oldToken), "former idol immediately loses controls while remaining in inventory");
            f.Advance(0.01f);
            Check(f.Plugin.Attached.Count == 1 && !Object.ReferenceEquals(old, f.Target), "one fresh audio target remains after switching");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f); string originalToken = Token(f.Idol);
            Player receiver = NewPlayer(2, 20, true);
            f.Player.Inventory.Items.Clear(); receiver.Inventory.Items.Add(f.Idol);
            Player.m_localPlayer = receiver; f.Advance(0.3f);
            Check(f.Marker == "" && receiver.View.State.GetString(PortableController.Marker, "") == "", "owner handover clears both old and joined player markers");
            Check(f.Plugin.Attached.Count == 0 && !f.Controller.HasItem(originalToken), "transferred item does not autoplay on a new owner");
            Use(receiver, receiver.Inventory, f.Idol); f.Advance(0.01f);
            Check(Token(f.Idol) == originalToken && f.LastCall.Id == receiver.View.State.m_uid, "explicit new-owner activation preserves item token under new player identity");
            Check(f.Plugin.Attached.Count == 1 && f.Target.HasAccess(receiver) && !f.Target.HasAccess(f.Player), "only new owner can control transferred item");
        }
    }
    private static void RemoteDiscoveryAndCleanup()
    {
        using (Fixture f = new Fixture())
        {
            Player remote = NewPlayer(2, 20, false);
            remote.View.State.Set(PortableController.Marker, "0123456789abcdef0123456789abcdef");
            f.Advance(0.3f); IRadioTarget target = f.Target;
            Check(target.IsReady && f.Plugin.Attached.Count == 1, "nearby remote player marker discovers audio source");
            Check(!target.HasAccess(f.Player) && !target.HasAccess(remote), "listener cannot control another player's idol");
            remote.transform.position = new Vector3(-15, 7, 3);
            Check(Math.Abs(target.SoundPosition.x + 15) < 0.001 && Math.Abs(target.SoundPosition.y - 8.1f) < 0.001, "remote positional source follows its owner");
            remote.View.State.Set(PortableController.Marker, ""); f.Advance(0.3f);
            Check(!target.IsReady && f.Plugin.Attached.Count == 0, "remote stop marker removes source");
            remote.View.State.Set(PortableController.Marker, "invalid"); f.Advance(0.3f);
            Check(f.Plugin.Attached.Count == 0, "malformed remote marker cannot create audio source");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f);
            Player leavingRemote = NewPlayer(2, 20, false);
            leavingRemote.View.State.Set(PortableController.Marker, "0123456789abcdef0123456789abcdef");
            f.Advance(0.3f);
            Player.m_localPlayer = null; f.Advance(0.3f);
            Check(f.Marker == "" && f.LastCall.Token == "", "world exit stops local service identity");
            Check(f.Plugin.Attached.Count == 0, "world exit releases all targets even before remote player objects finish unloading");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); f.Advance(0.01f); string saved = Token(f.Idol);
            f.Controller.Reset();
            Check(f.Marker == "" && f.Plugin.Attached.Count == 0 && !f.Controller.HasItem(saved), "session reset clears marker, sources and selected inventory identity");
            f.Advance(0.3f);
            Check(f.Plugin.Attached.Count == 0, "session reset does not reactivate owned item");
            f.Activate(); f.Advance(0.01f); int unpatches = Harmony.UnpatchCount;
            f.Controller.Dispose(); f.Controller.Dispose();
            Check(f.Marker == "" && f.Plugin.Attached.Count == 0 && f.LastCall.Token == "", "dispose releases active ownership and sources");
            Check(Harmony.UnpatchCount == unpatches + 1, "dispose unpatches exactly once");
            int calls = f.Plugin.Service.Calls.Count; f.Controller.Tick();
            Check(f.Plugin.Service.Calls.Count == calls, "disposed controller cannot publish state again");
        }
    }
    private static void GuardsAndDeferredInput()
    {
        using (Fixture f = new Fixture())
        {
            f.Player.Inventory.Items.Clear(); Use(f.Player, new Inventory(), f.Idol);
            Check(f.Marker == "" && f.Player.EquipCalls == 0, "item outside main inventory cannot be activated from a chest");
            f.Player.Inventory.Items.Add(f.Idol); f.Player.RejectEquip = true; f.Activate();
            Check(f.Marker == "" && f.Plugin.Service.Calls.Count == 0, "failed equip does not begin radio activation");
            f.Player.RejectEquip = false; f.Player.View.State.Owned = false; f.Activate();
            Check(f.Marker == "" && f.Plugin.Service.Calls.Count == 0, "non-owned local state cannot advertise radio");
            f.Player.View.State.Owned = true; f.Player.View.Valid = false; f.Activate();
            Check(f.Marker == "", "missing network view blocks activation");
        }
        using (Fixture f = new Fixture())
        {
            f.Player.Dead = true; f.Activate(); f.Player.Dead = false;
            f.Player.Teleporting = true; f.Activate(); f.Player.Teleporting = false;
            f.Player.Sleeping = true; f.Activate(); f.Player.Sleeping = false;
            f.Player.Cutscene = true; f.Activate(); f.Player.Cutscene = false;
            Check(f.Player.EquipCalls == 0 && f.Plugin.Service.Calls.Count == 0, "dead, teleporting, sleeping and cutscene players cannot activate");
            f.Plugin.RadioWindowVisible = true; f.Activate();
            Check(f.Player.EquipCalls == 0, "already visible radio window avoids duplicate activation");
            f.Plugin.RadioWindowVisible = false;
            f.Idol.m_customData = new Dictionary<string, string> { { PortableController.ItemToken, "not-an-id" } }; f.Activate();
            Check(Token(f.Idol).Length == 32 && Token(f.Idol) != "not-an-id", "invalid persisted item identity is replaced");
        }
        using (Fixture f = new Fixture())
        {
            InventoryGui.Visible = true; InventoryGui.instance.DelayedHide = true; f.Activate(); f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "inventory closing animation blocks music window");
            InventoryGui.Visible = false; Menu.Visible = true; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "game menu blocks deferred music window");
            Menu.Visible = false; UnifiedPopup.Visible = true; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "modal popup blocks deferred music window");
            UnifiedPopup.Visible = false; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 1, "releasing input blockers opens pending shared window");
        }
        using (Fixture f = new Fixture())
        {
            InventoryGui.Visible = true; InventoryGui.instance.DelayedHide = true; f.Activate(); f.Advance(3.1f);
            InventoryGui.Visible = false; f.Advance(0.3f);
            Check(f.Plugin.Opened.Count == 0, "stale pending request cannot unexpectedly open UI after timeout");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate();
            TextInput.Visible = true; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "native text or another Jotunn window prevents pending radio stealing input");
            TextInput.Visible = false; Chat.instance.Focus = true; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "chat focus prevents pending radio stealing input");
            Chat.instance.Focus = false; Console.Visible = true; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 0, "console prevents pending radio stealing input");
            Console.Visible = false; f.Advance(0.01f);
            Check(f.Plugin.Opened.Count == 1, "pending radio opens once text input releases within deadline");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); UnityEngine.Input.Escape = true; f.Advance(0.01f);
            UnityEngine.Input.Escape = false; f.Advance(0.1f);
            Check(f.Plugin.Opened.Count == 0 && f.Controller.HasItem(Token(f.Idol)), "Escape cancels pending UI without cancelling the selected music item");
        }
        using (Fixture f = new Fixture())
        {
            f.Activate(); ZInput.Cancel = true; f.Advance(0.01f); ZInput.Cancel = false; f.Advance(0.1f);
            Check(f.Plugin.Opened.Count == 0, "controller cancel also discards pending UI");
        }
    }
    private static int Main()
    {
        try
        {
            OrdinaryItemsRemainVanilla(); ActivationAndSharedWindow(); InventoryAndShipLifecycle();
            SwitchingAndTransferring(); RemoteDiscoveryAndCleanup(); GuardsAndDeferredInput();
            System.Console.WriteLine("PASS: " + assertions + " portable-controller behavior assertions against production source.");
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
