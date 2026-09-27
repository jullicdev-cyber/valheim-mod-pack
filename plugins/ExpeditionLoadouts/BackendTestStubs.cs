// Boundary doubles only. Never included in the production plugin.
using System;
using System.Collections.Generic;
using System.Reflection;
using ValheimModPack.ExpeditionLoadouts;

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
        public GameObject gameObject { get { return new GameObject { name = name }; } }
        public T GetComponent<T>() where T : class { object value; return Components.TryGetValue(typeof(T), out value) ? value as T : null; }
        public T GetComponentInParent<T>() where T : class { return GetComponent<T>(); }
    }
    public class GameObject : Component { }
    public class Transform { public Vector3 position; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float a, float b, float c) { x = a; y = b; z = c; }
        public float sqrMagnitude { get { return x*x+y*y+z*z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b) { return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); }
    }
    public static class Time { public static float realtimeSinceStartup; public static int frameCount; }
    public static class Mathf { public static float Clamp(float v, float min, float max) { return Math.Max(min, Math.Min(v,max)); } }
}
namespace BepInEx.Logging { public class ManualLogSource { public void LogError(object value) { } } }
namespace BepInEx.Bootstrap { public static class Chainloader { public static readonly Dictionary<string,object> PluginInfos = new Dictionary<string,object>(); } }
namespace HarmonyLib
{
    public class Harmony { public Harmony(string id) { } public void Patch(MethodInfo original, HarmonyMethod prefix) { } public void UnpatchSelf() { } }
    public class HarmonyMethod { public HarmonyMethod(Type type,string name) { } }
    public static class AccessTools
    {
        const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        public static FieldInfo Field(Type type,string name) { return type.GetField(name,All); }
        public static MethodInfo Method(Type type,string name,Type[] args) { return type.GetMethod(name,All,null,args,null); }
        public static Type TypeByName(string name) { return typeof(AccessTools).Assembly.GetType(name); }
    }
}
public struct Vector2i { public int x,y; public Vector2i(int a,int b) { x=a;y=b; } }
public class ItemDrop : UnityEngine.Component
{
    public ItemData m_itemData;
    public class ItemData
    {
        public enum ItemType { None, Material, Consumable, Ammo, AmmoNonEquipable, Fish, Tool, OneHandedWeapon,
            TwoHandedWeapon, TwoHandedWeaponLeft, Bow, Shield, Helmet, Chest, Legs, Hands, Shoulder, Torch,
            Utility, Trinket, Attach_Atgeir, Trophy, Misc, Customization }
        public class SharedData { public string m_name; public int m_maxStackSize=50, m_maxQuality=1; public ItemType m_itemType=ItemType.Material; }
        public SharedData m_shared = new SharedData();
        public UnityEngine.GameObject m_dropPrefab;
        public int m_stack=1,m_quality=1,m_variant,m_worldLevel;
        public float m_durability=100;
        public long m_crafterID;
        public string m_crafterName;
        public bool m_equipped;
        public Dictionary<string,string> m_customData = new Dictionary<string,string>();
        public Vector2i m_gridPos;
        public bool IsSameType(ItemData other) { return other != null && m_shared.m_name == other.m_shared.m_name && m_quality==other.m_quality; }
        public ItemData Clone() { var copy=(ItemData)MemberwiseClone(); copy.m_customData=new Dictionary<string,string>(m_customData);return copy; }
    }
}
public class Inventory
{
    public int Width=8,Height=6;
    public readonly List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
    public bool ThrowAfterMove, ThrowAfterRemove;
    public int GetWidth(){return Width;} public int GetHeight(){return Height;}
    public List<ItemDrop.ItemData> GetAllItems(){return Items;}
    public bool RemoveItem(ItemDrop.ItemData item){bool removed=Items.Remove(item);if(ThrowAfterRemove)throw new InvalidOperationException("Removal callback failed");return removed;}
    public ItemDrop.ItemData GetItemAt(int x,int y){return Items.Find(delegate(ItemDrop.ItemData i){return i.m_gridPos.x==x&&i.m_gridPos.y==y;});}
    public bool MoveItemToThis(Inventory source,ItemDrop.ItemData item,int amount,int x,int y)
    {
        var existing=GetItemAt(x,y);
        if(existing != null && !existing.IsSameType(item)) return false;
        int count=Math.Min(amount,Math.Min(item.m_stack,item.m_shared.m_maxStackSize-(existing==null?0:existing.m_stack)));
        if(count<=0)return false;
        if(existing==null){existing=item.Clone();existing.m_stack=count;existing.m_gridPos=new Vector2i(x,y);Items.Add(existing);}else existing.m_stack+=count;
        item.m_stack-=count;
        if(ThrowAfterMove)throw new InvalidOperationException("Third-party Changed callback failed");
        if(item.m_stack==0)source.Items.Remove(item);
        return count==amount;
    }
}
public class Player : UnityEngine.Component
{
    public static Player m_localPlayer;
    public Inventory Inventory=new Inventory();public long Id=7;public bool Dead,Teleporting,Sleeping,Cutscene;
    public Inventory GetInventory(){return Inventory;} public bool IsDead(){return Dead;}public bool IsTeleporting(){return Teleporting;}
    public bool IsSleeping(){return Sleeping;}public bool InCutscene(){return Cutscene;}public long GetPlayerID(){return Id;}
}
public class Piece : UnityEngine.Component {public bool Built=true;public bool IsPlacedByPlayer(){return Built;}}
public class TombStone : UnityEngine.Component { } public class Ship : UnityEngine.Component { }
public class Vagon : UnityEngine.Component { }
public class ZDO { public long Owner=2;public int InUse;public uint DataRevision=1;public long GetOwner(){return Owner;}public int GetInt(string key,int fallback){return InUse;} }
public class ZNetView : UnityEngine.Component
{
    public bool Valid=true;public ZDO Data=new ZDO();public int Requests;public Action OnRequest;
    public string Prefab="piece_chest_wood";public string GetPrefabName(){return Prefab;}
    public bool IsValid(){return Valid;}public bool HasOwner(){return Data.Owner!=0;}public bool IsOwner(){return Data.Owner==1;}
    public ZDO GetZDO(){return Data;}public void InvokeRPC(string name,object[] parameters){if(name!="RPC_RequestOpen")throw new Exception("Wrong RPC");Requests++;if(OnRequest!=null)OnRequest();}
}
public class Container : UnityEngine.Component
{
    private ZNetView m_nview = new ZNetView();
    public ZNetView View {get{return m_nview;}}
    private Inventory inventory=new Inventory();private bool inUse;
    private uint m_lastRevision;private bool m_loading;
    public bool FailRefresh;
    public bool Access=true,m_checkGuardStone=true,m_autoDestroyEmpty;public ZNetView m_rootObjectOverride;public Vagon m_wagon;
    public int Saves,Loads;
    public Container(){name="piece_chest_wood";Components[typeof(Piece)]=new Piece();UnityEngine.Object.Containers.Add(this);}
    private bool CheckAccess(long id){return Access;}
    private bool Load(){Loads++;if(!FailRefresh)m_lastRevision=m_nview.Data.DataRevision;return true;}
    private void Save(){Saves++;}
    private void RPC_OpenResponse(long sender,bool granted){ }
    public Inventory GetInventory(){return inventory;}
    public bool IsInUse(){return inUse;}
    public void SetInUse(bool value){if(!m_nview.IsOwner())return;inUse=value;m_nview.Data.InUse=value?1:0;}
}
public class InventoryGui
{
    public static InventoryGui instance;
    private Container m_currentContainer;
    private ItemDrop.ItemData m_dragItem;
    public void Open(Container c){m_currentContainer=c;}public void Drag(ItemDrop.ItemData i){m_dragItem=i;}
}
public static class PrivateArea {public static bool Access=true;public static bool CheckAccess(UnityEngine.Vector3 p,float range,bool flash,bool wardCheck){return Access;}}
public class ObjectDB
{
    public static ObjectDB instance=new ObjectDB();
    public readonly Dictionary<string,UnityEngine.GameObject> Items=new Dictionary<string,UnityEngine.GameObject>();
    public UnityEngine.GameObject GetItemPrefab(string name){UnityEngine.GameObject result;return Items.TryGetValue(name,out result)?result:null;}
}
public class Localization {public static Localization instance=new Localization();public string GetSelectedLanguage(){return "English";}}
public static class Utils { public static string GetPrefabName(UnityEngine.GameObject obj) { return obj.name; } }
namespace EquipmentAndQuickSlots {public static class API {public static int Rows=5,Height=6;public static int GetVisibleRows(){return Rows;}public static int GetFullHeight(){return Height;}}}
namespace QuickStackStore
{
    public class UserConfig
    {
        public static readonly UserConfig instance=new UserConfig();
        public readonly HashSet<string> Cells=new HashSet<string>(),Items=new HashSet<string>();
        public static UserConfig GetPlayerConfig(long id){return instance;}
        public bool IsSlotFavorited(Vector2i p){return Cells.Contains(p.x+","+p.y);}
        public bool IsItemNameFavorited(ItemDrop.ItemData.SharedData d){return Items.Contains(d.m_name);}
    }
}
