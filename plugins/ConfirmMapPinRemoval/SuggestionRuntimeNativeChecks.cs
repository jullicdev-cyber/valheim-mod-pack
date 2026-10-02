// Optional real-Unity physics probe. Never included in the shipped mod DLL.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimModPack.PinRemoval
{
    public static class SuggestionRuntimeNativeChecks
    {
        private static int checks;
        private static MethodInfo findTarget, visible;
        private static FieldInfo observation;
        private static readonly Vector3 Origin = new Vector3(5000, 10000, 5000);
        private static readonly RaycastHit[] Rays = new RaycastHit[32];

        private static void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException("Suggestion runtime native check: " + message); }

        private static bool Visible(Transform ignored, GameObject target, Collider collider, RaycastHit[] buffer = null)
        { return (bool)visible.Invoke(null, new object[] { Origin, ignored, target, collider, buffer ?? Rays }); }

        private static NearbyPinObservation Find(Collider collider)
        {
            object result = findTarget.Invoke(null, new object[] { collider });
            return result == null ? null : (NearbyPinObservation)observation.GetValue(result);
        }

        private static GameObject Root(List<GameObject> created, string name, Vector3 position)
        {
            var root = new GameObject(name); root.transform.position = position; created.Add(root); return root;
        }

        private static BoxCollider ChildCollider(GameObject parent, string name, Vector3 offset, Vector3 size)
        {
            var child = new GameObject(name); child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = offset;
            var collider = child.AddComponent<BoxCollider>(); collider.size = size; return collider;
        }

        private static bool Has(NearbyPinObservation value, string preset)
        { return value != null && value.PresetIds != null && Array.IndexOf(value.PresetIds, preset) >= 0; }

        public static string Run()
        {
            checks = 0;
            Type controller = typeof(PinArchive).Assembly.GetType("ValheimModPack.PinRemoval.PinSuggestionController", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            findTarget = controller.GetMethod("FindTarget", flags, null, new[] { typeof(Collider) }, null);
            visible = controller.GetMethod("Visible", flags, null,
                new[] { typeof(Vector3), typeof(Transform), typeof(GameObject), typeof(Collider), typeof(RaycastHit[]) }, null);
            Check(findTarget != null && visible != null, "production collider/LOS helpers are present");
            observation = findTarget.ReturnType.GetField("Observation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Check(observation != null && observation.FieldType == typeof(NearbyPinObservation), "production target retains policy observation");

            var created = new List<GameObject>();
            try
            {
                // Fixtures have only transforms and colliders: no Player, ZDO,
                // game prefabs, world generation, inventories or save writes.
                var berry = Root(created, "RaspberryBush(Clone)", Origin + Vector3.forward * 10);
                BoxCollider berryCollider = ChildCollider(berry, "stem collision", Vector3.zero, Vector3.one * .8f);
                Physics.SyncTransforms();
                NearbyPinObservation found = Find(berryCollider);
                Check(Has(found, "default.raspberries"), "loaded berry is identified through child collider hierarchy");
                Check(found.ObjectKey == berry.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture), "observation belongs to matched prefab root");
                Check(Visible(null, berry, berryCollider), "unobstructed visible berry is accepted by native raycast");

                var portal = Root(created, "portal_wood(Clone)", Origin + Vector3.right * 10);
                var portalCollider = ChildCollider(portal, "portal collider", Vector3.zero, Vector3.one);
                Check(Has(Find(portalCollider), "default.portal"), "loaded portal uses existing default preset");
                var cave = Root(created, "TrollCave02(Clone)", Origin + Vector3.left * 10);
                var caveCollider = ChildCollider(cave, "entrance", Vector3.zero, Vector3.one);
                Check(Has(Find(caveCollider), "default.trollcave") && Has(Find(caveCollider), "default.dungeon"), "loaded exterior location retains specific and generic dungeon mappings");
                var crypt = Root(created, "Crypt2(Clone)", Origin + Vector3.back * 10);
                var cryptCollider = ChildCollider(crypt, "entrance", Vector3.zero, Vector3.one);
                Check(Has(Find(cryptCollider), "default.burialchambers"), "loaded burial-chamber location hierarchy is recognized");
                foreach (string name in new[] { "Raspberry", "RaspberryBush_Fake", "DG_ForestCrypt", "DG_SunkenCrypt" })
                {
                    var decoy = Root(created, name, Origin + Vector3.up * 20);
                    var decoyCollider = ChildCollider(decoy, "collision", Vector3.zero, Vector3.one);
                    Check(Find(decoyCollider) == null, "exact matching rejects dropped items, unknown names and interior generator: " + name);
                }

                var blocker = Root(created, "foreign visibility blocker", Origin + Vector3.forward * 5);
                var blockerCollider = ChildCollider(blocker, "blocker collider", Vector3.zero, Vector3.one);
                Physics.SyncTransforms();
                Check(!Visible(null, berry, berryCollider), "closer foreign collider blocks visibility");
                Check(Visible(blocker.transform, berry, berryCollider), "own avatar hierarchy is ignored while finding nearest foreign hit");
                blockerCollider.isTrigger = true; Physics.SyncTransforms();
                Check(Visible(null, berry, berryCollider), "trigger collider does not hide a visible target");
                blockerCollider.isTrigger = false; blockerCollider.enabled = false; Physics.SyncTransforms();
                Check(Visible(null, berry, berryCollider), "disabled blocker has no physics effect");

                berry.SetActive(false); Physics.SyncTransforms();
                Check(Find(berryCollider) == null && !Visible(null, berry, berryCollider), "inactive target cannot be discovered or treated as visible");
                berry.SetActive(true); berryCollider.enabled = false; Physics.SyncTransforms();
                Check(Find(berryCollider) == null && !Visible(null, berry, berryCollider), "disabled target collider cannot be discovered or treated as visible");
                berryCollider.enabled = true; Physics.SyncTransforms();
                Check(Find(null) == null && !Visible(null, berry, null), "null collider is rejected");
                Check(!(bool)visible.Invoke(null, new object[] { Origin, null, null, berryCollider, Rays }), "null target is rejected");
                Check(!(bool)visible.Invoke(null, new object[] { Origin, null, berry, berryCollider, null }), "null query buffer is rejected");
                Check(!Visible(null, berry, berryCollider, new RaycastHit[0]), "empty query buffer cannot prove visibility");

                var manyOwnColliders = Root(created, "own dense collider hierarchy", Origin);
                for (int i = 0; i < 40; ++i)
                    ChildCollider(manyOwnColliders, "own collider " + i, Vector3.forward * (2 + i * .12f), new Vector3(.2f, .2f, .04f));
                Physics.SyncTransforms();
                int saturated = Physics.RaycastNonAlloc(Origin, Vector3.forward, Rays, 10,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                Check(saturated == Rays.Length, "native ray fixture fills the bounded query buffer");
                Check(!Visible(manyOwnColliders.transform, berry, berryCollider), "saturated query remains conservative even when returned hits could be own colliders");
                manyOwnColliders.SetActive(false); Physics.SyncTransforms();
                Check(Visible(null, berry, berryCollider), "visibility recovers when saturation is removed");

                UnityEngine.Object.DestroyImmediate(berryCollider);
                Physics.SyncTransforms();
                Check(Find(berryCollider) == null && !Visible(null, berry, berryCollider), "destroyed Unity collider is rejected despite retained managed reference");
                UnityEngine.Object.DestroyImmediate(portal);
                Check(Find(portalCollider) == null, "destroyed root and child collider cannot produce a stale suggestion");
                return "PASS: " + checks + " native loaded-hierarchy/visibility/saturation/stale-object assertions.";
            }
            finally
            {
                foreach (GameObject root in created) if (root != null) UnityEngine.Object.DestroyImmediate(root);
                Array.Clear(Rays, 0, Rays.Length); Physics.SyncTransforms();
            }
        }
    }
}
