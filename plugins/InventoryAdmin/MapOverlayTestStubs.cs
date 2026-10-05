// Deterministic rendering adapter fixture; excluded from released assemblies.
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static bool operator ==(Vector3 a, Vector3 b) { return a.x == b.x && a.y == b.y && a.z == b.z; }
        public static bool operator !=(Vector3 a, Vector3 b) { return !(a == b); }
        public override bool Equals(object other) { return other is Vector3 && this == (Vector3)other; }
        public override int GetHashCode() { return x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode(); }
    }
}
public sealed class Player { public static Player m_localPlayer; }
public sealed class Game { public static bool m_noMap; }
public struct ZDOID { public long UserID; }
public sealed class ZNet
{
    public static ZNet instance;
    public static long GetUID() { return 1; }
    public struct PlayerInfo { public ZDOID m_characterID; public bool m_publicPosition; }
}
public enum UGCType { CharacterName }
public static class CensorShittyWords
{
    public static int Calls;
    public static string FilterUGC(string text, UGCType type, long peerId) { Calls++; return text; }
}
public sealed class Minimap
{
    public static Minimap instance;
    public enum PinType { Player, Icon0 }
    public enum MapMode { None, Small, Large }
    public sealed class PinData
    {
        public string m_name;
        public PinType m_type;
        public Vector3 m_pos;
        public bool m_save, m_shouldDelete;
        public long m_ownerID;
    }
    private readonly List<PinData> m_pins = new List<PinData>(), m_playerPins = new List<PinData>();
    private readonly List<ZNet.PlayerInfo> m_tempPlayerInfo = new List<ZNet.PlayerInfo>();
    private bool m_pinUpdateRequired;
    public readonly List<PinData> Removed = new List<PinData>();
    public MapMode m_mode = MapMode.Small;
    public Vector3 ShownPosition;
    public IList<PinData> Pins { get { return m_pins; } }
    public bool Dirty { get { return m_pinUpdateRequired; } set { m_pinUpdateRequired = value; } }
    public PinData AddPin(Vector3 pos, PinType type, string name, bool save, bool isChecked, long owner = 0)
    {
        var pin = new PinData { m_pos = pos, m_type = type, m_name = name, m_save = save, m_ownerID = owner };
        m_pins.Add(pin); m_pinUpdateRequired = true; return pin;
    }
    public void RemovePin(PinData pin) { m_pins.Remove(pin); Removed.Add(pin); m_pinUpdateRequired = true; }
    public void ShowPointOnMap(Vector3 point) { ShownPosition = point; m_mode = Game.m_noMap ? MapMode.None : MapMode.Large; }
    public PinData AddPublicPlayer(long peerId, string name, Vector3 position)
    {
        var pin = AddPin(position, PinType.Player, name, false, false);
        m_playerPins.Add(pin);
        m_tempPlayerInfo.Add(new ZNet.PlayerInfo { m_characterID = new ZDOID { UserID = peerId }, m_publicPosition = true });
        return pin;
    }
}
