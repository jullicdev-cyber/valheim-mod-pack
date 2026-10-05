// Deterministic native-adapter fixture, excluded from released assemblies.
using System;
using System.Collections.Generic;
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class HarmonyPatch : Attribute
    { public HarmonyPatch(Type type, string method) { } }
}
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
    }
    public sealed class Transform
    { public Vector3 position, forward = new Vector3(0, 0, 1); }
    public static class Time { public static float realtimeSinceStartup, fixedDeltaTime = 0.02f; }
}
public sealed class ZNetView
{ public bool Valid = true, Owner = true; public bool IsValid() { return Valid; } public bool IsOwner() { return Owner; } }
public class Character
{
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public ZNetView View = new ZNetView();
    public bool Dead, Teleporting, Attached, Sleeping, Cutscene, DebugFlying;
    public UnityEngine.Vector3 Velocity;
    public readonly List<string> Messages = new List<string>();
    public T GetComponent<T>() where T : class { return View as T; }
    public virtual bool IsDead() { return Dead; }
    public virtual bool IsTeleporting() { return Teleporting; }
    public virtual bool IsAttached() { return Attached; }
    public bool IsSleeping() { return Sleeping; }
    public virtual bool InCutscene() { return Cutscene; }
    public virtual bool IsDebugFlying() { return DebugFlying; }
    public UnityEngine.Vector3 GetVelocity() { return Velocity; }
    public void Message(MessageHud.MessageType type, string message, int value, object sprite, bool log) { Messages.Add(message); }
}
public sealed class Player : Character
{
    public static Player m_localPlayer;
    public object Controller;
    public long PlayerId = 42;
    public object GetDoodadController() { return Controller; }
    public long GetPlayerID() { return PlayerId; }
}
public sealed class Ship
{
    public enum Speed { Stop, Back, Slow, Half, Full }
    public bool Owner = true;
    public UnityEngine.Transform transform = new UnityEngine.Transform();
    public bool IsOwner() { return Owner; }
}
public sealed class ShipControlls
{
    public Ship m_ship;
    public bool Valid = true;
    public long User = 42;
    public bool IsValid() { return Valid; }
    public long GetUser() { return User; }
}
public sealed class MessageHud { public enum MessageType { Center } }
public sealed class Localization
{
    public static Localization instance;
    public string Language = "English";
    public string GetSelectedLanguage() { return Language; }
}
