using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Metadata-only checks. Neither Valheim nor a Unity component is loaded or executed.
internal static class TerrainContractChecks
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        ++checks;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static TypeDefinition Type(AssemblyDefinition assembly, string fullName)
    {
        TypeDefinition result = AllTypes(assembly.MainModule.Types).FirstOrDefault(t => t.FullName == fullName);
        Check(result != null, "Missing native/plugin type: " + fullName);
        return result;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types) {
            yield return type;
            foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }

    private static MethodDefinition Method(TypeDefinition type, string name, string returns, params string[] parameters)
    {
        MethodDefinition[] matches = type.Methods.Where(m => m.Name == name && m.ReturnType.FullName == returns &&
            m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
        Check(matches.Length == 1, "Changed native/plugin method contract: " + type.FullName + "." + name);
        return matches[0];
    }

    private static void Field(TypeDefinition type, string name, string fieldType)
    {
        FieldDefinition[] matches = type.Fields.Where(f => f.Name == name && f.FieldType.FullName == fieldType).ToArray();
        Check(matches.Length == 1, "Changed terrain snapshot field: " + type.FullName + "." + name);
        Check(!matches[0].IsStatic, "A terrain snapshot member became static: " + type.FullName + "." + name);
    }

    private static bool Calls(MethodDefinition method, string declaringType, string name)
    {
        return CallInstructions(method).Any(i => {
            MethodReference target = (MethodReference)i.Operand;
            return target.DeclaringType.FullName == declaringType && target.Name == name;
        });
    }

    private static IEnumerable<Instruction> CallInstructions(MethodDefinition method)
    {
        return method.Body.Instructions.Where(i => i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Callvirt || i.OpCode.Code == Code.Newobj);
    }

    private static bool UsesField(MethodDefinition method, string declaringType, string name)
    {
        return method.Body.Instructions.Any(i => i.Operand is FieldReference &&
            ((FieldReference)i.Operand).DeclaringType.FullName == declaringType && ((FieldReference)i.Operand).Name == name);
    }

    private static bool Float(Instruction instruction, float value)
    {
        return instruction.OpCode.Code == Code.Ldc_R4 && (float)instruction.Operand == value;
    }

    private static bool Int(Instruction instruction, int value)
    {
        switch (instruction.OpCode.Code) {
            case Code.Ldc_I4_M1: return value == -1;
            case Code.Ldc_I4_0: return value == 0;
            case Code.Ldc_I4_1: return value == 1;
            case Code.Ldc_I4_2: return value == 2;
            case Code.Ldc_I4_3: return value == 3;
            case Code.Ldc_I4_4: return value == 4;
            case Code.Ldc_I4_5: return value == 5;
            case Code.Ldc_I4_6: return value == 6;
            case Code.Ldc_I4_7: return value == 7;
            case Code.Ldc_I4_8: return value == 8;
            case Code.Ldc_I4: return (int)instruction.Operand == value;
            case Code.Ldc_I4_S: return (sbyte)instruction.Operand == value;
            default: return false;
        }
    }

    private static void Native(AssemblyDefinition assembly)
    {
        TypeDefinition compiler = Type(assembly, "TerrainComp");
        TypeDefinition heightmap = Type(assembly, "Heightmap");
        TypeDefinition settings = Type(assembly, "TerrainOp/Settings");
        TypeDefinition view = Type(assembly, "ZNetView");
        TypeDefinition location = Type(assembly, "Location");
        Field(location, "m_noBuild", "System.Boolean");
        Field(location, "m_noBuildRadiusOverride", "System.Single");
        Field(location, "m_exteriorRadius", "System.Single");
        Field(location, "m_interiorRadius", "System.Single");
        Field(location, "m_hasInterior", "System.Boolean");
        MethodDefinition noBuild = Method(location, "IsInsideNoBuildLocation", "System.Boolean", "UnityEngine.Vector3");
        Check(noBuild.IsPublic && noBuild.IsStatic, "Native no-build query must remain a public static read-only location query.");
        Check(UsesField(noBuild, "Location", "s_allLocations") && UsesField(noBuild, "Location", "m_noBuild"), "Native no-build query no longer evaluates loaded protected locations.");
        Instruction insideCall = CallInstructions(noBuild).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "Location" && ((MethodReference)i.Operand).Name == "IsInside");
        Check(insideCall.Previous != null && Int(insideCall.Previous, 1) && insideCall.Previous.Previous != null && Float(insideCall.Previous.Previous, 0f),
            "Native no-build query no longer respects the location's no-build radius override.");
        MethodDefinition insideLocation = Method(location, "IsInside", "System.Boolean", "UnityEngine.Vector3", "System.Single", "System.Boolean");
        Check(UsesField(insideLocation, "Location", "m_noBuildRadiusOverride") && Calls(insideLocation, "Location", "GetMaxRadius") &&
            Calls(insideLocation, "Utils", "DistanceXZ"), "Native no-build footprint radius or horizontal-distance semantics changed.");
        MethodDefinition locationRadius = Method(location, "GetMaxRadius", "System.Single");
        Check(UsesField(locationRadius, "Location", "m_hasInterior") && UsesField(locationRadius, "Location", "m_exteriorRadius") &&
            UsesField(locationRadius, "Location", "m_interiorRadius") && Calls(locationRadius, "UnityEngine.Mathf", "Max"),
            "Native location radius no longer covers the larger exterior/interior footprint.");
        TypeDefinition attack = Type(assembly, "Attack");
        MethodDefinition terrainHit = Method(attack, "SpawnOnHitTerrain", "UnityEngine.GameObject", "UnityEngine.Vector3", "UnityEngine.GameObject", "Character", "System.Single", "ItemDrop/ItemData", "ItemDrop/ItemData", "System.Boolean");
        Check(Calls(terrainHit, "Location", "IsInsideNoBuildLocation"), "Native terrain-tool placement no longer uses the location no-build query.");
        foreach (string name in new[] { "m_initialized" }) Field(compiler, name, "System.Boolean");
        foreach (string name in new[] { "m_width", "m_pitch", "m_operations", "m_lastHash" }) Field(compiler, name, "System.Int32");
        foreach (string name in new[] { "m_levelDelta", "m_smoothDelta" }) Field(compiler, name, "System.Single[]");
        foreach (string name in new[] { "m_modifiedHeight", "m_modifiedPaint" }) Field(compiler, name, "System.Boolean[]");
        Field(compiler, "m_paintMask", "UnityEngine.Color[]");
        Field(compiler, "m_lastOpPoint", "UnityEngine.Vector3");
        Field(compiler, "m_lastOpRadius", "System.Single");
        Field(compiler, "m_lastDataRevision", "System.UInt32");
        Field(compiler, "m_nview", "ZNetView");
        Field(compiler, "m_hmap", "Heightmap");
        Field(heightmap, "m_heights", "System.Collections.Generic.List`1<System.Single>");
        Field(heightmap, "m_width", "System.Int32");
        Field(heightmap, "m_scale", "System.Single");
        foreach (string name in new[] { "m_level", "m_square", "m_raise", "m_smooth", "m_paintCleared" }) Field(settings, name, "System.Boolean");
        foreach (string name in new[] { "m_levelRadius", "m_levelOffset" }) Field(settings, name, "System.Single");

        MethodDefinition internalOperation = Method(compiler, "InternalDoOperation", "System.Void", "UnityEngine.Vector3", "UnityEngine.Vector3", "TerrainOp/Settings");
        Check(!internalOperation.IsStatic, "Terrain operation must operate on an individual compiler.");
        Check(Calls(internalOperation, "TerrainComp", "LevelTerrain"), "The native per-vertex operation no longer performs leveling.");
        Check(!Calls(internalOperation, "TerrainComp", "Save"), "Native per-vertex operation now saves independently; batching needs review.");
        Check(!CallInstructions(internalOperation).Any(i => ((MethodReference)i.Operand).Name.StartsWith("RPC_", StringComparison.Ordinal)), "Native internal operation became a network RPC.");
        foreach (string name in new[] { "m_level", "m_levelRadius", "m_square", "m_operations", "m_lastOpPoint", "m_lastOpRadius" })
            Check(UsesField(internalOperation, name.StartsWith("m_level", StringComparison.Ordinal) || name == "m_square" ? "TerrainOp/Settings" : "TerrainComp", name), "Native operation no longer uses " + name + ".");

        MethodDefinition level = Method(compiler, "LevelTerrain", "System.Void", "UnityEngine.Vector3", "System.Single", "System.Boolean");
        Check(Calls(level, "Heightmap", "WorldToVertex"), "Native zero-radius leveling no longer selects a heightmap vertex.");
        Check(Calls(level, "UnityEngine.Mathf", "CeilToInt"), "Native radius-to-vertex range changed.");
        Check(UsesField(level, "Heightmap", "m_scale"), "Native radius is no longer interpreted using the heightmap scale.");
        Check(Calls(level, "Heightmap", "GetHeight"), "Native leveling no longer reads current vertex height.");
        Check(UsesField(level, "TerrainComp", "m_smoothDelta") && UsesField(level, "TerrainComp", "m_levelDelta") && UsesField(level, "TerrainComp", "m_modifiedHeight"), "Native level/smooth arrays changed.");
        Instruction[] instructions = level.Body.Instructions.ToArray();
        int clampIndex = Array.FindIndex(instructions, i => i.Operand is MethodReference && ((MethodReference)i.Operand).FullName == "System.Single UnityEngine.Mathf::Clamp(System.Single,System.Single,System.Single)");
        Check(clampIndex > 1 && Float(instructions[clampIndex - 2], -8f) && Float(instructions[clampIndex - 1], 8f), "Installed game's native level limit is no longer +/-8 metres.");
        Check(instructions.Any(i => i.OpCode.Code == Code.Ldarg_3 && i.Next != null && (i.Next.OpCode.Code == Code.Brtrue || i.Next.OpCode.Code == Code.Brtrue_S)), "Native square mode no longer bypasses the circular-radius filter.");
        Check(instructions.Count(i => i.OpCode.Code == Code.Ble || i.OpCode.Code == Code.Ble_S) >= 2, "Native zero-radius inclusive vertex loops changed.");

        MethodDefinition save = Method(compiler, "Save", "System.Void", "System.Boolean");
        Check(UsesField(save, "TerrainComp", "m_initialized"), "Native Save no longer checks initialization.");
        Check(Calls(save, "ZNetView", "IsValid") && Calls(save, "ZNetView", "IsOwner"), "Native Save ownership gates changed.");
        Check(UsesField(save, "ZDOVars", "s_TCData") && CallInstructions(save).Any(i => ((MethodReference)i.Operand).FullName == "System.Void ZDO::Set(System.Int32,System.Byte[])"), "Terrain Save no longer persists compiled data in the network ZDO.");
        Check(UsesField(save, "TerrainComp", "m_lastDataRevision") && Calls(save, "ZDO", "get_DataRevision"), "Native Save no longer records the revision needed by rollback.");
        Instruction[] saveInstructions = save.Body.Instructions.ToArray();
        Check(saveInstructions.Any(i => i.OpCode.Code == Code.And && i.Previous != null && i.Previous.OpCode.Code == Code.Ldarg_1 && i.Previous.Previous != null && i.Previous.Previous.OpCode.Code == Code.Ceq), "Save(false) no longer bypasses the unchanged paint-hash optimization.");
        foreach (string name in new[] { "m_levelDelta", "m_smoothDelta", "m_modifiedHeight", "m_modifiedPaint", "m_paintMask", "m_operations", "m_lastOpPoint", "m_lastOpRadius" })
            Check(UsesField(save, "TerrainComp", name), "Terrain Save snapshot payload changed: " + name);

        MethodDefinition poke = Method(heightmap, "Poke", "System.Void", "System.Int32", "System.Boolean");
        Check(Calls(poke, "Heightmap", "Regenerate"), "Heightmap.Poke no longer regenerates terrain.");
        Check(poke.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldarg_1 && i.Next != null && Int(i.Next, 0) && i.Next.Next != null && (i.Next.Next.OpCode.Code == Code.Ble || i.Next.Next.OpCode.Code == Code.Ble_S)), "Heightmap.Poke(0, false) no longer regenerates immediately.");
        Method(heightmap, "GetAndCreateTerrainCompiler", "TerrainComp");
        Method(heightmap, "FindHeightmap", "Heightmap", "UnityEngine.Vector3");
        Method(heightmap, "WorldToVertex", "System.Void", "UnityEngine.Vector3", "System.Int32&", "System.Int32&");
        Method(heightmap, "CalcVertex", "UnityEngine.Vector3", "System.Int32", "System.Int32");
        Method(compiler, "FindTerrainCompiler", "TerrainComp", "UnityEngine.Vector3");
        Method(view, "ClaimOwnership", "System.Void");
        Method(view, "IsOwner", "System.Boolean");
        Method(view, "GetZDO", "ZDO");
        TypeDefinition scene = Type(assembly, "ZNetScene");
        MethodDefinition ready = Method(scene, "IsAreaReady", "System.Boolean", "UnityEngine.Vector3");
        Check(Calls(ready, "ZoneSystem", "IsZoneLoaded"), "Native area readiness no longer checks that terrain is loaded.");
        Check(Calls(ready, "ZDOMan", "FindSectorObjects") && Calls(ready, "ZNetScene", "FindInstance"), "Native area readiness no longer detects network objects queued for instantiation.");
        Check(Calls(ready, "ZNetScene", "IsPrefabZDOValid"), "Native area readiness no longer classifies valid network objects.");
        Instruction[] distanceConstructors = CallInstructions(ready).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == "SimulationDistance" && ((MethodReference)i.Operand).Name == ".ctor").ToArray();
        Check(distanceConstructors.Length == 1 && distanceConstructors[0].Previous != null && Int(distanceConstructors[0].Previous, 0) &&
            distanceConstructors[0].Previous.Previous != null && Int(distanceConstructors[0].Previous.Previous, 0) &&
            distanceConstructors[0].Previous.Previous.Previous != null && Int(distanceConstructors[0].Previous.Previous.Previous, 1), "Native area readiness must cover adjacent sectors around the prison footprint.");
        Method(scene, "FindInstance", "ZNetView", "ZDO");
    }

    private static IEnumerable<MethodDefinition> ClosedGraph(AssemblyDefinition assembly, MethodDefinition start)
    {
        Dictionary<string, MethodDefinition> methods = AllTypes(assembly.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
        Queue<MethodDefinition> pending = new Queue<MethodDefinition>();
        HashSet<string> seen = new HashSet<string>();
        pending.Enqueue(start);
        while (pending.Count != 0) {
            MethodDefinition current = pending.Dequeue();
            if (!seen.Add(current.FullName) || !current.HasBody) continue;
            yield return current;
            foreach (Instruction instruction in CallInstructions(current)) {
                MethodDefinition target;
                if (methods.TryGetValue(((MethodReference)instruction.Operand).FullName, out target)) pending.Enqueue(target);
            }
        }
    }

    private static void Setting(MethodDefinition method, string name, bool boolean, float number, bool isNumber)
    {
        Instruction[] writes = method.Body.Instructions.Where(i => i.OpCode.Code == Code.Stfld && i.Operand is FieldReference &&
            ((FieldReference)i.Operand).DeclaringType.FullName == "TerrainOp/Settings" && ((FieldReference)i.Operand).Name == name).ToArray();
        Check(writes.Length == 1 && writes[0].Previous != null && (isNumber ? Float(writes[0].Previous, number) : Int(writes[0].Previous, boolean ? 1 : 0)), "Unsafe native terrain-operation setting: " + name);
    }

    private static void Mutation(AssemblyDefinition assembly)
    {
        string rootName = "ValheimModPack.PartyPrison.TerrainLeveler";
        TypeDefinition leveler = Type(assembly, rootName);
        TypeDefinition site = Type(assembly, rootName + "/Site");
        TypeDefinition transaction = Type(assembly, rootName + "/Transaction");
        TypeDefinition snapshot = Type(assembly, rootName + "/Snapshot");
        TypeDefinition tile = Type(assembly, rootName + "/NativeTile");
        MethodDefinition plan = Method(leveler, "Plan", rootName + "/Site", "UnityEngine.Vector3", "UnityEngine.Vector3");
        MethodDefinition buildable = Method(leveler, "RequireBuildable", "System.Void", "UnityEngine.Vector3");
        Instruction noBuildQuery = CallInstructions(buildable).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "Location" && ((MethodReference)i.Operand).Name == "IsInsideNoBuildLocation");
        Check(noBuildQuery.Next != null && (noBuildQuery.Next.OpCode.Code == Code.Brfalse || noBuildQuery.Next.OpCode.Code == Code.Brfalse_S) &&
            buildable.Body.Instructions.Any(i => i.OpCode.Code == Code.Throw && i.Offset > noBuildQuery.Offset),
            "Native protected-location queries must reject the candidate instead of being ignored.");
        Instruction vertexNoBuild = CallInstructions(plan).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == rootName && ((MethodReference)i.Operand).Name == "RequireBuildable");
        Instruction vertexRecord = CallInstructions(plan).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == rootName + "/Vertex" && ((MethodReference)i.Operand).Name == ".ctor");
        Check(vertexNoBuild.Offset < vertexRecord.Offset, "Every selected terrain vertex must pass the actual no-build query before the candidate is prepared.");
        MethodDefinition coverage = Method(leveler, "RequireCoverage", "System.Void", "UnityEngine.Vector3", "System.Single", "System.Collections.Generic.List`1<" + rootName + "/NativeTile>");
        Check(Calls(coverage, rootName, "RequireBuildable"), "The padded square must respect no-build locations between heightmap vertices.");
        Instruction[] readiness = CallInstructions(plan).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == "ZNetScene" && ((MethodReference)i.Operand).Name == "IsAreaReady").ToArray();
        Instruction[] footprint = CallInstructions(plan).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == "ValheimModPack.PartyPrison.TerrainPlan" && ((MethodReference)i.Operand).Name == "RequireFootprint").ToArray();
        Instruction[] findMaps = CallInstructions(plan).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == "Heightmap" && ((MethodReference)i.Operand).Name == "FindHeightmap").ToArray();
        Check(readiness.Length == 1 && footprint.Length == 1 && findMaps.Length == 1 && footprint[0].Offset < readiness[0].Offset && readiness[0].Offset < findMaps[0].Offset, "Terrain planning must check area readiness after altar protection and before inspecting heightmaps.");
        Check(readiness[0].Next != null && (readiness[0].Next.OpCode.Code == Code.Brtrue || readiness[0].Next.OpCode.Code == Code.Brtrue_S) &&
            plan.Body.Instructions.Any(i => i.OpCode.Code == Code.Throw && i.Offset > readiness[0].Offset && i.Offset < findMaps[0].Offset), "Terrain planning ignores a false native area-readiness result.");
        List<MethodDefinition> inspection = ClosedGraph(assembly, plan).ToList();
        List<MethodReference> inspectionCalls = inspection.SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(inspectionCalls.Any(m => m.DeclaringType.FullName == "TerrainComp" && m.Name == "FindTerrainCompiler"), "Terrain planning no longer inspects the existing compiler.");
        Check(!inspectionCalls.Any(m => m.Name == "GetAndCreateTerrainCompiler" || m.Name == "ClaimOwnership" || m.Name == "SetOwner" || m.Name == "InternalDoOperation" || m.Name == "Save" || m.Name == "Persist" || m.Name == "Poke"), "Candidate inspection must not create, claim, modify, or save terrain.");
        Check(!inspectionCalls.Any(m => m.DeclaringType.FullName == "System.Reflection.MethodBase" && m.Name == "Invoke"), "Read-only planning invokes a reflective mutation.");
        List<MethodReference> terrainCalls = AllTypes(new[] { leveler }).SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(CallInstructions).Select(i => (MethodReference)i.Operand).ToList();
        Check(!terrainCalls.Any(m => m.Name == "ApplyOperation" || m.Name == "DoOperation" || m.Name.StartsWith("RPC_", StringComparison.Ordinal) || m.DeclaringType.FullName == "ZRoutedRpc" || m.DeclaringType.FullName == "ZRpc"), "Terrain construction must batch native compiled data rather than send an operation RPC per vertex.");
        Check(!terrainCalls.Any(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name.StartsWith("Find", StringComparison.Ordinal)), "Terrain handling must not scan all scene objects.");

        MethodDefinition applySite = Method(site, "Apply", rootName + "/Transaction");
        Instruction unchanged = CallInstructions(applySite).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == tile.FullName && ((MethodReference)i.Operand).Name == "CheckUnchanged");
        Instruction applyTransaction = CallInstructions(applySite).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == transaction.FullName && ((MethodReference)i.Operand).Name == "Apply");
        Check(unchanged.Offset < applyTransaction.Offset, "A stale terrain plan is modified before it is revalidated.");
        Check(Calls(applySite, transaction.FullName, "Dispose"), "A partially applied terrain transaction is not rolled back.");
        MethodDefinition unchangedMethod = Method(tile, "CheckUnchanged", "System.Void");
        Check(Calls(unchangedMethod, "Heightmap", "HaveQueuedRebuild") && Calls(unchangedMethod, "ZDO", "get_DataRevision") && Calls(unchangedMethod, "Heightmap", "GetHeight"), "Stale-plan protection must check pending geometry, network revision, and sampled heights.");
        Check(Calls(unchangedMethod, rootName, "RequireBuildable"), "A prepared terrain plan must recheck actual no-build locations before terrain mutation.");

        MethodDefinition apply = Method(transaction, "Apply", "System.Void");
        Setting(apply, "m_level", true, 0f, false);
        Setting(apply, "m_square", true, 0f, false);
        Setting(apply, "m_levelRadius", false, 0f, true);
        Setting(apply, "m_levelOffset", false, 0f, true);
        foreach (string name in new[] { "m_raise", "m_smooth", "m_paintCleared" }) Setting(apply, name, false, 0f, false);
        Check(UsesField(apply, leveler.FullName, "Operation"), "Native leveling operation is not used.");
        Check(Calls(apply, "Heightmap", "GetAndCreateTerrainCompiler"), "Terrain mutation does not acquire native terrain compilers.");
        Instruction snapshotConstruction = CallInstructions(apply).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == snapshot.FullName && ((MethodReference)i.Operand).Name == ".ctor");
        Instruction firstOperation = CallInstructions(apply).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "System.Reflection.MethodBase" && ((MethodReference)i.Operand).Name == "Invoke");
        Instruction persist = CallInstructions(apply).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == snapshot.FullName && ((MethodReference)i.Operand).Name == "Persist");
        Check(snapshotConstruction.Offset < firstOperation.Offset && firstOperation.Offset < persist.Offset, "Snapshots and compiler persistence are not outside the per-vertex mutation phase.");
        Check(Calls(apply, "Heightmap", "GetHeight"), "Native clamp/rebuild verification is missing after applying terrain.");
        foreach (MethodDefinition method in new[] { apply, Method(transaction, "Dispose", "System.Void") }) {
            Instruction[] pokes = CallInstructions(method).Where(i => ((MethodReference)i.Operand).DeclaringType.FullName == "Heightmap" && ((MethodReference)i.Operand).Name == "Poke").ToArray();
            Check(pokes.Length == 1 && pokes[0].Previous != null && Int(pokes[0].Previous, 0) && pokes[0].Previous.Previous != null && Int(pokes[0].Previous.Previous, 0), "Apply and rollback must regenerate terrain immediately with Poke(0, false).");
        }

        MethodDefinition snapshotConstructor = Method(snapshot, ".ctor", "System.Void", "TerrainComp", "Heightmap");
        MethodDefinition restore = Method(snapshot, "Restore", "System.Void");
        foreach (MethodDefinition method in new[] { snapshotConstructor, restore }) {
            Check(CallInstructions(method).Count(i => ((MethodReference)i.Operand).DeclaringType.FullName == "System.Array" && ((MethodReference)i.Operand).Name == "Clone") == 5, "Rollback requires independent copies of all five terrain arrays: " + method.Name);
            foreach (string name in new[] { "Level", "Smooth", "ModifiedHeight", "ModifiedPaint", "Paint", "Operations", "LastPoint", "LastRadius", "LastHash" })
                Check(UsesField(method, leveler.FullName, name), "Rollback snapshot misses terrain state: " + name + " in " + method.Name);
        }
        MethodDefinition claim = Method(snapshot, "Claim", "System.Void");
        Check(Calls(claim, "ZNetView", "ClaimOwnership") && Calls(claim, "ZNetView", "IsOwner") && Calls(claim, "TerrainComp", "IsOwner"), "Terrain persistence must verify host ownership.");
        MethodDefinition persistMethod = Method(snapshot, "Persist", "System.Void");
        Check(UsesField(persistMethod, leveler.FullName, "Save") && Calls(persistMethod, snapshot.FullName, "Claim") && Calls(persistMethod, "ZDO", "GetByteArray"), "Native compiled terrain persistence or readback is missing.");
        Check(persistMethod.Body.Instructions.Any(i => Int(i, 0) && i.Next != null && i.Next.OpCode.Code == Code.Box && ((TypeReference)i.Next.Operand).FullName == "System.Boolean"), "Save must be invoked with false so unchanged paint hashes cannot skip height persistence.");
        Instruction restorePersist = CallInstructions(restore).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == snapshot.FullName && ((MethodReference)i.Operand).Name == "Persist");
        Instruction restoreOwner = CallInstructions(restore).Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "ZDO" && ((MethodReference)i.Operand).Name == "SetOwner");
        Check(restorePersist.Offset < restoreOwner.Offset, "Rollback gives up ownership before the original terrain is persisted.");
        Check(Calls(Method(transaction, "Dispose", "System.Void"), snapshot.FullName, "Restore"), "Disposing an uncommitted transaction no longer restores its snapshots.");
    }

    private static void BoolRoute(MethodDefinition method, string name, bool argument)
    {
        Instruction[] calls = CallInstructions(method).Where(i => ((MethodReference)i.Operand).Name == name).ToArray();
        Check(calls.Length == 1, "Manual/automatic prison route is missing or ambiguous: " + method.Name);
        Check(calls[0].Previous != null && Int(calls[0].Previous, argument ? 1 : 0), "Terrain preparation policy leaked between manual and automatic builds: " + method.Name);
    }

    private static void Plugin(AssemblyDefinition assembly)
    {
        TypeDefinition plugin = Type(assembly, "ValheimModPack.PartyPrison.Plugin");
        TypeDefinition builder = Type(assembly, "ValheimModPack.PartyPrison.ArenaBuilder");
        Type(assembly, "ValheimModPack.PartyPrison.TerrainLeveler");
        Type(assembly, "ValheimModPack.PartyPrison.TerrainLeveler/Site");
        Type(assembly, "ValheimModPack.PartyPrison.TerrainLeveler/Transaction");
        BoolRoute(Method(plugin, "BuildPrison", "System.Void"), "BuildPrisonCore", true);
        BoolRoute(Method(plugin, "AutoBuildNearAltars", "System.Void"), "BuildPrisonCore", false);
        MethodDefinition command = Method(plugin, "Command", "System.Void", "Terminal/ConsoleEventArgs");
        Check(Calls(command, plugin.FullName, "BuildPrison"), "The build console command bypasses the manual terrain route.");
        MethodDefinition defaultBuilder = Method(builder, "BuildNearAltars", "ValheimModPack.PartyPrison.PrisonRegion");
        Instruction defaultCall = CallInstructions(defaultBuilder).Single(i => ((MethodReference)i.Operand).Name == "BuildNearAltars");
        Check(defaultCall.Previous != null && defaultCall.Previous.OpCode.Code == Code.Ldnull && defaultCall.Previous.Previous != null && Int(defaultCall.Previous.Previous, 0), "Compatibility/default generation must keep terrain unchanged.");
        MethodDefinition build = Method(builder, "BuildNearAltars", "ValheimModPack.PartyPrison.PrisonRegion", "System.Boolean", "System.Action`1<ValheimModPack.PartyPrison.PrisonRegion>", "System.Action`1<System.String>");
        List<Instruction> buildCalls = CallInstructions(build).ToList();
        Instruction apply = buildCalls.Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "ValheimModPack.PartyPrison.TerrainLeveler/Site" && ((MethodReference)i.Operand).Name == "Apply");
        Instruction construct = buildCalls.Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == builder.FullName && ((MethodReference)i.Operand).Name == "Build");
        Instruction durableWrite = buildCalls.Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "System.Action`1<ValheimModPack.PartyPrison.PrisonRegion>" && ((MethodReference)i.Operand).Name == "Invoke");
        Instruction commit = buildCalls.Single(i => ((MethodReference)i.Operand).DeclaringType.FullName == "ValheimModPack.PartyPrison.TerrainLeveler/Transaction" && ((MethodReference)i.Operand).Name == "Commit");
        Check(apply.Offset < construct.Offset && construct.Offset < durableWrite.Offset && durableWrite.Offset < commit.Offset, "Terrain commit must follow successful building and the durable prison-state write.");
        Check(build.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Finally), "Terrain transaction is not disposed when construction fails.");
        Check(Calls(build, builder.FullName, "TryRollback"), "Failed prison-state persistence must remove the newly constructed structure.");
    }

    public static int Main(string[] args)
    {
        try {
            if (args.Length != 2) throw new ArgumentException("Expected installed assembly_valheim.dll and PartyPrison.dll paths.");
            using (AssemblyDefinition native = AssemblyDefinition.ReadAssembly(args[0]))
            using (AssemblyDefinition plugin = AssemblyDefinition.ReadAssembly(args[1])) {
                Native(native); Plugin(plugin); Mutation(plugin);
            }
            Console.WriteLine("PASS: " + checks + " native terrain and prison construction contracts. No game process launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL at contract " + checks + ": " + error.Message); return 1; }
    }
}
