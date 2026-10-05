[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixture = Join-Path $root ('.cache/group-radius-motion-contracts-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$output = Join-Path $fixture 'GroupRadiusMotion.dll'
$refs = @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','netstandard.dll') | ForEach-Object { '/reference:' + (Join-Path $managed $_) }
$refs += '/reference:' + (Join-Path $root 'Game/BepInEx/core/0Harmony.dll')
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:library /codepage:65001 "/out:$output" @refs (Join-Path $PSScriptRoot 'GroupRadiusMotion.cs')
if ($LASTEXITCODE -ne 0) { throw 'Group radius native contract compilation failed.' }
[Reflection.Assembly]::LoadFrom((Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')) | Out-Null
$native = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'assembly_valheim.dll'))
$adapter = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($output)
$script:checks = 0
function Check([bool]$value, [string]$reason) { $script:checks++; if (-not $value) { throw $reason } }
function NativeType([string]$name) { return $native.MainModule.Types | Where-Object Name -eq $name }
function NativeMethod([string]$type, [string]$name) { return (NativeType $type).Methods | Where-Object Name -eq $name }
foreach ($name in @('UpdateWalking','UpdateSwimming')) {
    $method = NativeMethod 'Character' $name
    Check ($null -ne $method -and $method.Parameters.Count -eq 1 -and $method.Parameters[0].ParameterType.FullName -eq 'System.Single') "Native motor contract changed: $name"
    $loads = @($method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.FullName -eq 'UnityEngine.Vector3 Character::m_moveDir' })
    Check ($loads.Count -gt 0) "Native motor no longer consumes world-space move direction: $name"
    $stores = @($method.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stfld' -and $_.Operand.FullName -eq 'UnityEngine.Vector3 Character::m_moveDir' })
    Check ($stores.Count -eq 0) "Motor now rewrites move direction; restore strategy needs review: $name"
}
$teleport = NativeMethod 'Player' 'TeleportTo'
Check ($teleport.ReturnType.FullName -eq 'System.Boolean' -and ($teleport.Parameters.Name -join ',') -eq 'pos,rot,distantTeleport') 'Native teleport signature changed.'
$il = @($teleport.Body.Instructions)
Check ($il[2].Operand.FullName -eq 'System.Boolean ZNetView::IsOwner()') 'Native teleport must check owner before forwarding RPC.'
Check ([bool]($il | Where-Object { $_.Operand -eq 'RPC_TeleportTo' })) 'Native remote teleport route missing.'
$interior = (NativeMethod 'Character' 'InInterior') | Where-Object { $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'UnityEngine.Vector3' }
Check ([bool]($interior.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq 3000 })) 'Native interior height changed.'
$controls = NativeMethod 'Ship' 'UpdateControlls'
Check ([bool]($controls.Body.Instructions | Where-Object { $_.Operand.FullName -eq 'System.Boolean ZNetView::IsOwner()' })) 'Ship controls must retain native ownership guard.'
Check ([bool]($controls.Body.Instructions | Where-Object { $_.Operand.FullName -eq 'Ship/Speed Ship::m_speed' -and $_.OpCode.Name -eq 'ldfld' })) 'Ship controls must publish native speed setting.'
Check ([bool]($controls.Body.Instructions | Where-Object { $_.Operand.FullName -eq 'System.Void ZDO::Set(System.Int32,System.Int32,System.Boolean)' })) 'Ship controls must publish through ordinary ZDO state.'
Check (((NativeMethod 'Ship' 'ApplyControlls').Parameters.Name -join ',') -eq 'dir') 'Native boat input signature changed.'
Check (((NativeMethod 'ShipControlls' 'GetUser').ReturnType.FullName) -eq 'System.Int64') 'Native boat pilot identity contract changed.'
$types = @($adapter.MainModule.Types)
$types += $types | ForEach-Object { $_.NestedTypes }
foreach ($type in $types) {
    foreach ($method in $type.Methods) {
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            if ($instruction.OpCode.Name -notin @('call','callvirt')) { continue }
            $target = $instruction.Operand.FullName
            Check ($target -notmatch 'UnityEngine\.Transform::set_(position|rotation)|UnityEngine\.Rigidbody::|::TeleportTo\(|ZNetView::InvokeRPC|::SetOwner\(|Inventory::|ItemDrop::') "Runtime must not teleport, move bodies, force RPCs or touch inventories: $target"
        }
    }
}
Write-Output "PASS: $script:checks group radius installed-game IL and runtime safety contracts."
