[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$folder=Join-Path $root '.cache/pin-confirm-tests'
New-Item -ItemType Directory -Force $folder | Out-Null
$exe=Join-Path $folder 'Tests.exe'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources=@('Confirmation.cs','RemovalDialog.cs','PinLabel.cs','Tests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$exe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Confirmation tests failed' }
$hostExe=Join-Path $folder 'HostTests.exe'
$sources=@('Plugin.cs','Confirmation.cs','RemovalDialog.cs','HostDoubles.cs','HostTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$hostExe" @sources
if ($LASTEXITCODE -ne 0) { throw 'Host test build failed' }
& $hostExe
if ($LASTEXITCODE -ne 0) { throw 'Plugin integration tests failed' }
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
    if ($pinField[0].FieldType.FullName -ne 'System.Collections.Generic.List`1<Minimap/PinData>') { throw 'Pin list type changed' }
    if (-not ($map.Fields | Where-Object { $_.Name -eq 'm_mode' -and $_.IsPublic })) { throw 'Map mode API changed' }
    if (-not ($map.Methods | Where-Object { $_.Name -eq 'HidePinTextInput' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.Boolean' })) { throw 'Pin rename API changed' }
    if (-not ($asm.MainModule.GetType('Player').Methods | Where-Object { $_.Name -eq 'OnDestroy' -and $_.Parameters.Count -eq 0 })) { throw 'Player cleanup API changed' }
    $closest=$map.Methods | Where-Object Name -eq 'GetClosestPinToCursor'
    if ($closest.ReturnType.FullName -ne 'Minimap/PinData') { throw 'Pin selection return type changed' }
    foreach($selector in @('ScreenToWorldPoint','get_PinInteractRadius')) {
        if (-not ($closest.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains("Minimap::$selector(") })) { throw "Selection no longer uses vanilla $selector" }
    }
    Write-Output 'OK: installed game deletion/selection/rename/map-mode/player-cleanup API verified.'
} finally { $asm.Dispose() }
$jotunn=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'))
try {
    $gui=$jotunn.MainModule.GetType('Jotunn.Managers.GUIManager')
    foreach($entry in @(@('CreateWoodpanel',7),@('CreateText',13),@('CreateButton',7),@('BlockInput',1),@('get_CustomGUIFront',0))) {
        if (-not ($gui.Methods | Where-Object { $_.Name -eq $entry[0] -and $_.Parameters.Count -eq $entry[1] -and $_.IsPublic })) { throw "Jotunn UI API changed: $($entry[0])" }
    }
    Write-Output 'OK: packaged Jotunn wood-panel UI API verified. Rendering and gameplay still require an in-game check.'
} finally { $jotunn.Dispose() }
