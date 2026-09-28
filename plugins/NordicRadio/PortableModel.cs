using System;
using System.Collections.Generic;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimModPack.NordicRadio
{
    // Original low-poly idol, generated once. The vanilla hammer supplies only
    // standard item/network behaviour; none of its artwork is redistributed.
    public static class PortableModel
    {
        public const string PrefabName = "vmp_skald_idol";
        private static readonly List<Object> ownedAssets = new List<Object>();
        private static bool registered;

        public static void Register()
        {
            if (registered) return;
            GameObject source = PrefabManager.Instance.GetPrefab("Hammer");
            GameObject furniture = PrefabManager.Instance.GetPrefab("piece_table");
            if (!source || !furniture)
                throw new InvalidOperationException("NordicRadio requires the vanilla Hammer and piece_table prefabs.");
            Material template = null;
            WearNTear wear = furniture.GetComponent<WearNTear>();
            GameObject visual = wear && wear.m_new ? wear.m_new : furniture;
            foreach (MeshRenderer renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
                foreach (Material candidate in renderer.sharedMaterials)
                    if (!template && candidate && candidate.shader &&
                        candidate.shader.name.IndexOf("snow", StringComparison.OrdinalIgnoreCase) < 0)
                        template = candidate;
            if (!template) throw new InvalidOperationException("NordicRadio could not find the vanilla furniture material.");

            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabName, source);
            if (!prefab) throw new InvalidOperationException("NordicRadio idol prefab name is already registered.");
            try
            {
                for (int i = prefab.transform.childCount - 1; i >= 0; --i)
                    Object.DestroyImmediate(prefab.transform.GetChild(i).gameObject);
                foreach (Collider component in prefab.GetComponents<Collider>()) Object.DestroyImmediate(component);
                foreach (Renderer component in prefab.GetComponents<Renderer>()) Object.DestroyImmediate(component);
                foreach (MeshFilter component in prefab.GetComponents<MeshFilter>()) Object.DestroyImmediate(component);
                foreach (LODGroup component in prefab.GetComponents<LODGroup>()) Object.DestroyImmediate(component);

                // VisEquipment instantiates the direct "attach" child for a
                // held item and resets its transform. Keep the rear grip at
                // the attach origin, then use the game's equipoffset marker
                // to turn only the held visual around that grip. The dropped
                // visual/collider and inventory icon keep their orientation.
                GameObject attach = Child(prefab.transform, "attach");
                GameObject model = Child(attach.transform, "SkaldIdolModel");
                model.transform.localPosition = new Vector3(0f, -0.20f, -0.14f);
                GameObject equipOffset = Child(prefab.transform, "equipoffset");
                equipOffset.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                List<Part> parts = BuildGeometry();
                foreach (Part part in parts)
                {
                    GameObject child = Child(model.transform, part.Name);
                    child.AddComponent<MeshFilter>().sharedMesh = part.Mesh.ToMesh(part.Name);
                    child.AddComponent<MeshRenderer>().sharedMaterial =
                        MakeMaterial(template, part.Name, part.Color, part.Pattern, part.Glow);
                }
                // Keep collision on the dropped root. VisEquipment therefore
                // cannot copy a physics body or collider into the player's hand.
                BoxCollider collider = prefab.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, 0.042f, -0.12f);
                collider.size = new Vector3(0.30f, 0.49f, 0.30f);
                Rigidbody body = prefab.GetComponent<Rigidbody>();
                if (body) body.centerOfMass = collider.center;
                ItemDrop drop = prefab.GetComponent<ItemDrop>();
                if (!drop || !prefab.GetComponent<ZNetView>())
                    throw new InvalidOperationException("Vanilla Hammer is missing item/network components.");
                drop.m_pieceEnableObj = drop.m_pieceDisabledObj = null;
                drop.m_itemData.m_dropPrefab = prefab;
                drop.m_itemData.m_stack = drop.m_itemData.m_quality = 1;
                ItemDrop.ItemData.SharedData data = drop.m_itemData.m_shared;
                data.m_name = "$vmp_skald_idol";
                data.m_description = "$vmp_skald_idol_description";
                data.m_itemType = ItemDrop.ItemData.ItemType.Tool;
                data.m_attachOverride = ItemDrop.ItemData.ItemType.None;
                data.m_animationState = ItemDrop.ItemData.AnimationState.OneHanded;
                data.m_maxStackSize = data.m_maxQuality = 1;
                data.m_weight = 2f;
                data.m_teleportable = true;
                data.m_useDurability = data.m_canBeReparied = data.m_destroyBroken = false;
                data.m_buildPieces = null;
                data.m_centerCamera = false;
                data.m_toolTier = 0;
                data.m_blockPower = data.m_blockPowerPerLevel = 0f;
                data.m_deflectionForce = data.m_deflectionForcePerLevel = 0f;
                data.m_movementModifier = 0f;
                data.m_attack = new Attack { m_attackAnimation = "" };
                data.m_secondaryAttack = new Attack { m_attackAnimation = "" };
                data.m_damages = new HitData.DamageTypes();
                data.m_damagesPerLevel = new HitData.DamageTypes();
                data.m_buildEffect = new EffectList();
                data.m_equipStatusEffect = null;
                AddLocalization();
                ItemConfig config = new ItemConfig
                {
                    Name = "$vmp_skald_idol", Description = "$vmp_skald_idol_description",
                    CraftingStation = "forge", MinStationLevel = 1, Amount = 1, Weight = 2f,
                    Icon = CreateIcon(parts),
                    Requirements = new[]
                    {
                        new RequirementConfig("Wood", 10, 0, false),
                        new RequirementConfig("FineWood", 5, 0, false),
                        new RequirementConfig("Bronze", 2, 0, false),
                        new RequirementConfig("SurtlingCore", 1, 0, false)
                    }
                };
                if (!ItemManager.Instance.AddItem(new CustomItem(prefab, false, config)))
                    throw new InvalidOperationException("Jotunn rejected the NordicRadio portable idol.");
                registered = true;
            }
            catch
            {
                Object.DestroyImmediate(prefab);
                foreach (Object asset in ownedAssets) if (asset) Object.Destroy(asset);
                ownedAssets.Clear();
                throw;
            }
        }

        private static void AddLocalization()
        {
            CustomLocalization localization = LocalizationManager.Instance.GetLocalization();
            localization.AddJsonFile("English", "{\"vmp_skald_idol\":\"Skald's Idol\",\"vmp_skald_idol_description\":\"A carved wooden skald with a living ember in his chest. Use it to open the music controls. Your music travels with you, even aboard a ship.\"}");
            localization.AddJsonFile("Russian", "{\"vmp_skald_idol\":\"Идол скальда\",\"vmp_skald_idol_description\":\"Резной деревянный скальд с живым угольком в груди. Используйте предмет, чтобы открыть управление музыкой. Музыка путешествует вместе с вами — даже на корабле.\"}");
        }

        private static GameObject Child(Transform parent, string name)
        {
            GameObject obj = new GameObject(name);
            obj.layer = parent.gameObject.layer;
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private sealed class Part
        {
            public readonly string Name;
            public readonly Color Color;
            public readonly int Pattern;
            public readonly bool Glow;
            public readonly Shape Mesh = new Shape();
            public Part(string name, Color color, int pattern, bool glow)
            { Name = name; Color = color; Pattern = pattern; Glow = glow; }
        }

        private static List<Part> BuildGeometry()
        {
            Part wood = new Part("IdolCarvedWood", new Color(0.31f, 0.19f, 0.095f), 0, false);
            Part face = new Part("IdolFaceRelief", new Color(0.47f, 0.31f, 0.16f), 0, false);
            Part bronze = new Part("IdolBronzeFittings", new Color(0.57f, 0.37f, 0.13f), 1, false);
            Part dark = new Part("IdolCarvedRecesses", new Color(0.09f, 0.048f, 0.022f), 3, false);
            Part amber = new Part("IdolSurtlingCore", new Color(1f, 0.37f, 0.04f), 1, true);

            // Compact wooden base, carved robe and broad shoulders. Faceted
            // profiles and broad relief remain readable at inventory-icon size.
            Column(wood.Mesh, new[] { 0f, 0.036f, 0.055f },
                new[] { 0.115f, 0.12f, 0.105f }, new[] { 0.085f, 0.09f, 0.077f }, 8);
            Column(wood.Mesh, new[] { 0.045f, 0.14f, 0.26f, 0.325f },
                new[] { 0.093f, 0.10f, 0.142f, 0.104f }, new[] { 0.066f, 0.066f, 0.070f, 0.061f }, 8);
            Column(face.Mesh, new[] { 0.306f, 0.365f, 0.446f, 0.464f },
                new[] { 0.062f, 0.083f, 0.079f, 0.050f }, new[] { 0.052f, 0.062f, 0.059f, 0.043f }, 8);
            Band(bronze.Mesh, 0.038f, 0.017f, 0.119f, 0.088f);
            Band(bronze.Mesh, 0.116f, 0.023f, 0.103f, 0.070f);
            Band(bronze.Mesh, 0.438f, 0.020f, 0.083f, 0.064f);

            // Angular eyebrows, a prominent nose and a split carved beard.
            foreach (float side in new[] { -1f, 1f })
            {
                dark.Mesh.Beam(new Vector3(side * 0.016f, 0.406f, -0.061f),
                    new Vector3(side * 0.065f, 0.412f, -0.048f), 0.012f);
                face.Mesh.Beam(new Vector3(side * 0.016f, 0.422f, -0.068f),
                    new Vector3(side * 0.067f, 0.430f, -0.049f), 0.020f);
                face.Mesh.Beam(new Vector3(side * 0.012f, 0.370f, -0.073f),
                    new Vector3(side * 0.068f, 0.350f, -0.061f), 0.027f);
                // Beard planes form separate tapered braids, not a cylinder.
                face.Mesh.Octahedron(new Vector3(side * 0.038f, 0.318f, -0.074f),
                    new Vector3(0.035f, 0.054f, 0.022f));
                bronze.Mesh.Box(new Vector3(side * 0.038f, 0.287f, -0.076f),
                    new Vector3(0.038f, 0.016f, 0.036f));
                // Carved arms hold the bronze frame around the glowing heart.
                wood.Mesh.Beam(new Vector3(side * 0.119f, 0.285f, -0.002f),
                    new Vector3(side * 0.122f, 0.194f, -0.045f), 0.043f);
                face.Mesh.Beam(new Vector3(side * 0.116f, 0.193f, -0.046f),
                    new Vector3(side * 0.069f, 0.195f, -0.085f), 0.036f);
            }
            face.Mesh.Octahedron(new Vector3(0f, 0.394f, -0.069f), new Vector3(0.018f, 0.034f, 0.033f));
            dark.Mesh.Box(new Vector3(0f, 0.367f, -0.066f), new Vector3(0.087f, 0.015f, 0.018f));

            // Recessed heart with four bronze retaining bars, open between them.
            dark.Mesh.Octahedron(new Vector3(0f, 0.213f, -0.069f), new Vector3(0.072f, 0.068f, 0.019f));
            amber.Mesh.Octahedron(new Vector3(0f, 0.213f, -0.094f), new Vector3(0.049f, 0.047f, 0.033f));
            Vector3[] frame = { new Vector3(0f, 0.276f, -0.089f), new Vector3(0.067f, 0.213f, -0.090f),
                new Vector3(0f, 0.150f, -0.089f), new Vector3(-0.067f, 0.213f, -0.090f) };
            for (int i = 0; i < frame.Length; ++i)
                bronze.Mesh.Beam(frame[i], frame[(i + 1) % frame.Length], 0.012f);
            foreach (float side in new[] { -1f, 1f })
            {
                dark.Mesh.Beam(new Vector3(side * 0.066f, 0.055f, -0.073f), new Vector3(side * 0.080f, 0.100f, -0.073f), 0.010f);
                dark.Mesh.Beam(new Vector3(side * 0.050f, 0.059f, -0.075f), new Vector3(side * 0.052f, 0.096f, -0.075f), 0.008f);
            }
            bronze.Mesh.Beam(new Vector3(0f, 0.058f, -0.079f), new Vector3(0f, 0.095f, -0.079f), 0.008f);
            bronze.Mesh.Beam(new Vector3(0f, 0.086f, -0.079f), new Vector3(0.016f, 0.074f, -0.079f), 0.008f);

            // A rear bronze grip fits the right hand; no extra leather ingredient.
            bronze.Mesh.Beam(new Vector3(0f, 0.138f, 0.045f), new Vector3(0f, 0.138f, 0.14f), 0.024f);
            bronze.Mesh.Beam(new Vector3(0f, 0.262f, 0.055f), new Vector3(0f, 0.262f, 0.14f), 0.024f);
            wood.Mesh.Beam(new Vector3(0f, 0.138f, 0.14f), new Vector3(0f, 0.262f, 0.14f), 0.034f);
            return new List<Part> { wood, face, bronze, dark, amber };
        }

        private static void Band(Shape shape, float center, float height, float x, float z)
        {
            Column(shape, new[] { center - height / 2f, center + height / 2f }, new[] { x, x }, new[] { z, z }, 8);
        }

        private static void Column(Shape shape, float[] heights, float[] widths, float[] depths, int sides)
        {
            for (int level = 0; level < heights.Length - 1; ++level)
                for (int side = 0; side < sides; ++side)
                {
                    float a = side * 2f * Mathf.PI / sides, b = (side + 1) * 2f * Mathf.PI / sides;
                    shape.Quad(new Vector3(Mathf.Cos(a) * widths[level], heights[level], Mathf.Sin(a) * depths[level]),
                        new Vector3(Mathf.Cos(a) * widths[level + 1], heights[level + 1], Mathf.Sin(a) * depths[level + 1]),
                        new Vector3(Mathf.Cos(b) * widths[level + 1], heights[level + 1], Mathf.Sin(b) * depths[level + 1]),
                        new Vector3(Mathf.Cos(b) * widths[level], heights[level], Mathf.Sin(b) * depths[level]), Vector2.one);
                }
            for (int side = 0; side < sides; ++side)
            {
                float a = side * 2f * Mathf.PI / sides, b = (side + 1) * 2f * Mathf.PI / sides;
                int top = heights.Length - 1;
                shape.Triangle(new Vector3(0f, heights[0], 0f), new Vector3(Mathf.Cos(a) * widths[0], heights[0], Mathf.Sin(a) * depths[0]),
                    new Vector3(Mathf.Cos(b) * widths[0], heights[0], Mathf.Sin(b) * depths[0]), Vector2.zero, Vector2.right, Vector2.one);
                shape.Triangle(new Vector3(0f, heights[top], 0f), new Vector3(Mathf.Cos(b) * widths[top], heights[top], Mathf.Sin(b) * depths[top]),
                    new Vector3(Mathf.Cos(a) * widths[top], heights[top], Mathf.Sin(a) * depths[top]), Vector2.zero, Vector2.right, Vector2.one);
            }
        }
        private static Material MakeMaterial(Material template, string name, Color color, int pattern, bool glow)
        {
            Material result = new Material(template);
            result.name = "NordicRadio_" + name;
            ownedAssets.Add(result);
            Texture2D texture = new Texture2D(32, 32, TextureFormat.RGBA32, true);
            texture.name = result.name + "_Albedo";
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Repeat;
            Color[] pixels = new Color[1024];
            for (int y = 0; y < 32; ++y)
                for (int x = 0; x < 32; ++x)
                {
                    int noise = ((x * 73 + y * 137 + x * y * 11) ^ (x * 19 + y * 3)) & 31;
                    float grain = pattern == 0 ? Mathf.Sin((x + Mathf.Sin(y * 0.3f)) * 1.6f) * 0.08f : 0f;
                    float shade = 0.88f + noise / 150f + grain;
                    if (pattern == 2) shade += Mathf.Sin(x * 0.62f) * 0.055f;
                    pixels[y * 32 + x] = new Color(color.r * shade, color.g * shade, color.b * shade, 1f);
                }
            texture.SetPixels(pixels);
            texture.Apply(true, false);
            ownedAssets.Add(texture);
            if (result.HasProperty("_MainTex"))
            {
                result.SetTexture("_MainTex", texture);
                result.SetTextureScale("_MainTex", Vector2.one);
                result.SetTextureOffset("_MainTex", Vector2.zero);
            }
            if (result.HasProperty("_Color")) result.SetColor("_Color", Color.white);
            foreach (string property in new[] { "_BumpMap", "_MetallicGlossMap", "_MetallicTex", "_OcclusionMap", "_NoiseTex", "_MossTex" })
                if (result.HasProperty(property)) result.SetTexture(property, null);
            if (result.HasProperty("_Glossiness")) result.SetFloat("_Glossiness", pattern == 1 ? 0.24f : 0.08f);
            if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", pattern == 1 && !glow ? 0.55f : 0f);
            if (result.HasProperty("_MossAlpha")) result.SetFloat("_MossAlpha", 0f);
            if (result.HasProperty("_EmissionMap")) result.SetTexture("_EmissionMap", glow ? texture : null);
            if (result.HasProperty("_EmissionColor")) result.SetColor("_EmissionColor", glow ? Color.white * 1.7f : Color.black);
            result.DisableKeyword("_NORMALMAP");
            result.DisableKeyword("_METALLICGLOSSMAP");
            if (glow) result.EnableKeyword("_EMISSION"); else result.DisableKeyword("_EMISSION");
            return result;
        }

        // A deterministic software render of the actual model gives the item a
        // matching icon without a hidden Camera or a dedicated-server GPU call.
        private static Sprite CreateIcon(List<Part> parts)
        {
            const int size = 128;
            Color[] pixels = new Color[size * size];
            float[] depths = new float[pixels.Length];
            for (int i = 0; i < depths.Length; ++i) depths[i] = float.PositiveInfinity;
            Vector3 forward = new Vector3(-1.8f, -1.25f, 2.6f).normalized;
            Vector3 right = Vector3.Cross(forward, Vector3.up).normalized;
            Vector3 up = Vector3.Cross(right, forward).normalized;
            Vector3 light = new Vector3(-0.6f, 1f, -0.8f).normalized;
            foreach (Part part in parts)
            {
                // The idol core is emissive but also belongs in its inventory icon.
                for (int i = 0; i < part.Mesh.Vertices.Count; i += 3)
                {
                    Vector3 a = Project(part.Mesh.Vertices[i], right, up, forward);
                    Vector3 b = Project(part.Mesh.Vertices[i + 1], right, up, forward);
                    Vector3 c = Project(part.Mesh.Vertices[i + 2], right, up, forward);
                    Vector3 normal = part.Mesh.Normals[i];
                    if (Vector3.Dot(normal, forward) >= 0f) continue;
                    float area = Edge(a, b, c.x, c.y);
                    if (Mathf.Abs(area) < 0.001f) continue;
                    int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, size - 1);
                    int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, size - 1);
                    int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, size - 1);
                    int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, size - 1);
                    float shading = 0.62f + Mathf.Max(0f, Vector3.Dot(normal, light)) * 0.62f;
                    Color color = part.Color * shading;
                    color.a = 1f;
                    for (int y = minY; y <= maxY; ++y)
                        for (int x = minX; x <= maxX; ++x)
                        {
                            float w0 = Edge(b, c, x + 0.5f, y + 0.5f) / area;
                            float w1 = Edge(c, a, x + 0.5f, y + 0.5f) / area;
                            float w2 = 1f - w0 - w1;
                            if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                            float depth = w0 * a.z + w1 * b.z + w2 * c.z;
                            int index = y * size + x;
                            if (depth >= depths[index]) continue;
                            depths[index] = depth;
                            pixels[index] = color;
                        }
                }
            }
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = "NordicRadio_IdolIcon";
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            ownedAssets.Add(texture);
            Sprite icon = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            icon.name = "NordicRadio_IdolIcon";
            ownedAssets.Add(icon);
            return icon;
        }
        private static Vector3 Project(Vector3 p, Vector3 right, Vector3 up, Vector3 forward)
        {
            p -= new Vector3(0f, 0.232f, 0f);
            return new Vector3(64f + Vector3.Dot(p, right) * 228f, 64f + Vector3.Dot(p, up) * 228f, Vector3.Dot(p, forward));
        }
        private static float Edge(Vector3 a, Vector3 b, float x, float y)
        { return (x - a.x) * (b.y - a.y) - (y - a.y) * (b.x - a.x); }

        private sealed class Shape
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            private readonly List<Vector2> uv = new List<Vector2>();
            public void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector2 u, Vector2 v, Vector2 w)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
                Normals.Add(normal); Normals.Add(normal); Normals.Add(normal);
                uv.Add(u); uv.Add(v); uv.Add(w);
            }
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 scale)
            {
                Triangle(a, b, c, Vector2.zero, new Vector2(scale.x, 0f), scale);
                Triangle(a, c, d, Vector2.zero, scale, new Vector2(0f, scale.y));
            }
            public void Box(Vector3 center, Vector3 size)
            { Box(center, size, Vector3.right, Vector3.up, Vector3.forward); }
            private void Box(Vector3 center, Vector3 size, Vector3 right, Vector3 up, Vector3 forward)
            {
                Vector3 h = size * 0.5f;
                Vector3[] p = new Vector3[8];
                for (int i = 0; i < 8; ++i)
                    p[i] = center + right * ((i & 1) == 0 ? -h.x : h.x)
                        + up * ((i & 2) == 0 ? -h.y : h.y) + forward * ((i & 4) == 0 ? -h.z : h.z);
                Quad(p[0], p[2], p[3], p[1], new Vector2(size.y * 2f, size.x * 2f));
                Quad(p[4], p[5], p[7], p[6], new Vector2(size.x * 2f, size.y * 2f));
                Quad(p[0], p[4], p[6], p[2], new Vector2(size.z * 2f, size.y * 2f));
                Quad(p[1], p[3], p[7], p[5], new Vector2(size.y * 2f, size.z * 2f));
                Quad(p[0], p[1], p[5], p[4], new Vector2(size.x * 2f, size.z * 2f));
                Quad(p[2], p[6], p[7], p[3], new Vector2(size.z * 2f, size.x * 2f));
            }
            public void Beam(Vector3 a, Vector3 b, float width)
            {
                Vector3 up = (b - a).normalized;
                Vector3 reference = Mathf.Abs(Vector3.Dot(up, Vector3.forward)) > 0.95f ? Vector3.right : Vector3.forward;
                Vector3 right = Vector3.Cross(up, reference).normalized;
                Box((a + b) * 0.5f, new Vector3(width, (b - a).magnitude, width), right, up, Vector3.Cross(right, up));
            }
            public void Octahedron(Vector3 center, Vector3 radii)
            {
                Vector3 top = center + Vector3.up * radii.y, bottom = center - Vector3.up * radii.y;
                Vector3[] ring = { center + Vector3.right * radii.x, center - Vector3.forward * radii.z, center - Vector3.right * radii.x, center + Vector3.forward * radii.z };
                for (int i = 0; i < 4; ++i)
                {
                    Triangle(top, ring[i], ring[(i + 1) % 4], Vector2.zero, Vector2.right, Vector2.one);
                    Triangle(bottom, ring[(i + 1) % 4], ring[i], Vector2.zero, Vector2.right, Vector2.one);
                }
            }
            public Mesh ToMesh(string name)
            {
                Mesh mesh = new Mesh();
                mesh.name = "NordicRadio_" + name;
                mesh.SetVertices(Vertices);
                mesh.SetNormals(Normals);
                mesh.SetUVs(0, uv);
                int[] indices = new int[Vertices.Count];
                for (int i = 0; i < indices.Length; ++i) indices[i] = i;
                mesh.triangles = indices;
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                ownedAssets.Add(mesh);
                return mesh;
            }
        }
    }
}
