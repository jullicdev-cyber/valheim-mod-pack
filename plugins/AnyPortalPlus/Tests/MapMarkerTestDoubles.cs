using System;
using System.Collections.Generic;
using System.Reflection;
namespace UnityEngine
{
    public class Sprite { }
    public struct Vector3 { public float x, y, z; public Vector3(float a, float b, float c) { x = a; y = b; z = c; } }
    public static class Time { public static float unscaledTime; }
}
namespace BepInEx { public static class Paths { public static string GameRootPath; } }
namespace HarmonyLib
{
    public static class AccessTools
    {
        public static bool HistoryEnabled;
        public static Type TypeByName(string name) { return HistoryEnabled ? typeof(AccessTools).Assembly.GetType(name) : null; }
        public static FieldInfo Field(Type t, string name) { return t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
        public static MethodInfo Method(Type t, string name, Type[] args) { return t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, args, null); }
    }
}
namespace Splatform
{
    public struct PlatformUserID
    {
        public string Text; public static PlatformUserID None; public bool IsValid { get { return !String.IsNullOrEmpty(Text); } }
        public override string ToString() { return Text ?? ""; }
    }
}
public struct ZDOID
{
    public long Owner; public uint Id; public static ZDOID None;
    public ZDOID(long owner, uint id) { Owner = owner; Id = id; }
    public override string ToString() { return Owner + ":" + Id; }
    public override bool Equals(object other) { return other is ZDOID && this == (ZDOID)other; }
    public override int GetHashCode() { return Owner.GetHashCode() ^ Id.GetHashCode(); }
    public static bool operator ==(ZDOID a, ZDOID b) { return a.Owner == b.Owner && a.Id == b.Id; }
    public static bool operator !=(ZDOID a, ZDOID b) { return !(a == b); }
}
public class ZDO { public ZDOID m_uid; public UnityEngine.Vector3 Position; public bool Valid = true; public bool IsValid() { return Valid; } public UnityEngine.Vector3 GetPosition() { return Position; } }
public class ZDOMan
{
    public static ZDOMan instance; public List<ZDO> Portals = new List<ZDO>();
    public List<ZDO> GetPortalList() { return Portals; }
}
public class ZNet
{
    public static ZNet instance; public long World = 99; public bool Server;
    public long GetWorldUID() { return World; } public bool IsServer() { return Server; }
}
public class MessageHud { public enum MessageType { Center } }
public class Player
{
    public static Player m_localPlayer; public long Character = 55; public List<string> Messages = new List<string>();
    public long GetPlayerID() { return Character; }
    public void Message(MessageHud.MessageType type, string value, int amount, UnityEngine.Sprite icon) { Messages.Add(value); }
}
public class Localization
{
    public static Localization instance = new Localization(); public string GetSelectedLanguage() { return "English"; }
}
public class Minimap
{
    public static Minimap instance;
    public enum PinType { Icon0 = 0, Icon1 = 1, Icon2 = 2, Icon3 = 3, Icon4 = 6 }
    public class PinNameData { public PinNameData(PinData pin) { } }
    public class PinData
    {
        public bool m_save, m_checked; public string m_name; public PinType m_type; public UnityEngine.Vector3 m_pos;
        public long m_ownerID; public Splatform.PlatformUserID m_author; public PinNameData m_NamePinData;
    }
    public List<PinData> m_pins = new List<PinData>(); public bool m_pinUpdateRequired; public int DestroyedMarkers;
    public PinData AddPin(UnityEngine.Vector3 pos, PinType type, string name, bool save, bool check, long owner, Splatform.PlatformUserID author)
    { var pin = new PinData { m_pos = pos, m_type = type, m_name = name, m_save = save, m_checked = check, m_ownerID = owner, m_author = author }; m_pins.Add(pin); return pin; }
    public void RemovePin(PinData pin) { m_pins.Remove(pin); }
    private UnityEngine.Sprite GetSprite(PinType type) { return new UnityEngine.Sprite(); }
    private void DestroyPinMarker(PinData pin) { DestroyedMarkers++; }
}
public delegate void PopupButtonCallback();
public class PopupBase { }
public class YesNoPopup : PopupBase
{
    public PopupButtonCallback Yes, No;
    public YesNoPopup(string title, string text, PopupButtonCallback yes, PopupButtonCallback no, bool localize, bool cover)
    { Yes = yes; No = no; }
}
public class UnifiedPopup
{
    public static UnifiedPopup instance = new UnifiedPopup(); public Stack<PopupBase> popupStack = new Stack<PopupBase>();
    public static bool IsVisible() { return instance.popupStack.Count != 0; }
    public static void Push(PopupBase popup) { instance.popupStack.Push(popup); }
    public static void Pop() { instance.popupStack.Pop(); }
}
namespace XPortal
{
    public class KnownPortal { public ZDOID Id; public string Name; public UnityEngine.Vector3 Location; public int Icon = -1; }
    public class KnownPortalsManager
    {
        public static KnownPortalsManager Instance = new KnownPortalsManager();
        public bool HasCompleteSnapshot; public long SnapshotRevision; public List<KnownPortal> Portals = new List<KnownPortal>();
        public List<KnownPortal> GetList() { return new List<KnownPortal>(Portals); }
        public bool ContainsId(ZDOID id) { return Portals.Exists(p => p.Id == id); }
    }
    public static class Log { public static int Errors; public static void Warning(object message) { } public static void Error(object message) { Errors++; } }
}
namespace XPortal.RPC
{
    public static class SendToServer { public static int Requests; public static void SyncRequest(string reason) { Requests++; } }
}
namespace ValheimModPack.PinRemoval
{
    public class PinHistoryController
    {
        public static PinHistoryController active = new PinHistoryController(); public static bool Fail;
        public static int Created, Renamed, Removed;
        internal static void RememberQuickPin(Minimap.PinData pin, string preset) { if (Fail) throw new InvalidOperationException("test history failure"); Created++; }
        internal static void RememberRename(Minimap.PinData pin, string name) { if (Fail) throw new InvalidOperationException("test history failure"); Renamed++; }
        public void RemoveMany(Minimap map, IEnumerable<Minimap.PinData> pins)
        { if (Fail) throw new InvalidOperationException("test history failure"); foreach (var pin in pins) { map.RemovePin(pin); Removed++; } }
    }
}
