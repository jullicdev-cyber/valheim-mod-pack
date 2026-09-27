// Production AutoStoreFavorites/SlotPolicy with boundary doubles, never shipped.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using BepInEx.Bootstrap;
using HarmonyLib;
using ValheimModPack;
public struct Vector2i
{
    public int x, y;
    public Vector2i(int x, int y) { this.x = x; this.y = y; }
    public override int GetHashCode() { return x * 397 ^ y; }
    public override bool Equals(object value) { return value is Vector2i && ((Vector2i)value).x == x && ((Vector2i)value).y == y; }
}
public sealed class ItemDrop
{
    public sealed class ItemData
    {
        public sealed class SharedData { public string m_name; }
        public SharedData m_shared;
        public Vector2i m_gridPos;
        public bool m_equipped;
    }
}
public sealed class Inventory
{
    public int Width = 8, Height = 6;
    public readonly List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
    public int GetWidth() { return Width; } public int GetHeight() { return Height; }
    public List<ItemDrop.ItemData> GetAllItems() { return Items; }
}
public sealed class Player
{
    public static Player m_localPlayer;
    public long Id = 123;
    public int Warnings;
    public Inventory Inventory = new Inventory();
    public Inventory GetInventory() { return Inventory; }
    public long GetPlayerID() { return Id; }
    public void Message(MessageHud.MessageType type, string text, int amount, object icon) { Warnings++; }
}
public sealed class MessageHud { public enum MessageType { Center } }
namespace EquipmentAndQuickSlots
{ public static class API { public static int Visible = 5, Full = 6; public static int GetVisibleRows() { return Visible; } public static int GetFullHeight() { return Full; } } }
namespace ValheimModPack { public static class Plugin { public const string Id = "valheimmodpack.eaqsquickstackbridge"; } }
namespace BepInEx
{ public sealed class BaseUnityPlugin { public bool enabled = true; } public static class Paths { public static string ConfigPath; } }
namespace BepInEx.Bootstrap
{
    public sealed class Metadata { public System.Version Version; }
    public sealed class Info { public BepInEx.BaseUnityPlugin Instance = new BepInEx.BaseUnityPlugin(); public Metadata Metadata = new Metadata(); }
    public static class Chainloader { public static readonly Dictionary<string, Info> PluginInfos = new Dictionary<string, Info>(); }
}
namespace HarmonyLib
{
    public sealed class HarmonyMethod
    { public MethodInfo Method; public HarmonyMethod(Type type, string name) { Method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic); } }
    public sealed class Harmony
    {
        public sealed class Record { public string Owner; public MethodInfo Target, Prefix, Postfix; }
        public static readonly List<Record> Patches = new List<Record>();
        private string id;
        public Harmony(string id) { this.id = id; }
        public void Patch(MethodInfo target, HarmonyMethod prefix = null, HarmonyMethod postfix = null) { Patches.Add(new Record { Owner = id, Target = target, Prefix = prefix == null ? null : prefix.Method, Postfix = postfix == null ? null : postfix.Method }); }
        public void UnpatchSelf() { Patches.RemoveAll(record => record.Owner == id); }
    }
    public static class AccessTools
    {
        public static string Missing;
        public static Type TypeByName(string name) { return name == Missing ? null : typeof(AccessTools).Assembly.GetType(name); }
        public static MethodInfo Method(Type type, string name, Type[] args)
        { return type.FullName + ":" + name == Missing ? null : type.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null); }
        public static FieldInfo Field(Type type, string name) { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
    }
}
namespace QuickStackStore
{
    public sealed class UserConfig
    {
        public static bool Throw, Missing;
        public static readonly Dictionary<long, UserConfig> Configs = new Dictionary<long, UserConfig>();
        public HashSet<Vector2i> favoritedSlots = new HashSet<Vector2i>();
        public HashSet<string> favoritedItems = new HashSet<string>();
        public HashSet<Vector2i> Slots { get { return favoritedSlots; } }
        public HashSet<string> Types { get { return favoritedItems; } }
        private long id;
        public static UserConfig GetPlayerConfig(long id)
        {
            AutoStoreHostTests.Hook("BeforeGetConfig", id);
            if (Throw) throw new InvalidOperationException("Favorite API failed"); if (Missing) return null;
            UserConfig config; if (!Configs.TryGetValue(id, out config)) Configs[id] = config = new UserConfig { id = id };
            AutoStoreHostTests.Hook("AfterQuickConfig", id, config); return config;
        }
        private void Save() { File.WriteAllText(Path.Combine(BepInEx.Paths.ConfigPath, "QuickStackStore_player_" + id + ".dat"), "canonical"); AutoStoreHostTests.Hook("AfterQuickSave", this); }
        public bool IsItemNameOrSlotFavorited(ItemDrop.ItemData item) { if (Throw) throw new InvalidOperationException("Favorite API failed"); return Slots.Contains(item.m_gridPos) || Types.Contains(item.m_shared.m_name); }
    }
}
namespace AzuAutoStore
{ public sealed class AzuAutoStorePlugin { private void Update() { } } }
namespace AzuAutoStore.Util
{ public static class Functions { public static void TryStore() { } public static void TryStoreThisItem(ItemDrop.ItemData item, Inventory inventory) { } } }
namespace AzuAutoStore.Patches.Favoriting
{
    public sealed class UserConfig
    {
        public bool Favorite;
        public HashSet<Vector2i> _favoritedSlots = new HashSet<Vector2i>();
        public HashSet<string> _favoritedItems = new HashSet<string>();
        public static readonly Dictionary<long, UserConfig> Configs = new Dictionary<long, UserConfig>();
        public static UserConfig GetPlayerConfig(long id)
        {
            AutoStoreHostTests.Hook("BeforeGetConfig", id); UserConfig config;
            if (!Configs.TryGetValue(id, out config)) Configs[id] = config = new UserConfig();
            AutoStoreHostTests.Hook("AfterAzuConfig", id, config); return config;
        }
        private void Save() { AutoStoreHostTests.Hook("BeforeAzuSave", this); }
    }
}
namespace AzuAutoStore.Interfaces
{
    public static class VanillaContainers { private static bool CantStoreFavorite(ItemDrop.ItemData item, AzuAutoStore.Patches.Favoriting.UserConfig config) { return config.Favorite; } }
    public static class BackpackContainer { private static bool CantStoreFavorite(ItemDrop.ItemData item, AzuAutoStore.Patches.Favoriting.UserConfig config) { return config.Favorite; } }
    public static class kgDrawer { private static bool CantStoreFavorite(ItemDrop.ItemData item, AzuAutoStore.Patches.Favoriting.UserConfig config) { return config.Favorite; } }
    public static class mkzDrawer { private static bool CantStoreFavorite(ItemDrop.ItemData item, AzuAutoStore.Patches.Favoriting.UserConfig config) { return config.Favorite; } }
}
public static class AutoStoreHostTests
{
    private static int count;
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "vmp-azu-host-" + Guid.NewGuid().ToString("N"));
    public static object Hook(string name, params object[] args)
    { return typeof(AutoStoreFavorites).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static void AddPlugin(string id, string version) { Chainloader.PluginInfos[id] = new Info { Metadata = new Metadata { Version = new System.Version(version) } }; }
    private static void Reset()
    {
        Chainloader.PluginInfos.Clear(); Harmony.Patches.Clear(); AccessTools.Missing = null;
        Player.m_localPlayer = null; EquipmentAndQuickSlots.API.Visible = 5; EquipmentAndQuickSlots.API.Full = 6;
        QuickStackStore.UserConfig.Configs.Clear(); QuickStackStore.UserConfig.Throw = QuickStackStore.UserConfig.Missing = false;
        AzuAutoStore.Patches.Favoriting.UserConfig.Configs.Clear();
        BepInEx.Paths.ConfigPath = Path.Combine(TestRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(BepInEx.Paths.ConfigPath);
        AddPlugin("goldenrevolver.quick_stack_store", "1.4.15"); AddPlugin("randyknapp.mods.equipmentandquickslots", "3.1.3");
    }
    private static bool Call(string name)
    { return (bool)typeof(AutoStoreFavorites).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null); }
    private static bool Filter(ItemDrop.ItemData item, bool nativeFavorite)
    {
        object[] arguments = { item, false };
        bool original = (bool)typeof(AutoStoreFavorites).GetMethod("BeforeFavoriteFilter", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
        return original ? nativeFavorite : (bool)arguments[1];
    }
    // Azu's actual ShouldSkip IL checks equipment, optional hotbar, then CantStoreFavorite.
    private static bool NativeSkip(ItemDrop.ItemData item, bool ignoreHotbar, bool nativeFavorite)
    { return item.m_equipped || (ignoreHotbar && item.m_gridPos.y == 0 && item.m_gridPos.x >= 0 && item.m_gridPos.x <= 8) || Filter(item, nativeFavorite); }
    private static ItemDrop.ItemData Item(int x, int y, string type)
    { var item = new ItemDrop.ItemData { m_gridPos = new Vector2i(x, y), m_shared = new ItemDrop.ItemData.SharedData { m_name = type } }; Player.m_localPlayer.Inventory.Items.Add(item); return item; }
    public static void Main()
    {
        var errors = new List<Exception>(); Reset();
        using (var absent = new AutoStoreFavorites(s => { }, errors.Add)) { absent.InstallGuards(); absent.Initialize(); Check(Harmony.Patches.Count == 0 && errors.Count == 0, "Optional absent Azu causes no patches or error"); }
        Reset(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6");
        using (var bridge = new AutoStoreFavorites(s => { }, errors.Add))
        {
            bridge.InstallGuards(); Check(Harmony.Patches.Count == 3 && !bridge.Ready, "Awake installs three guards before config initialization");
            Check(!Call("BeforeUpdate") && !Call("BeforeStore"), "K and single inventory store blocked before filter readiness");
            bridge.Initialize(); Check(bridge.Ready && Harmony.Patches.Count == 11, "Four precise filters and four state-sharing hooks install before enabling store dispatch");
            Check(Call("BeforeUpdate") && !Call("BeforeStore"), "Ready Update runs, manual store still requires a local player");
            var owner = new Player(); Player.m_localPlayer = owner;
            Check(Call("BeforeStore"), "Consistent live player may use original store implementation");
            var plain = Item(2, 2, "$item_wood"); Check(!Filter(plain, false), "Ordinary item is not newly excluded");
            Check(Filter(plain, true), "Azu's own favorite is preserved");
            var config = QuickStackStore.UserConfig.GetPlayerConfig(owner.Id);
            config.Slots.Add(plain.m_gridPos); Check(Filter(plain, false), "Quick Stack favorite slot protects current item");
            plain.m_shared.m_name = "$item_stone"; Check(Filter(plain, false), "Favorite slot protects replacement item of another type");
            plain.m_gridPos.x++; Check(!Filter(plain, false), "Slot favorite does not follow an item moved out");
            config.Types.Add(plain.m_shared.m_name); Check(Filter(plain, false), "Favorite item type protects moved item");
            var stack = Item(5, 3, plain.m_shared.m_name); Check(Filter(stack, false), "Favorite type protects every matching stack");
            config.Types.Clear(); Check(!Filter(stack, false), "Unfavoriting applies immediately without reload");
            stack.m_equipped = true; Check(Filter(stack, false), "Equipped item remains protected even in an ordinary cell"); stack.m_equipped = false;
            stack.m_gridPos.y = 5; Check(Filter(stack, false), "EAQS extra equipment/quick row protected");
            stack.m_gridPos.x = 7; Check(Filter(stack, false), "Unused hidden cells protected as well");
            stack.m_gridPos.y = 4; Check(!Filter(stack, false), "VPlus extra visible inventory row stays usable");
            stack.m_gridPos.y = 0;
            Check(NativeSkip(stack, true, false), "Configured Azu hotbar exclusion remains active");
            Check(!NativeSkip(stack, false, false), "Explicit Azu hotbar opt-out is not silently overridden");
            config.Types.Add(stack.m_shared.m_name); Check(NativeSkip(stack, false, false), "Favorite still protects hotbar item when Azu hotbar exclusion disabled");
            var dropped = new ItemDrop.ItemData { m_gridPos = plain.m_gridPos, m_shared = plain.m_shared };
            Check(!Filter(dropped, false), "Ground/chest copy with matching favorite position/type is untouched");
            Check(Filter(dropped, true), "Existing Azu protection of external item is preserved");
            owner.Inventory.Items.Remove(plain); Check(!Filter(plain, false), "Former player item is no longer intercepted after transfer/drop");
            var other = new Player { Id = 456 }; Player.m_localPlayer = other;
            var unmarked = Item(2, 2, "$item_stone"); Check(!Filter(unmarked, false), "Another character does not inherit previous favorites");
            Player.m_localPlayer = owner;
            owner.Inventory.Height = 7; Check(!Call("BeforeStore") && !bridge.Ready, "Geometry mismatch blocks before any transfer");
            Check(owner.Warnings == 1 && errors.Count == 1, "Failure warns once and logs once");
            Call("BeforeUpdate"); Call("BeforeStore"); Check(owner.Warnings == 1 && errors.Count == 1, "Failure does not spam warnings");
            Check(Filter(stack, false) && !Filter(dropped, false), "Failed guard blocks local inventory only; ground flow remains unchanged");
        }
        Check(Harmony.Patches.Count == 0, "Disposal removes only owned hooks");
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.7"); Player.m_localPlayer = new Player();
        using (var mismatch = new AutoStoreFavorites(s => { }, errors.Add))
        { mismatch.InstallGuards(); mismatch.Initialize(); Check(!mismatch.Ready && !Call("BeforeStore") && Harmony.Patches.Count == 3, "Unknown Azu version keeps dispatch guards and never installs partial filters"); }
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6"); AccessTools.Missing = "QuickStackStore.UserConfig:IsItemNameOrSlotFavorited";
        using (var changed = new AutoStoreFavorites(s => { }, errors.Add))
        { changed.InstallGuards(); changed.Initialize(); Check(!changed.Ready && Harmony.Patches.Count == 3 && errors.Count == 1, "Changed Quick Stack API fails closed"); }
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6"); AccessTools.Missing = "AzuAutoStore.Interfaces.mkzDrawer:CantStoreFavorite";
        using (var changed = new AutoStoreFavorites(s => { }, errors.Add))
        { changed.InstallGuards(); changed.Initialize(); Check(!changed.Ready && Harmony.Patches.Count == 3, "Missing one favorite filter cannot partially enable K"); }
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6"); AccessTools.Missing = "AzuAutoStore.Util.Functions:TryStore";
        using (var changed = new AutoStoreFavorites(s => { }, errors.Add))
        { changed.InstallGuards(); changed.Initialize(); Check(!changed.Ready && !Chainloader.PluginInfos[AutoStoreFavorites.Azu].Instance.enabled && !Call("BeforeUpdate"), "Changed dispatch disables shortcut component and retains fallback Update guard"); }
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6");
        using (var throwing = new AutoStoreFavorites(s => { }, errors.Add))
        {
            throwing.InstallGuards(); throwing.Initialize(); Player.m_localPlayer = new Player(); var item = Item(3, 2, "$item_wood");
            QuickStackStore.UserConfig.Throw = true;
            Check(Filter(item, false) && !throwing.Ready && !Call("BeforeStore"), "Favorite API exception blocks current and subsequent inventory storing");
        }
        Reset(); errors.Clear(); AddPlugin(AutoStoreFavorites.Azu, "3.1.6");
        using (var unavailable = new AutoStoreFavorites(s => { }, errors.Add))
        {
            unavailable.InstallGuards(); unavailable.Initialize(); Player.m_localPlayer = new Player(); QuickStackStore.UserConfig.Missing = true;
            Check(!Call("BeforeStore") && !unavailable.Ready, "Missing favorite data cannot be interpreted as no favorites");
        }
        System.Console.WriteLine("OK: " + count + " production Azu/Quick Stack favorite bridge lifecycle and filter assertions.");
        string resolved = Path.GetFullPath(TestRoot), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("vmp-azu-host-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
    }
}
