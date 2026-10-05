// AnyPortal+ additions, 2026-10-06. GPL-3.0, see ../../LICENSE.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace XPortal.Plus
{
    internal static class PlusMapMarkers
    {
        private static readonly FieldInfo Pins = AccessTools.Field(typeof(Minimap), "m_pins");
        private static readonly MethodInfo Sprite = AccessTools.Method(typeof(Minimap), "GetSprite", new[] { typeof(Minimap.PinType) });
        private static readonly MethodInfo DestroyMarker = AccessTools.Method(typeof(Minimap), "DestroyPinMarker", new[] { typeof(Minimap.PinData) });
        private static readonly FieldInfo UpdateNeeded = AccessTools.Field(typeof(Minimap), "m_pinUpdateRequired");
        private static readonly FieldInfo PopupStack = AccessTools.Field(typeof(UnifiedPopup), "popupStack");
        private static readonly FieldInfo PopupInstance = AccessTools.Field(typeof(UnifiedPopup), "instance");
        private static Player player;
        private static Minimap map;
        private static ZNet network;
        private static long world, character;
        private static int generation;
        private static float nextTick;
        private static bool unavailable;
        private static MapPinBindingsStore store;
        private static List<MapPinBinding> bindings = new List<MapPinBinding>();
        private static PopupBase popup;
        private static PendingRefresh pending;
        private sealed class PendingRefresh
        {
            internal int Generation;
            internal long World, Character, Revision;
            internal Player Player;
            internal Minimap Map;
            internal ZNet Network;
            internal float Started;
            internal bool Retried;
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + 1f;
            if (popup != null)
            {
                Stack<PopupBase> stack = GetPopupStack();
                if (stack == null || !stack.Contains(popup)) popup = null;
            }
            bool usable = EnsureContext();
            nextTick = Time.unscaledTime + 1f; // Reset during menu/headless startup must not turn this into per-frame work.
            if (!usable || pending == null) return;
            PendingRefresh request = pending;
            if (request.Generation != generation || !ReferenceEquals(player, request.Player) || !ReferenceEquals(map, request.Map)
                || !ReferenceEquals(network, request.Network) || world != request.World || character != request.Character)
            { pending = null; return; }
            if (Time.unscaledTime < request.Started || Time.unscaledTime - request.Started >= 10f)
            {
                pending = null;
                Message(Russian ? "Не удалось обновить список порталов. Повторите очистку позже." : "Could not refresh the portal list. Try cleanup again later.");
                return;
            }
            var registry = KnownPortalsManager.Instance;
            if (registry.HasCompleteSnapshot && registry.SnapshotRevision > request.Revision)
            { pending = null; if (!UnifiedPopup.IsVisible()) ShowCleanupConfirmation(); return; }
            if (!request.Retried && Time.unscaledTime - request.Started >= 1f)
            {
                request.Retried = true;
                try { RPC.SendToServer.SyncRequest("AnyPortal+ map pin cleanup refresh retry"); }
                catch (Exception error) { pending = null; Report(error); }
            }
        }
        internal static void Reset()
        {
            CloseOwnedPopup(); ++generation; player = null; map = null; network = null; world = character = 0;
            store = null; bindings.Clear(); unavailable = false; nextTick = 0; pending = null;
        }
        private static bool EnsureContext()
        {
            Player currentPlayer = Player.m_localPlayer; Minimap currentMap = Minimap.instance; ZNet currentNet = ZNet.instance;
            long currentWorld = currentNet == null ? 0 : currentNet.GetWorldUID();
            long currentCharacter = currentPlayer == null ? 0 : currentPlayer.GetPlayerID();
            if (currentPlayer == null || currentMap == null || currentNet == null || currentWorld == 0 || currentCharacter == 0)
            { Reset(); return false; }
            if (!ReferenceEquals(player, currentPlayer) || !ReferenceEquals(map, currentMap) || !ReferenceEquals(network, currentNet)
                || world != currentWorld || character != currentCharacter)
            {
                Reset(); player = currentPlayer; map = currentMap; network = currentNet; world = currentWorld; character = currentCharacter;
                try
                {
                    string path = Path.Combine(Paths.GameRootPath, "ValheimModpack", "AnyPortalPlus", "MapPins",
                        world.ToString(CultureInfo.InvariantCulture) + "-" + character.ToString(CultureInfo.InvariantCulture) + ".dat");
                    store = new MapPinBindingsStore(path, world, character); bindings = store.Load();
                    if (Pins == null) throw new MissingFieldException("Minimap.m_pins");
                }
                catch (Exception error) { unavailable = true; Report(error); }
            }
            return !unavailable && store != null;
        }
        internal static UnityEngine.Sprite GetIconSprite(int icon)
        {
            if (icon < 0 || !MapPinBinding.ValidIcon(icon) || Minimap.instance == null || Sprite == null) return null;
            try { return (UnityEngine.Sprite)Sprite.Invoke(Minimap.instance, new object[] { (Minimap.PinType)icon }); }
            catch (Exception error) { Log.Warning("AnyPortal+ could not read a map icon: " + error.Message); return null; }
        }
        internal static bool AddOrUpdate(KnownPortal portal)
        {
            if (!EnsureContext()) return false;
            try
            {
                if (portal == null || portal.Id == ZDOID.None || !KnownPortalsManager.Instance.ContainsId(portal.Id))
                    throw new InvalidOperationException("Portal is no longer available");
                if (portal.Icon != -1 && !MapPinBinding.ValidIcon(portal.Icon)) throw new InvalidDataException("Invalid portal icon");
                int icon = portal.Icon < 0 ? (int)Minimap.PinType.Icon4 : portal.Icon;
                var desired = new MapPinBinding { PortalId = portal.Id.ToString(), Name = portal.Name ?? "", Icon = icon,
                    X = portal.Location.x, Y = portal.Location.y, Z = portal.Location.z, Owner = 0, Author = "" };
                desired.Validate();
                MapPinBinding previous = bindings.Find(b => b.PortalId == desired.PortalId);
                Minimap.PinData target = previous == null ? null : FindUnique(previous);
                bool created = false;
                if (target == null)
                {
                    // Adoption is explicit: the player pressed the map-pin button for this exact portal.
                    target = FindUnique(desired);
                    if (target == null)
                    {
                        target = map.AddPin(portal.Location, (Minimap.PinType)icon, desired.Name, true, false, 0, Splatform.PlatformUserID.None);
                        if (target == null) throw new InvalidOperationException("The game refused to create a map pin");
                        created = true;
                    }
                }
                desired.Owner = target.m_ownerID; desired.Author = AuthorOf(target);
                var replacement = CopyBindings();
                replacement.RemoveAll(b => b.PortalId == desired.PortalId || b.SamePin(desired)); replacement.Add(desired);
                try
                {
                    if (created) RememberCreated(target);
                    else if (target.m_name != desired.Name) RememberRename(target, desired.Name);
                    // Ownership is durable before changing an existing marker.
                    store.Save(replacement);
                }
                catch { if (created) map.RemovePin(target); throw; }
                bindings = replacement;
                bool changed = !Snapshot(target, desired.PortalId).SamePin(desired);
                target.m_name = desired.Name; target.m_type = (Minimap.PinType)desired.Icon; target.m_pos = portal.Location;
                if (changed)
                {
                    if (DestroyMarker != null) DestroyMarker.Invoke(map, new object[] { target });
                    target.m_NamePinData = new Minimap.PinNameData(target);
                    if (UpdateNeeded != null) UpdateNeeded.SetValue(map, true);
                }
                Message(Russian ? "Метка портала сохранена: " + desired.Name : "Portal map pin saved: " + desired.Name);
                return true;
            }
            catch (Exception error) { Report(error); return false; }
        }

        internal static void CleanupWithConfirmation()
        {
            if (!EnsureContext() || popup != null || UnifiedPopup.IsVisible() || pending != null) return;
            if (!network.IsServer())
            {
                pending = new PendingRefresh { Generation = generation, World = world, Character = character,
                    Revision = KnownPortalsManager.Instance.SnapshotRevision, Player = player, Map = map, Network = network,
                    Started = Time.unscaledTime };
                try { RPC.SendToServer.SyncRequest("AnyPortal+ map pin cleanup refresh"); }
                catch (Exception error) { pending = null; Report(error); return; }
                Message(Russian ? "Обновляю полный список порталов перед очисткой…" : "Refreshing the complete portal list before cleanup…");
                return;
            }
            ShowCleanupConfirmation();
        }
        private static void ShowCleanupConfirmation()
        {
            if (!EnsureContext() || popup != null || UnifiedPopup.IsVisible()) return;
            try
            {
                HashSet<string> live; bool complete;
                ReadLiveIds(out live, out complete);
                if (!complete) { Message(Russian ? "Дождитесь полной синхронизации порталов." : "Wait for the complete portal list to synchronize."); return; }
                List<MapPinBinding> candidates = MapPinCleanupPolicy.Select(bindings, live, SavedPinSnapshots(), true);
                if (candidates.Count == 0) { Message(Russian ? "Нет устаревших меток AnyPortal+." : "No obsolete AnyPortal+ map pins."); return; }
                int request = generation; long expectedWorld = world, expectedCharacter = character;
                Player expectedPlayer = player; Minimap expectedMap = map; ZNet expectedNetwork = network;
                PopupBase own = null;
                own = new YesNoPopup(Russian ? "Очистить метки порталов?" : "Clean up portal map pins?",
                    (Russian ? "Удалить метки AnyPortal+ для уничтоженных порталов: " : "Remove AnyPortal+ map pins for destroyed portals: ")
                    + candidates.Count + (Russian ? ". Другие метки останутся." : ". Other map pins will remain."),
                    delegate
                    {
                        if (!ReferenceEquals(popup, own)) { ClosePopupIfTop(own); return; }
                        CloseOwnedPopup();
                        try
                        {
                            if (!EnsureContext() || request != generation || !ReferenceEquals(player, expectedPlayer)
                                || !ReferenceEquals(map, expectedMap) || !ReferenceEquals(network, expectedNetwork)) return;
                            ReadLiveIds(out live, out complete);
                            if (!MapPinCleanupPolicy.SameContext(expectedWorld, expectedCharacter, world, character, complete)) return;
                            var current = MapPinCleanupPolicy.Select(bindings, live, SavedPinSnapshots(), true);
                            var selected = new List<Minimap.PinData>(); var removedIds = new List<string>();
                            foreach (MapPinBinding proposed in candidates)
                            {
                                MapPinBinding valid = current.Find(b => b.SameBinding(proposed));
                                Minimap.PinData pin = valid == null ? null : FindUnique(valid);
                                if (pin != null) { selected.Add(pin); removedIds.Add(valid.PortalId); }
                            }
                            if (selected.Count == 0) return;
                            store.Save(bindings); // Fail before deletion when the ownership file cannot be saved.
                            RemoveWithHistory(selected);
                            var replacement = CopyBindings();
                            replacement.RemoveAll(b => removedIds.Contains(b.PortalId));
                            store.Save(replacement); bindings = replacement;
                            Message((Russian ? "Удалено меток порталов: " : "Removed portal map pins: ") + selected.Count);
                        }
                        catch (Exception error) { Report(error); }
                    }, delegate { if (ReferenceEquals(popup, own)) CloseOwnedPopup(); else ClosePopupIfTop(own); }, false, true);
                popup = own; UnifiedPopup.Push(own);
            }
            catch (Exception error) { CloseOwnedPopup(); Report(error); }
        }
        private static void ReadLiveIds(out HashSet<string> ids, out bool complete)
        {
            ids = new HashSet<string>(StringComparer.Ordinal); complete = false;
            var positions = new List<MapPinBinding>();
            if (ZNet.instance == null || !ReferenceEquals(ZNet.instance, network) || world != ZNet.instance.GetWorldUID()) return;
            if (network.IsServer())
            {
                if (ZDOMan.instance == null) return;
                List<ZDO> portals = ZDOMan.instance.GetPortalList(); if (portals == null) return;
                foreach (ZDO portal in portals)
                    if (portal != null && portal.IsValid() && portal.m_uid != ZDOID.None)
                    {
                        ids.Add(portal.m_uid.ToString()); Vector3 position = portal.GetPosition();
                        positions.Add(new MapPinBinding { X = position.x, Y = position.y, Z = position.z });
                    }
                complete = true;
            }
            else
            {
                var registry = KnownPortalsManager.Instance;
                if (!registry.HasCompleteSnapshot) return;
                foreach (KnownPortal portal in registry.GetList())
                    if (portal != null && portal.Id != ZDOID.None)
                    {
                        ids.Add(portal.Id.ToString());
                        positions.Add(new MapPinBinding { X = portal.Location.x, Y = portal.Location.y, Z = portal.Location.z });
                    }
                complete = true;
            }
            if (complete) MapPinCleanupPolicy.ProtectLiveLocations(ids, bindings, positions);
        }
        private static List<MapPinBinding> CopyBindings()
        { var copy = new List<MapPinBinding>(); foreach (MapPinBinding binding in bindings) copy.Add(binding.Copy()); return copy; }
        private static List<Minimap.PinData> CurrentPins()
        { return (List<Minimap.PinData>)Pins.GetValue(map); }
        private static List<MapPinBinding> SavedPinSnapshots()
        {
            var result = new List<MapPinBinding>();
            foreach (Minimap.PinData pin in CurrentPins()) if (pin != null && pin.m_save) result.Add(Snapshot(pin, ""));
            return result;
        }
        private static Minimap.PinData FindUnique(MapPinBinding binding)
        {
            Minimap.PinData found = null;
            foreach (Minimap.PinData pin in CurrentPins())
                if (pin != null && pin.m_save && binding.SamePin(Snapshot(pin, binding.PortalId)))
                { if (found != null) return null; found = pin; }
            return found;
        }
        private static MapPinBinding Snapshot(Minimap.PinData pin, string id)
        { return new MapPinBinding { PortalId = id, Name = pin.m_name ?? "", Icon = (int)pin.m_type, X = pin.m_pos.x, Y = pin.m_pos.y, Z = pin.m_pos.z, Owner = pin.m_ownerID, Author = AuthorOf(pin) }; }
        private static string AuthorOf(Minimap.PinData pin) { return pin.m_author.IsValid ? pin.m_author.ToString() : ""; }
        private static Type HistoryType() { return AccessTools.TypeByName("ValheimModPack.PinRemoval.PinHistoryController"); }
        private static void RememberCreated(Minimap.PinData pin)
        {
            Type history = HistoryType(); if (history == null) return;
            MethodInfo remember = AccessTools.Method(history, "RememberQuickPin", new[] { typeof(Minimap.PinData), typeof(string) });
            if (remember == null) throw new MissingMethodException("Pin history creation integration changed");
            remember.Invoke(null, new object[] { pin, "" });
        }
        private static void RememberRename(Minimap.PinData pin, string name)
        {
            Type history = HistoryType(); if (history == null) return;
            MethodInfo remember = AccessTools.Method(history, "RememberRename", new[] { typeof(Minimap.PinData), typeof(string) });
            if (remember == null) throw new MissingMethodException("Pin history rename integration changed");
            remember.Invoke(null, new object[] { pin, name });
        }
        private static void RemoveWithHistory(List<Minimap.PinData> pins)
        {
            Type history = HistoryType();
            if (history == null) { foreach (Minimap.PinData pin in pins) map.RemovePin(pin); return; }
            FieldInfo active = AccessTools.Field(history, "active");
            MethodInfo remove = AccessTools.Method(history, "RemoveMany", new[] { typeof(Minimap), typeof(IEnumerable<Minimap.PinData>) });
            object instance = active == null ? null : active.GetValue(null);
            if (instance == null || remove == null) throw new InvalidOperationException("Pin history unavailable; map pins retained");
            remove.Invoke(instance, new object[] { map, pins });
        }
        private static void CloseOwnedPopup()
        {
            PopupBase own = popup; popup = null;
            ClosePopupIfTop(own);
        }
        private static Stack<PopupBase> GetPopupStack()
        {
            if (PopupStack == null) return null;
            object instance = PopupStack.IsStatic ? null : PopupInstance == null ? null : PopupInstance.GetValue(null);
            if (!PopupStack.IsStatic && instance == null) return null;
            return PopupStack.GetValue(instance) as Stack<PopupBase>;
        }
        private static void ClosePopupIfTop(PopupBase own)
        {
            if (own == null) return;
            try
            {
                Stack<PopupBase> stack = GetPopupStack();
                if (stack != null && stack.Count != 0 && ReferenceEquals(stack.Peek(), own)) UnifiedPopup.Pop();
            }
            catch (Exception error) { Log.Warning("AnyPortal+ popup cleanup: " + error.Message); }
        }
        private static bool Russian { get { return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian"; } }
        private static void Message(string value)
        { if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, value, 0, null); }
        private static void Report(Exception error)
        {
            Log.Error("AnyPortal+ map pins: " + error);
            Message(Russian ? "Не удалось изменить метки порталов. Проверьте журнал BepInEx." : "Could not change portal map pins. Check the BepInEx log.");
        }
    }
}
