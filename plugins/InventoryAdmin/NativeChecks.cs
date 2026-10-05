// Optional isolated-engine verification. Never ship this assembly as a game plugin.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    // Inactive and detached: only exposes Safe's UI checks without a ZNetView,
    // animation controller, Awake, or a local/player-world registration.
    public sealed class InventoryAdminProbePlayer : Player
    {
        public override bool IsDead() { return false; }
        public override bool IsTeleporting() { return false; }
        public override bool InCutscene() { return false; }
    }

    public static class NativeChecks
    {
        private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static int checks;
        private static Type adapter;
        private static void Check(bool value, string reason)
        { ++checks; if (!value) throw new InvalidOperationException("Inventory administration native check: " + reason); }
        private static object Field(object target, string name)
        { return target.GetType().GetField(name, All).GetValue(target); }
        private static object Invoke(string name, params object[] arguments)
        {
            foreach (MethodInfo method in adapter.GetMethods(All))
            {
                if (method.Name != name || method.GetParameters().Length != arguments.Length) continue;
                bool matches = true;
                ParameterInfo[] parameters = method.GetParameters();
                for (int i = 0; i < arguments.Length; ++i)
                    if (arguments[i] != null && !parameters[i].ParameterType.IsInstanceOfType(arguments[i])) matches = false;
                if (!matches) continue;
                try { return method.Invoke(null, arguments); }
                catch (TargetInvocationException error)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                    throw;
                }
            }
            throw new MissingMethodException(adapter.FullName, name);
        }
        private static void Reject(Action action, string reason)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidOperationException) { rejected = true; }
            catch (System.IO.InvalidDataException) { rejected = true; }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, reason);
        }
        private static object WorldCharacters(string method, params object[] args)
        {
            Type plugin = Chainloader.PluginInfos["valheimmodpack.worldcharacters"].Instance.GetType();
            try { return plugin.GetMethod(method, All).Invoke(null, args); }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
                throw;
            }
        }
        private static ItemDrop.ItemData Item(string prefab, int count, int x, int y)
        {
            GameObject definition = ObjectDB.instance.GetItemPrefab(prefab);
            if (!definition) throw new InvalidOperationException("Fixture item is missing: " + prefab);
            ItemDrop.ItemData result = definition.GetComponent<ItemDrop>().m_itemData.Clone();
            result.m_dropPrefab = definition; result.m_stack = count; result.m_gridPos = new Vector2i(x, y);
            return result;
        }
        private static byte[] Save(Inventory inventory)
        { var package = new ZPackage(); inventory.Save(package); return package.GetArray(); }
        private static object Backpack(ItemDrop.ItemData item)
        {
            Assembly assembly = Chainloader.PluginInfos["vapok.mods.adventurebackpacks"].Instance.GetType().Assembly;
            Type extensions = assembly.GetType("Vapok.Common.Managers.ItemExtensions", true);
            object data = extensions.GetMethod("Data", new[] { typeof(ItemDrop.ItemData) }).Invoke(null, new object[] { item });
            Type component = assembly.GetType("AdventureBackpacks.Components.BackpackComponent", true);
            return data.GetType().GetMethod("GetOrCreate").MakeGenericMethod(component).Invoke(data, new object[] { "" });
        }
        private static object Capture(Inventory source, int visibleRows, long peer, long character)
        { return Invoke("BuildView", source, visibleRows, peer, "local-host", character, Guid.NewGuid().ToString("N")); }
        private static object Row(object captured, string prefab)
        {
            object view = Field(captured, "View");
            foreach (object row in (IEnumerable)Field(view, "Items")) if ((string)Field(row, "Prefab") == prefab) return row;
            throw new InvalidOperationException("Captured inventory did not contain " + prefab);
        }
        private static byte[] Prepare(object captured, object row, int count)
        { return (byte[])Invoke("Prepare", captured, Field(row, "SlotId"), Field(row, "Fingerprint"), count); }
        private static void Remove(object captured, object row, int count)
        { Invoke("Remove", captured, Field(row, "SlotId"), Field(row, "Fingerprint"), count); }

        public static string Run()
        {
            // Native Inventory.Load evaluates GetDamage for world-level gear;
            // that native method reads this Game setting even in a detached
            // inventory. Supply its real inactive component, without Awake,
            // a player profile, a save, a world, or an Update loop.
            GameObject context = null;
            FieldInfo singleton = typeof(Game).GetField("<instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);
            object original = singleton.GetValue(null);
            try
            {
                if (Game.instance == null)
                {
                    context = new GameObject("InventoryAdmin native damage context"); context.SetActive(false);
                    Game component = context.AddComponent<Game>(); singleton.SetValue(null, component);
                    if (context.activeSelf || component.GetPlayerProfile() != null)
                        throw new InvalidOperationException("Native damage fixture unexpectedly initialized a world profile.");
                }
                RunDetached();
            }
            finally
            {
                if (context != null) UnityEngine.Object.DestroyImmediate(context);
                singleton.SetValue(null, original);
            }
            Check(ReferenceEquals(Game.instance, original), "detached gear context restores the prior native Game singleton");
            CheckBackpackBusyGuard();
            foreach (string name in new[] { "RequestAdministrativeSave", "IsAdministrativeSaveDurable", "GetAdministrativeDurableSequence", "GetAdministrativeOwner" })
                Check(Chainloader.PluginInfos["valheimmodpack.worldcharacters"].Instance.GetType().GetMethod(name, All) != null,
                    "World Characters durability API is present: " + name);
            Check(!(bool)WorldCharacters("IsAdministrativeSaveDurable", 1L), "unloaded menu cannot claim an administrative snapshot was durable");
            Check((long)WorldCharacters("GetAdministrativeDurableSequence", Int64.MinValue) <= 0,
                "unknown peer has no durable administrative sequence");
            Check((long)WorldCharacters("GetAdministrativeCharacter", Int64.MinValue) == 0,
                "unknown peer cannot impersonate an approved character identity");
            Check((string)WorldCharacters("GetAdministrativeOwner", new object[] { null }) == String.Empty,
                "unknown connection cannot supply an approved account identity");
            Reject(() => WorldCharacters("RequestAdministrativeSave"), "unloaded character cannot produce an administrative save acknowledgement");
            return "PASS: " + checks + " inventory administration native assertions. Detached inventories only; live multiplayer transaction/disconnect flows require a cooperative client test.\n"
                + InventoryAdminUiNativeChecks.Run() + "\n" + InventoryAdminInputNativeChecks.Run()
                + "\n" + GroupRadiusUiNativeChecks.Run() + "\n" + GroupRadiusMotionNativeChecks.Run();
        }

        private static void CheckBackpackBusyGuard()
        {
            Assembly assembly = Chainloader.PluginInfos["vapok.mods.adventurebackpacks"].Instance.GetType().Assembly;
            FieldInfo opened = assembly.GetType("AdventureBackpacks.Patches.InventoryGuiPatches", true).GetField("BackpackIsOpen", All);
            Check(opened != null && opened.FieldType == typeof(bool), "installed backpack exposes its actual open-inventory state");
            object original = opened.GetValue(null);
            GameObject holder = new GameObject("InventoryAdmin inactive busy-state fixture"); holder.SetActive(false);
            try
            {
                Player player = holder.AddComponent<InventoryAdminProbePlayer>();
                opened.SetValue(null, false);
                Check((bool)Invoke("Safe", player), "idle detached player is eligible when backpack UI is closed");
                opened.SetValue(null, true);
                Check(!(bool)Invoke("Safe", player), "an open backpack blocks native inventory administration");
                Check(Player.m_localPlayer == null && !holder.activeSelf, "busy-state fixture never registers a live local player");
            }
            finally
            {
                opened.SetValue(null, original);
                UnityEngine.Object.DestroyImmediate(holder);
            }
        }

        private static void RunDetached()
        {
            checks = 0;
            Check(Player.m_localPlayer == null, "fixture runs without a live character or user world");
            Check(Chainloader.PluginInfos.ContainsKey("valheimmodpack.inventoryadmin"), "administration plugin loaded with the full pack");
            object plugin = Chainloader.PluginInfos["valheimmodpack.inventoryadmin"].Instance;
            Check(((UnityEngine.Behaviour)plugin).enabled, "administration plugin is enabled");
            adapter = plugin.GetType().Assembly.GetType("ValheimModPack.InventoryAdmin.NativeAdapter", true);
            Check(typeof(Inventory).GetMethod("Save", new[] { typeof(ZPackage) }) != null
                && typeof(Inventory).GetMethod("Load", new[] { typeof(ZPackage) }) != null,
                "actual native inventory serialization endpoints");
            Check(typeof(Humanoid).GetMethod("UnequipItem", All, null, new[] { typeof(ItemDrop.ItemData), typeof(bool) }, null) != null,
                "actual equipment removal endpoint");
            var source = new Inventory("Admin fixture source", null, 8, 10);
            ItemDrop.ItemData wood = Item("Wood", 7, 2, 1);
            source.GetAllItems().Add(wood);
            ItemDrop.ItemData helmet = Item("HelmetLeather", 1, 0, 6);
            helmet.m_quality = 3; helmet.m_durability = 23.45f; helmet.m_variant = 0; helmet.m_worldLevel = 2;
            helmet.m_crafterID = 987654321L; helmet.m_crafterName = "Native probe";
            helmet.m_customData["eaqs_slot"] = "head"; helmet.m_customData["eaqs_player"] = "44";
            helmet.m_customData["eaqs_parked"] = "1"; helmet.m_customData["eaqs_weaponshield"] = "1";
            helmet.m_customData["probe.enchantment"] = "Enchantments \u2603";
            source.GetAllItems().Add(helmet);
            GameObject backpackPrefab = ObjectDB.instance.m_items.FirstOrDefault(p => p && p.name.StartsWith("Backpack") && p.GetComponent<ItemDrop>());
            Check(backpackPrefab != null, "real Adventure Backpacks prefab is present");
            ItemDrop.ItemData bag = Item(backpackPrefab.name, 1, 1, 2);
            var contents = new Inventory("Admin fixture backpack", null, 4, 4);
            ItemDrop.ItemData iron = Item("Iron", 5, 0, 0); contents.GetAllItems().Add(iron);
            object backpack = Backpack(bag);
            backpack.GetType().GetMethod("SetInventory").Invoke(backpack, new object[] { contents });
            backpack.GetType().GetMethod("Serialize").Invoke(backpack, null);
            source.GetAllItems().Add(bag);
            string bagKey = bag.m_customData.Keys.Single(k => k.Contains("AdventureBackpacks.Components.BackpackComponent"));
            byte[] original = Save(source);
            object view = Capture(source, 5, 77, 44);
            Check(original.SequenceEqual(Save(source)), "viewing the main inventory and nested backpack does not move or replace items");
            object woodRow = Row(view, "Wood"), helmetRow = Row(view, "HelmetLeather"), ironRow = Row(view, "Iron");
            object inventoryView = Field(view, "View");
            Type codec = plugin.GetType().Assembly.GetType("ValheimModPack.InventoryAdmin.InventoryCodec", true);
            byte[] wireView = (byte[])codec.GetMethod("EncodeInventoryView").Invoke(null, new[] { inventoryView });
            object decodedView = codec.GetMethod("DecodeInventoryView").Invoke(null, new object[] { wireView });
            Check(((ICollection)Field(decodedView, "Items")).Count == 4,
                "actual captured main, equipment and backpack rows survive the bounded protocol codec");
            Check((int)Field(helmetRow, "Y") == 6, "equipment in hidden EAQS rows remains visible to the administrator");
            Check((int)Field(ironRow, "Stack") == 5, "nested backpack contents are visible");
            byte[] woodBlob = Prepare(view, woodRow, 3);
            Check(wood.m_stack == 7 && source.GetAllItems().Contains(wood), "preparing a transfer does not remove the source");
            ItemDrop.ItemData woodCopy = (ItemDrop.ItemData)Invoke("ReadBlob", woodBlob);
            Check(woodCopy.m_stack == 3 && woodCopy.m_dropPrefab.name == "Wood", "partial transfer blob has precisely the requested count");
            var stackDestination = new Inventory("Admin fixture metadata destination", null, 8, 10);
            ItemDrop.ItemData previousWood = Item("Wood", 40, 7, 4);
            previousWood.m_customData["probe.owner"] = "existing"; stackDestination.GetAllItems().Add(previousWood);
            Invoke("Add", stackDestination, 5, woodBlob);
            Check(previousWood.m_stack == 40 && stackDestination.GetAllItems().Count == 2,
                "exact-cell receipt does not auto-stack into an existing item or mix custom metadata");
            Check(stackDestination.GetAllItems().Any(i => !ReferenceEquals(i, previousWood) && i.m_stack == 3 && i.m_gridPos.y < 5),
                "partial stack arrives intact in a separate ordinary cell");
            Reject(() => Prepare(view, woodRow, 8), "a transfer larger than the captured stack is rejected");
            Reject(() => Prepare(view, woodRow, 0), "zero-count transfers are rejected");
            Remove(view, woodRow, 3);
            Check(wood.m_stack == 4 && source.GetAllItems().Contains(wood), "native partial removal preserves the original remaining stack");
            Reject(() => Remove(view, woodRow, 3), "reusing a stale source fingerprint cannot remove the stack twice");
            Check(wood.m_stack == 4, "stale request rejection leaves the remaining stack unchanged");
            view = Capture(source, 5, 77, 44); woodRow = Row(view, "Wood");
            Action originalChanged = source.m_onChanged;
            source.m_onChanged = () => { throw new InvalidOperationException("Fixture inventory callback failed after removal."); };
            try { Remove(view, woodRow, 1); }
            finally { source.m_onChanged = originalChanged; }
            Check(wood.m_stack == 3 && source.GetAllItems().Contains(wood),
                "a callback failure after native removal does not obscure a completed exact decrement");
            view = Capture(source, 5, 77, 44); helmetRow = Row(view, "HelmetLeather");
            byte[] helmetBlob = Prepare(view, helmetRow, 1);
            ItemDrop.ItemData helmetCopy = (ItemDrop.ItemData)Invoke("ReadBlob", helmetBlob);
            Check(helmetCopy.m_quality == 3 && Math.Abs(helmetCopy.m_durability - 23.45f) < 0.011f
                && helmetCopy.m_worldLevel == 2 && helmetCopy.m_crafterID == 987654321L && helmetCopy.m_crafterName == "Native probe",
                "transfer retains quality, native durability precision, world level and crafter");
            Check(helmetCopy.m_customData["probe.enchantment"] == "Enchantments \u2603",
                "non-EAQS custom item metadata survives native serialization");
            Check(!helmetCopy.m_equipped && !helmetCopy.m_customData.ContainsKey("eaqs_slot")
                && !helmetCopy.m_customData.ContainsKey("eaqs_player") && !helmetCopy.m_customData.ContainsKey("eaqs_parked")
                && !helmetCopy.m_customData.ContainsKey("eaqs_weaponshield"), "transferred item does not inherit another player's EAQS slot ownership");
            var destination = new Inventory("Admin fixture destination", null, 8, 10);
            Check((bool)Invoke("CanAdd", destination, 5, helmetBlob), "ordinary empty destination cell is available");
            Invoke("Add", destination, 5, helmetBlob);
            ItemDrop.ItemData receivedHelmet = destination.GetAllItems().Single();
            Check(receivedHelmet.m_gridPos.y < 5 && !receivedHelmet.m_equipped, "receipt uses an ordinary visible cell without auto-equipping");
            Check(receivedHelmet.m_customData["probe.enchantment"] == "Enchantments \u2603", "receipt retains item metadata");
            Remove(view, helmetRow, 1);
            Check(!source.GetAllItems().Contains(helmet), "full hidden-row item removal does not leave an orphan slot item");
            view = Capture(source, 5, 77, 44); ironRow = Row(view, "Iron");
            byte[] ironBlob = Prepare(view, ironRow, 2); Remove(view, ironRow, 2);
            Check(iron.m_stack == 3, "nested backpack partial removal changes the live child inventory");
            Check(!String.IsNullOrEmpty(bag.m_customData[bagKey]), "changed nested inventory is serialized into the backpack item");
            ItemDrop.ItemData loadedBag = bag.Clone();
            object loadedComponent = Backpack(loadedBag);
            var loadedContents = (Inventory)loadedComponent.GetType().GetMethod("GetInventory").Invoke(loadedComponent, null);
            Check(loadedContents.GetAllItems().Any(i => i.m_dropPrefab.name == "Iron" && i.m_stack == 3),
                "native backpack clone reconstructs the persisted remaining child count");
            view = Capture(source, 5, 77, 44);
            ironRow = Row(view, "Iron");
            source.GetAllItems().Remove(bag);
            try { Reject(() => Prepare(view, ironRow, 1), "contents of a backpack no longer owned by the target cannot be taken"); }
            finally { source.GetAllItems().Add(bag); }
            Check(iron.m_stack == 3, "a moved-backpack rejection leaves its child items intact");
            view = Capture(source, 5, 77, 44);
            byte[] bagBlob = Prepare(view, Row(view, bag.m_dropPrefab.name), 1);
            ItemDrop.ItemData receivedBag = (ItemDrop.ItemData)Invoke("ReadBlob", bagBlob);
            Check(receivedBag.m_customData[bagKey] == bag.m_customData[bagKey], "whole-backpack transfer retains the exact child inventory payload");
            object receivedBackpack = Backpack(receivedBag);
            Inventory receivedContents = (Inventory)receivedBackpack.GetType().GetMethod("GetInventory").Invoke(receivedBackpack, null);
            Check(receivedContents.GetAllItems().Any(i => i.m_dropPrefab.name == "Iron" && i.m_stack == 3),
                "whole-backpack transfer retains playable child inventory");
            for (int y = 0; y < 5; ++y) for (int x = 0; x < 8; ++x)
                if (destination.GetItemAt(x, y) == null) destination.GetAllItems().Add(Item("Stone", 50, x, y));
            Check(!(bool)Invoke("CanAdd", destination, 5, ironBlob), "empty hidden equipment rows cannot satisfy destination capacity");
            byte[] full = Save(destination);
            Reject(() => Invoke("Add", destination, 5, ironBlob), "a full destination rejects a transfer");
            Check(full.SequenceEqual(Save(destination)), "failed receipt does not partially mutate a full inventory");
        }
    }

    // Routes the production detector through actual native ZInput entry points;
    // keyboard states are isolated fixtures, never physical user input.
    public static class InventoryAdminInputNativeChecks
    {
        private static readonly HashSet<KeyCode> held = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> down = new HashSet<KeyCode>();
        private static int keyCalls, downCalls, checks;
        private static bool fixedUpdate;
        private static Type pluginType;
        private static readonly BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static void Check(bool value, string reason)
        { ++checks; if (!value) throw new InvalidOperationException("Inventory administration input check: " + reason); }
        private static bool KeyPrefix(KeyCode __0, bool __1, ref bool __result)
        { ++keyCalls; fixedUpdate |= __1; __result = held.Contains(__0); return false; }
        private static bool DownPrefix(KeyCode __0, bool __1, ref bool __result)
        { ++downCalls; fixedUpdate |= __1; __result = down.Contains(__0); return false; }
        private static void Keys(params KeyCode[] keys)
        { held.Clear(); down.Clear(); foreach (KeyCode key in keys) held.Add(key); }
        private static bool Read(string method, KeyboardShortcut key)
        { return (bool)pluginType.GetMethod(method, All).Invoke(null, new object[] { key }); }
        public static string Run()
        {
            checks = keyCalls = downCalls = 0; fixedUpdate = false;
            Check(Player.m_localPlayer == null && Game.instance == null, "input fixtures run outside a user character/world");
            object plugin = Chainloader.PluginInfos["valheimmodpack.inventoryadmin"].Instance; pluginType = plugin.GetType();
            MethodInfo getKey = AccessTools.Method(typeof(ZInput), "GetKey", new[] { typeof(KeyCode), typeof(bool) });
            MethodInfo getDown = AccessTools.Method(typeof(ZInput), "GetKeyDown", new[] { typeof(KeyCode), typeof(bool) });
            Check(getKey != null && getDown != null, "native dynamic/fixed key APIs are present");
            FieldInfo claimed = pluginType.GetField("claimed", All), released = pluginType.GetField("released", All);
            MethodInfo refresh = pluginType.GetMethod("RefreshClaimed", All);
            Check(claimed != null && released != null && refresh != null, "production claimed-key release endpoints are present");
            object originalClaimed = claimed.GetValue(plugin), originalReleased = released.GetValue(plugin);
            var harmony = new Harmony("valheimmodpack.inventoryadmin.optional-native-input");
            try
            {
                harmony.Patch(getKey, prefix: new HarmonyMethod(typeof(InventoryAdminInputNativeChecks), "KeyPrefix"));
                harmony.Patch(getDown, prefix: new HarmonyMethod(typeof(InventoryAdminInputNativeChecks), "DownPrefix"));
                var standard = new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl);
                Keys(KeyCode.LeftControl, KeyCode.F9); down.Add(KeyCode.F9);
                Check(Read("ShortcutHeld", standard) && Read("ShortcutDown", standard), "default Ctrl+F9 uses actual ZInput held/edge states");
                down.Clear();
                Check(Read("ShortcutHeld", standard) && !Read("ShortcutDown", standard), "holding a shortcut cannot manufacture another opening edge");
                Keys(KeyCode.RightControl, KeyCode.F9); down.Add(KeyCode.F9);
                Check(Read("ShortcutDown", standard), "right Control works with the default left-Control binding");
                Keys(KeyCode.F9); down.Add(KeyCode.F9);
                Check(!Read("ShortcutHeld", standard) && !Read("ShortcutDown", standard), "unmodified F9 does not invoke the Ctrl shortcut");
                Keys(KeyCode.LeftControl);
                Check(!Read("ShortcutHeld", standard) && !Read("ShortcutDown", standard), "released main key cannot leave the shortcut held");
                foreach (KeyCode extra in new[] { KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand })
                {
                    Keys(KeyCode.LeftControl, KeyCode.F9, extra); down.Add(KeyCode.F9);
                    Check(!Read("ShortcutDown", standard), "extra modifier does not alias default shortcut: " + extra);
                }
                var rebound = new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl);
                Keys(KeyCode.RightControl, KeyCode.F); down.Add(KeyCode.F);
                Check(Read("ShortcutHeld", rebound) && Read("ShortcutDown", rebound), "Ctrl+F rebind uses the current main key and right modifier");
                Check(!Read("ShortcutDown", standard), "rebound F cannot trigger the old F9 shortcut");
                var shifted = new KeyboardShortcut(KeyCode.F, KeyCode.LeftControl, KeyCode.LeftShift);
                Keys(KeyCode.RightControl, KeyCode.RightShift, KeyCode.F); down.Add(KeyCode.F);
                Check(Read("ShortcutDown", shifted), "two remapped modifiers accept their right-hand equivalents");
                Check(!Read("ShortcutDown", rebound), "Ctrl+Shift+F remains distinct from Ctrl+F");
                Keys(KeyCode.F9, KeyCode.LeftControl); down.Add(KeyCode.F9);
                Check(!Read("ShortcutHeld", new KeyboardShortcut(KeyCode.None)) && !Read("ShortcutDown", new KeyboardShortcut(KeyCode.None)),
                    "an unbound action never matches a physical fixture state");
                Keys(KeyCode.F9); claimed.SetValue(plugin, KeyCode.F9); released.SetValue(plugin, -1);
                refresh.Invoke(plugin, null);
                Check((KeyCode)claimed.GetValue(plugin) == KeyCode.F9 && (int)released.GetValue(plugin) == -1,
                    "held native main key retains its gameplay-input claim");
                Keys(); refresh.Invoke(plugin, null);
                Check((KeyCode)claimed.GetValue(plugin) == KeyCode.F9 && (int)released.GetValue(plugin) == Time.frameCount,
                    "native release records its dynamic frame without exposing buffered gameplay edges");
                refresh.Invoke(plugin, null);
                Check((KeyCode)claimed.GetValue(plugin) == KeyCode.F9, "the release frame keeps the original key consumed");
                Check(Time.frameCount > 0, "menu fixture has advanced beyond the first native frame");
                released.SetValue(plugin, Time.frameCount - 1); refresh.Invoke(plugin, null);
                Check((KeyCode)claimed.GetValue(plugin) == KeyCode.None && (int)released.GetValue(plugin) == -1,
                    "the following frame clears a released claim and does not stick indefinitely");
                Keys(KeyCode.LeftControl, KeyCode.F); claimed.SetValue(plugin, KeyCode.F9); released.SetValue(plugin, -1);
                refresh.Invoke(plugin, null);
                Check((int)released.GetValue(plugin) == Time.frameCount,
                    "holding a rebound F shortcut cannot keep the old released F9 claim stuck");
                Check(keyCalls > 0 && downCalls > 0 && !fixedUpdate, "production detector reads native dynamic keys, not legacy or fixed-update edges");
                return "InventoryAdmin native input PASS; " + checks + " checks (actual ZInput hooks with isolated key states; no physical keyboard/player actions).";
            }
            finally
            {
                claimed.SetValue(plugin, originalClaimed); released.SetValue(plugin, originalReleased);
                harmony.UnpatchSelf(); held.Clear(); down.Clear();
            }
        }
    }
}
