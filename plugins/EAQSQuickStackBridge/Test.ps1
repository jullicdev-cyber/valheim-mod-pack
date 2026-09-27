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
$startupExe = Join-Path $output 'StartupTests.exe'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /target:exe "/out:$startupExe" (Join-Path $PSScriptRoot 'Plugin.cs') (Join-Path $PSScriptRoot 'SlotPolicy.cs') (Join-Path $PSScriptRoot 'StartupTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Startup test compilation failed' }
& $startupExe
if ($LASTEXITCODE -ne 0) { throw 'Startup ordering regression failed' }
$autoExe = Join-Path $output 'AutoStoreHostTests.exe'
$autoSources = @('AutoStoreFavorites.cs','FavoriteStateLink.cs','SlotPolicy.cs','AutoStoreHostTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /codepage:65001 /target:exe "/out:$autoExe" @autoSources
if ($LASTEXITCODE -ne 0) { throw 'Azu favorite bridge test compilation failed' }
& $autoExe
if ($LASTEXITCODE -ne 0) { throw 'Azu favorite bridge regression failed' }
$stateExe = Join-Path $output 'FavoriteStateTests.exe'
$stateSources = @('FavoriteStateLink.cs','FavoriteStateTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /codepage:65001 /target:exe "/out:$stateExe" @stateSources
if ($LASTEXITCODE -ne 0) { throw 'Favorite state test compilation failed' }
& $stateExe
if ($LASTEXITCODE -ne 0) { throw 'Favorite state regression failed' }
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$qs = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Goldenrevolver-Quick_Stack_Store_Sort_Trash_Restock/QuickStackStore.dll'))
$ea = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/RandyKnapp-EquipmentAndQuickSlots/EquipmentAndQuickSlots.dll'))
$azu = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root 'Game/BepInEx/plugins/Azumatt-AzuAutoStore/AzuAutoStore.dll'))
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
    $azuPlugin=$azu.MainModule.GetType('AzuAutoStore.AzuAutoStorePlugin')
    $version=$azuPlugin.CustomAttributes | Where-Object {$_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'}
    if ($version.ConstructorArguments[2].Value -ne '3.1.6') { throw 'Azu pinned version changed' }
    $update=@($azuPlugin.Methods | Where-Object { $_.Name -eq 'Update' -and -not $_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'System.Void' })
    if ($update.Count -ne 1) { throw 'Azu shortcut Update guard changed' }
    $functions=$azu.MainModule.GetType('AzuAutoStore.Util.Functions')
    foreach($entry in @(@('TryStore',''),@('TryStoreThisItem','ItemDrop/ItemData,Inventory'))) {
        $dispatch=@($functions.Methods | Where-Object { $_.Name -eq $entry[0] -and ($_.Parameters.ParameterType.FullName -join ',') -eq $entry[1] -and $_.IsStatic -and $_.ReturnType.FullName -eq 'System.Void' })
        if ($dispatch.Count -ne 1) { throw "Manual Azu dispatch changed: $($entry[0])" }
    }
    foreach($name in @('VanillaContainers','BackpackContainer','kgDrawer','mkzDrawer')) {
        $type=$azu.MainModule.GetType("AzuAutoStore.Interfaces.$name")
        $filter=@($type.Methods | Where-Object Name -eq 'CantStoreFavorite')
        if ($filter.Count -ne 1 -or -not $filter[0].IsStatic -or $filter[0].ReturnType.FullName -ne 'System.Boolean' -or ($filter[0].Parameters.ParameterType.FullName -join ',') -ne 'ItemDrop/ItemData,AzuAutoStore.Patches.Favoriting.UserConfig') { throw "Azu filter API changed: $name" }
        $path = if ($name -eq 'VanillaContainers') { 'ShouldSkip' } else { 'TryStore' }
        $caller=$type.Methods | Where-Object { $_.Name -eq $path -and ($path -eq 'ShouldSkip' -or $_.Parameters.Count -eq 0) }
        if (-not ($caller.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains("${name}::CantStoreFavorite(") })) { throw "Azu manual filter no longer called: $name" }
    }
    $user=$qs.MainModule.GetType('QuickStackStore.UserConfig')
    foreach($name in @('GetPlayerConfig','IsItemNameOrSlotFavorited')) {
        $method=@($user.Methods | Where-Object Name -eq $name)
        if ($method.Count -ne 1 -or -not $method[0].IsPublic) { throw "Quick Stack user favorite API changed: $name" }
    }
    $favorite=$user.Methods | Where-Object Name -eq 'IsItemNameOrSlotFavorited'
    foreach($name in @('IsSlotFavorited','IsItemNameFavorited')) {
        if (-not ($favorite.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains("UserConfig::$name(") })) { throw "Quick Stack favorite semantics changed: $name" }
    }
    $ground=$functions.Methods | Where-Object Name -eq 'CheckItemDropInstanceAndStore'
    if ($ground.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains('CantStoreFavorite') }) { throw 'Ground auto-pull now shares player favorite path; revalidate bridge scope' }
    Write-Output 'OK: actual pinned DLL API and both sorting filter call sites verified. Not a Unity gameplay test.'
    Write-Output 'OK: Azu 3.1.6 manual dispatch, four favorite filters and Quick Stack slot/type semantics verified; ground auto-pull unchanged.'
}
finally { $qs.Dispose(); $ea.Dispose(); $azu.Dispose() }
