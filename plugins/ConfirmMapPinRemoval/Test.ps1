[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder=Join-Path $root '.cache/pin-confirm-tests'
New-Item -ItemType Directory -Force $folder | Out-Null
$exe=Join-Path $folder 'Tests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe "/out:$exe" (Join-Path $PSScriptRoot 'Confirmation.cs') (Join-Path $PSScriptRoot 'Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Confirmation tests failed' }
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
try {
    $map=$asm.MainModule.GetType('Minimap')
    foreach ($name in @('RemovePinUnderPointer','GetClosestPinToCursor')) {
        $methods=@($map.Methods | Where-Object { $_.Name -eq $name -and $_.Parameters.Count -eq 0 })
        if ($methods.Count -ne 1) { throw "Map API changed: $name" }
    }
    $pointer=$map.Methods | Where-Object Name -eq 'RemovePinUnderPointer'
    if (-not ($pointer.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains('Minimap::RemovePin(UnityEngine.Vector3,System.Single)') })) { throw 'Pointer deletion path changed' }
    $pinField=@($map.Fields | Where-Object Name -eq 'm_pins')
    if ($pinField.Count -ne 1) { throw 'Pin list changed' }
    $popup=$asm.MainModule.GetType('YesNoPopup')
    if (-not ($popup.Methods | Where-Object { $_.Name -eq '.ctor' -and $_.Parameters.Count -eq 6 })) { throw 'Native popup constructor changed' }
    Write-Output 'OK: actual game pointer deletion path and native popup API verified. Gameplay not tested.'
} finally { $asm.Dispose() }
