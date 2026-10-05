[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [switch]$WithGraphics, [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before native verification.' }
$smoke = [IO.Path]::GetFullPath((Join-Path $root ('.cache/qol-native-' + [guid]::NewGuid().ToString('N').Substring(0,8))))
if (-not $smoke.StartsWith([IO.Path]::GetFullPath((Join-Path $root '.cache')) + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Native probe directory must stay inside the workspace cache.'
}
function Assert-ProbePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($smoke + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Probe path outside isolated directory: $fullPath" }
    return $fullPath
}
New-Item -ItemType Directory -Force -Path (Join-Path $smoke 'BepInEx/plugins'),(Join-Path $smoke 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/core') -Destination (Join-Path $smoke 'BepInEx/core') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/config') -Destination (Join-Path $smoke 'BepInEx/config') -Recurse
foreach ($path in @('Game/BepInEx/plugins/Jotunn.dll','Game/BepInEx/plugins/ValheimModding-YamlDotNet/YamlDotNet.dll',
    'Game/BepInEx/plugins/ValheimModding-YamlDotNet/YamlDotNetDetector.dll',
    'Game/BepInEx/plugins/RandyKnapp-EquipmentAndQuickSlots/EquipmentAndQuickSlots.dll',
    'Game/BepInEx/plugins/Goldenrevolver-Quick_Stack_Store_Sort_Trash_Restock/QuickStackStore.dll',
    'Game/BepInEx/plugins/Azumatt-AzuAutoStore/AzuAutoStore.dll','local-plugins/EAQSQuickStackBridge.dll',
    'local-plugins/ExpeditionLoadouts.dll','local-plugins/ChestSearch.dll','local-plugins/ConfirmMapPinRemoval.dll',
    'Game/BepInEx/plugins/Advize-PlantEverything/Advize_PlantEverything.dll',
    'local-plugins/InterfaceInputFix.dll','local-plugins/RenewableResourceTimers.dll','local-plugins/NordicRadio.dll','Game/BepInEx/plugins/isimp-Bindrune/Bindrune.dll')) {
    Copy-Item -LiteralPath (Join-Path $root $path) -Destination (Join-Path $smoke ('BepInEx/plugins/' + (Split-Path $path -Leaf)))
}
foreach ($folder in @('XPortal','Vapok-AdventureBackpacks')) {
    Copy-Item -LiteralPath (Join-Path $root ('Game/BepInEx/plugins/'+$folder)) -Destination (Join-Path $smoke ('BepInEx/plugins/'+$folder)) -Recurse
}
# Prefer the just-built fork over a previous packaged XPortal binary in this fixture.
$portalBuild = Join-Path $root 'local-plugins/XPortal.dll'
if (Test-Path -LiteralPath $portalBuild -PathType Leaf) {
    Copy-Item -LiteralPath $portalBuild -Destination (Assert-ProbePath (Join-Path $smoke 'BepInEx/plugins/XPortal/XPortal.dll')) -Force
}
# Include every vendor and local plugin from the actual pack. The explicit checks
# above may lag newly added mods; they must not silently disappear from this fixture.
foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $root 'Game/BepInEx/plugins')) {
    if ($entry.Name -like 'ValheimModPack-*') {
        foreach ($dll in Get-ChildItem -LiteralPath $entry.FullName -Filter '*.dll' -File -Recurse) {
            $destination = Join-Path $smoke ('BepInEx/plugins/' + $dll.Name)
            if (-not (Test-Path -LiteralPath $destination)) {
                $built = Join-Path $root ('local-plugins/' + $dll.Name)
                $source = if (Test-Path -LiteralPath $built) { $built } else { $dll.FullName }
                Copy-Item -LiteralPath $source -Destination $destination
            }
        }
        continue
    }
    $destination = Join-Path $smoke ('BepInEx/plugins/' + $entry.Name)
    if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse }
}
# Avoid duplicate flat copies for vendors also kept in their original folders.
foreach ($name in @('YamlDotNet','YamlDotNetDetector','EquipmentAndQuickSlots','QuickStackStore','AzuAutoStore','Advize_PlantEverything','Bindrune')) {
    $duplicate = Assert-ProbePath (Join-Path $smoke ('BepInEx/plugins/' + $name + '.dll'))
    if (Test-Path -LiteralPath $duplicate) { Remove-Item -LiteralPath $duplicate }
}
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
WriteUnityLog = true
LogLevels = All
'@ | Set-Content -LiteralPath (Join-Path $smoke 'BepInEx/config/BepInEx.cfg') -Encoding UTF8
$mapProbe = Join-Path $smoke 'PinHistoryNativeChecks.dll'
& (Join-Path $root 'plugins/ConfirmMapPinRemoval/Build-NativeChecks.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $root 'local-plugins/ConfirmMapPinRemoval.dll') -OutputDirectory $smoke
& (Join-Path $root 'plugins/EAQSQuickStackBridge/Build-NativeChecks.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $root 'local-plugins/EAQSQuickStackBridge.dll') -OutputDirectory $smoke
& (Join-Path $root 'plugins/InventoryAdmin/Build-NativeChecks.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $root 'local-plugins/InventoryAdmin.dll') -OutputDirectory $smoke
& (Join-Path $root 'plugins/NordicRadio/Build-PersonalAudioChecks.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $root 'local-plugins/NordicRadio.dll') -OutputDirectory $smoke
& (Join-Path $root 'plugins/AnyPortalPlus/Build-UiNativeChecks.ps1') -GameDirectory $GameDirectory -OutputDirectory $smoke
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),(Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'),(Join-Path $root 'local-plugins/ExpeditionLoadouts.dll'),(Join-Path $root 'local-plugins/ChestSearch.dll'))
$refs += @('Game/BepInEx/core/0Harmony.dll','local-plugins/InterfaceInputFix.dll','local-plugins/RenewableResourceTimers.dll','Game/BepInEx/plugins/isimp-Bindrune/Bindrune.dll') | ForEach-Object { Join-Path $root $_ }
$refs += Join-Path $root 'Game/BepInEx/plugins/Azumatt-Recycle_N_Reclaim/Recycle_N_Reclaim.dll'
$refs += Join-Path $root 'Game/BepInEx/plugins/VentureValheim-Venture_Floating_Items/VentureValheim.FloatingItems.dll'
$refs += Join-Path $managed 'UnityEngine.PhysicsModule.dll'
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/target:library','/codepage:65001',('/out:'+(Join-Path $smoke 'BepInEx/plugins/BackendNativeProbe.dll')),'/reference:System.Runtime.Serialization.dll')
$argsList += $refs | ForEach-Object { '/reference:'+$_ }
$argsList += Join-Path $PSScriptRoot 'BackendNativeProbe.cs'
$argsList += Join-Path $root 'plugins/ChestSearch/NativeChecks.cs'
$argsList += Join-Path $root 'plugins/InterfaceInputFix/NativeChecks.cs'
$argsList += Join-Path $root 'plugins/InterfaceInputFix/BindruneNativeChecks.cs'
$argsList += Join-Path $root 'plugins/RenewableResourceTimers/NativeChecks.cs'
$argsList += Join-Path $root 'scripts/RecycleNativeChecks.cs'
$argsList += Join-Path $root 'scripts/FloatingItemsNativeChecks.cs'
$argsList += Join-Path $root 'plugins/FermenterCompatibility/NativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native probe compilation failed.' }
if ($CompileOnly) {
    Write-Output "Native verification fixture compiled without launching Valheim: $smoke"
    return
}
$previousRoot = $env:VMP_QOL_SMOKE_ROOT
$previousRestart = $env:VMP_BIND_RESTART
$process = $null
try {
    $env:VMP_QOL_SMOKE_ROOT = $smoke
    foreach ($phase in @(0,1)) {
    $env:VMP_BIND_RESTART = [string]$phase
    if ($phase -eq 1) {
        # Model a pack update replacing shared cfg, but retaining personal bindings.
        foreach ($cfg in Get-ChildItem -LiteralPath (Join-Path $smoke 'BepInEx/config') -Filter '*.cfg' -File) {
            Remove-Item -LiteralPath (Assert-ProbePath $cfg.FullName)
        }
        Copy-Item -Path (Join-Path $root 'Game/BepInEx/config/*.cfg') -Destination (Join-Path $smoke 'BepInEx/config') -Force
    }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started while preparing the probe.' }
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $smoke 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $smoke 'Unity.log'),(Join-Path $smoke 'Saves')
    if ($WithGraphics) { $arguments = $arguments.Replace('-batchmode -nographics', '-screen-fullscreen 0 -screen-width 1280 -screen-height 720') }
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $smoke -WindowStyle Hidden -PassThru
    Write-Output "Native verification PID $($process.Id); isolated folder $smoke"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill(); throw 'Native verification timed out.' }
    }
    $resultFile = Assert-ProbePath (Join-Path $smoke 'result.txt')
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No native result; inspect $smoke/Unity.log" }
    $result = [IO.File]::ReadAllText($resultFile)
    Write-Output $result
    if (-not $result.StartsWith('PASS') -or $result.Contains('FAIL')) { throw 'Native verification failed.' }
    Move-Item -LiteralPath $resultFile -Destination (Assert-ProbePath (Join-Path $smoke ('result-phase-' + $phase + '.txt')))
    }
    foreach ($name in @('ExpeditionLoadouts','ChestSearch','ConfirmMapPinRemoval','EAQSQuickStackBridge','InterfaceInputFix','RenewableResourceTimers','NordicRadio','WorldCharacters','FermenterCompatibility','InventoryAdmin')) {
        Write-Output ($name + ' SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $smoke "BepInEx/plugins/$name.dll")).Hash)
    }
    Write-Output ('AnyPortal+ SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $smoke 'BepInEx/plugins/XPortal/XPortal.dll')).Hash)
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:VMP_QOL_SMOKE_ROOT = $previousRoot
    $env:VMP_BIND_RESTART = $previousRestart
}
