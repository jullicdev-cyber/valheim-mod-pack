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
            if ($instruction.OpCode.Name -notin @('call','callvirt','newobj')) { continue }
            if ($methods.ContainsKey($instruction.Operand.FullName)) { $pending.Enqueue($methods[$instruction.Operand.FullName]) }
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
    $spawn = Method $arenaType 'TickWaveSpawning' 1
    $instantiation = @(Calls $spawn 'UnityEngine\.Object::Instantiate')
    Check ($instantiation.Count -eq 1) 'The wave scheduler must have exactly one native mob creation site.'
    Check ([bool]($spawn.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and [single]$_.Operand -eq [single]0.6 })) 'Mob creation must be staggered by at least 0.6 seconds.'
    Check ([bool](Calls $spawn 'UnityEngine\.Time::get_realtimeSinceStartup')) 'The wave scheduler needs a monotonic clock gate.'
    if ($instantiation.Count -eq 1) {
        Check (-not [bool]($spawn.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.Cil.Instruction] -and $_.Operand.Offset -le $instantiation[0].Offset -and
            $_.Offset -ge $instantiation[0].Offset
        })) 'Native mob creation must not occur in an in-frame loop.'
    }
    No-WorldScan $spawn
    $auto = Method $pluginType 'AutoWaves' 0
    $live = @(Calls $auto 'ArenaBuilder::LiveMobCount')
    $inside = @(Calls $auto 'ArenaBuilder::IsInsideArena')
    $ready = @(Calls $auto 'Plugin::HostFightReady')
    Check ($live.Count -eq 1 -and $inside.Count -gt 0 -and $ready.Count -gt 0 -and
        $inside[0].Offset -lt $live[0].Offset -and $ready[0].Offset -lt $live[0].Offset) 'Empty or unoccupied arenas must skip the mob-count query.'
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
    $maintenance = Method $pluginType 'MaintainCombatGear' 0
    Clock-Gate $maintenance 'nextGearMaintenance' 0.5 'ArenaBuilder::RemoveObsoleteGearWhenSettled'
    Clock-Gate $maintenance 'nextStoredGearMaintenance' 1 'ArenaBuilder::ExpireStoredPrisonGear'
    Check (@(Calls $maintenance 'Plugin::RefreshHostCombatState').Count -eq 1) (
        'Local gear polling must not add another combat region query outside the once-per-second host-storage gate.')
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

    $progress = Method $pluginType 'ProgressCustody' 0
    Check ([bool]($progress.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'cachedOffer' })) 'Admission retries must reuse the initial inventory offer instead of repeatedly serializing backpacks.'
    $sendClear = Method $pluginType 'SendClear' 2
    Check ([bool]($sendClear.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'custodySentTokens' })) 'A second sentence must not inherit the previous sentence admission cooldown.'
    foreach ($name in @('HostFightReady','WriteCustodyState')) {
        $state = Method $pluginType $name
        Check ([bool](Calls $state 'CustodyStore::FindState')) ('Frequent state updates must not clone full item backups: ' + $name)
        Check (-not [bool](Calls $state 'CustodyStore::Find\(')) ('Full custody payload lookup remains in a frequent state update: ' + $name)
    }
    Write-Output ('PASS: ' + $script:checks + ' prison inventory, admission, arena and spawn performance contracts against the installed game. No game process launched.')
} finally {
    $plugin.Dispose()
    $native.Dispose()
}
