[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [switch]$BuildOnly, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $BuildOnly -and (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue)) { throw 'Close Valheim before native verification.' }
if (-not $PluginAssembly) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory
    $PluginAssembly = Join-Path $root 'local-plugins/WorldCharacters.dll'
}
$PluginAssembly = (Resolve-Path -LiteralPath $PluginAssembly).ProviderPath
$smoke = Join-Path $root ('.cache/worldcharacters-native-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $smoke,(Join-Path $smoke 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx') -Destination (Join-Path $smoke 'BepInEx') -Recurse
$target = Join-Path $smoke 'BepInEx/plugins/ValheimModPack-WorldCharacters'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -LiteralPath $PluginAssembly -Destination (Join-Path $target 'WorldCharacters.dll')
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += $PluginAssembly
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','netstandard.dll') | ForEach-Object { Join-Path $GameDirectory ('valheim_Data/Managed/'+$_) }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$argsList = @('/nologo','/codepage:65001','/target:library',('/out:'+(Join-Path $smoke 'BepInEx/plugins/WorldCharactersProbe.dll')))
$argsList += $refs | ForEach-Object { '/reference:'+$_ }; $argsList += Join-Path $PSScriptRoot 'NativeProbe.cs'
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native probe compilation failed.' }
if ($BuildOnly) { Write-Output "Native probe compiled, not executed: $smoke"; return }
$previous = $env:VMP_WORLDCHARACTERS_PROBE
$process = $null
try {
    $env:VMP_WORLDCHARACTERS_PROBE = $smoke
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $smoke 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $smoke 'Unity.log'),(Join-Path $smoke 'Saves')
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during test setup.' }
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $smoke -WindowStyle Hidden -PassThru
    Write-Output "Native probe PID $($process.Id), isolated directory: $smoke"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) { if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill(); throw 'Native probe timed out.' } }
    $resultFile = Join-Path $smoke 'result.txt'
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No result; inspect $smoke/Unity.log" }
    $result = [IO.File]::ReadAllText($resultFile); Write-Output $result
    if (-not $result.StartsWith('PASS')) { throw 'Native probe failed.' }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:VMP_WORLDCHARACTERS_PROBE = $previous
}
