using System;
using System.IO;
using System.Collections.Generic;

namespace UnityEngine
{
    public enum KeyCode { None, F8, F9, F10, F11, LeftControl, RightControl, LeftShift, RightShift, LeftAlt, RightAlt, LeftCommand, RightCommand }
    public struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z) { this.x=x; this.y=y; this.z=z; } public static Vector3 zero { get { return new Vector3(); } } }
    public sealed class Transform { public Vector3 position; }
    public static class Time { public static float realtimeSinceStartup; public static int frameCount; }
    public static class Input { public static bool GetKey(KeyCode key) { return false; } public static bool GetKeyDown(KeyCode key) { return false; } }
    public class GameObject { public string name; public T GetComponent<T>() where T : class { return null; } }
    public class Sprite { }
}
namespace BepInEx.Configuration
{
    public struct KeyboardShortcut
    {
        public UnityEngine.KeyCode MainKey; public UnityEngine.KeyCode[] Modifiers;
        public KeyboardShortcut(UnityEngine.KeyCode main, params UnityEngine.KeyCode[] modifiers) { MainKey = main; Modifiers = modifiers; }
    }
    public sealed class ConfigEntry<T> { public T Value; }
    public sealed class ConfigFile
    { public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) { return new ConfigEntry<T> { Value = value }; } }
}
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public sealed class BepInDependency : Attribute { public BepInDependency(string id, string version) { } }
    public static class Paths { public static string BepInExRootPath; }
    public sealed class FakeLogger
    {
        public readonly List<string> Errors = new List<string>();
        public void LogError(object value) { Errors.Add(value.ToString()); }
        public void LogWarning(object value) { Errors.Add(value.ToString()); }
    }
    public class BaseUnityPlugin
    { public readonly BepInEx.Configuration.ConfigFile Config = new BepInEx.Configuration.ConfigFile(); public readonly FakeLogger Logger = new FakeLogger(); }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    public sealed class Harmony { public Harmony(string id) { } public void PatchAll(System.Reflection.Assembly assembly) { } public void UnpatchSelf() { } }
}
namespace Jotunn.Managers { public sealed class GUIManager { } }
public sealed class Localization
{ public static Localization instance; public string GetSelectedLanguage() { return "English"; } public string Localize(string value) { return value; } }
public sealed class Player
{
    public static Player m_localPlayer; public long Character = 100; public string Name = "Host";
    public readonly UnityEngine.Transform transform = new UnityEngine.Transform(); public bool Dead; public bool IsDead() { return Dead; }
    public long GetPlayerID() { return Character; } public string GetPlayerName() { return Name; }
    public bool IsTeleporting() { return false; } public bool InCutscene() { return false; }
    public void Message(MessageHud.MessageType type, string message) { }
}
public sealed class MessageHud { public enum MessageType { Center } }
public static class InventoryGui { public static bool IsVisible() { return false; } }
public static class Menu { public static bool IsVisible() { return false; } }
public static class Console { public static bool IsVisible() { return false; } }
public sealed class Chat { public static Chat instance; public bool HasFocus() { return false; } }
public static class TextInput { public static bool IsVisible() { return false; } }
public static class UnifiedPopup { public static bool IsVisible() { return false; } }
public sealed class ObjectDB { public static ObjectDB instance; public UnityEngine.GameObject GetItemPrefab(string value) { return null; } }
public sealed class ItemDrop
{
    public readonly ItemData m_itemData = new ItemData();
    public sealed class ItemData
    {
        public int m_stack = 2, m_quality = 1, m_variant; public float m_durability;
        public UnityEngine.GameObject m_dropPrefab = new UnityEngine.GameObject { name = "Wood" };
        public ItemData Clone() { return (ItemData)MemberwiseClone(); }
        public float GetMaxDurability() { return 100; } public UnityEngine.Sprite GetIcon() { return null; }
    }
}
public sealed class Terminal
{
    public delegate void ConsoleEvent(ConsoleEventArgs args);
    public sealed class ConsoleCommand { public ConsoleCommand(string name, string description, ConsoleEvent callback) { } }
    public sealed class ConsoleEventArgs
    {
        public string[] Values; public int Length { get { return Values.Length; } } public string this[int index] { get { return Values[index]; } }
        public readonly ContextType Context = new ContextType();
    }
    public sealed class ContextType { public readonly List<string> Lines = new List<string>(); public void AddString(string value) { Lines.Add(value); } }
}
public static class ZInput
{
    public static readonly HashSet<UnityEngine.KeyCode> Held = new HashSet<UnityEngine.KeyCode>();
    public static readonly HashSet<UnityEngine.KeyCode> Down = new HashSet<UnityEngine.KeyCode>();
    public static bool GetKey(UnityEngine.KeyCode key, bool ignoreInvertedControls) { return Held.Contains(key); }
    public static bool GetKeyDown(UnityEngine.KeyCode key, bool ignoreInvertedControls) { return Down.Contains(key); }
}
public interface IServiceTestSocket { string GetHostName(); }
public class ZSteamSocket : IServiceTestSocket
{ public string Owner; public virtual string GetHostName() { return Owner; } }
public sealed class DecoratedSteamSocket : ZSteamSocket { }
public sealed class UnsupportedServiceTestSocket : IServiceTestSocket
{
    public int HostNameReads; public string HostName = "untrusted"; public bool ThrowOnRead;
    public string GetHostName() { ++HostNameReads; if (ThrowOnRead) throw new IOException("Socket identity is no longer available."); return HostName; }
}
public sealed class ZRpc
{
    public bool Connected = true; public readonly List<ZPackage> Sent = new List<ZPackage>();
    public bool IsConnected() { return Connected; }
    public void Register<T>(string name, Action<ZRpc, T> callback) { }
    public void Invoke(string name, ZPackage package) { Sent.Add(new ZPackage(package.GetArray())); }
}
public sealed class ZNetPeer
{
    public IServiceTestSocket m_socket; public ZRpc m_rpc = new ZRpc(); public long m_uid, m_playerID;
    public string m_playerName = "Player"; public bool Ready = true; public bool IsReady() { return Ready; }
    public ZDOID m_characterID; public UnityEngine.Vector3 m_refPos; public bool m_publicRefPos;
}
public struct ZDOID { public long UserID; public uint ID; public bool IsNone() { return UserID == 0 && ID == 0; } }
public static class ZDOVars { public const int s_playerID=1,s_dead=2; }
public sealed class ZDO
{
    public UnityEngine.Vector3 Position; public long Character; public bool Dead;
    public long GetLong(int key,long fallback) { return Character; } public bool GetBool(int key,bool fallback) { return Dead; }
}
public sealed class ZDOMan
{
    public static ZDOMan instance;
    public readonly Dictionary<ZDOID,ZDO> Zdos = new Dictionary<ZDOID,ZDO>();
    public ZDO GetZDO(ZDOID id) { ZDO value; return Zdos.TryGetValue(id,out value) ? value : null; }
}
public sealed class ZNet
{
    public static ZNet instance; public static long Uid = 77;
    public readonly List<ZNetPeer> Peers = new List<ZNetPeer>(); public bool Server = true; public long World = 999;
    public static long GetUID() { return Uid; } public bool IsServer() { return Server; }
    public long GetWorldUID() { return World; } public List<ZNetPeer> GetPeers() { return Peers; }
    public ZNetPeer GetServerPeer() { return Peers.Count == 0 ? null : Peers[0]; }
}
public sealed class ZPackage
{
    private readonly MemoryStream stream; private readonly BinaryReader reader; private readonly BinaryWriter writer;
    public ZPackage() { stream = new MemoryStream(); reader = new BinaryReader(stream); writer = new BinaryWriter(stream); }
    public ZPackage(byte[] bytes) { stream = new MemoryStream(bytes, true); reader = new BinaryReader(stream); writer = new BinaryWriter(stream); }
    public byte[] GetArray() { return stream.ToArray(); } public int GetPos() { return (int)stream.Position; } public int Size() { return (int)stream.Length; }
    public void Write(int value) { writer.Write(value); } public void Write(string value) { writer.Write(value); }
    public void Write(byte[] value) { writer.Write(value.Length); writer.Write(value); }
    public int ReadInt() { return reader.ReadInt32(); } public string ReadString() { return reader.ReadString(); }
}
namespace ValheimModPack.WorldCharacters
{
    public static class Plugin
    {
        public static bool AdministrativeReady = true;
        public static readonly Dictionary<long, long> Durable = new Dictionary<long, long>();
        public static readonly Dictionary<ZNetPeer, string> ApprovedOwners = new Dictionary<ZNetPeer, string>();
        public static readonly Dictionary<ZRpc, ZNetPeer> ApprovedConnections = new Dictionary<ZRpc, ZNetPeer>();
        public static long Next = 1000;
        public static bool IsAdministrativePeerReady(long peer) { return Durable.ContainsKey(peer); }
        public static long GetAdministrativeDurableSequence(long peer) { long value; return Durable.TryGetValue(peer, out value) ? value : 0; }
        public static long GetAdministrativeCharacter(long peer)
        {
            if (peer == ZNet.GetUID()) return Player.m_localPlayer.GetPlayerID();
            foreach (ZNetPeer candidate in ZNet.instance.GetPeers()) if (candidate.m_uid == peer) return candidate.m_playerID;
            return 0;
        }
        public static string GetAdministrativeOwner(ZNetPeer peer)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || peer == null) return String.Empty;
            string owner; ZNetPeer approved;
            return Durable.ContainsKey(peer.m_uid) && ApprovedOwners.TryGetValue(peer, out owner) && ZNet.instance.Peers.Contains(peer)
                && peer.Ready && peer.m_rpc != null && peer.m_rpc.IsConnected() && ApprovedConnections.TryGetValue(peer.m_rpc, out approved)
                && ReferenceEquals(approved, peer) ? owner : String.Empty;
        }
        public static long RequestAdministrativeSave() { return ++Next; }
        public static bool IsAdministrativeSaveDurable(long sequence) { return GetAdministrativeDurableSequence(ZNet.GetUID()) >= sequence; }
    }
}
namespace ValheimModPack.InventoryAdmin
{
    internal static class GameplayInputCache
    {
        public static void ConsumeAll() { }
        public static void Consume(string name) { }
    }
    public sealed class CapturedInventory { public InventoryView View; }
    public static class NativeAdapter
    {
        public static int Added, Removed; public static bool CanReceive = true;
        public static int BlobStack = 2, BlobQuality = 1, BlobVariant; public static float BlobDurability;
        public static string BlobPrefab = "Wood";
        public static CapturedInventory BuildView(Player player, long peer, string owner, long character, string id)
        { return new CapturedInventory { View = new InventoryView { RequestId = id, TargetPeerId = peer, TargetOwner = owner, TargetCharacter = character } }; }
        public static byte[] Prepare(CapturedInventory capture, string slot, string fingerprint, int count) { return new byte[] { 1, 2, 3 }; }
        public static ItemDrop.ItemData ReadBlob(byte[] blob)
        {
            if (blob == null || blob.Length == 0) throw new InvalidDataException("Missing item blob.");
            return new ItemDrop.ItemData { m_stack = BlobStack, m_quality = BlobQuality, m_variant = BlobVariant, m_durability = BlobDurability,
                m_dropPrefab = new UnityEngine.GameObject { name = BlobPrefab } };
        }
        public static bool CanAdd(Player player, byte[] blob) { ReadBlob(blob); return CanReceive; }
        public static void Remove(CapturedInventory capture, string slot, string fingerprint, int count) { ++Removed; }
        public static void Add(Player player, byte[] blob) { if (!CanReceive) throw new InvalidOperationException("No space."); ++Added; }
    }
    public sealed class AdminUiBindings
    {
        public Func<bool> CanUse, IsHost; public Func<long> LocalPeerId; public Func<string> ShortcutLabel;
        public Func<bool> IsTrackingPlayers; public Func<string> TrackingShortcutLabel; public Action<bool> SetTrackingPlayers;
        public Func<long,bool> CanFindPlayerOnMap; public Action<long> FindPlayerOnMap;
        public Func<GroupRadiusAdminView> GetGroupRadius; public Func<string> GroupRadiusShortcutLabel;
        public Action<long,bool,float> UpdateGroupRadius; public Action<long,long> SetGroupRadiusLeader; public Action<long,long,bool> SetGroupRadiusExemption;
        public Func<string,string,string> Translate; public Action RequestPlayers; public Action<long> RequestInventory;
        public Action<string,string,int> Delete, Take; public Action<long,bool> SetAdmin; public Action<Exception> Error; public Action OnClosed;
    }
    public sealed class AdminWindow : IDisposable
    {
        public bool IsVisible, Busy; public string Status = ""; public List<AdminPlayerView> Players = new List<AdminPlayerView>();
        public AdminWindow(AdminUiBindings binding) { }
        public void Show() { IsVisible = true; } public void Hide() { IsVisible = false; }
        public void ShowGroupRadius() { IsVisible = true; }
        public void Tick() { } public void SetStatus(string text) { Status = text; } public void SetBusy(bool busy) { Busy = busy; }
        public void HandleInputReset() { }
        public void SetPlayers(List<AdminPlayerView> views) { Players = new List<AdminPlayerView>(views); } public void SetSnapshot(AdminInventoryView view) { }
        public void Dispose() { }
    }
    public sealed class AdminMapOverlay
    {
        public AdminMapOverlay(Func<bool> access,Func<bool> tracking,Func<IList<PlayerLocation>> locations) { }
        public void Tick() { } public void Clear() { } public bool CanFind(long id) { return false; } public bool Find(long id) { return false; }
    }
    public sealed class AdminPlayerView { public long PeerId; public string Name; public bool IsAdmin; }
    public sealed class AdminItemView
    {
        public string ItemToken, Prefab, Name, Group; public int Count, Quality, SlotX, SlotY; public float Durability, MaxDurability;
        public bool Equipped; public UnityEngine.Sprite Icon;
    }
    public sealed class AdminInventoryView { public long PeerId; public string Name, SnapshotToken; public List<AdminItemView> Items = new List<AdminItemView>(); }
    public sealed class GroupRadiusAdminView
    {
        public bool Enabled,ReadOnly; public float Radius; public long LeaderPeerId,Revision; public string LeaderName,Notice;
        public HashSet<long> ExemptPeers = new HashSet<long>();
    }
    public sealed class GroupRadiusFrame
    {
        public bool Active,Exempt; public float Radius,GraceUntil; public UnityEngine.Vector3 LeaderPosition; public long Sequence; public Action<string> Notice;
    }
    public static class GroupRadiusMotion
    {
        public static bool LogicalValid = true;
        public static void Bind(Func<GroupRadiusFrame> accessor) { }
        public static void Tick() { } public static void ResetWorld() { }
        public static bool TryGetLogicalPosition(Player player,out UnityEngine.Vector3 point)
        { point = player == null ? UnityEngine.Vector3.zero : player.transform.position; return player != null && !player.IsDead() && LogicalValid; }
    }
}
