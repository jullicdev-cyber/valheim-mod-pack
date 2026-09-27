// Test-only boundary doubles. PortableController.cs itself is compiled unchanged.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator *(Vector3 a, float value) { return new Vector3(a.x * value, a.y * value, a.z * value); }
    }
    public sealed class Transform { public Vector3 position; }
    public sealed class GameObject { public string name; public GameObject(string name) { this.name = name; } }
    public static class Time { public static int frameCount; public static float unscaledTime; }
}

namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        public readonly MethodInfo Method;
        public HarmonyMethod(Type type, string method) { Method = AccessTools.Method(type, method, null); }
    }
    public sealed class Harmony
    {
        public static int PatchCount, UnpatchCount;
        public Harmony(string id) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix)
        {
            if (original == null || prefix == null || prefix.Method == null) throw new ArgumentException("Missing interception endpoint.");
            ++PatchCount;
        }
        public void UnpatchSelf() { ++UnpatchCount; }
    }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] parameters)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return parameters == null ? type.GetMethod(name, flags) : type.GetMethod(name, flags, null, parameters, null);
        }
    }
}

public struct ZDOID : IEquatable<ZDOID>
{
    public long User;
    public uint Number;
    public ZDOID(long user, uint number) { User = user; Number = number; }
    public bool IsNone() { return User == 0 && Number == 0; }
    public bool Equals(ZDOID other) { return User == other.User && Number == other.Number; }
    public override bool Equals(object value) { return value is ZDOID && Equals((ZDOID)value); }
    public override int GetHashCode() { return User.GetHashCode() ^ Number.GetHashCode(); }
    public static bool operator ==(ZDOID a, ZDOID b) { return a.Equals(b); }
    public static bool operator !=(ZDOID a, ZDOID b) { return !a.Equals(b); }
}
public sealed class ZDO
{
    public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
    public ZDOID m_uid;
    public bool Owned = true;
    public bool IsOwner() { return Owned; }
    public string GetString(string key, string fallback) { string value; return Values.TryGetValue(key, out value) ? value : fallback; }
    public void Set(string key, string value) { Values[key] = value; }
}
public sealed class ZNetView
{
    public ZDO State;
    public bool Valid = true;
    public bool IsValid() { return Valid; }
    public ZDO GetZDO() { return State; }
}
public sealed class ItemDrop
{
    public sealed class ItemData
    {
        public UnityEngine.GameObject m_dropPrefab;
        public Dictionary<string, string> m_customData;
    }
}
public sealed class Inventory
{
    public readonly List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
    public List<ItemDrop.ItemData> GetAllItems() { return Items; }
    public bool ContainsItem(ItemDrop.ItemData item) { return Items.Contains(item); }
}
public class Character { }
public class Humanoid : Character
{
    public bool UseItem(Inventory inventory, ItemDrop.ItemData item, bool fromInventoryGui) { return true; }
    public bool StartAttack(Character target, bool secondaryAttack) { return true; }
}
public sealed class Player : Humanoid
{
    public static Player m_localPlayer;
    public static readonly List<Player> Players = new List<Player>();
    public readonly Inventory Inventory = new Inventory();
    public readonly UnityEngine.Transform transform = new UnityEngine.Transform();
    public ZNetView View;
    public ItemDrop.ItemData Equipped;
    public bool Dead, Teleporting, Cutscene, Sleeping, RejectEquip;
    public bool isActiveAndEnabled = true;
    public int EquipCalls;
    public bool IsDead() { return Dead; }
    public bool IsTeleporting() { return Teleporting; }
    public bool InCutscene() { return Cutscene; }
    public bool IsSleeping() { return Sleeping; }
    public bool IsItemEquiped(ItemDrop.ItemData item) { return Equipped == item; }
    public bool EquipItem(ItemDrop.ItemData item, bool triggerEffects)
    {
        ++EquipCalls;
        if (RejectEquip) return false;
        Equipped = item; return true;
    }
    public Inventory GetInventory() { return Inventory; }
    public static List<Player> GetAllPlayers() { return Players; }
    public T GetComponent<T>() where T : class { return View as T; }
}
public sealed class InventoryGui
{
    public static InventoryGui instance;
    public static bool Visible;
    public bool DelayedHide;
    public int HideCalls;
    public static bool IsVisible() { return Visible; }
    public void Hide() { ++HideCalls; if (!DelayedHide) Visible = false; }
}
public static class Menu { public static bool Visible; public static bool IsVisible() { return Visible; } }
public static class UnifiedPopup { public static bool Visible; public static bool IsVisible() { return Visible; } }
public sealed class Localization
{
    public static Localization instance;
    public string Localize(string key) { return key == "$vmp_skald_idol" ? "Идол скальда" : key; }
}

namespace ValheimModPack.NordicRadio
{
    public static class PortableModel { public const string PrefabName = "vmp_skald_idol"; }
    public sealed class ServiceCall
    {
        public ZDOID Id; public string Token;
        public ServiceCall(ZDOID id, string token) { Id = id; Token = token; }
    }
    public sealed class RadioService
    {
        public readonly List<ServiceCall> Calls = new List<ServiceCall>();
        public readonly List<ZDOID> Watched = new List<ZDOID>();
        public void SetPortable(ZDOID id, string token) { Calls.Add(new ServiceCall(id, token)); }
        public void Watch(ZDOID id) { Watched.Add(id); }
    }
    public sealed class Plugin
    {
        public const string Id = "valheimmodpack.nordicradio";
        public static Plugin Instance;
        internal PortableController Portable;
        public readonly RadioService Service = new RadioService();
        public readonly List<IRadioTarget> Attached = new List<IRadioTarget>();
        public readonly List<IRadioTarget> Detached = new List<IRadioTarget>();
        public readonly List<IRadioTarget> Opened = new List<IRadioTarget>();
        public readonly List<Exception> Errors = new List<Exception>();
        public bool RadioWindowVisible;
        public void Attach(IRadioTarget target) { Attached.Add(target); }
        public void Detach(IRadioTarget target) { Attached.Remove(target); Detached.Add(target); }
        public void OpenRadio(IRadioTarget target) { Opened.Add(target); }
        public void Report(Exception error) { Errors.Add(error); }
    }
}
