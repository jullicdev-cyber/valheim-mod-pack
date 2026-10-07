[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/PartyPrison.dll' }
[Reflection.Assembly]::LoadFrom((Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')) | Out-Null
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginAssembly)
$native = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
$script:checks = 0
function Check([bool]$condition, [string]$message) {
    $script:checks++
    if (-not $condition) { throw $message }
}
function All-Types($types) {
    foreach ($type in $types) {
        $type
        if ($type.HasNestedTypes) { All-Types $type.NestedTypes }
    }
}
function Method([string]$type, [string]$name, [int]$parameters = -1) {
    $found = @($types | Where-Object FullName -eq $type | ForEach-Object Methods | Where-Object {
        $_.Name -eq $name -and ($parameters -lt 0 -or $_.Parameters.Count -eq $parameters)
    })
    Check ($found.Count -eq 1) ('Missing or ambiguous performance contract method: ' + $type + '.' + $name)
    return $found[0]
}
function Graph($entry) {
    $pending = New-Object 'System.Collections.Generic.Queue[object]'
    $visited = New-Object 'System.Collections.Generic.HashSet[string]'
    $result = New-Object 'System.Collections.Generic.List[object]'
    $pending.Enqueue($entry)
    while ($pending.Count) {
        $current = $pending.Dequeue()
        if (-not $visited.Add($current.FullName) -or -not $current.HasBody) { continue }
        $result.Add($current)
        foreach ($instruction in $current.Body.Instructions) {
            if ($instruction.OpCode.Name -notin @('call','callvirt','newobj','ldftn','ldvirtftn')) { continue }
            $target = $instruction.Operand
            if ($methods.ContainsKey($target.FullName)) { $pending.Enqueue($methods[$target.FullName]); continue }
            # Closed generic references have a different FullName from their
            # production definition. Include them without resolving game DLLs.
            if ($target -is [Mono.Cecil.GenericInstanceMethod]) { $target = $target.ElementMethod }
            $owner = $target.DeclaringType
            if ($owner -is [Mono.Cecil.GenericInstanceType]) { $owner = $owner.ElementType }
            $definition = @($types | Where-Object FullName -eq $owner.FullName | ForEach-Object Methods | Where-Object {
                $_.Name -eq $target.Name -and $_.Parameters.Count -eq $target.Parameters.Count -and
                $_.GenericParameters.Count -eq $target.GenericParameters.Count
            })
            if ($definition.Count -eq 1) { $pending.Enqueue($definition[0]) }
        }
    }
    return $result.ToArray()
}
function Calls($method, [string]$pattern) {
    return @($method.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @('call','callvirt','newobj') -and $_.Operand.FullName -match $pattern
    })
}
function No-WorldScan($entry) {
    foreach ($method in @(Graph $entry)) {
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.Operand -isnot [Mono.Cecil.MemberReference]) { continue }
            Check ($instruction.Operand.FullName -notmatch 'UnityEngine\.Object::Find|Resources::FindObjects|ZDOMan::m_objectsByID|ArenaBuilder::TaggedWorldObjects') (
                'Recurring prison activity must not scan every scene or world object: ' + $method.FullName + ' -> ' + $instruction.Operand.FullName)
        }
    }
}
function Clock-Gate($method, [string]$field, [single]$interval, [string]$work) {
    $instructions = @($method.Body.Instructions)
    $workCalls = @(Calls $method $work)
    Check ($workCalls.Count -gt 0) ('The throttled activity disappeared: ' + $method.FullName)
    $firstWork = $workCalls[0].Offset
    Check ([bool]($instructions | Where-Object {
        $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq $field -and $_.Offset -lt $firstWork
    })) ('The cooldown must be consulted before work: ' + $method.Name + '.' + $field)
    Check ([bool]($instructions | Where-Object {
        $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $field -and $_.Offset -lt $firstWork
    })) ('The cooldown must be advanced before work: ' + $method.Name + '.' + $field)
    Check ([bool]($instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq $interval })) (
        'The activity budget changed: ' + $method.Name + ' should wait ' + $interval + ' seconds.')
    Check ([bool](Calls $method 'UnityEngine\.Time::get_realtimeSinceStartup')) ('Activity cooldown must use the monotonic game clock: ' + $method.Name)
    Check ([bool]($instructions | Where-Object {
        $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and $_.Offset -lt $firstWork -and
        $_.Operand -is [Mono.Cecil.Cil.Instruction] -and $_.Operand.Offset -gt $firstWork
    })) ('The cooldown needs a path which skips work: ' + $method.Name)
}
try {
    Check ($plugin.Name.Name -eq 'PartyPrison') 'The production CLR assembly identity must remain PartyPrison for updates and dependent probes.'
    $types = @(All-Types $plugin.MainModule.Types)
    $methods = @{}
    foreach ($type in $types) { foreach ($method in $type.Methods) { $methods[$method.FullName] = $method } }
    $entry = @($types | Where-Object FullName -eq 'ValheimModPack.PartyPrison.PrisonGuard' | ForEach-Object Methods | Where-Object Name -eq 'InventoryAccess')
    Check ($entry.Count -eq 1) 'The ordinary inventory access guard is missing or ambiguous.'
    $queue = New-Object 'System.Collections.Generic.Queue[object]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $queue.Enqueue($entry[0])
    while ($queue.Count) {
        $method = $queue.Dequeue()
        if (-not $seen.Add($method.FullName) -or -not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.OpCode.Name -notin @('call','callvirt','newobj')) { continue }
            $target = $instruction.Operand
            # Walk our complete access path. This catches the original regression
            # even if a scene search is hidden in a helper instead of the guard.
            Check ($target.FullName -notmatch 'UnityEngine\.Object::Find|Resources::FindObjects|::GetEnumerator\(|System\.Linq\.Enumerable::To(Array|List)') ('Inventory reads must not scan scenes or enumerate container registries: ' + $target.FullName)
            if ($methods.ContainsKey($target.FullName)) { $queue.Enqueue($methods[$target.FullName]) }
        }
    }
    Check ($seen.Count -ge 2) 'The inventory guard must exercise the custody lookup in this contract.'
    $lookup = @($types | Where-Object FullName -eq 'ValheimModPack.PartyPrison.CustodyInventory' | ForEach-Object Methods | Where-Object Name -eq 'ChestForInventory')
    Check ($lookup.Count -eq 1) 'Custody inventory identity lookup is missing.'
    Check ([bool]($lookup[0].Body.Instructions | Where-Object { $_.Operand -eq 'VMP_PP_Custody' })) 'Custody lookup must consult the current marker, including chests tagged after Awake.'
    Check ([bool]($lookup[0].Body.Instructions | Where-Object { $_.Operand.FullName -match 'ZDO::GetBool' })) 'Custody classification must remain dynamic.'
    Check ([bool]($lookup[0].Body.Instructions | Where-Object { $_.Operand -eq 'VMP_PP_PublicChest' })) 'Public prison chests must bypass the temporary legacy custody mask.'
    $container = $native.MainModule.Types | Where-Object Name -eq 'Container'
    $assigners = @($container.Methods | Where-Object { $_.HasBody -and [bool]($_.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.FullName -eq 'Inventory Container::m_inventory' }) })
    Check ($assigners.Count -eq 1 -and $assigners[0].Name -eq 'Awake') 'Native container inventory lifecycle changed; registration needs review.'
    foreach ($name in @('Awake','Load','AddDefaultItems','GetInventory')) {
        Check ([bool]($container.Methods | Where-Object Name -eq $name)) ('Installed native lifecycle target is missing: Container.' + $name)
        $patch = @($types | Where-Object {
            [bool]($_.CustomAttributes | Where-Object {
                $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and
                [bool]($_.ConstructorArguments | Where-Object { $_.Value.FullName -eq 'Container' }) -and
                [bool]($_.ConstructorArguments | Where-Object { $_.Value -eq $name })
            })
        })
        Check ($patch.Count -gt 0) ('The registry lifecycle has no Harmony hook: Container.' + $name)
    }
    $arenaType = 'ValheimModPack.PartyPrison.ArenaBuilder'
    $pluginType = 'ValheimModPack.PartyPrison.Plugin'
    $count = Method $arenaType 'LiveMobCount' 1
    $confine = Method $arenaType 'EnforceOwnedMobs' 1
    No-WorldScan $count
    No-WorldScan $confine
    Check ([bool](Calls $count 'ArenaBuilder::PendingMobCount')) 'Pending spawns must count as an active wave, preventing duplicate automatic waves.'
    Check ([bool](@(Graph $count) | ForEach-Object { Calls $_ 'ZDOMan::FindSectorObjects' })) 'Mob counting must use bounded native sectors.'
    Check ([bool](Calls $confine 'ArenaBuilder::Contains(Confinement|Room)')) 'Owned mobs must be allowed in the cell as well as the arena.'
    Check (-not [bool](Calls $confine 'ArenaBuilder::InsideArena')) 'The cell doorway must not teleport mobs back into the arena.'
    Check ([bool]($confine.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq 0.5 })) 'Mob escape checks must be throttled to at most twice per second.'
    $cleanup = Method $arenaType 'CleanupMobs' 2
    Check ([bool](Calls $cleanup 'ArenaBuilder::Contains(Confinement|Room)')) 'Inactive cleanup must preserve living mobs inside the cell.'

    $wave = Method $arenaType 'SpawnWave' 4
    foreach ($method in @(Graph $wave)) {
        Check (-not [bool](Calls $method 'UnityEngine\.Object::Instantiate')) 'Accepting a wave must queue mobs without instantiating the whole wave in one frame.'
    }
    $mixedWave = Method $arenaType 'SpawnWave' 5
    Check ([bool](Calls $wave 'ArenaBuilder::SpawnWave\(')) 'The ordinary wave overload must share the mixed scheduler rather than a separate synchronous spawn path.'
    Check ([bool]($mixedWave.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'newarr' -and $_.Operand.FullName -eq 'UnityEngine.GameObject'
    }) -and [bool]($mixedWave.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'Prefabs'
    })) 'Mixed waves must resolve each mob into their own detached prefab queue before acceptance.'
    Check ([bool](Calls $mixedWave 'ArenaWaveProgressionPolicy::Start') -and [bool](Calls $mixedWave 'ArenaBuilder::WriteWaveProgressZdo')) (
        'An accepted sentence wave must publish its durable serial and expected count before native mob creation.')
    foreach ($method in @(Graph $mixedWave)) {
        Check (-not [bool](Calls $method 'UnityEngine\.Object::Instantiate')) 'Mixed wave acceptance must not instantiate any native mob in its validation frame.'
    }
    $spawn = Method $arenaType 'TickWaveSpawning' 1
    $instantiation = @(Calls $spawn 'UnityEngine\.Object::Instantiate')
    Check ($instantiation.Count -eq 1) 'The wave scheduler must have exactly one native mob creation site.'
    Check ([bool]($spawn.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq [single]0.6 })) 'Mob creation must be staggered by at least 0.6 seconds.'
    Check ([bool](Calls $spawn 'UnityEngine\.Time::get_realtimeSinceStartup')) 'The wave scheduler needs a monotonic clock gate.'
    foreach ($field in @('Prefabs','Spawned')) {
        Check ([bool]($spawn.Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq $field
        })) ('The scheduler must choose the individual current mob before placement: ' + $field)
    }
    Check (-not [bool]($spawn.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'Prefab'
    })) 'A mixed wave must not reuse one singular mob prefab for all spawn slots.'
    Check (@($spawn.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'Hatchling' }).Count -eq 2) (
        'The current prefab must separately govern its flying collision check and two-metre spawn height.')
    Check ([bool](Calls $spawn 'ArenaBuilder::RecordWaveSpawned') -and [bool](Calls $spawn 'ArenaBuilder::AbortSpawnWave')) (
        'Successful spawns and cancelled/failed spawn queues must update the same durable wave identity.')
    if ($instantiation.Count -eq 1) {
        Check (-not [bool]($spawn.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.Cil.Instruction] -and $_.Operand.Offset -le $instantiation[0].Offset -and
            $_.Offset -ge $instantiation[0].Offset
        })) 'Native mob creation must not occur in an in-frame loop.'
    }
    No-WorldScan $spawn
    $position = Method $arenaType 'TryWavePosition' 4
    $randomChoice = Method 'ValheimModPack.PartyPrison.ArenaSpawnGeometry' 'TryChoose' 4
    Check ([bool](Calls $position 'ArenaSpawnGeometry::TryChoose') -and
        [bool](@(Graph $position) | ForEach-Object { Calls $_ 'UnityEngine\.Random::get_value' }) -and
        [bool](@(Graph $position) | ForEach-Object { Calls $_ 'UnityEngine\.Physics::CheckCapsule' })) (
        'Production random placement must use the bounded sampler and actual native physical clearance.')
    Check ([bool]($randomChoice.Body.Instructions | Where-Object {
        $_.OpCode.Name -in @('ldc.i4','ldc.i4.s') -and [int]$_.Operand -eq 12
    })) 'A blocked random spawn must remain bounded to twelve candidate checks per scheduler attempt.'
    foreach ($method in @(Graph $position)) {
        Check (-not [bool](Calls $method 'ArenaGeometry::Spawn|HashSet`1.*::.ctor')) (
            'Random spawn selection must not fall back to fixed corner spokes or reserve unique positions.')
    }
    No-WorldScan $position
    $auto = Method $pluginType 'AutoWaves' 0
    $live = @(Calls $auto 'ArenaBuilder::LiveMobCount')
    $inside = @(Calls $auto 'ArenaBuilder::IsInsideArena')
    $ready = @(Calls $auto 'Plugin::HostFightReady')
    Check ($live.Count -eq 1 -and $inside.Count -gt 0 -and $ready.Count -gt 0 -and
        $inside[0].Offset -lt $live[0].Offset -and $ready[0].Offset -lt $live[0].Offset) 'Empty or unoccupied arenas must skip the mob-count query.'
    $completed = @(Calls $auto 'ArenaBuilder::CompleteWaveIfDefeated')
    Check ($completed.Count -eq 1 -and $inside[0].Offset -lt $completed[0].Offset -and $ready[0].Offset -lt $completed[0].Offset) (
        'Only a ready inmate inside the arena may trigger completion queries or automatic difficulty progress.')
    $completion = Method $arenaType 'CompleteWaveIfDefeated' 3
    Check ([bool](Calls $completion 'ArenaBuilder::HasPendingWave') -and [bool](Calls $completion 'ArenaBuilder::IsLiveCurrentWaveMob')) (
        'Wave credit must consider its own outstanding spawn queue and matching live mobs.')
    Check ([bool](@(Graph $completion) | ForEach-Object { Calls $_ 'ZDOMan::FindSectorObjects' })) (
        'Wave completion must count only bounded native region sectors.')
    No-WorldScan $completion
    $waveWriter = Method $arenaType 'WriteWaveProgressZdo' 2
    Check ([bool](Calls $waveWriter 'ZDOMan::ForceSendZDO')) 'Wave progress must replicate its small native ZDO rather than checkpoint the whole world.'
    foreach ($method in @(Graph $auto)) {
        Check (-not [bool](Calls $method 'ZNet::Save|Game::Save|WorldCharacters\.Plugin::RequestAdministrativeSave')) (
            'Automatic waves and difficulty progress must not block the server on character or world checkpoints.')
        Check (-not [bool](Calls $method 'ArenaBuilder::ConfigureSentenceKit|ArenaBuilder::ClearSentenceKit|ArenaBuilder::RemoveObsolete.*|Plugin::ApplyCombatChoice')) (
            'Automatic difficulty upgrades must keep the existing kit epoch and held loan equipment intact.')
    }
    foreach ($key in @('VMP_PP_KitRevision','VMP_PP_KitFamily','VMP_PP_KitDifficulty','VMP_PP_Gear')) {
        Check (-not [bool]($waveWriter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $key })) (
            'Wave publication must not overwrite equipment identity or expiry provenance: ' + $key)
    }
    $alliance = Method $arenaType 'AreAlliedPrisonMobs' 2
    No-WorldScan $alliance
    Check ([bool](Calls $alliance 'ZNetView::IsValid') -and @(Calls $alliance 'ZDO::GetBool').Count -eq 2) (
        'Enemy alliance must inspect both valid native views and both prison markers before accepting a mob pair.')
    Check (-not [bool](Calls $alliance 'ArenaBuilder::Tagged|ArenaBuilder::GetKit|ArenaBuilder::GetWave|Inventory::|Character::SetFaction')) (
        'Every AI enemy test must remain a local marker lookup without region queries, inventory reads or global faction mutation.')
    $alliancePrefix = Method 'ValheimModPack.PartyPrison.PrisonEnemyAlliancePatch' 'Prefix' 3
    $alliedGuard = @(Calls $alliancePrefix 'ArenaBuilder::AreAlliedPrisonMobs')
    $overrideEnemy = @($alliancePrefix.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stind.i1' })
    Check ($alliedGuard.Count -eq 1 -and $overrideEnemy.Count -eq 1 -and $alliedGuard[0].Offset -lt $overrideEnemy[0].Offset -and
        [bool]($alliancePrefix.Body.Instructions | Where-Object {
            $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and $_.Offset -gt $alliedGuard[0].Offset -and
            $_.Offset -lt $overrideEnemy[0].Offset
        })) 'The IsEnemy override must stay behind the exact prison mob-pair guard.'
    $nativeEnemy = @($native.MainModule.Types | Where-Object Name -eq 'BaseAI' | ForEach-Object Methods | Where-Object {
        $_.Name -eq 'IsEnemy' -and $_.Parameters.Count -eq 2 -and $_.Parameters[0].ParameterType.FullName -eq 'Character' -and
        $_.Parameters[1].ParameterType.FullName -eq 'Character'
    })
    Check ($nativeEnemy.Count -eq 1 -and $nativeEnemy[0].IsStatic -and $nativeEnemy[0].ReturnType.FullName -eq 'System.Boolean') (
        'The native mixed-enemy alliance hook must retain the exact static Character-pair bool ABI.')
    Clock-Gate (Method $pluginType 'Enforce' 1) 'nextLoanMaintenance' 0.5 'Inventory::GetAllItems'
    # Immediate release cleanup is allowed; the ordinary idle-player fallback is gated in Update.
    Clock-Gate (Method $pluginType 'Update' 0) 'nextLoanRemoval' 1 'Plugin::RemoveLoans'
    Clock-Gate (Method $pluginType 'FinishRelease' 0) 'nextReleaseAck' 2 'Plugin::ToHost'

    $combatWriter = Method $pluginType 'WriteCombatState' 1
    foreach ($method in @(Graph $combatWriter)) {
        Check (-not [bool](Calls $method 'Plugin::RefreshHostCombatState|ArenaBuilder::Get(CombatChoice|KitZdo)|ArenaBuilder::Tagged|ZDOMan::FindSectorObjects')) (
            'Per-peer state serialization must use the cached combat snapshot without a region query: ' + $method.FullName)
    }
    $hostTick = Method $pluginType 'HostTick' 0
    $refresh = @(Calls $hostTick 'Plugin::RefreshHostCombatState')
    $custodyTick = @(Calls $hostTick 'Plugin::HostCustodyTick')
    $peerState = @(Calls $hostTick 'Plugin::Send')
    Check ($refresh.Count -eq 1 -and $custodyTick.Count -eq 1 -and $peerState.Count -gt 0 -and
        $custodyTick[0].Offset -lt $refresh[0].Offset -and $refresh[0].Offset -lt $peerState[0].Offset) (
        'The host must refresh combat once after custody changes and before all peer snapshots.')
    Check (@(Calls $hostTick 'ZNet::Save').Count -eq 0) (
        'Routine post-release kit cleanup must not block the host with a whole-world save.')
    $publicRelease = Method $pluginType 'CompleteHostRelease' 4
    Check (@(Calls $publicRelease 'Plugin::ReopenEmergencyChests|ZNet::Save').Count -eq 0) (
        'An already-public custody release must not use the emergency world-checkpoint path.')
    $publicReopen = @(Calls $publicRelease 'Plugin::ReopenPublicChests')
    $custodyCompletion = @(Calls $publicRelease 'CustodyStore::MarkCollected')
    Check ($publicReopen.Count -eq 1 -and $custodyCompletion.Count -eq 1 -and
        $custodyCompletion[0].Offset -lt $publicReopen[0].Offset) (
        'The ordinary release must finish its custody journal before refreshing public chest access.')
    Check (@(Calls $publicRelease 'Plugin::CompleteEmergencyCustodyRelease').Count -eq 2) (
        'Emergency sentences and private/recovery custody must retain the separate durable release path.')
    $reopenPublic = Method $pluginType 'ReopenPublicChests' 0
    Check (@(Calls $reopenPublic 'CustodyInventory::SetPublic').Count -eq 1) (
        'Ordinary release must still restore the native public chest access flags.')
    foreach ($method in @(Graph $reopenPublic)) {
        Check (@(Calls $method 'ZNet::Save').Count -eq 0) (
            'Ordinary public chest access must not hide a whole-world checkpoint in a helper: ' + $method.FullName)
    }
    foreach ($field in @('emergencyChestsPending','nextEmergencyChests')) {
        Check ([bool]($reopenPublic.Body.Instructions | Where-Object {
            $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $field
        })) ('A temporarily unavailable public chest must preserve its deferred reopen recovery: ' + $field)
    }
    Check (@(Calls (Method $pluginType 'ReopenEmergencyChests' 0) 'ZNet::Save').Count -eq 1) (
        'Private/emergency chest recovery must preserve its world durability checkpoint.')
    Check (@(Calls (Method $pluginType 'HostCustodyTick' 0) 'ZNet::Save').Count -ge 3) (
        'Admission and private-to-public custody handoff must preserve their world durability barriers.')
    $clearKit = Method $arenaType 'ClearSentenceKit' 1
    Check ([bool](Calls $clearKit 'PrisonKitStorage::Publish') -and [bool](Calls $clearKit 'ZDOMan::ForceSendZDO')) (
        'Released equipment stock must still publish native chest contents and sync the cleared kit identity.')
    foreach ($method in @(Graph $clearKit)) {
        Check (@(Calls $method 'ZNet::Save').Count -eq 0) (
            'Released equipment cleanup must not hide a whole-world checkpoint in a helper: ' + $method.FullName)
    }
    $maintenance = Method $pluginType 'MaintainCombatGear' 0
    Clock-Gate $maintenance 'nextGearMaintenance' 0.5 'ArenaBuilder::RemoveObsoleteGearWhenSettled'
    Clock-Gate $maintenance 'nextStoredGearMaintenance' 1 'ArenaBuilder::ExpireStoredPrisonGear'
    Check (@(Calls $maintenance 'Plugin::RefreshHostCombatState').Count -eq 0) (
        'Gear polling must reuse the combat state already refreshed by HostTick.')
    foreach ($poll in @($maintenance, (Method $pluginType 'ApplyCombatChoice' 3))) {
        Check (-not [bool](Calls $poll 'ZNet::Save')) 'Enemy choices and routine stale gear removal must not force a world checkpoint.'
    }
    Check ([bool](Calls $hostTick 'SentenceStore::QueueTickOnline') -and -not [bool](Calls $hostTick 'SentenceStore::TickOnline\(')) (
        'The periodic prison timer must use the nonblocking durable worker queue.')
    $timerQueue = Method 'ValheimModPack.PartyPrison.SentenceStore' 'QueueTickOnline' 2
    Check ([bool](Calls $timerQueue 'System\.Threading\.Tasks\.Task::Run')) 'Sentence timer disk persistence must run on a worker.'
    Check ([bool](Calls $timerQueue 'System\.Threading\.Tasks\.Task::get_IsCompleted')) 'Timer polling must observe completion before draining a previous worker.'
    foreach ($timerMethod in @(Graph $timerQueue)) {
        Check (-not [bool](Calls $timerMethod 'UnityEngine\.|ZNet::|Player::|Inventory::')) 'Background sentence persistence must not access native game objects.'
    }
    $controlPrefix = Method 'ValheimModPack.PartyPrison.ControlsPatch' 'Prefix' 13
    Check (-not [bool]($controlPrefix.Parameters | Where-Object { $_.Name -eq '__args' -or $_.ParameterType.FullName -eq 'System.Object[]' })) (
        'Every-frame controls must bind typed references without boxed argument arrays.')
    Check (-not [bool]($controlPrefix.Body.Instructions | Where-Object { $_.OpCode.Name -in @('box','newarr') })) 'Warm input guard must not box controls or allocate an argument array.'
    $nativeControls = @($native.MainModule.Types | Where-Object Name -eq 'Player' | ForEach-Object Methods | Where-Object Name -eq 'SetControls')
    Check ($nativeControls.Count -eq 1 -and $nativeControls[0].Parameters.Count -eq 12) 'Native Player.SetControls signature changed.'
    for ($controlIndex = 0; $controlIndex -lt 12; ++$controlIndex) {
        Check ($controlPrefix.Parameters[$controlIndex + 1].ParameterType.FullName -ceq ($nativeControls[0].Parameters[$controlIndex].ParameterType.FullName + '&')) (
            'Typed controls prefix must preserve exact native argument type at index ' + $controlIndex)
    }
    No-WorldScan $maintenance
    $kitLookup = Method $arenaType 'GetKitZdo' 1
    Check ([bool](Calls $kitLookup 'ArenaBuilder::StoredGearChests') -and -not [bool](Calls $kitLookup 'ArenaBuilder::TaggedRegionObjects')) (
        'Equipment identity and stored-gear expiry must reuse the same regional chest cache.')
    No-WorldScan $kitLookup
    $chestCache = Method $arenaType 'StoredGearChests' 1
    $regionalQuery = @(Calls $chestCache 'ArenaBuilder::TaggedRegionObjects')
    Check ($regionalQuery.Count -eq 1) 'The five-chest cache must use exactly one bounded region query.'
    Check ([bool]($chestCache.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq 'nextStoredGearQuery' -and $_.Offset -lt $regionalQuery[0].Offset
    })) 'The cached native chest lookup must consult its clock before querying the region.'
    Check ([bool]($chestCache.Body.Instructions | Where-Object {
        $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and $_.Offset -lt $regionalQuery[0].Offset
    })) 'The native chest cache must have a fast return before regional enumeration.'
    Check ([bool]($chestCache.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq 1 })) (
        'The shared equipment cache must retain its one-second lookup budget.')
    Check ([bool](Calls (Method $arenaType 'InvalidateLayoutCache' 0) 'ArenaBuilder::InvalidateStoredGearCache')) (
        'A build or layout upgrade must invalidate the shared chest cache immediately.')
    No-WorldScan $chestCache

    $custodyType = 'ValheimModPack.PartyPrison.CustodyInventory'
    $adapterType = 'ValheimModPack.PartyPrison.BackpackAccess`2'
    $cacheType = 'ValheimModPack.PartyPrison.BackpackAccessCache`2'
    $bagPlan = Method $custodyType 'BackpackGearPlan' 1
    $walkBags = Method $custodyType 'PlanBackpackGear' 6
    foreach ($method in @(Graph $bagPlan)) {
        Check (-not [bool](Calls $method 'HarmonyLib\.AccessTools::(TypeByName|AllTypes)|System\.Reflection\.Assembly::GetTypes')) (
            'Equipment expiry must never enumerate every loaded type: ' + $method.FullName)
    }
    foreach ($method in @($bagPlan, $walkBags)) {
        Check (-not [bool](Calls $method 'System\.Reflection\.(MethodBase|MethodInfo)::Invoke|HarmonyLib\.AccessTools::')) (
            'The recurring backpack traversal must use the cached typed adapter: ' + $method.FullName)
    }
    $bagTest = @($walkBags.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'IsBackpack'
    })
    $bagInventory = @($walkBags.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'Inventory'
    })
    Check ($bagTest.Count -eq 1 -and $bagInventory.Count -eq 1 -and $bagTest[0].Offset -lt $bagInventory[0].Offset) (
        'Ordinary items must be classified before calling the real backpack inventory API.')
    Check ([bool]($walkBags.Body.Instructions | Where-Object {
        $_.OpCode.FlowControl -eq [Mono.Cecil.Cil.FlowControl]::Cond_Branch -and
        $_.Offset -gt $bagTest[0].Offset -and $_.Offset -lt $bagInventory[0].Offset -and
        $_.Operand -is [Mono.Cecil.Cil.Instruction] -and $_.Operand.Offset -gt $bagInventory[0].Offset
    })) 'The ordinary-item branch must skip backpack component and inventory access.'
    Check ([bool]($bagPlan.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq 'EmptyBackpackPlan'
    })) 'A backpack-free inventory must reuse the empty plan without allocating a traversal.'
    foreach ($limit in @(8192, 128, 4)) {
        Check ([bool]($walkBags.Body.Instructions | Where-Object {
            ($_.OpCode.Name -eq ('ldc.i4.' + $limit)) -or ($_.OpCode.Name -in @('ldc.i4','ldc.i4.s') -and [int]$_.Operand -eq $limit)
        })) ('Typed traversal must retain its bounded item, bag and depth limits: ' + $limit)
    }
    $adapterCache = Method $cacheType 'Get' 2
    $resolution = @(Calls $adapterCache 'System\.Func.*::Invoke')
    Check ($resolution.Count -eq 1) 'The optional backpack adapter must have one cold resolution call.'
    Check ([bool]($adapterCache.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'value' -and $_.Offset -lt $resolution[0].Offset
    }) -and [bool]($adapterCache.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ret' -and $_.Offset -lt $resolution[0].Offset
    })) 'A bound backpack adapter must return before any API resolution.'
    Check ([bool]($adapterCache.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq 10
    }) -and [bool]($adapterCache.Body.Instructions | Where-Object {
        $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq 'nextResolution' -and $_.Offset -lt $resolution[0].Offset
    })) 'An absent or incompatible optional API must use a ten-second cold retry budget.'
    $resolveAdapter = Method $custodyType 'ResolveBackpackApi' 0
    Check (@(Calls $resolveAdapter 'CustodyInventory::LoadedType').Count -eq 1 -and
        @(Calls $resolveAdapter 'System\.Reflection\.Assembly::GetType').Count -eq 2) (
        'Only ABAPI may use loaded-assembly lookup; holder and component types must come from its assembly.')
    foreach ($name in @('Vapok.Common.Managers.ItemExtensions','AdventureBackpacks.Components.BackpackComponent')) {
        Check ([bool]($resolveAdapter.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $name })) (
            'The typed backpack resolver must bind its exact owner-assembly type: ' + $name)
    }
    $loadedType = Method $custodyType 'LoadedType' 1
    Check ([bool](Calls $loadedType 'System\.Reflection\.Assembly::GetType') -and
        -not [bool](Calls $loadedType '::GetTypes|AccessTools::TypeByName|AccessTools::AllTypes')) (
        'The bounded optional-plugin lookup must use exact type names without global type-array allocation.')
    foreach ($name in @('Create','Bind')) {
        Check ([bool](Calls (Method $adapterType $name) 'System\.Delegate::CreateDelegate')) (
            'Backpack API binding must produce typed delegates once: ' + $name)
    }
    $settled = Method $arenaType 'RemoveObsoleteGearWhenSettled' 4
    Check ([bool](@(Graph $settled) | ForEach-Object Body | ForEach-Object Instructions | Where-Object {
        $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq 2
    })) 'The backpack optimization must retain the two-second grace for newly observed public chest equipment.'

    # Validate the ABI against the installed plugin without loading Unity or
    # activating its components. Vapok types must be AdventureBackpacks' own copy.
    $abPath = Join-Path $GameDirectory 'BepInEx/plugins/Vapok-AdventureBackpacks/AdventureBackpacks.dll'
    if (Test-Path -LiteralPath $abPath -PathType Leaf) {
    $ab = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($abPath)
    try {
        $api = @($ab.MainModule.Types | Where-Object FullName -eq 'AdventureBackpacks.API.ABAPI')
        $extensions = @($ab.MainModule.Types | Where-Object FullName -eq 'Vapok.Common.Managers.ItemExtensions')
        $component = @($ab.MainModule.Types | Where-Object FullName -eq 'AdventureBackpacks.Components.BackpackComponent')
        Check ($api.Count -eq 1 -and $extensions.Count -eq 1 -and $component.Count -eq 1) 'Installed AB must own all three adapter ABI types.'
        Check ([Object]::ReferenceEquals($api[0].Module, $extensions[0].Module) -and
            [Object]::ReferenceEquals($api[0].Module, $component[0].Module)) 'Backpack adapter types must share the exact same owner assembly.'
        foreach ($entry in @(@('IsBackpack','System.Boolean'), @('GetBackpackInventory','Inventory'))) {
            $method = @($api[0].Methods | Where-Object {
                $_.Name -eq $entry[0] -and $_.IsPublic -and $_.IsStatic -and $_.Parameters.Count -eq 1 -and
                $_.Parameters[0].ParameterType.FullName -eq 'ItemDrop/ItemData' -and $_.ReturnType.FullName -eq $entry[1]
            })
            Check ($method.Count -eq 1) ('Installed AB typed static ABI changed: ' + $entry[0])
        }
        $data = @($extensions[0].Methods | Where-Object {
            $_.Name -eq 'Data' -and $_.IsPublic -and $_.IsStatic -and $_.Parameters.Count -eq 1 -and
            $_.Parameters[0].ParameterType.FullName -eq 'ItemDrop/ItemData'
        })
        Check ($data.Count -eq 1) 'Installed AB must have one exact native ItemData-to-holder overload.'
        $holder = @($ab.MainModule.Types | Where-Object FullName -eq $data[0].ReturnType.FullName)
        Check ($holder.Count -eq 1 -and [Object]::ReferenceEquals($api[0].Module, $holder[0].Module)) 'AB Data return type must resolve in the API owner assembly.'
        $getComponent = @($holder[0].Methods | Where-Object {
            $_.Name -eq 'GetOrCreate' -and $_.IsPublic -and -not $_.IsStatic -and $_.GenericParameters.Count -eq 1 -and
            $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.String' -and
            $_.ReturnType -is [Mono.Cecil.GenericParameter] -and $_.ReturnType.Position -eq 0
        })
        Check ($getComponent.Count -eq 1) 'AB holder must expose the open-instance GetOrCreate<TComponent>(string) typed return ABI.'
        $serialize = @($component[0].Methods | Where-Object {
            $_.Name -eq 'Serialize' -and $_.IsPublic -and -not $_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'System.String'
        })
        Check ($serialize.Count -eq 1) 'Installed AB Serialize ABI must remain an open-instance zero-argument string return.'
    } finally { $ab.Dispose() }
    } else {
        Write-Output 'SKIP: Installed AdventureBackpacks ABI fixture; optional plugin absent. All production adapter and traversal performance contracts still run.'
    }

    $progress = Method $pluginType 'ProgressCustody' 0
    Check ([bool]($progress.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'cachedOffer' })) 'Admission retries must reuse the initial inventory offer instead of repeatedly serializing backpacks.'
    $sendClear = Method $pluginType 'SendClear' 2
    Check ([bool]($sendClear.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'custodySentTokens' })) 'A second sentence must not inherit the previous sentence admission cooldown.'
    foreach ($name in @('HostFightReady','WriteCustodyState')) {
        $state = Method $pluginType $name
        Check ([bool](Calls $state 'CustodyStore::FindState')) ('Frequent state updates must not clone full item backups: ' + $name)
        Check (-not [bool](Calls $state 'CustodyStore::Find\(')) ('Full custody payload lookup remains in a frequent state update: ' + $name)
    }
    # Native death ABI: the arena borrows only the game's skill-loss branch;
    # the detached Unity fixture covers its behavior when a launch is possible.
    $playerType = @($native.MainModule.Types | Where-Object FullName -eq 'Player')[0]
    $foodType = @($playerType.NestedTypes | Where-Object Name -eq 'Food')
    Check ($foodType.Count -eq 1) 'Native consumed food must keep its explicit food record type.'
    foreach ($entry in @(@('m_name','System.String'), @('m_item','ItemDrop/ItemData'), @('m_time','System.Single'), @('m_health','System.Single'), @('m_stamina','System.Single'), @('m_eitr','System.Single'))) {
        $field = @($foodType[0].Fields | Where-Object Name -eq $entry[0])
        Check ($field.Count -eq 1 -and $field[0].IsPublic -and $field[0].FieldType.FullName -eq $entry[1]) ('Native food record ABI changed: ' + $entry[0])
    }
    $getFoods = @($playerType.Methods | Where-Object { $_.Name -eq 'GetFoods' -and $_.Parameters.Count -eq 0 })
    Check ($getFoods.Count -eq 1 -and $getFoods[0].IsPublic -and $getFoods[0].ReturnType.FullName -eq 'System.Collections.Generic.List`1<Player/Food>') 'Exact consumed food removal needs the public native food list.'
    $foodTotals = @($playerType.Methods | Where-Object { $_.Name -eq 'GetTotalFoodValue' -and $_.Parameters.Count -eq 3 })
    Check ($foodTotals.Count -eq 1 -and -not $foodTotals[0].IsStatic -and $foodTotals[0].ReturnType.FullName -eq 'System.Void' -and
        @($foodTotals[0].Parameters | Where-Object { $_.ParameterType.FullName -ne 'System.Single&' }).Count -eq 0) 'Native exact-food total recomputation ABI changed.'
    $setEitr = @($playerType.Methods | Where-Object { $_.Name -eq 'SetMaxEitr' -and $_.Parameters.Count -eq 2 })
    Check ($setEitr.Count -eq 1 -and $setEitr[0].Parameters[0].ParameterType.FullName -eq 'System.Single' -and $setEitr[0].Parameters[1].ParameterType.FullName -eq 'System.Boolean') 'Native exact-food eitr clamp ABI changed.'
    $foodExpiry = Method $arenaType 'RemoveObsoleteFoodEffects' 5
    Check ([bool](Calls $foodExpiry 'Player::GetFoods') -and [bool](Calls $foodExpiry 'ArenaBuilder::RemoveObsoleteFoodEffectsFrom')) 'Food expiry must use the exact production food-list predicate.'
    foreach ($method in @(Graph $foodExpiry)) {
        Check (-not [bool](Calls $method 'Player::ClearFood|Player::RemoveOneFood|Player::UpdateFood')) 'Issued food expiry must not remove random/personal food or advance every food clock.'
    }
    Check ([bool](Calls $foodExpiry 'Character::SetMaxHealth|Player::SetMaxHealth') -and [bool](Calls $foodExpiry 'Player::SetMaxStamina')) 'Food removal must immediately clamp native health and stamina totals.'
    Check (@($foodExpiry.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq 'NativeFoodTotals' }).Count -gt 0 -and
        @($foodExpiry.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.Name -eq 'NativeSetMaxEitr' }).Count -gt 0) 'Food total/private eitr ABI resolution must be cached rather than repeated during maintenance.'
    Clock-Gate $maintenance 'nextGearMaintenance' 0.5 'ArenaBuilder::RemoveObsoleteFoodEffects'
    Check ([bool](Calls (Method $pluginType 'FinishRelease' 0) 'ArenaBuilder::RemoveObsoleteFoodEffects')) 'Durable release must remove consumed issued-food effects before its checkpoint.'
    $foodRefresh = Method 'ValheimModPack.PartyPrison.PrisonFoodRefreshPatch' 'Postfix' 3
    foreach ($name in @('m_name','m_item')) {
        Check ([bool]($foodRefresh.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $name })) ('A successful native food refresh must adopt the actual consumed serving identity: ' + $name)
    }
    foreach ($name in @('m_time','m_health','m_stamina','m_eitr')) {
        Check (-not [bool]($foodRefresh.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $name })) ('The identity postfix must not alter native food clocks or benefits: ' + $name)
    }
    $ammoPair = Method $arenaType 'CanStackIssuedAmmo' 2
    Check ([bool](Calls $ammoPair 'ArenaBuilder::IsIssuedAmmo') -and [bool](Calls $ammoPair 'PrisonGearPolicy::TryParse')) 'Issued ammo stacking must compare an isolated type and valid exact provenance.'
    foreach ($guard in @((Method 'ValheimModPack.PartyPrison.StackIsolationPatch' 'Prefix'), (Method 'ValheimModPack.PartyPrison.SlotStackIsolationPatch' 'Prefix'))) {
        Check ([bool](@(Graph $guard) | ForEach-Object { Calls $_ 'ArenaBuilder::CanStackIssuedAmmo' })) ('Both native automatic and manual-slot stacking need issued-ammo provenance isolation: ' + $guard.Name)
    }
    $hardDeath = @($playerType.Methods | Where-Object { $_.Name -eq 'HardDeath' -and $_.Parameters.Count -eq 0 })
    Check ($hardDeath.Count -eq 1 -and -not $hardDeath[0].IsStatic -and $hardDeath[0].ReturnType.FullName -eq 'System.Boolean') 'Native HardDeath must remain the instance bool cooldown policy.'
    foreach ($name in @('m_timeSinceDeath','m_hardDeathCooldown')) {
        $field = @($playerType.Fields | Where-Object Name -eq $name)
        Check ($field.Count -eq 1 -and -not $field[0].IsStatic -and $field[0].FieldType.FullName -eq 'System.Single') ('Native death timer ABI changed: ' + $name)
    }
    $skillsType = @($native.MainModule.Types | Where-Object FullName -eq 'Skills')[0]
    foreach ($name in @('Clear','OnDeath')) {
        $method = @($skillsType.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })
        Check ($method.Count -eq 1 -and $method[0].IsPublic -and -not $method[0].IsStatic -and $method[0].ReturnType.FullName -eq 'System.Void') ('Native skill penalty ABI changed: ' + $name)
    }
    $penalty = Method 'ValheimModPack.PartyPrison.ArenaDefeatPenalty' 'Apply' 1
    Check ([bool](Calls $penalty 'Skills::OnDeath') -and [bool](Calls $penalty 'Skills::Clear') -and [bool](Calls $penalty 'ZoneSystem::GetGlobalKey')) 'Arena skill loss must reuse the native skill and world-rule APIs.'
    Check (-not [bool](Calls $penalty 'Player::OnDeath|Player::ClearHardDeath|Skills::LowerAllSkills')) 'Arena penalty must not create a second native death or bypass native skill policy.'
    $enforce = Method $pluginType 'Enforce' 1
    Check (@(Calls $enforce 'ArenaDefeatPenalty::Apply').Count -eq 1) 'Arena defeat must have one skill penalty dispatch.'
    $restore = @(Calls $enforce 'Character::SetHealth|Player::SetHealth')
    $ready = @(Calls $enforce 'WorldCharacters\.Plugin::get_AdministrativeReady')
    $loss = @(Calls $enforce 'ArenaDefeatPenalty::Apply')
    Check ($restore.Count -eq 1 -and $ready.Count -eq 1 -and $restore[0].Offset -lt $loss[0].Offset -and $loss[0].Offset -lt $ready[0].Offset) 'Lethal arena damage must restore the actor before skill processing and before the administrative readiness gate.'
    foreach ($target in @('Plugin::NotifyDefeat','WorldCharacters\.Plugin::RequestAdministrativeSave')) {
        $call = @(Calls $enforce $target)
        Check ($call.Count -eq 1 -and $restore[0].Offset -lt $call[0].Offset) ('Health restoration must precede a fallible defeat action: ' + $target)
    }
    foreach ($field in @('defeatNotificationPending','defeatCheckpointPending','nextDefeatRecovery')) {
        Check ([bool]($enforce.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq $field })) ('Defeat action retries must remain independently tracked and throttled: ' + $field)
        foreach ($name in @('Reset','ClientMessage')) {
            $resetMethod = Method $pluginType $name
            Check ([bool]($resetMethod.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.Name -eq $field })) ('Defeat retry state must clear on world/sentence reset: ' + $field)
        }
    }
    $deathPrefix = Method 'ValheimModPack.PartyPrison.DefeatPatch' 'Prefix' 1
    Check ([bool](Calls $deathPrefix 'ZNetView::IsValid') -and [bool](Calls $deathPrefix 'ZNetView::IsOwner')) 'Only the authoritative local inmate may intercept lethal damage.'
    Check ([bool](Calls $deathPrefix 'Character::IsDead|Player::IsDead')) 'Arena interception must retain the native already-dead guard.'
    Write-Output ('PASS: ' + $script:checks + ' prison inventory, admission, arena, death and spawn performance contracts against the installed game. No game process launched.')
} finally {
    $plugin.Dispose()
    $native.Dispose()
}
