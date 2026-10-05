// Optional detached-engine checks. Never include this source in a released plugin.
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimModPack.InventoryAdmin
{
    public static class GroupRadiusMotionNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static int checks;
        private static void Check(bool condition, string reason)
        { checks++; if (!condition) throw new InvalidOperationException("Group radius native check: " + reason); }
        private static object Invoke(string method, params object[] arguments)
        { return typeof(GroupRadiusMotion).GetMethod(method, All).Invoke(null, arguments); }
        private static void Patched(Type type, string method, params Type[] signature)
        {
            MethodInfo target = type.GetMethod(method, All, null, signature, null);
            Check(target != null, "installed native endpoint exists: " + type.Name + "." + method);
            Patches patches = Harmony.GetPatchInfo(target);
            Check(patches != null && patches.Prefixes.Any(p => p.owner == Plugin.Id), "released adapter prefix installed: " + type.Name + "." + method);
        }
        public static string Run()
        {
            checks = 0;
            Check(Player.m_localPlayer == null, "detached fixture has no live local player");
            Patched(typeof(Character), "UpdateWalking", typeof(float));
            Patched(typeof(Character), "UpdateSwimming", typeof(float));
            Patched(typeof(Player), "TeleportTo", typeof(Vector3), typeof(Quaternion), typeof(bool));
            Patched(typeof(Ship), "UpdateControlls", typeof(float));
            Patched(typeof(Ship), "ApplyControlls", typeof(Vector3));
            foreach (string method in new[] { "UpdateWalking", "UpdateSwimming" })
            {
                Patches patches = Harmony.GetPatchInfo(typeof(Character).GetMethod(method, All));
                Check(patches.Postfixes.Any(p => p.owner == Plugin.Id) && patches.Finalizers.Any(p => p.owner == Plugin.Id),
                    "native motion input is restored on completion and exceptions: " + method);
            }
            Check(typeof(ShipControlls).GetField("m_ship", All).FieldType == typeof(Ship)
                && typeof(ShipControlls).GetMethod("GetUser", All).ReturnType == typeof(long), "actual pilot identity and ship endpoint match adapter contract");
            Check((int)Ship.Speed.Stop == 0 && (int)Ship.Speed.Back == 1 && (int)Ship.Speed.Slow == 2,
                "installed native Stop/Back/Slow control settings match safe throttle decisions");
            Vector3 clipped = (Vector3)Invoke("ClipIntent", new Vector3(100, 10, 0), Vector3.zero, 100f, new Vector3(1, 7, 1), 1f);
            Check(clipped.x == 0f && clipped.y == 7f && clipped.z == 1f, "real Unity Vector3 radial clipping preserves vertical/tangent intent");
            clipped = (Vector3)Invoke("ClipIntent", new Vector3(100, 10, 0), Vector3.zero, 100f, new Vector3(-1, 7, 0), 1f);
            Check(clipped.x == -1f && clipped.y == 7f, "real Unity Vector3 inward recovery intent is unchanged");
            GameObject holder = new GameObject("Group radius detached remote-replica fixture"); holder.SetActive(false);
            try
            {
                Player replica = holder.AddComponent<Player>(); Vector3 position = new Vector3(105, 20, 30); holder.transform.position = position;
                object[] motion = { replica, null, new Vector3(1, 2, 3), null, 1f, false };
                Invoke("FilterMotion", motion);
                Vector3 unchanged = (Vector3)motion[2];
                Check(unchanged == new Vector3(1, 2, 3), "detached remote native player receives no movement rewrite");
                object[] teleport = { replica, null, new Vector3(9000, 20, 30), true, true, null };
                Check((bool)Invoke("AllowTeleport", teleport) && (bool)teleport[4], "detached remote native player's teleport path remains untouched");
                object[] logical = { replica, Vector3.zero };
                Check(!(bool)Invoke("TryGetLogicalPosition", logical), "detached remote replica cannot supply a leader position");
                Check(holder.transform.position == position && !holder.activeSelf && Player.m_localPlayer == null,
                    "fixture never initializes, teleports or registers a live player");
                Check(holder.GetComponent<ZNetView>() == null, "fixture creates no networked player view or native world ownership");
            }
            finally { UnityEngine.Object.DestroyImmediate(holder); }
            return "PASS: " + checks + " group radius native assertions. Detached replicas only; live cooperative travel requires a multiplayer test.";
        }
    }
}
