// Boundary doubles for Test.ps1 only. Production builds never include this file.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public enum FindObjectsSortMode { None }
    public class Object
    {
        public string name;
        public static readonly List<Container> Containers = new List<Container>();
        public static T[] FindObjectsByType<T>(FindObjectsSortMode mode) { return (T[])(object)Containers.ToArray(); }
    }
    public class Component : Object
    {
        public readonly Dictionary<Type, object> Components = new Dictionary<Type, object>();
        public readonly Transform transform = new Transform();
        public bool isActiveAndEnabled = true;
        public T GetComponent<T>() where T : class { object value; return Components.TryGetValue(typeof(T), out value) ? value as T : null; }
        public T GetComponentInParent<T>() where T : class { return GetComponent<T>(); }
    }
    public class GameObject : Component { }
    public class TextAsset : Object { public string text; }
    public class Transform { public Vector3 position; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude { get { return x*x + y*y + z*z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x, a.y-b.y, a.z-b.z); }
        public static float Distance(Vector3 a, Vector3 b) { return (float)Math.Sqrt((a-b).sqrMagnitude); }
    }
}
public static class ForbiddenWrites
{
    public static int Count;
    public static void Write() { Count++; throw new InvalidOperationException("A read-only search attempted a write or forced load"); }
}
public class Character : UnityEngine.Component { }
public class Player : Character
{
    public static Player m_localPlayer;
    public long Id = 7;
    public bool Dead;
    public bool IsDead() { return Dead; }
    public long GetPlayerID() { return Id; }
}
public class Piece : UnityEngine.Component { public bool Built = true; public bool IsPlacedByPlayer() { return Built; } }
public class TombStone : UnityEngine.Component { }
public class Ship : UnityEngine.Component { }
public class Vagon : UnityEngine.Component { }
public class ItemDrop : UnityEngine.Component
{
    public class ItemData
    {
        public class SharedData { public string m_name; }
        public SharedData m_shared = new SharedData();
        public int m_stack;
        public UnityEngine.GameObject m_dropPrefab;
    }
}
public class Inventory
{
    public readonly List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
    public int Reads;
    public List<ItemDrop.ItemData> GetAllItems() { Reads++; return Items; }
    public void Load(object package) { ForbiddenWrites.Write(); }
    public void Save(object package) { ForbiddenWrites.Write(); }
    public bool RemoveItem(ItemDrop.ItemData item) { ForbiddenWrites.Write(); return false; }
}
public struct ZDOID
{
    public long UserID; public uint ID;
    public static bool operator ==(ZDOID a, ZDOID b) { return a.UserID == b.UserID && a.ID == b.ID; }
    public static bool operator !=(ZDOID a, ZDOID b) { return !(a == b); }
    public override bool Equals(object other) { return other is ZDOID && this == (ZDOID)other; }
    public override int GetHashCode() { return UserID.GetHashCode() ^ (int)ID; }
}
public static class ZDOVars { public const int s_inUse = 1; }
public class ZDO
{
    public uint DataRevision = 1;
    public ZDOID m_uid = new ZDOID { UserID = 10, ID = 1 };
    public bool Valid = true;
    public int InUse;
    public int Prefab = 1;
    public bool IsValid() { return Valid; }
    public int GetPrefab() { return Prefab; }
    public int GetInt(int key, int fallback) { return InUse; }
    public void SetOwner(long owner) { ForbiddenWrites.Write(); }
    public void Set(string key, int value) { ForbiddenWrites.Write(); }
}
public class ZNetView : UnityEngine.Component
{
    public bool Valid = true;
    public ZDO Data = new ZDO();
    public bool IsValid() { return Valid; }
    public ZDO GetZDO() { return Data; }
    public void ClaimOwnership() { ForbiddenWrites.Write(); }
    public void InvokeRPC(string name, params object[] data) { ForbiddenWrites.Write(); }
}
public class Container : UnityEngine.Component
{
    private uint m_lastRevision = 1;
    private bool m_loading;
    public readonly ZNetView View = new ZNetView();
    public readonly Inventory Inventory = new Inventory();
    public bool Access = true, InUse, ThrowAccess, m_autoDestroyEmpty;
    public ZNetView m_rootObjectOverride;
    public Vagon m_wagon;
    public string m_name = "$piece_chest";
    public Container()
    {
        Components[typeof(Piece)] = new Piece(); Components[typeof(ZNetView)] = View;
        UnityEngine.Object.Containers.Add(this);
    }
    private bool CheckAccess(long id) { if (ThrowAccess) throw new Exception("Permission callback failed"); return Access; }
    private bool Load() { ForbiddenWrites.Write(); return true; }
    private void Save() { ForbiddenWrites.Write(); }
    public void SetRevision(uint value) { m_lastRevision = value; }
    public void SetLoading(bool value) { m_loading = value; }
    public bool IsInUse() { return InUse; }
    public Inventory GetInventory() { return Inventory; }
    public void SetInUse(bool value) { ForbiddenWrites.Write(); }
}
public static class PrivateArea
{
    public static bool Allowed = true;
    public static bool CheckAccess(UnityEngine.Vector3 p, float range, bool flash, bool check)
    { if (flash) throw new Exception("Search must not flash wards"); return Allowed; }
}
public class ZNetScene
{
    public static ZNetScene instance = new ZNetScene();
    public readonly Dictionary<int, UnityEngine.GameObject> Prefabs = new Dictionary<int, UnityEngine.GameObject>();
    public UnityEngine.GameObject GetPrefab(int hash) { UnityEngine.GameObject prefab; return Prefabs.TryGetValue(hash, out prefab) ? prefab : null; }
}
public class TestLocalizationSettings
{
    public List<UnityEngine.TextAsset> Localizations { get; set; }
}
public class Localization
{
    public static Localization instance = new Localization();
    private static object m_localizationSettings;
    public readonly Dictionary<string, string> Current = new Dictionary<string, string>();
    public string Localize(string token) { string value; return Current.TryGetValue(token, out value) ? value : token; }
    public static void SetCsv(string text)
    {
        m_localizationSettings = new TestLocalizationSettings { Localizations = new List<UnityEngine.TextAsset> { new UnityEngine.TextAsset { text = text } } };
    }
}
