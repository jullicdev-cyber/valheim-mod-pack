[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before native verification.' }
$smoke = Join-Path $root ('.cache/qol-native-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path (Join-Path $smoke 'BepInEx/plugins'),(Join-Path $smoke 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/core') -Destination (Join-Path $smoke 'BepInEx/core') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/config') -Destination (Join-Path $smoke 'BepInEx/config') -Recurse
foreach ($path in @('Game/BepInEx/plugins/Jotunn.dll','Game/BepInEx/plugins/ValheimModding-YamlDotNet/YamlDotNet.dll',
    'Game/BepInEx/plugins/RandyKnapp-EquipmentAndQuickSlots/EquipmentAndQuickSlots.dll',
    'Game/BepInEx/plugins/Goldenrevolver-Quick_Stack_Store_Sort_Trash_Restock/QuickStackStore.dll',
    'Game/BepInEx/plugins/Azumatt-AzuAutoStore/AzuAutoStore.dll','local-plugins/EAQSQuickStackBridge.dll',
    'local-plugins/ExpeditionLoadouts.dll','local-plugins/ChestSearch.dll','local-plugins/ConfirmMapPinRemoval.dll')) {
    Copy-Item -LiteralPath (Join-Path $root $path) -Destination (Join-Path $smoke ('BepInEx/plugins/' + (Split-Path $path -Leaf)))
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
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),(Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'),(Join-Path $root 'local-plugins/ExpeditionLoadouts.dll'),(Join-Path $root 'local-plugins/ChestSearch.dll'))
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/target:library','/codepage:65001',('/out:'+(Join-Path $smoke 'BepInEx/plugins/BackendNativeProbe.dll')),'/reference:System.Runtime.Serialization.dll')
$argsList += $refs | ForEach-Object { '/reference:'+$_ }
$argsList += Join-Path $PSScriptRoot 'BackendNativeProbe.cs'
$argsList += Join-Path $root 'plugins/ChestSearch/NativeChecks.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native probe compilation failed.' }
$previousRoot = $env:VMP_QOL_SMOKE_ROOT
$process = $null
try {
    $env:VMP_QOL_SMOKE_ROOT = $smoke
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started while preparing the probe.' }
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $smoke 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $smoke 'Unity.log'),(Join-Path $smoke 'Saves')
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $smoke -WindowStyle Hidden -PassThru
    Write-Output "Native verification PID $($process.Id); isolated folder $smoke"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill(); throw 'Native verification timed out.' }
    }
    $resultFile = Join-Path $smoke 'result.txt'
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No native result; inspect $smoke/Unity.log" }
    $result = [IO.File]::ReadAllText($resultFile)
    Write-Output $result
    if (-not $result.StartsWith('PASS') -or $result.Contains('FAIL')) { throw 'Native verification failed.' }
    foreach ($name in @('ExpeditionLoadouts','ChestSearch','ConfirmMapPinRemoval','EAQSQuickStackBridge')) {
        Write-Output ($name + ' SHA256: ' + (Get-FileHash -LiteralPath (Join-Path $smoke "BepInEx/plugins/$name.dll")).Hash)
    }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:VMP_QOL_SMOKE_ROOT = $previousRoot
}
