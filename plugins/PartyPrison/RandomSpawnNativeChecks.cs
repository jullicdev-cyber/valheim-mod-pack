// Detached menu fixtures only; excluded from the released gameplay assembly.
using System;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PartyPrison.NativeVerification
{
    public static class RandomSpawnNativeChecks
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static void Run(Action<bool, string> check)
        {
            check(Player.m_localPlayer == null && Game.instance == null, "random spawn fixture has no live character or world");
            MethodInfo placement = typeof(ArenaBuilder).GetMethod("TryWavePosition", All);
            if (placement == null) throw new MissingMethodException("ArenaBuilder", "TryWavePosition");
            UnityEngine.Random.State previous = UnityEngine.Random.state;
            try {
                foreach (double radius in new[] { 18d, ArenaGeometry.ExpandedRadius })
                    foreach (float yaw in new[] { 0f, 73f }) CheckRegion(check, placement, radius, yaw);
            }
            finally { UnityEngine.Random.state = previous; }
        }

        private static void CheckRegion(Action<bool, string> check, MethodInfo placement, double radius, float yaw)
        {
            GameObject root = new GameObject("VMP detached random spawn fixture");
            try {
                root.transform.position = new Vector3(3200f, 1500f, -3200f);
                root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                Vector3 origin = root.transform.position;
                Quaternion rotation = root.transform.rotation;
                float half = radius >= ArenaGeometry.ExpandedRadius ? (float)ArenaGeometry.HalfWidth : (float)ArenaGeometry.LegacyHalfWidth;
                float cell = radius >= ArenaGeometry.ExpandedRadius ? -14f : -8f;
                Vector3 cellPosition = origin + rotation * new Vector3(cell, 1f, 0f);
                Vector3 arenaPosition = origin + rotation * new Vector3(4f, 1f, 4f);
                PrisonRegion region = new PrisonRegion {
                    Center = Point(origin + Vector3.up * 6f), CellSpawn = Point(cellPosition),
                    ArenaSpawn = Point(arenaPosition), Radius = radius, HalfHeight = 8d
                };
                // A real native floor remains below the capsule while every
                // fixture is far from the menu's own scene geometry.
                BoxCollider floor = Box(root, "native floor", new Vector3(0f, -.4f, 0f), new Vector3(half * 2f, .8f, half * 2f));
                BoxCollider obstacle = Box(root, "native obstruction", new Vector3(4f, 2f, 4f), new Vector3(60f, 5f, 60f));
                obstacle.enabled = false; Physics.SyncTransforms();
                UnityEngine.Random.InitState(179); Vector3 first, repeated;
                bool firstFound = Choose(placement, region, 0, false, out first);
                UnityEngine.Random.InitState(179);
                check(firstFound && Choose(placement, region, 7, false, out repeated) && first == repeated,
                    "native spawn ignores wave index and independently permits repeated locations");
                check(floor.enabled && Math.Abs(first.y - cellPosition.y) < .001f,
                    "ground enemies keep the native arena floor height");
                UnityEngine.Random.InitState(813); var bins = new bool[9];
                double minimum = ArenaGeometry.Divider(region) + ArenaSpawnGeometry.WallMargin;
                double maximum = ArenaGeometry.RoomHalfWidth(region) - ArenaSpawnGeometry.WallMargin;
                for (int i = 0; i < 128; ++i) {
                    Vector3 position;
                    bool found = Choose(placement, region, i, false, out position);
                    Vector3 local = Quaternion.Inverse(rotation) * (position - origin);
                    check(found && ArenaBuilder.IsInsideArena(region, position) && local.x >= minimum - .002d
                        && local.x <= maximum + .002d && local.z >= minimum - .002d && local.z <= maximum + .002d,
                        "actual native random spawn stays inside legacy or expanded rotated arena bounds");
                    int x = Math.Max(0, Math.Min(2, (int)(3d * (local.x - minimum) / (maximum - minimum))));
                    int z = Math.Max(0, Math.Min(2, (int)(3d * (local.z - minimum) / (maximum - minimum))));
                    bins[x * 3 + z] = true;
                }
                foreach (bool covered in bins) check(covered, "native seeded random positions cover the arena interior and edges");

                obstacle.enabled = true; obstacle.isTrigger = true; Physics.SyncTransforms();
                Vector3 candidate;
                check(Choose(placement, region, 0, false, out candidate), "native spawn ignores trigger volumes");
                obstacle.isTrigger = false; Physics.SyncTransforms();
                check(!Choose(placement, region, 0, false, out candidate) && candidate == Vector3.zero,
                    "native physical collision rejects every obstructed ground candidate");
                check(!Choose(placement, region, 0, true, out candidate),
                    "native physical collision rejects every obstructed flying candidate");

                obstacle.center = new Vector3(4f, .8f, 4f); obstacle.size = new Vector3(60f, .8f, 60f); Physics.SyncTransforms();
                check(!Choose(placement, region, 0, false, out candidate) && Choose(placement, region, 0, true, out candidate),
                    "flying clearance is checked two metres above floor obstructions");
                obstacle.center = new Vector3(4f, 3.75f, 4f); obstacle.size = new Vector3(60f, 1f, 60f); Physics.SyncTransforms();
                check(Choose(placement, region, 0, false, out candidate) && !Choose(placement, region, 0, true, out candidate),
                    "overhead obstruction blocks drakes while preserving clear ground spawns");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); Physics.SyncTransforms(); }
        }

        private static bool Choose(MethodInfo placement, PrisonRegion region, int index, bool flying, out Vector3 position)
        {
            object[] arguments = { region, index, flying, Vector3.zero };
            bool found = (bool)placement.Invoke(null, arguments);
            position = (Vector3)arguments[3]; return found;
        }

        private static BoxCollider Box(GameObject root, string name, Vector3 center, Vector3 size)
        {
            var item = new GameObject(name); item.transform.SetParent(root.transform, false);
            BoxCollider collider = item.AddComponent<BoxCollider>(); collider.center = center; collider.size = size;
            return collider;
        }

        private static PrisonPoint Point(Vector3 value) { return new PrisonPoint(value.x, value.y, value.z); }
    }
}
