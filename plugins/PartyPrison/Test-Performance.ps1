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
try {
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
    Write-Output ('PASS: ' + $script:checks + ' custody inventory performance/lifecycle contracts against the installed game. No game process launched.')
} finally {
    $plugin.Dispose()
    $native.Dispose()
}
