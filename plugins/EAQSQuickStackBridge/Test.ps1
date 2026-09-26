[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root '.cache/bridge-tests'
New-Item -ItemType Directory -Force $output | Out-Null
$exe = Join-Path $output 'SlotPolicyTests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe "/out:$exe" (Join-Path $PSScriptRoot 'SlotPolicy.cs') (Join-Path $PSScriptRoot 'SlotPolicyTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Slot policy regression failed' }
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$qs = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Goldenrevolver-Quick_Stack_Store_Sort_Trash_Restock/QuickStackStore.dll'))
$ea = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/RandyKnapp-EquipmentAndQuickSlots/EquipmentAndQuickSlots.dll'))
try {
    $api = $ea.MainModule.GetType('EquipmentAndQuickSlots.API')
    foreach ($name in @('GetVisibleRows','GetFullHeight')) {
        $method = @($api.Methods | Where-Object Name -eq $name)
        if ($method.Count -ne 1 -or -not $method[0].IsPublic -or -not $method[0].IsStatic -or $method[0].ReturnType.FullName -ne 'System.Int32') { throw "EAQS API changed: $name" }
    }
    $sort = $qs.MainModule.GetType('QuickStackStore.SortModule')
    foreach ($name in @('ShouldSortSlot','GetAllowedSlots')) {
        $method = $sort.Methods | Where-Object Name -eq $name
        if (-not ($method.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains('CompatibilitySupport::IsEquipOrQuickSlot') })) { throw "Slot filter no longer guards $name" }
    }
    $target = $qs.MainModule.GetType('QuickStackStore.CompatibilitySupport').Methods | Where-Object Name -eq 'InternalIsEquipOrQuickSlot'
    if (($target.Parameters.ParameterType.FullName -join ',') -ne 'System.Int32,System.Int32,Vector2i,System.Boolean') { throw 'Harmony target signature changed' }
    Write-Output 'OK: actual pinned DLL API and both sorting filter call sites verified. Not a Unity gameplay test.'
}
finally { $qs.Dispose(); $ea.Dispose() }
