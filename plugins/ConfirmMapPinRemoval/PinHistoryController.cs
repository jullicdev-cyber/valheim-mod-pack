using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;
namespace ValheimModPack.PinRemoval
{
    public sealed class HistoryRow
    {
        public string Title, Details, ActionLabel;
        public bool Enabled;
        public Action Activate;
    }
    public sealed class PinHistoryController : IDisposable
    {
        private static PinHistoryController active;
        private readonly Action<Exception> report;
        private readonly FieldInfo pins;
        private readonly PinHistoryWindow view = new PinHistoryWindow();
        private readonly SharedPinMetadata shared;
        private PinArchive archive;
        private Player owner;
        private Minimap map;
        private long world, character;
        private bool unavailable, deletedTab = true;
        private int page;
        private GameObject launcher;
        private bool canOpen;
        public bool IsOpen { get { return view.IsVisible; } }
        public Func<bool> OpenShortcut;
        public Func<string> ShortcutLabel;
        public PinHistoryController(Harmony harmony, FieldInfo pins, Action<Exception> report)
        {
            this.pins = pins; this.report = report; active = this;
            var create = AccessTools.Method(typeof(Minimap), "ShowPinNameInput", new[] { typeof(Vector3) });
            if (create == null || AccessTools.Field(typeof(Minimap), "m_namePin") == null)
                throw new MissingMemberException("Map creation API changed");
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(PinHistoryController), "AfterCreated"));
            var adopt = AccessTools.Method(typeof(Minimap), "OnMapLeftClick", Type.EmptyTypes);
            if (adopt == null) throw new MissingMethodException("Map pin adoption API changed");
            harmony.Patch(adopt, prefix: new HarmonyMethod(typeof(PinHistoryController), "BeforeAdoption"),
                postfix: new HarmonyMethod(typeof(PinHistoryController), "AfterAdoption"));
            shared = new SharedPinMetadata(harmony, report);
        }
        private static void BeforeAdoption(Minimap __instance, out List<KeyValuePair<Minimap.PinData, PinRecord>> __state)
        {
            __state = null;
            if (active == null) return;
            try
            {
                if (!active.EnsureContext() || !ReferenceEquals(active.map, __instance)) return;
                __state = new List<KeyValuePair<Minimap.PinData, PinRecord>>();
                foreach (Minimap.PinData pin in active.CurrentPins)
                {
                    if (!pin.m_save || pin.m_ownerID == 0) continue;
                    var record = Snapshot(pin); active.archive.Enrich(record);
                    if (record.CreatedUtc != 0) __state.Add(new KeyValuePair<Minimap.PinData, PinRecord>(pin, record));
                }
            }
            catch (Exception error) { active.Fault(error); }
        }
        private static void AfterAdoption(Minimap __instance, List<KeyValuePair<Minimap.PinData, PinRecord>> __state)
        {
            if (active == null || __state == null) return;
            try
            {
                if (!active.EnsureContext() || !ReferenceEquals(active.map, __instance)) return;
                foreach (var before in __state)
                    if (before.Key.m_ownerID == 0 && active.CurrentPins.Contains(before.Key))
                    {
                        var adopted = Snapshot(before.Key); adopted.PresetKey = before.Value.PresetKey; adopted.BoundName = before.Value.BoundName;
                        active.archive.RememberCreation(adopted, before.Value.CreatorName, before.Value.CreatedUtc);
                    }
            }
            catch (Exception error) { active.Fault(error); }
        }
        private static void AfterCreated(Minimap __instance, Minimap.PinData ___m_namePin)
        {
            if (active == null || ___m_namePin == null || !___m_namePin.m_save) return;
            try
            {
                if (active.EnsureContext() && ReferenceEquals(active.map, __instance))
                    active.archive.RememberCreation(Snapshot(___m_namePin), active.owner.GetPlayerName(), DateTime.UtcNow.Ticks);
            }
            catch (Exception error) { active.Fault(error); }
        }
        private bool EnsureContext()
        {
            var player = Player.m_localPlayer; var currentMap = Minimap.instance;
            if (player == null || currentMap == null || ZNet.instance == null) { ResetContext(); return false; }
            long currentWorld = ZNet.instance.GetWorldUID(), currentCharacter = player.GetPlayerID();
            if (currentWorld == 0 || currentCharacter == 0) { ResetContext(); return false; }
            if (!ReferenceEquals(owner, player) || !ReferenceEquals(map, currentMap) || world != currentWorld || character != currentCharacter)
            {
                ResetContext(); owner = player; map = currentMap; world = currentWorld; character = currentCharacter;
                string directory = Path.Combine(Paths.GameRootPath, "ValheimModpack", "MapPinHistory");
                string file = Path.Combine(directory, world.ToString(CultureInfo.InvariantCulture) + "-" + character.ToString(CultureInfo.InvariantCulture) + ".bin");
                try { archive = new PinArchive(file, world, character); }
                catch (Exception error) { unavailable = true; Fault(error); }
            }
            return !unavailable && archive != null;
        }
        private void ResetContext()
        { Close(); archive = null; owner = null; map = null; world = 0; character = 0; unavailable = false; page = 0; }
        private void Fault(Exception error)
        {
            report(error);
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                    Russian ? "Не удалось сохранить историю меток. Проверьте журнал BepInEx." : "Could not save map pin history. Check the BepInEx log.", 0, null);
        }
        private static bool Russian { get { return Localization.instance.GetSelectedLanguage() == "Russian"; } }
        private List<Minimap.PinData> CurrentPins { get { return (List<Minimap.PinData>)pins.GetValue(map); } }
        public void Remove(Minimap expectedMap, Minimap.PinData pin)
        {
            RemoveMany(expectedMap, new[] { pin });
        }
        public void RemoveMany(Minimap expectedMap, IEnumerable<Minimap.PinData> selectedPins)
        {
            if (selectedPins == null) throw new ArgumentNullException("selectedPins");
            if (!EnsureContext() || !ReferenceEquals(map, expectedMap))
                throw new InvalidOperationException("Map history unavailable: deletion blocked to preserve the pin");
            PinArchive expectedArchive = archive;
            Player expectedOwner = owner;
            long expectedWorld = world, expectedCharacter = character;
            var targets = new List<Minimap.PinData>();
            var targetIndex = new HashSet<Minimap.PinData>();
            var snapshots = new List<PinRecord>();
            var current = CurrentPins;
            var currentIndex = new HashSet<Minimap.PinData>(current);
            foreach (Minimap.PinData pin in selectedPins)
            {
                if (pin == null || !pin.m_save || !currentIndex.Contains(pin))
                    throw new InvalidOperationException("Deletion batch changed: no pins were removed");
                if (!targetIndex.Add(pin)) continue;
                targets.Add(pin);
            }
            if (targets.Count == 0) return;
            // An enumerable may run arbitrary code while it is being staged.
            // Revalidate the same world/player/map and complete selection before
            // committing, so a stale batch cannot mutate another session.
            if (!BatchContext(expectedArchive, expectedMap, expectedOwner, expectedWorld, expectedCharacter))
                throw new InvalidOperationException("Map history context changed: no pins were removed");
            currentIndex.Clear();
            foreach (Minimap.PinData pin in CurrentPins) currentIndex.Add(pin);
            foreach (Minimap.PinData pin in targets)
            {
                if (!pin.m_save || !currentIndex.Contains(pin))
                    throw new InvalidOperationException("Deletion batch changed: no pins were removed");
                snapshots.Add(Snapshot(pin));
            }
            // Persist first. Failed/partial game deletion is safe: restore checks the actual map.
            try { expectedArchive.RecordManyBeforeDelete(snapshots, DateTime.UtcNow.Ticks); }
            catch
            {
                expectedOwner.Message(MessageHud.MessageType.Center, Russian
                    ? (targets.Count == 1 ? "Метка сохранена на карте: не удалось записать историю удаления." : "Метки сохранены на карте: не удалось записать историю удаления.")
                    : (targets.Count == 1 ? "Pin kept on map: deletion history could not be saved." : "Pins kept on map: deletion history could not be saved."), 0, null);
                throw;
            }
            foreach (Minimap.PinData pin in targets)
            {
                if (!BatchContext(expectedArchive, expectedMap, expectedOwner, expectedWorld, expectedCharacter))
                    throw new InvalidOperationException("Map history context changed during deletion; retained snapshots remain recoverable");
                expectedMap.RemovePin(pin);
            }
        }
        private bool BatchContext(PinArchive expectedArchive, Minimap expectedMap, Player expectedOwner, long expectedWorld, long expectedCharacter)
        {
            return ReferenceEquals(archive, expectedArchive) && ReferenceEquals(map, expectedMap) && ReferenceEquals(owner, expectedOwner)
                && ReferenceEquals(expectedOwner, Player.m_localPlayer) && ReferenceEquals(expectedMap, Minimap.instance)
                && ZNet.instance != null && expectedWorld == ZNet.instance.GetWorldUID() && expectedCharacter == expectedOwner.GetPlayerID();
        }
        public void Tick(bool allowOpen)
        {
            view.CleanupHidden();
            if (!EnsureContext()) { SetLauncherVisible(false); return; }
            bool valid = SafePlayer(owner) && map.m_mode == Minimap.MapMode.Large && !Minimap.InTextInput()
                && !UnifiedPopup.IsVisible() && !Menu.IsVisible() && !global::Console.IsVisible()
                && (Chat.instance == null || !Chat.instance.HasFocus());
            canOpen = valid && allowOpen;
            if (!valid || !allowOpen) Close();
            SetLauncherVisible(canOpen && !view.IsVisible);
            if (view.IsVisible)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB")) Close();
            }
            else if (canOpen && OpenShortcut != null && OpenShortcut()) Open();
        }
        private void SetLauncherVisible(bool visible)
        {
            if (!visible) { if (launcher != null) launcher.SetActive(false); return; }
            if (launcher == null)
            {
                if (GUIManager.CustomGUIFront == null) return;
                launcher = GUIManager.Instance.CreateButton("",
                    GUIManager.CustomGUIFront.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-220, -65), 420, 50);
                launcher.name = "ConfirmMapPinRemoval.HistoryLauncher";
                var sound = launcher.GetComponent<ButtonSfx>(); if (sound != null) sound.m_selectSfxPrefab = null;
                launcher.GetComponent<Button>().onClick.AddListener(Open);
            }
            PinLauncherLabel.Apply(launcher, Russian ? "История меток" : "Map pin history",
                ShortcutLabel == null ? "" : ShortcutLabel(), Russian ? "Клавиша не назначена" : "Unbound");
            launcher.SetActive(true);
        }
        private void Open()
        {
            if (!canOpen || view.IsVisible || !EnsureContext()) return;
            try { page = 0; deletedTab = true; view.Show(Close, ChangeTab, ChangePage); Refresh(); SetLauncherVisible(false); }
            catch (Exception error) { Close(); Fault(error); }
        }
        private void ChangeTab(bool deleted) { deletedTab = deleted; page = 0; RefreshSafe(); }
        private void ChangePage(int direction) { page += direction; RefreshSafe(); }
        private void RefreshSafe() { try { Refresh(); } catch (Exception error) { Close(); Fault(error); } }
        private void Refresh()
        {
            if (!EnsureContext() || !view.IsVisible) return;
            bool ru = Russian; var records = new List<PinRecord>();
            if (deletedTab) { for (int i = archive.Deleted.Count - 1; i >= 0; i--) records.Add(archive.Deleted[i].Copy()); }
            else
            {
                foreach (var pin in CurrentPins) if (pin.m_save) { var record = Snapshot(pin); archive.Enrich(record); records.Add(record); }
                records.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            }
            int pages = Math.Max(1, (records.Count + 5) / 6); page = Math.Max(0, Math.Min(page, pages - 1));
            var rows = new List<HistoryRow>();
            for (int i = page * 6; i < records.Count && i < page * 6 + 6; i++)
            {
                var record = records[i]; string id = record.Id; var sourceArchive = archive;
                string details = (ru ? "Автор: " : "Author: ") + ResolveAuthor(record) + " · "
                    + (ru ? "Создана: " : "Created: ") + DateLabel(record.CreatedUtc, ru);
                details += "\n" + (deletedTab ? (ru ? "Удалена: " : "Deleted: ") + DateLabel(record.DeletedUtc, ru) + " · " : "")
                    + "X " + record.X.ToString("0", CultureInfo.InvariantCulture) + " / Z " + record.Z.ToString("0", CultureInfo.InvariantCulture);
                bool present = deletedTab && Exists(record);
                Minimap.PinData current = deletedTab ? null : CurrentPins.Find(pin => Snapshot(pin).Key == record.Key);
                rows.Add(new HistoryRow { Title = PinLabel.Format(QuickPinController.DisplayRecord(record), ru), Details = details,
                    ActionLabel = !deletedTab ? (ru ? "Название" : "Rename") : record.RestoredUtc != 0 ? (ru ? "Восстановлена" : "Restored") : present ? (ru ? "Уже на карте" : "On map") : (ru ? "Вернуть" : "Restore"),
                    Enabled = deletedTab ? record.RestoredUtc == 0 && !present : current != null,
                    Activate = deletedTab ? (Action)(() => Restore(sourceArchive, id)) : () => QuickPinController.RenameFromHistory(current) });
            }
            view.Render(rows, deletedTab, page, pages, records.Count, ru);
        }
        private static string DateLabel(long ticks, bool ru)
        { return ticks == 0 ? (ru ? "неизвестно" : "unknown") : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        private string ResolveAuthor(PinRecord record)
        {
            if (!String.IsNullOrEmpty(record.CreatorName)) return record.CreatorName;
            if (!String.IsNullOrEmpty(record.Author))
            {
                foreach (var info in ZNet.instance.GetPlayerList())
                    if (info.m_userInfo.m_id.IsValid && info.m_userInfo.m_id.ToString() == record.Author)
                        return info.m_name;
                return record.Author;
            }
            return record.Owner != 0 ? (Russian ? "ID игрока " : "Player ID ") + record.Owner.ToString(CultureInfo.InvariantCulture)
                : (Russian ? "неизвестен" : "unknown");
        }
        private bool Exists(PinRecord record)
        {
            foreach (var pin in CurrentPins)
                if (pin.m_save && Snapshot(pin).Key == record.Key) return true;
            return false;
        }
        private void Restore(PinArchive expected, string id)
        {
            try
            {
                // A stale button from a destroyed world/window must never mutate the next world.
                if (!view.IsVisible || !EnsureContext() || !ReferenceEquals(expected, archive) || !SafePlayer(owner)
                    || map.m_mode != Minimap.MapMode.Large || UnifiedPopup.IsVisible() || Menu.IsVisible()
                    || global::Console.IsVisible() || Minimap.InTextInput()
                    || (Chat.instance != null && Chat.instance.HasFocus())) return;
                archive.Restore(id, Exists, AddRestoredPin, DateTime.UtcNow.Ticks);
                Refresh();
            }
            catch (Exception error) { Fault(error); RefreshSafe(); }
        }
        private void AddRestoredPin(PinRecord record)
        {
            Splatform.PlatformUserID author;
            if (String.IsNullOrEmpty(record.Author)) author = Splatform.PlatformUserID.None;
            else if (!Splatform.PlatformUserID.TryParse(record.Author, out author)) throw new InvalidDataException("Invalid saved pin author");
            var restored = map.AddPin(new Vector3(record.X, record.Y, record.Z), (Minimap.PinType)record.Type,
                record.Name, true, record.Checked, record.Owner, author);
            if (restored == null) throw new InvalidOperationException("Game refused to restore the pin");
            restored.m_doubleSize = record.DoubleSize; restored.m_animate = record.Animate; restored.m_worldSize = record.WorldSize;
            var restoredRecord = Snapshot(restored);
            if (record.Name == record.BoundName) { restoredRecord.PresetKey = record.PresetKey; restoredRecord.BoundName = record.BoundName; }
            if (record.CreatedUtc > 0) archive.RememberCreation(restoredRecord, record.CreatorName, record.CreatedUtc);
            else if (record.BoundName.Length != 0 && record.Name == record.BoundName) archive.BindPreset(restoredRecord, record.PresetKey);
        }
        internal static void RememberQuickPin(Minimap.PinData pin, string presetKey)
        {
            if (active == null || !active.EnsureContext() || !active.CurrentPins.Contains(pin)) throw new InvalidOperationException("Map history context unavailable");
            PinRecord record = Snapshot(pin); record.PresetKey = presetKey ?? ""; record.BoundName = String.IsNullOrEmpty(presetKey) ? "" : pin.m_name;
            active.archive.RememberCreation(record, active.owner.GetPlayerName(), DateTime.UtcNow.Ticks);
        }
        internal static void RememberRename(Minimap.PinData pin, string name)
        {
            if (active == null || !active.EnsureContext() || !active.CurrentPins.Contains(pin)) throw new InvalidOperationException("Map rename context unavailable");
            PinRecord record = Snapshot(pin); record.Name = name; active.archive.BindPreset(record, "");
        }
        internal static string PresetFor(Minimap.PinData pin)
        {
            return pin != null && active != null && active.EnsureContext() && active.CurrentPins.Contains(pin)
                ? active.archive.PresetFor(Snapshot(pin)) : "";
        }
        // For callers already enumerating the current map's live pin list. The
        // world/character guard remains; avoid another linear membership search
        // for each pin during bounded nearby recommendation scans.
        internal static string PresetForKnownCurrentPin(Minimap.PinData pin)
        {
            return pin != null && active != null && active.EnsureContext()
                ? active.archive.PresetFor(Snapshot(pin)) : "";
        }
        internal static PinRecord Snapshot(Minimap.PinData pin)
        {
            return new PinRecord { Name = pin.m_name ?? "", Type = (int)pin.m_type, X = pin.m_pos.x, Y = pin.m_pos.y, Z = pin.m_pos.z,
                Owner = pin.m_ownerID, Author = pin.m_author.IsValid ? pin.m_author.ToString() : "", Checked = pin.m_checked,
                DoubleSize = pin.m_doubleSize, Animate = pin.m_animate, WorldSize = pin.m_worldSize };
        }
        internal static List<PinRecord> ExportSharedMetadata()
        {
            var result = new List<PinRecord>();
            if (active == null || !active.EnsureContext()) return result;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Minimap.PinData pin in active.CurrentPins)
            {
                if (!pin.m_save || (int)pin.m_type == 4) continue; // Same Death-pin exclusion as native GetSharedMapData.
                PinRecord record = Snapshot(pin); active.archive.Enrich(record);
                if (record.CreatedUtc == 0) continue;
                record = SharedPinMetadata.Canonical(record, active.character);
                if (record.Owner != 0 && !String.IsNullOrEmpty(record.Author) && keys.Add(record.Key)) result.Add(record);
            }
            result.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            if (result.Count > 512) result.RemoveRange(512, result.Count - 512);
            return result;
        }
        internal static void ImportSharedMetadata(IList<PinRecord> records)
        {
            if (active == null || !active.EnsureContext()) return;
            var candidates = new Dictionary<string, PinRecord>(StringComparer.Ordinal);
            foreach (PinRecord record in records) if (record.CreatedUtc > 0) candidates[record.Key] = record;
            var matched = new List<PinRecord>();
            foreach (Minimap.PinData pin in active.CurrentPins)
            {
                if (!pin.m_save) continue;
                PinRecord local = Snapshot(pin), incoming;
                if (!candidates.TryGetValue(SharedPinMetadata.Canonical(local, active.character).Key, out incoming)) continue;
                // Keep the live pin's local identity (owner 0 for own pins). Never create or rename a map pin here.
                local.CreatedUtc = incoming.CreatedUtc; local.CreatorName = incoming.CreatorName; matched.Add(local);
                if (incoming.BoundName == local.Name && !String.IsNullOrEmpty(incoming.PresetKey))
                { local.PresetKey = incoming.PresetKey; local.BoundName = incoming.BoundName; }
            }
            active.archive.ImportCreationMetadata(matched);
            QuickPinController.RefreshKnownCaptions();
        }
        internal static bool SafePlayer(Player player)
        { return player != null && !player.IsDead() && !player.IsTeleporting() && !player.IsSleeping() && !player.InCutscene(); }
        public void Close() { view.Hide(); SetLauncherVisible(false); }
        public void Dispose()
        {
            shared.Dispose();
            ResetContext(); if (launcher != null) { launcher.SetActive(false); UnityEngine.Object.Destroy(launcher); launcher = null; }
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
