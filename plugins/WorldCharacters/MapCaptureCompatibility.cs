using System;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

namespace ValheimModPack.WorldCharacters
{
    internal static class MapCaptureCompatibility
    {
        private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly MethodInfo GetMap = typeof(Minimap).GetMethod("GetMapData", Methods, null, Type.EmptyTypes, null);
        private static readonly MethodInfo SaveMap = typeof(Minimap).GetMethod("SaveMapData", Methods, null, Type.EmptyTypes, null);
        private static readonly MethodInfo SavedPin = PinPredicate();
        // These exact native bodies serialize the inputs read by GameMapCapture.
        // A game update conservatively restores the original native save path.
        internal const string GetMapHash = "62201610AC36D8262780D85D7119ACB4A494E17711650F97FC5940A70F403621";
        internal const string SaveMapHash = "A25216E257A2AF40261DA6AA31B894E411CB88EE51DA6BAAB5D4142D75980B14";
        internal const string SavedPinHash = "1A9C7E04BEEFC0EC06DEB2438243B61C2390886A8AC7E5C71A4087D294922776";
        private static readonly bool KnownNative = Matches(GetMap, GetMapHash) && Matches(SaveMap, SaveMapHash) && Matches(SavedPin, SavedPinHash);

        private static MethodInfo PinPredicate()
        {
            Type type = typeof(Minimap).GetNestedType("<>c", BindingFlags.NonPublic);
            return type == null ? null : type.GetMethod("<GetMapData>b__75_0", Methods);
        }

        internal static bool Matches(MethodInfo method, string expected)
        {
            try
            {
                if (method == null || method.GetMethodBody() == null) return false;
                using (SHA256 sha = SHA256.Create())
                    return String.Equals(BitConverter.ToString(sha.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", ""), expected, StringComparison.Ordinal);
            }
            catch (Exception) { return false; }
        }

        internal static bool HasIntervention(MethodInfo method)
        {
            try
            {
                if (method == null) return true;
                Patches patches = Harmony.GetPatchInfo(method);
                return patches != null && patches.Owners.Count != 0;
            }
            catch (Exception) { return true; }
        }

        // Invoked only when a map capture is due (once per minute or an explicit
        // save). Harmony metadata must be checked again if another mod patches
        // serialization after startup; hashing the native IL is done only once.
        internal static bool CanSkipNativeCapture()
        { return KnownNative && !HasIntervention(GetMap) && !HasIntervention(SaveMap) && !HasIntervention(SavedPin); }
    }
}
