using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static implicit operator bool(Object value) { return !ReferenceEquals(value, null); }
        public static bool operator !(Object value) { return ReferenceEquals(value, null); }
    }
    public class GameObject : Object
    {
        public readonly Dictionary<Type, object> Components = new Dictionary<Type, object>();
        public T GetComponent<T>() where T : class { object value; return Components.TryGetValue(typeof(T), out value) ? (T)value : null; }
    }
    public enum KeyCode { None, UpArrow, DownArrow }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public override bool Equals(object value) { return value is Vector3 && Equals((Vector3)value); }
        public bool Equals(Vector3 value) { return x == value.x && y == value.y && z == value.z; }
        public override int GetHashCode() { return x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode(); }
        public override string ToString() { return x + "," + y + "," + z; }
        public static bool operator ==(Vector3 a, Vector3 b) { return a.Equals(b); }
        public static bool operator !=(Vector3 a, Vector3 b) { return !a.Equals(b); }
    }
    public static class Time { public static float realtimeSinceStartup; }
}

public struct ZDOID : IEquatable<ZDOID>
{
    public long userID;
    public uint id;
    public ZDOID(long userID, uint id) { this.userID = userID; this.id = id; }
    public static ZDOID None { get { return new ZDOID(); } }
    public bool IsNone() { return userID == 0 && id == 0; }
    public bool Equals(ZDOID other) { return userID == other.userID && id == other.id; }
    public override bool Equals(object value) { return value is ZDOID && Equals((ZDOID)value); }
    public override int GetHashCode() { return userID.GetHashCode() ^ id.GetHashCode(); }
    public override string ToString() { return userID + ":" + id; }
    public static bool operator ==(ZDOID a, ZDOID b) { return a.Equals(b); }
    public static bool operator !=(ZDOID a, ZDOID b) { return !a.Equals(b); }
}

public sealed class ZPackage
{
    private readonly MemoryStream stream;
    private readonly BinaryReader reader;
    private readonly BinaryWriter writer;
    public ZPackage() { stream = new MemoryStream(); reader = new BinaryReader(stream, Encoding.UTF8); writer = new BinaryWriter(stream, Encoding.UTF8); }
    public ZPackage(byte[] bytes) : this() { writer.Write(bytes); stream.Position = 0; }
    public int GetPos() { return (int)stream.Position; }
    public void SetPos(int position) { stream.Position = position; }
    public int Size() { return (int)stream.Length; }
    public byte[] GetArray() { return stream.ToArray(); }
    public void Write(int value) { writer.Write(value); }
    public void Write(long value) { writer.Write(value); }
    public void Write(float value) { writer.Write(value); }
    public void Write(string value) { writer.Write(value); }
    public void Write(bool value) { writer.Write(value); }
    public void Write(ZDOID value) { writer.Write(value.userID); writer.Write(value.id); }
    public void Write(UnityEngine.Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
    public void Write(ZPackage value) { byte[] bytes = value.GetArray(); writer.Write(bytes.Length); writer.Write(bytes); }
    public int ReadInt() { return reader.ReadInt32(); }
    public long ReadLong() { return reader.ReadInt64(); }
    public bool ReadBool() { return reader.ReadBoolean(); }
    public string ReadString() { return reader.ReadString(); }
    public ZDOID ReadZDOID() { return new ZDOID(reader.ReadInt64(), reader.ReadUInt32()); }
    public UnityEngine.Vector3 ReadVector3() { return new UnityEngine.Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); }
    public ZPackage ReadPackage()
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid nested package length.");
        return new ZPackage(reader.ReadBytes(length));
    }
}

public static class ZDOExtraData { public enum ConnectionType { Portal } }

public sealed class ZDO
{
    private readonly Dictionary<string, object> values = new Dictionary<string, object>();
    public ZDOID m_uid;
    public int m_prefab;
    public UnityEngine.Vector3 Position;
    public long Owner;
    public bool Live = true;
    public bool IsPortal = true;
    public ZDOID Connection;
    public UnityEngine.Vector3 GetPosition() { return Position; }
    public int GetPrefab() { return m_prefab; }
    public bool IsValid() { return Live; }
    public void SetOwner(long value) { Owner = value; }
    public long GetOwner() { return Owner; }
    public void SetConnection(ZDOExtraData.ConnectionType type, ZDOID value) { Connection = value; }
    public void Set(string key, string value) { values[key] = value; }
    public void Set(string key, long value) { values[key] = value; }
    public void Set(string key, int value) { values[key] = value; }
    public void Set(string key, ZDOID value) { values[key] = value; }
    public string GetString(string key, string fallback = "") { object value; return values.TryGetValue(key, out value) ? (string)value : fallback; }
    public long GetLong(string key, long fallback = 0) { object value; return values.TryGetValue(key, out value) ? (long)value : fallback; }
    public int GetInt(string key, int fallback = 0) { object value; return values.TryGetValue(key, out value) ? (int)value : fallback; }
    public ZDOID GetZDOID(string key) { object value; return values.TryGetValue(key, out value) ? (ZDOID)value : ZDOID.None; }
}

public sealed class ZDOMan
{
    public static ZDOMan instance;
    public readonly Dictionary<ZDOID, ZDO> m_objectsByID = new Dictionary<ZDOID, ZDO>();
    public ZDO GetZDO(ZDOID id) { ZDO value; return m_objectsByID.TryGetValue(id, out value) && value.Live ? value : null; }
    public List<ZDO> GetPortalList() { return m_objectsByID.Values.Where(value => value.Live && value.IsPortal).ToList(); }
    public static long GetSessionID() { return ZNet.GetUID(); }
    public void ForceSendZDO(ZDOID id) { }
}

public sealed class WorldGenerator
{
    public static WorldGenerator instance;
    public Func<UnityEngine.Vector3, Heightmap.Biome> Selector = point => Heightmap.Biome.Meadows;
    public Heightmap.Biome GetBiome(UnityEngine.Vector3 point) { return Selector(point); }
}

public static class Heightmap
{
    public enum Biome { None = 0, Meadows = 1, Swamp = 2, Mountain = 4, BlackForest = 8, Plains = 16, AshLands = 32, DeepNorth = 64, Ocean = 256, Mistlands = 512 }
}

public sealed class ZNetPeer
{
    public long m_uid;
    public bool Ready = true;
    public ZRpc m_rpc = new ZRpc();
    public bool IsReady() { return Ready; }
}

public sealed class ZRpc { public bool Connected = true; public bool IsConnected() { return Connected; } }

public sealed class ZNet
{
    public static ZNet instance;
    public bool Server = true;
    public long Uid = 77;
    public long World = 999;
    public readonly List<ZNetPeer> Peers = new List<ZNetPeer>();
    public bool IsServer() { return Server; }
    public static long GetUID() { return instance == null ? 0 : instance.Uid; }
    public long GetWorldUID() { return World; }
    public List<ZNetPeer> GetPeers() { return Peers; }
    public List<ZNetPeer> GetConnectedPeers() { return Peers; }
    public ZNetPeer GetPeer(long id) { return Peers.FirstOrDefault(peer => peer.m_uid == id); }
}

public sealed class ZRoutedRpc
{
    public static ZRoutedRpc instance;
    public long ServerPeerId = 88;
    public long GetServerPeerID() { return ServerPeerId; }
}

public sealed class Localization
{
    public static Localization instance = new Localization();
    public string Localize(string token) { return token; }
}

public sealed class Game
{
    public static Game instance = new Game();
    public TestProfile GetPlayerProfile() { return new TestProfile(); }
}
public sealed class TestProfile { public string GetName() { return "Player"; } }
public sealed class ObjectDB : UnityEngine.Object
{
    public static ObjectDB instance;
    public UnityEngine.GameObject Hammer;
    public UnityEngine.GameObject GetItemPrefab(string name) { return Hammer; }
}
public sealed class ItemDrop : UnityEngine.Object
{
    public readonly ItemData m_itemData = new ItemData();
    public sealed class ItemData { public readonly SharedData m_shared = new SharedData(); }
    public sealed class SharedData { public readonly BuildPieces m_buildPieces = new BuildPieces(); }
    public sealed class BuildPieces { public readonly List<UnityEngine.GameObject> m_pieces = new List<UnityEngine.GameObject>(); }
}
public sealed class Piece : UnityEngine.Object
{
    public Requirement[] m_resources;
    public sealed class Requirement { public ItemDrop m_resItem; public int m_amount; }
}
public sealed class Minimap
{
    public static Minimap instance = new Minimap();
    public void ShowPointOnMap(UnityEngine.Vector3 point) { }
}

namespace BepInEx
{
    public sealed class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    public sealed class BepInIncompatibility : Attribute { public BepInIncompatibility(string id) { } }
    public sealed class BepInDependency : Attribute { public BepInDependency(string id) { } }
    public class BaseUnityPlugin { public readonly Configuration.ConfigFile Config = new Configuration.ConfigFile(); }
}
namespace BepInEx.Configuration
{
    public struct KeyboardShortcut
    {
        public UnityEngine.KeyCode MainKey;
        public KeyboardShortcut(UnityEngine.KeyCode key) { MainKey = key; }
    }
    public sealed class ConfigEntry<T>
    {
        private T value;
        private readonly ConfigFile owner;
        public ConfigEntry(ConfigFile owner, T value) { this.owner = owner; this.value = value; }
        public T Value { get { return value; } set { if (EqualityComparer<T>.Default.Equals(this.value, value)) return; this.value = value; owner.Changed(); } }
    }
    public sealed class ConfigFile
    {
        private readonly Dictionary<string, object> entries = new Dictionary<string, object>();
        public event EventHandler ConfigReloaded;
        public event EventHandler SettingChanged;
        public ConfigEntry<T> Bind<T>(string section, string key, T fallback, string description)
        {
            string id = section + "/" + key; object value;
            if (!entries.TryGetValue(id, out value)) { value = new ConfigEntry<T>(this, fallback); entries.Add(id, value); }
            return (ConfigEntry<T>)value;
        }
        internal void Changed() { if (SettingChanged != null) SettingChanged(this, EventArgs.Empty); }
        public void Reload() { if (ConfigReloaded != null) ConfigReloaded(this, EventArgs.Empty); }
    }
}
namespace Jotunn
{
    public static class Main { public const string ModGuid = "com.jotunn.jotunn", Version = "2.30.2"; }
}
namespace Jotunn.Utils
{
    public enum CompatibilityLevel { EveryoneMustHaveMod }
    public enum VersionStrictness { Patch }
    public sealed class NetworkCompatibility : Attribute { public NetworkCompatibility(CompatibilityLevel level, VersionStrictness strictness) { } }
}
namespace Jotunn.Managers
{
    public static class MinimapManager
    {
        public static event Action OnVanillaMapDataLoaded;
        public static void Loaded() { if (OnVanillaMapDataLoaded != null) OnVanillaMapDataLoaded(); }
    }
}

public static class StringExtensions
{
    public static int GetStableHashCode(this string value)
    {
        unchecked
        {
            int first = 5381, second = first;
            for (int i = 0; i < value.Length && value[i] != '\0'; i += 2)
            {
                first = ((first << 5) + first) ^ value[i];
                if (i == value.Length - 1 || value[i + 1] == '\0') break;
                second = ((second << 5) + second) ^ value[i + 1];
            }
            return first + second * 1566083941;
        }
    }
}

namespace XPortal
{
    internal static class Environment
    {
        internal static bool IsServer { get { return ZNet.instance != null && ZNet.instance.IsServer(); } }
        internal static long ServerPeerId { get { return ZRoutedRpc.instance.GetServerPeerID(); } }
        internal static bool IsHeadless = true;
    }
    internal static class Log
    {
        internal static readonly List<string> Warnings = new List<string>();
        internal static readonly List<string> Errors = new List<string>();
        internal static void Debug(string text) { }
        internal static void Info(string text) { }
        internal static void Warning(string text) { Warnings.Add(text); }
        internal static void Error(string text) { Errors.Add(text); }
    }
    internal static class PortalColour
    {
        internal static string GetPortalColour(ZDOID id) { return "#FF6400"; }
    }
}

namespace XPortal.Plus { internal static class PlusMapMarkers { internal static void Tick() { } internal static void Reset() { } } }
namespace XPortal.Patches { internal static class Patcher { internal static void Patch() { } internal static void Unpatch() { } } }
namespace XPortal.UI
{
    internal sealed class PortalConfigurationPanel
    {
        internal static readonly PortalConfigurationPanel Instance = new PortalConfigurationPanel();
        internal bool Active;
        internal void AddInputs() { }
        internal bool IsActive() { return Active; }
        internal void HandleInput() { }
        internal void Dispose() { Active = false; }
        internal void Hide(bool delayed, object state = null) { Active = false; }
        internal void ConfigurePortal(KnownPortal portal) { Active = true; }
    }
}

namespace XPortal.RPC
{
    internal static class SendToClient
    {
        internal static readonly List<KnownPortal> Updates = new List<KnownPortal>();
        internal static readonly List<byte[]> Snapshots = new List<byte[]>();
        internal static int ConfigRequests;
        internal static void SyncPortal(KnownPortal portal) { Updates.Add(new KnownPortal(new ZPackage(portal.Pack().GetArray()))); }
        internal static void Resync(ZPackage package, string reason) { Snapshots.Add(package.GetArray()); }
        internal static void Config(long peer, ZPackage package) { ConfigRequests++; }
        internal static void Config(ZPackage package) { ConfigRequests++; }
        internal static void PingMap(UnityEngine.Vector3 point, string name) { }
    }
    internal static class SendToServer
    {
        internal static readonly List<KnownPortal> Updates = new List<KnownPortal>();
        internal static void ConfigRequest() { }
        internal static void SyncRequest(string reason) { }
        internal static void RemoveRequest(ZDOID id) { }
        internal static void AddOrUpdateRequest(KnownPortal portal)
        {
            byte[] bytes = portal.Pack().GetArray();
            Updates.Add(new KnownPortal(new ZPackage(bytes)));
            if (Environment.IsServer) Server.ServerEvents.RPC_AddOrUpdateRequest(ZNet.GetUID(), new ZPackage(bytes));
        }
    }
    internal static class RPCManager { internal static void Register() { } }
}
