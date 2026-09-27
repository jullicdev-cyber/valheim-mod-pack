using System;
using System.Collections.Generic;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimModPack.NordicRadio
{
    // Original model: built once, with shared meshes/materials on placed instances.
    // All geometry, textures and the inventory icon are generated here; game art
    // is referenced only at runtime for its lighting-compatible material shader.
    public static class HornModel
    {
        public const string PrefabName = "vmp_skald_horn";
        private static readonly List<Object> ownedAssets = new List<Object>();
        private static bool registered;
        private static readonly Color Wood = new Color(0.29f, 0.19f, 0.115f);
        private static readonly Color Bronze = new Color(0.53f, 0.36f, 0.16f);
        private static readonly Color Horn = new Color(0.61f, 0.49f, 0.30f);
        private static readonly Color Leather = new Color(0.23f, 0.12f, 0.065f);
        private static readonly Color Shadow = new Color(0.10f, 0.06f, 0.035f);
        private static readonly Color Amber = new Color(1f, 0.43f, 0.065f);

        public static void Register(int wood, int bronze, int leather, int core)
        {
            if (registered) return;
            GameObject source = PrefabManager.Instance.GetPrefab("piece_table");
            if (!source) throw new InvalidOperationException("NordicRadio requires the vanilla piece_table prefab.");
            Material sourceMaterial = null;
            WearNTear sourceWear = source.GetComponent<WearNTear>();
            GameObject sourceVisual = sourceWear && sourceWear.m_new ? sourceWear.m_new : source;
            foreach (MeshRenderer renderer in sourceVisual.GetComponentsInChildren<MeshRenderer>(true))
                foreach (Material candidate in renderer.sharedMaterials)
                {
                    // piece_table's first renderer is its snow overlay in 1.0.16.
                    // Reusing that shader makes ordinary furniture render as snow.
                    // HasProperty is false for every material in Unity's Null
                    // graphics device; shader metadata is still available there.
                    if (!candidate || !candidate.shader) continue;
                    string shaderName = candidate.shader.name;
                    if (shaderName.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (sourceMaterial == null)
                        sourceMaterial = candidate;
                }
            if (!sourceMaterial)
                throw new InvalidOperationException("NordicRadio could not find the vanilla furniture material.");
            GameObject prefab = PrefabManager.Instance.CreateClonedPrefab(PrefabName, source);
            if (!prefab) throw new InvalidOperationException("NordicRadio prefab name is already registered.");
            try
            {
                // The clone is under Jotunn's inactive prefab container: no live
                // world objects are affected by this removal of table visuals.
                for (int i = prefab.transform.childCount - 1; i >= 0; --i)
                    Object.DestroyImmediate(prefab.transform.GetChild(i).gameObject);
                foreach (Collider c in prefab.GetComponents<Collider>()) Object.DestroyImmediate(c);
                foreach (Renderer r in prefab.GetComponents<Renderer>()) Object.DestroyImmediate(r);
                foreach (MeshFilter f in prefab.GetComponents<MeshFilter>()) Object.DestroyImmediate(f);
                foreach (LODGroup group in prefab.GetComponents<LODGroup>()) Object.DestroyImmediate(group);
                int layer = LayerMask.NameToLayer("piece");
                if (layer < 0) throw new InvalidOperationException("The piece collision layer is unavailable.");
                prefab.layer = layer;
                GameObject model = Child(prefab.transform, "SkaldModel");
                List<Part> parts = BuildGeometry();
                foreach (Part part in parts)
                {
                    Material material = MakeMaterial(sourceMaterial, part.Name, part.Color, part.Pattern, part.Glow);
                    GameObject child = Child(model.transform, part.Glow ? "PlayingGlow" : part.Name);
                    child.AddComponent<MeshFilter>().sharedMesh = part.Mesh.ToMesh(part.Name);
                    child.AddComponent<MeshRenderer>().sharedMaterial = material;
                    if (part.Glow) child.SetActive(false);
                }
                GameObject lamp = Child(model.transform, "CoreLight");
                lamp.transform.localPosition = new Vector3(0f, 0.29f, -0.18f);
                Light light = lamp.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = Amber;
                light.range = 1.4f;
                light.intensity = 0.65f;
                light.shadows = LightShadows.None;
                light.enabled = false;

                // Broad cabinet and a separate horn collider: hammer targeting
                // uses the piece layer; neither collider blocks the bell opening.
                BoxCollider cabinet = prefab.AddComponent<BoxCollider>();
                cabinet.center = new Vector3(0f, 0.295f, 0f);
                cabinet.size = new Vector3(1.2f, 0.59f, 0.66f);
                BoxCollider hornCollider = prefab.AddComponent<BoxCollider>();
                hornCollider.center = new Vector3(0.025f, 0.91f, 0f);
                hornCollider.size = new Vector3(1.02f, 0.39f, 0.26f);
                GameObject snap = Child(prefab.transform, "_snappoint");
                snap.tag = "snappoint";

                WearNTear wear = prefab.GetComponent<WearNTear>();
                if (!wear || !prefab.GetComponent<ZNetView>() || !prefab.GetComponent<Piece>())
                    throw new InvalidOperationException("Vanilla furniture is missing build/network components.");
                // The current game's SetHealthVisual assumes all three are valid.
                wear.m_new = wear.m_worn = wear.m_broken = model;
                wear.m_wet = null;
                wear.m_snow = wear.m_snowWorn = wear.m_snowBroken = null;
                wear.m_fragmentRoots = new[] { model };
                wear.m_nonSolidRenderers = new List<Renderer>();
                wear.m_noRoofWear = true;
                wear.m_supports = false;
                wear.m_health = 200f;
                wear.m_comOffset = new Vector3(0f, 0.45f, 0f);
                Piece piece = prefab.GetComponent<Piece>();
                piece.m_comfort = 0;
                piece.m_comfortObject = null;
                piece.m_groundOnly = false;
                piece.m_groundPiece = false;
                piece.m_notOnWood = false;
                piece.m_notOnFloor = false;
                piece.m_notOnTiltingSurface = true;
                piece.m_canBeRemoved = true;
                piece.m_canRotate = true;
                prefab.AddComponent<RadioPiece>();
                AddLocalization();
                PieceConfig config = new PieceConfig
                {
                    Name = "$vmp_skald_horn", Description = "$vmp_skald_horn_description",
                    PieceTable = "Hammer", Category = "Furniture", CraftingStation = "piece_workbench",
                    Icon = CreateIcon(parts),
                    Requirements = new[]
                    {
                        new RequirementConfig("FineWood", Math.Max(1, wood), 0, true),
                        new RequirementConfig("Bronze", Math.Max(1, bronze), 0, true),
                        new RequirementConfig("LeatherScraps", Math.Max(1, leather), 0, true),
                        new RequirementConfig("SurtlingCore", Math.Max(1, core), 0, true)
                    }
                };
                if (!PieceManager.Instance.AddPiece(new CustomPiece(prefab, false, config)))
                    throw new InvalidOperationException("Jotunn rejected the NordicRadio building piece.");
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
            localization.AddJsonFile("English", "{\"vmp_skald_horn\":\"Skald's Horn\",\"vmp_skald_horn_description\":\"A carved, rune-bound horn. Share the host's music with nearby Vikings.\",\"vmp_radio_open\":\"Music controls\"}");
            localization.AddJsonFile("Russian", "{\"vmp_skald_horn\":\"Рог скальда\",\"vmp_skald_horn_description\":\"Резной рог с древними рунами. Музыка хозяина мира для всех викингов поблизости.\",\"vmp_radio_open\":\"Управление музыкой\"}");
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
            Part wood = new Part("CarvedWood", Wood, 0, false);
            Part bronze = new Part("BronzeFittings", Bronze, 1, false);
            Part horn = new Part("HornShell", Horn, 2, false);
            Part leather = new Part("LeatherBindings", Leather, 3, false);
            Part dark = new Part("CarvedRecesses", Shadow, 3, false);
            Part amber = new Part("CoreAndRunes", new Color(0.55f, 0.19f, 0.035f), 1, false);
            Part glow = new Part("PlayingGlow", Amber, 1, true);

            // Individual boards, four feet and a recessed resonator cabinet.
            wood.Mesh.Box(new Vector3(0f, 0.115f, 0f), new Vector3(1.14f, 0.10f, 0.59f));
            wood.Mesh.Box(new Vector3(0f, 0.535f, 0f), new Vector3(1.20f, 0.105f, 0.66f));
            foreach (float x in new[] { -0.48f, 0.48f })
            {
                foreach (float z in new[] { -0.23f, 0.23f })
                    wood.Mesh.Box(new Vector3(x, 0.057f, z), new Vector3(0.16f, 0.114f, 0.16f));
                wood.Mesh.Box(new Vector3(x, 0.325f, 0f), new Vector3(0.13f, 0.36f, 0.54f));
                bronze.Mesh.Box(new Vector3(x, 0.535f, 0f), new Vector3(0.062f, 0.116f, 0.67f));
            }
            for (int i = 0; i < 5; ++i)
                wood.Mesh.Box(new Vector3(-0.35f + i * 0.176f, 0.325f, 0.238f), new Vector3(0.169f, 0.32f, 0.055f));
            dark.Mesh.Box(new Vector3(0f, 0.315f, 0.155f), new Vector3(0.88f, 0.30f, 0.065f));
            wood.Mesh.Box(new Vector3(0f, 0.178f, -0.263f), new Vector3(0.93f, 0.055f, 0.06f));
            wood.Mesh.Box(new Vector3(0f, 0.466f, -0.263f), new Vector3(0.93f, 0.055f, 0.06f));
            for (int i = -4; i <= 4; ++i)
            {
                if (i == 0) continue;
                wood.Mesh.Box(new Vector3(i * 0.093f, 0.322f, -0.267f), new Vector3(0.025f, 0.235f, 0.045f));
            }
            amber.Mesh.Octahedron(new Vector3(0f, 0.325f, -0.12f), new Vector3(0.115f, 0.14f, 0.105f));
            glow.Mesh.Octahedron(new Vector3(0f, 0.325f, -0.12f), new Vector3(0.118f, 0.143f, 0.108f));

            // Two carved rests lift the curved horn clear of the wooden top.
            wood.Mesh.Box(new Vector3(-0.35f, 0.665f, 0f), new Vector3(0.12f, 0.16f, 0.22f));
            wood.Mesh.Box(new Vector3(0.24f, 0.725f, 0f), new Vector3(0.12f, 0.28f, 0.24f));
            Vector3[] centers =
            {
                new Vector3(-0.34f, 0.63f, 0f), new Vector3(-0.49f, 0.74f, 0f),
                new Vector3(-0.48f, 0.89f, 0f), new Vector3(-0.33f, 0.99f, 0f),
                new Vector3(-0.10f, 1.015f, 0f), new Vector3(0.16f, 1.015f, 0f),
                new Vector3(0.38f, 1.04f, 0f), new Vector3(0.56f, 1.105f, 0f)
            };
            float[] radii = { 0.026f, 0.040f, 0.055f, 0.077f, 0.108f, 0.16f, 0.223f, 0.292f };
            Tube(horn.Mesh, centers, radii, 14, false);
            float[] inside = new float[radii.Length];
            for (int i = 0; i < radii.Length; ++i) inside[i] = Mathf.Max(0.01f, radii[i] - 0.018f);
            Tube(dark.Mesh, centers, inside, 14, true);
            // Closed tip, open bell, with a substantial bronze rim.
            Ring(bronze.Mesh, centers[7], Tangent(centers, 7), radii[7] + 0.012f, inside[7], 0.037f, 14);
            Ring(bronze.Mesh, centers[4], Tangent(centers, 4), radii[4] + 0.012f, radii[4] - 0.012f, 0.045f, 14);
            Ring(bronze.Mesh, centers[2], Tangent(centers, 2), radii[2] + 0.010f, radii[2] - 0.010f, 0.04f, 14);
            Ring(leather.Mesh, centers[5], Tangent(centers, 5), radii[5] + 0.010f, radii[5] - 0.010f, 0.075f, 14);
            Ring(leather.Mesh, centers[0], Tangent(centers, 0), radii[0] + 0.013f, 0.001f, 0.047f, 10);
            foreach (float z in new[] { -0.125f, 0.125f })
                leather.Mesh.Beam(new Vector3(0.24f, 0.59f, z), new Vector3(0.20f, 0.98f, z), 0.023f);

            // The angular relief is readable at ordinary gameplay distance.
            foreach (float x in new[] { -0.49f, 0.49f })
            {
                Rune(dark.Mesh, x, 0.34f, -0.278f, 0.082f, 0.014f);
                Rune(amber.Mesh, x, 0.34f, -0.284f, 0.061f, 0.006f);
                Rune(glow.Mesh, x, 0.34f, -0.285f, 0.062f, 0.006f);
            }
            for (int i = -3; i <= 3; ++i)
            {
                float x = i * 0.12f;
                dark.Mesh.Beam(new Vector3(x - 0.043f, 0.513f, -0.331f), new Vector3(x, 0.549f, -0.331f), 0.009f);
                dark.Mesh.Beam(new Vector3(x, 0.549f, -0.331f), new Vector3(x + 0.043f, 0.513f, -0.331f), 0.009f);
            }
            return new List<Part> { wood, bronze, horn, leather, dark, amber, glow };
        }

        private static void Rune(Shape mesh, float x, float y, float z, float h, float width)
        {
            mesh.Beam(new Vector3(x, y - h, z), new Vector3(x, y + h, z), width);
            mesh.Beam(new Vector3(x, y + h * 0.7f, z), new Vector3(x + h * 0.55f, y + h * 0.15f, z), width);
            mesh.Beam(new Vector3(x, y + h * 0.15f, z), new Vector3(x - h * 0.55f, y - h * 0.45f, z), width);
        }
        private static Vector3 Tangent(Vector3[] centers, int i)
        { return (centers[Math.Min(i + 1, centers.Length - 1)] - centers[Math.Max(i - 1, 0)]).normalized; }
        private static Vector3 Radial(Vector3 tangent, float angle)
        { return Vector3.forward * Mathf.Cos(angle) + Vector3.Cross(tangent, Vector3.forward).normalized * Mathf.Sin(angle); }
        private static void Tube(Shape mesh, Vector3[] centers, float[] radii, int sides, bool inward)
        {
            for (int i = 0; i < centers.Length - 1; ++i)
                for (int j = 0; j < sides; ++j)
                {
                    float a = j * 2f * Mathf.PI / sides, b = (j + 1) * 2f * Mathf.PI / sides;
                    Vector3 p0 = centers[i] + Radial(Tangent(centers, i), a) * radii[i];
                    Vector3 p1 = centers[i] + Radial(Tangent(centers, i), b) * radii[i];
                    Vector3 p2 = centers[i + 1] + Radial(Tangent(centers, i + 1), b) * radii[i + 1];
                    Vector3 p3 = centers[i + 1] + Radial(Tangent(centers, i + 1), a) * radii[i + 1];
                    if (inward) mesh.Quad(p3, p2, p1, p0, new Vector2(0.3f, 0.7f));
                    else mesh.Quad(p0, p1, p2, p3, new Vector2(0.3f, 0.7f));
                }
        }
        private static void Ring(Shape mesh, Vector3 center, Vector3 tangent, float outer, float inner, float width, int sides)
        {
            Vector3[] centers = { center - tangent * width * 0.5f, center + tangent * width * 0.5f };
            Tube(mesh, centers, new[] { outer, outer }, sides, false);
            Tube(mesh, centers, new[] { inner, inner }, sides, true);
            for (int j = 0; j < sides; ++j)
            {
                Vector3 a = Radial(tangent, j * 2f * Mathf.PI / sides);
                Vector3 b = Radial(tangent, (j + 1) * 2f * Mathf.PI / sides);
                mesh.Quad(centers[0] + b * outer, centers[0] + a * outer, centers[0] + a * inner, centers[0] + b * inner, Vector2.one);
                mesh.Quad(centers[1] + a * outer, centers[1] + b * outer, centers[1] + b * inner, centers[1] + a * inner, Vector2.one);
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

        // A deterministic software render of the actual model gives the hammer a
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
                if (part.Glow) continue;
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
            texture.name = "NordicRadio_HammerIcon";
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            ownedAssets.Add(texture);
            Sprite icon = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            icon.name = "NordicRadio_HammerIcon";
            ownedAssets.Add(icon);
            return icon;
        }
        private static Vector3 Project(Vector3 p, Vector3 right, Vector3 up, Vector3 forward)
        {
            p -= new Vector3(0f, 0.66f, 0f);
            return new Vector3(64f + Vector3.Dot(p, right) * 77f, 64f + Vector3.Dot(p, up) * 77f, Vector3.Dot(p, forward));
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
