using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    // A request belongs to one player, world and live map instance. Our own
    // input lease is deliberately excluded from ongoing context validation.
    internal sealed class MapActionContext
    {
        internal readonly Minimap Map;
        private readonly Player player;
        private readonly ZNet network;
        private readonly long world, character;
        private MapActionContext(Minimap map, Player owner, ZNet net)
        { Map = map; player = owner; network = net; world = net.GetWorldUID(); character = owner.GetPlayerID(); }

        internal static MapActionContext Capture(Minimap map)
        {
            var owner = Player.m_localPlayer; var net = ZNet.instance;
            if (!BaseContext(map, owner) || net == null || net.GetWorldUID() == 0 || owner.GetPlayerID() == 0) return null;
            return new MapActionContext(map, owner, net);
        }
        internal bool Current()
        {
            return ReferenceEquals(network, ZNet.instance) && ReferenceEquals(player, Player.m_localPlayer)
                && network != null && network.GetWorldUID() == world && player.GetPlayerID() == character
                && BaseContext(Map, player);
        }
        internal static bool BaseContext(Minimap map, Player owner)
        {
            return map != null && ReferenceEquals(map, Minimap.instance) && map.m_mode == Minimap.MapMode.Large
                && ReferenceEquals(owner, Player.m_localPlayer) && PinHistoryController.SafePlayer(owner)
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !global::Console.IsVisible() && !InventoryGui.IsVisible()
                && (Chat.instance == null || !Chat.instance.HasFocus())
                && (TextInput.instance == null || TextInput.instance.m_panel == null || !TextInput.instance.m_panel.activeInHierarchy);
        }
    }

    public sealed class PinActionController : IDisposable
    {
        private readonly FieldInfo pins;
        private readonly Func<Minimap.PinData, string> displayName;
        private readonly Action<Minimap.PinData> rename, requestDelete;
        private readonly Action<Exception> report;
        private readonly PinActionMenuView view;
        private MapActionContext context;
        private Minimap.PinData target;
        private int generation;
        private bool disposed;
        public bool IsOpen { get { return target != null; } }

        public PinActionController(FieldInfo pins, Func<Minimap.PinData, string> displayName,
            Action<Minimap.PinData> rename, Action<Minimap.PinData> requestDelete, Action<Exception> report)
        {
            if (pins == null || displayName == null || rename == null || requestDelete == null || report == null)
                throw new ArgumentNullException();
            this.pins = pins; this.displayName = displayName; this.rename = rename; this.requestDelete = requestDelete; this.report = report;
            view = new PinActionMenuView(Text);
        }
        public void Show(Minimap map, Minimap.PinData pin)
        {
            if (disposed || IsOpen || TextInput.IsVisible()) return;
            try
            {
                var requested = MapActionContext.Capture(map);
                if (requested == null || pin == null || !pin.m_save || !CurrentPins(map).Contains(pin)) return;
                context = requested; target = pin; int request = ++generation;
                view.Show(displayName(pin), () => Choose(request, requested, pin, rename),
                    () => Choose(request, requested, pin, requestDelete), () => { if (request == generation) Close(); });
            }
            catch (Exception error) { Close(); Report(error); }
        }
        private List<Minimap.PinData> CurrentPins(Minimap map)
        { return (List<Minimap.PinData>)pins.GetValue(map); }
        private bool Valid(MapActionContext requested, Minimap.PinData pin)
        { return requested != null && requested.Current() && pin != null && pin.m_save && CurrentPins(requested.Map).Contains(pin); }
        private void Choose(int request, MapActionContext requested, Minimap.PinData pin, Action<Minimap.PinData> action)
        {
            if (request != generation || !ReferenceEquals(context, requested) || !ReferenceEquals(target, pin)) return;
            try
            {
                bool valid = Valid(requested, pin);
                // Release our lease before the next modal acquires its own one.
                CloseCore();
                if (valid && !disposed && Valid(requested, pin)) action(pin);
            }
            catch (Exception error) { Close(); Report(error); }
        }
        public void Tick(bool allowed)
        {
            if (!IsOpen) return;
            try
            {
                if (!allowed || !view.IsVisible || !Valid(context, target)
                    || Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) Close();
            }
            catch (Exception error) { Close(); Report(error); }
        }
        private void CloseCore()
        { ++generation; context = null; target = null; view.Hide(); }
        public void Close()
        { try { CloseCore(); } catch (Exception error) { Report(error); } }
        private void Report(Exception error) { try { report(error); } catch { } }
        private static string Text(string key)
        {
            bool ru = Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
            switch (key)
            {
                case "action_title": return ru ? "Действия с меткой" : "Map pin actions";
                case "action_rename": return ru ? "Переименовать" : "Rename";
                case "action_delete": return ru ? "Удалить" : "Delete";
                case "action_cancel": return ru ? "Отмена" : "Cancel";
                default: return key ?? "";
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; Close();
            try { view.Dispose(); } catch (Exception error) { Report(error); }
        }
    }
}
