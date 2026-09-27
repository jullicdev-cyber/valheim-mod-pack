using System;
using System.Reflection;

// The production controller runs unchanged. Only native AudioSource and patch
// dispatch are replaced; Test-MusicDucking also compiles against the real API.
namespace UnityEngine
{
    public sealed class AudioSource
    {
        private float value;
        public float volume { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); } }
    }
}
namespace HarmonyLib
{
    public static class Priority { public const int First = 800, Last = 0; }
    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] parameters)
        { return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, parameters, null); }
        public static FieldInfo Field(Type type, string name)
        { return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static); }
    }
    public sealed class HarmonyMethod
    {
        public int priority;
        public MethodInfo method;
        public HarmonyMethod(Type type, string name)
        { method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static); }
    }
    public sealed class Harmony
    {
        internal static HarmonyMethod Prefix, Postfix;
        public Harmony(string id) { }
        public void Patch(MethodInfo original, HarmonyMethod prefix, HarmonyMethod postfix)
        { Prefix = prefix; Postfix = postfix; }
        public void UnpatchSelf() { Prefix = Postfix = null; }
    }
}
public sealed class MusicMan
{
    private UnityEngine.AudioSource m_musicSource;
    public MusicMan(UnityEngine.AudioSource source) { m_musicSource = source; }
    public void SetSource(UnityEngine.AudioSource source) { m_musicSource = source; }
    private void UpdateMusic(float delta) { }
    // Simulates branches that either write a freshly computed volume or retain it.
    public void Frame(float? freshVolume)
    {
        if (HarmonyLib.Harmony.Prefix != null)
            HarmonyLib.Harmony.Prefix.method.Invoke(null, new object[] { m_musicSource });
        if (freshVolume.HasValue) m_musicSource.volume = freshVolume.Value;
        if (HarmonyLib.Harmony.Postfix != null)
            HarmonyLib.Harmony.Postfix.method.Invoke(null, new object[] { m_musicSource });
    }
}
