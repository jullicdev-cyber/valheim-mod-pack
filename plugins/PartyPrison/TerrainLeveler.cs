using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace ValheimModPack.PartyPrison
{
    /// <summary>One host-side terrain transaction; no work runs from Update or other hot paths.</summary>
    public static class TerrainLeveler
    {
        private const float HeightTolerance = 0.04f;
        private const float UnchangedTolerance = 0.002f;
        private static int unityThread;
        private static readonly MethodInfo Operation = NativeMethod("InternalDoOperation", typeof(Vector3), typeof(Vector3), typeof(TerrainOp.Settings));
        private static readonly MethodInfo Save = NativeMethod("Save", typeof(bool));
        private static readonly FieldInfo Initialized = NativeField("m_initialized");
        private static readonly FieldInfo Width = NativeField("m_width");
        private static readonly FieldInfo Pitch = NativeField("m_pitch");
        private static readonly FieldInfo Level = NativeField("m_levelDelta");
        private static readonly FieldInfo Smooth = NativeField("m_smoothDelta");
        private static readonly FieldInfo ModifiedHeight = NativeField("m_modifiedHeight");
        private static readonly FieldInfo ModifiedPaint = NativeField("m_modifiedPaint");
        private static readonly FieldInfo Paint = NativeField("m_paintMask");
        private static readonly FieldInfo Operations = NativeField("m_operations");
        private static readonly FieldInfo LastPoint = NativeField("m_lastOpPoint");
        private static readonly FieldInfo LastRadius = NativeField("m_lastOpRadius");
        private static readonly FieldInfo LastHash = NativeField("m_lastHash");
        private static readonly FieldInfo LastRevision = NativeField("m_lastDataRevision");
        private static readonly FieldInfo View = NativeField("m_nview");
        private static readonly FieldInfo Map = NativeField("m_hmap");
        private static readonly FieldInfo Heights = typeof(Heightmap).GetField("m_heights", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Inspect loaded terrain only. Rejected sites create no terrain compilers or network changes.</summary>
        public static Site Plan(Vector3 center, Vector3 altar)
        {
            RequireHost();
            TerrainPlan.RequireFootprint(center.x, center.z, altar.x, altar.z);
            // Loaded ground alone is insufficient: nearby network objects may still be queued.
            if (ZNetScene.instance == null || !ZNetScene.instance.IsAreaReady(center))
                throw new InvalidOperationException("Объекты возле площадки ещё загружаются. Подойдите ближе и повторите постройку.");
            float extent = (float)(TerrainPlan.HalfWidth + TerrainPlan.FootprintPadding);
            List<Heightmap> maps = new List<Heightmap>();
            Heightmap.FindHeightmap(center, extent * 1.414214f + 1f, maps);
            HashSet<Heightmap> seen = new HashSet<Heightmap>();
            List<NativeTile> tiles = new List<NativeTile>();
            List<double> ground = new List<double>(), baseline = new List<double>();
            foreach (Heightmap hmap in maps) {
                if (hmap == null || hmap.IsDistantLod || !seen.Add(hmap)) continue;
                Vector3 position = hmap.transform.position;
                if (hmap.m_width < 1 || hmap.m_width > 512 || !Finite(hmap.m_scale) || hmap.m_scale <= 0f || hmap.m_scale > 2f)
                    throw new InvalidOperationException("Неподдерживаемый размер участка земли.");
                float half = hmap.m_width * hmap.m_scale * 0.5f;
                if (position.x + half < center.x - extent || position.x - half > center.x + extent ||
                    position.z + half < center.z - extent || position.z - half > center.z + extent) continue;
                if (hmap.HaveQueuedRebuild())
                    throw new InvalidOperationException("Земля ещё обновляется. Повторите создание тюрьмы через несколько секунд.");
                List<float> heights = Heights == null ? null : Heights.GetValue(hmap) as List<float>;
                int pitch = hmap.m_width + 1;
                if (heights == null || heights.Count != pitch * pitch)
                    throw new InvalidOperationException("Высота земли ещё не загружена. Подойдите ближе к площадке.");
                TerrainComp compiler = TerrainComp.FindTerrainCompiler(position);
                if (compiler != null) CheckCompiler(compiler, hmap, true);
                float[] level = compiler == null ? null : (float[])Level.GetValue(compiler);
                float[] smooth = compiler == null ? null : (float[])Smooth.GetValue(compiler);
                NativeTile tile = new NativeTile(hmap, compiler, position, hmap.m_width, hmap.m_scale);
                // A seam vertex belongs to every adjoining heightmap. Keep each copy.
                for (int z = 0; z <= hmap.m_width; ++z)
                    for (int x = 0; x <= hmap.m_width; ++x) {
                        Vector3 vertex = position + new Vector3((x - hmap.m_width * 0.5f) * hmap.m_scale, 0f,
                            (z - hmap.m_width * 0.5f) * hmap.m_scale);
                        if (Mathf.Abs(vertex.x - center.x) > extent || Mathf.Abs(vertex.z - center.z) > extent) continue;
                        int index = z * pitch + x;
                        float worldHeight = position.y + hmap.GetHeight(x, z);
                        if (!Finite(worldHeight)) throw new InvalidOperationException("Не удалось прочитать высоту земли.");
                        float oldLevel = level == null ? 0f : level[index], oldSmooth = smooth == null ? 0f : smooth[index];
                        tile.Vertices.Add(new Vertex(x, z, vertex, worldHeight, oldLevel, oldSmooth));
                        ground.Add(worldHeight); baseline.Add(worldHeight - oldLevel - oldSmooth);
                        if (ground.Count > TerrainPlan.MaximumSamples)
                            throw new InvalidOperationException("Площадка затрагивает слишком много вершин земли.");
                    }
                if (tile.Vertices.Count != 0) tiles.Add(tile);
                if (tiles.Count > TerrainPlan.MaximumCompilers)
                    throw new InvalidOperationException("Площадка затрагивает слишком много участков земли.");
            }
            RequireCoverage(center, extent, tiles);
            TerrainLevelPlan decision = TerrainPlan.Create(center.x, center.z, altar.x, altar.z,
                ZoneSystem.instance.m_waterLevel, ground, baseline, tiles.Count);
            return new Site(center, decision, tiles, ZNet.instance, ZNet.instance.GetWorldUID());
        }

        public sealed class Site
        {
            private readonly Vector3 center;
            private readonly TerrainLevelPlan decision;
            private readonly List<NativeTile> tiles;
            private readonly ZNet network;
            private readonly long world;
            private bool applied;
            public float TargetHeight { get { return (float)decision.TargetHeight; } }
            public float FloorHeight { get { return (float)decision.FloorHeight; } }

            internal Site(Vector3 center, TerrainLevelPlan decision, List<NativeTile> tiles, ZNet network, long world)
            { this.center = center; this.decision = decision; this.tiles = tiles; this.network = network; this.world = world; }

            public Transaction Apply()
            {
                RequireHost();
                if (applied) throw new InvalidOperationException("Эта площадка уже была выровнена.");
                if (!ReferenceEquals(network, ZNet.instance) || world != ZNet.instance.GetWorldUID())
                    throw new InvalidOperationException("Игровой мир сменился. Заново выберите площадку.");
                // Revalidate the whole read-only plan before creating or claiming anything.
                foreach (NativeTile tile in tiles) tile.CheckUnchanged();
                applied = true;
                Transaction transaction = new Transaction(center, TargetHeight, tiles, network, world);
                try { transaction.Apply(); return transaction; }
                catch (Exception error) {
                    try { transaction.Dispose(); }
                    catch (Exception rollback) {
                        throw new InvalidOperationException("Не удалось выровнять землю; также не удалось полностью отменить изменение. " +
                            "Не создавайте тюрьму повторно до проверки площадки. " + rollback.Message, new AggregateException(error, rollback));
                    }
                    throw new InvalidOperationException("Не удалось выровнять площадку. Изменения земли отменены. " + RootMessage(error), error);
                }
            }
        }

        public sealed class Transaction : IDisposable
        {
            private readonly Vector3 center;
            private readonly float target;
            private readonly List<NativeTile> tiles;
            private readonly List<Snapshot> snapshots = new List<Snapshot>();
            private readonly ZNet network;
            private readonly long world;
            private bool committed, disposed;

            internal Transaction(Vector3 center, float target, List<NativeTile> tiles, ZNet network, long world)
            { this.center = center; this.target = target; this.tiles = tiles; this.network = network; this.world = world; }

            internal void Apply()
            {
                // Take every snapshot and ownership before the first height is changed.
                foreach (NativeTile tile in tiles) {
                    TerrainComp compiler = tile.Heightmap.GetAndCreateTerrainCompiler();
                    CheckCompiler(compiler, tile.Heightmap, true);
                    Snapshot snapshot = new Snapshot(compiler, tile.Heightmap);
                    snapshots.Add(snapshot);
                    snapshot.Claim();
                    tile.Compiler = compiler;
                }
                TerrainOp.Settings settings = new TerrainOp.Settings {
                    m_level = true, m_levelOffset = 0f, m_levelRadius = 0f, m_square = true,
                    m_raise = false, m_smooth = false, m_paintCleared = false
                };
                foreach (NativeTile tile in tiles)
                    foreach (Vertex vertex in tile.Vertices) {
                        Vector3 point = vertex.Position; point.y = target;
                        Operation.Invoke(tile.Compiler, new object[] { point, Vector3.zero, settings });
                    }
                foreach (Snapshot snapshot in snapshots) snapshot.Persist();
                foreach (Snapshot snapshot in snapshots) snapshot.Heightmap.Poke(0, false);
                // Native terrain retains its +/-8 m clamp. Detect a clamp or stale mesh before building.
                foreach (NativeTile tile in tiles)
                    foreach (Vertex vertex in tile.Vertices)
                        if (Mathf.Abs(tile.Heightmap.transform.position.y + tile.Heightmap.GetHeight(vertex.X, vertex.Z) - target) > HeightTolerance)
                            throw new InvalidOperationException("Valheim не смог выровнять эту площадку в пределах допустимой высоты земли.");
                ResetGrass();
            }

            public void Commit()
            {
                RequireHost();
                if (disposed) throw new ObjectDisposedException("TerrainLeveler.Transaction");
                RequireWorld(); committed = true;
            }

            public void Dispose()
            {
                if (disposed) return;
                CheckThread();
                if (committed) { disposed = true; return; }
                RequireWorld();
                List<Exception> errors = new List<Exception>();
                foreach (Snapshot snapshot in snapshots) {
                    try { snapshot.Restore(); }
                    catch (Exception error) { errors.Add(error); }
                }
                foreach (Snapshot snapshot in snapshots) {
                    try { if (snapshot.Heightmap != null) snapshot.Heightmap.Poke(0, false); }
                    catch (Exception error) { errors.Add(error); }
                }
                foreach (NativeTile tile in tiles) {
                    try {
                        if (tile.Heightmap == null) throw new InvalidOperationException("Участок земли выгрузился во время отмены.");
                        foreach (Vertex vertex in tile.Vertices)
                            if (Mathf.Abs(tile.Heightmap.transform.position.y + tile.Heightmap.GetHeight(vertex.X, vertex.Z) - vertex.Ground) > HeightTolerance)
                                throw new InvalidOperationException("Высота земли после отмены не совпала с исходной.");
                    }
                    catch (Exception error) { errors.Add(error); }
                }
                try { ResetGrass(); } catch (Exception error) { errors.Add(error); }
                disposed = true;
                if (errors.Count != 0)
                    throw new InvalidOperationException("Ошибки отмены выравнивания земли: " + RootMessage(errors[0]), new AggregateException(errors));
            }

            private void RequireWorld()
            {
                RequireHost();
                if (!ReferenceEquals(network, ZNet.instance) || world != ZNet.instance.GetWorldUID())
                    throw new InvalidOperationException("Мир сменился до завершения операции с землёй.");
            }

            private void ResetGrass()
            { if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(center, (float)(TerrainPlan.HalfWidth + TerrainPlan.FootprintPadding) * 1.414214f); }
        }

        internal sealed class NativeTile
        {
            internal readonly Heightmap Heightmap;
            internal TerrainComp Compiler;
            internal readonly Vector3 Position;
            internal readonly int Width;
            internal readonly float Scale;
            internal readonly List<Vertex> Vertices = new List<Vertex>();
            private readonly uint revision;
            internal NativeTile(Heightmap hmap, TerrainComp compiler, Vector3 position, int width, float scale)
            {
                Heightmap = hmap; Compiler = compiler; Position = position; Width = width; Scale = scale;
                revision = compiler == null ? 0u : NativeView(compiler).GetZDO().DataRevision;
            }
            internal void CheckUnchanged()
            {
                if (Heightmap == null || Heightmap.transform.position != Position || Heightmap.m_width != Width ||
                    Heightmap.m_scale != Scale || Heightmap.HaveQueuedRebuild()) Changed();
                TerrainComp current = TerrainComp.FindTerrainCompiler(Position);
                if (!ReferenceEquals(current, Compiler)) Changed();
                if (current != null) {
                    CheckCompiler(current, Heightmap, true);
                    if (NativeView(current).GetZDO().DataRevision != revision) Changed();
                }
                foreach (Vertex vertex in Vertices)
                    if (Mathf.Abs(Position.y + Heightmap.GetHeight(vertex.X, vertex.Z) - vertex.Ground) > UnchangedTolerance) Changed();
            }
            private static void Changed() { throw new InvalidOperationException("Земля изменилась после проверки. Заново выберите площадку."); }
        }

        internal sealed class Vertex
        {
            internal readonly int X, Z;
            internal readonly Vector3 Position;
            internal readonly float Ground, OldLevel, OldSmooth;
            internal Vertex(int x, int z, Vector3 position, float ground, float level, float smooth)
            { X = x; Z = z; Position = position; Ground = ground; OldLevel = level; OldSmooth = smooth; }
        }

        private sealed class Snapshot
        {
            internal readonly TerrainComp Compiler;
            internal readonly Heightmap Heightmap;
            private readonly ZNetView view;
            private readonly float[] level, smooth;
            private readonly bool[] modifiedHeight, modifiedPaint;
            private readonly Color[] paint;
            private readonly int operations, hash;
            private readonly long owner;
            private readonly Vector3 point;
            private readonly float radius;
            internal Snapshot(TerrainComp compiler, Heightmap hmap)
            {
                Compiler = compiler; Heightmap = hmap; view = NativeView(compiler);
                owner = view.GetZDO().GetOwner();
                level = (float[])((float[])Level.GetValue(compiler)).Clone();
                smooth = (float[])((float[])Smooth.GetValue(compiler)).Clone();
                modifiedHeight = (bool[])((bool[])ModifiedHeight.GetValue(compiler)).Clone();
                modifiedPaint = (bool[])((bool[])ModifiedPaint.GetValue(compiler)).Clone();
                paint = (Color[])((Color[])Paint.GetValue(compiler)).Clone();
                operations = (int)Operations.GetValue(compiler); hash = (int)LastHash.GetValue(compiler);
                point = (Vector3)LastPoint.GetValue(compiler); radius = (float)LastRadius.GetValue(compiler);
            }
            internal void Claim()
            {
                if (Compiler == null || Heightmap == null || view == null || !view.IsValid())
                    throw new InvalidOperationException("Участок земли выгрузился до выравнивания.");
                view.ClaimOwnership();
                if (!view.IsOwner() || !Compiler.IsOwner())
                    throw new InvalidOperationException("Хост не получил управление участком земли.");
            }
            internal void Persist()
            {
                Claim(); Save.Invoke(Compiler, new object[] { false });
                byte[] data = view.GetZDO().GetByteArray(ZDOVars.s_TCData, null);
                if (data == null || data.Length == 0)
                    throw new InvalidOperationException("Не удалось сохранить выравнивание земли в мире.");
            }
            internal void Restore()
            {
                Claim();
                Level.SetValue(Compiler, (float[])level.Clone()); Smooth.SetValue(Compiler, (float[])smooth.Clone());
                ModifiedHeight.SetValue(Compiler, (bool[])modifiedHeight.Clone());
                ModifiedPaint.SetValue(Compiler, (bool[])modifiedPaint.Clone()); Paint.SetValue(Compiler, (Color[])paint.Clone());
                Operations.SetValue(Compiler, operations); LastHash.SetValue(Compiler, hash);
                LastPoint.SetValue(Compiler, point); LastRadius.SetValue(Compiler, radius);
                Persist();
                view.GetZDO().SetOwner(owner);
            }
        }

        private static void RequireCoverage(Vector3 center, float extent, List<NativeTile> tiles)
        {
            // Checking the padded rectangle, including its edges, detects missing neighboring tiles.
            int steps = Mathf.CeilToInt(extent * 2f);
            for (int z = 0; z <= steps; ++z)
                for (int x = 0; x <= steps; ++x) {
                    float px = center.x - extent + extent * 2f * x / steps;
                    float pz = center.z - extent + extent * 2f * z / steps;
                    bool covered = false;
                    foreach (NativeTile tile in tiles) {
                        float half = tile.Width * tile.Scale * 0.5f;
                        if (Mathf.Abs(px - tile.Position.x) <= half && Mathf.Abs(pz - tile.Position.z) <= half) { covered = true; break; }
                    }
                    if (!covered || !ZoneSystem.instance.IsZoneLoaded(new Vector3(px, center.y, pz)))
                        throw new InvalidOperationException("Вся площадка под тюрьму должна быть загружена. Подойдите ближе.");
                }
        }

        private static void CheckCompiler(TerrainComp compiler, Heightmap hmap, bool requireCurrent)
        {
            if (compiler == null || !(bool)Initialized.GetValue(compiler) || !ReferenceEquals(Map.GetValue(compiler), hmap) ||
                compiler.transform.position != hmap.transform.position ||
                (int)Width.GetValue(compiler) != hmap.m_width || (int)Pitch.GetValue(compiler) != hmap.m_width + 1)
                throw new InvalidOperationException("Не удалось проверить компилятор земли в этой версии Valheim.");
            ZNetView view = NativeView(compiler);
            int count = (hmap.m_width + 1) * (hmap.m_width + 1);
            foreach (FieldInfo field in new FieldInfo[] { Level, Smooth, ModifiedHeight, ModifiedPaint, Paint }) {
                Array data = field.GetValue(compiler) as Array;
                if (data == null || data.Length != count)
                    throw new InvalidOperationException("Не удалось проверить сохранённую высоту земли.");
            }
            if (requireCurrent && (uint)LastRevision.GetValue(compiler) != view.GetZDO().DataRevision)
                throw new InvalidOperationException("Земля ещё синхронизируется. Повторите создание тюрьмы через несколько секунд.");
        }

        private static ZNetView NativeView(TerrainComp compiler)
        {
            ZNetView view = compiler == null ? null : View.GetValue(compiler) as ZNetView;
            if (view == null || !view.IsValid()) throw new InvalidOperationException("Участок земли не имеет действительного сетевого состояния.");
            return view;
        }

        private static MethodInfo NativeMethod(string name, params Type[] parameters)
        {
            MethodInfo method = typeof(TerrainComp).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, parameters, null);
            if (method == null) throw new MissingMethodException("TerrainComp", name);
            return method;
        }
        private static FieldInfo NativeField(string name)
        {
            FieldInfo field = typeof(TerrainComp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException("TerrainComp", name);
            return field;
        }
        private static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        private static string RootMessage(Exception error)
        { while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException; return error.Message; }
        private static void CheckThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            if (unityThread == 0) unityThread = current;
            if (unityThread != current) throw new InvalidOperationException("Выравнивание земли доступно только из игрового потока Unity.");
        }
        private static void RequireHost()
        {
            CheckThread();
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZoneSystem.instance == null || Player.m_localPlayer == null)
                throw new InvalidOperationException("Выравнивать площадку под тюрьму может только хост в игровом мире.");
        }
    }
}
