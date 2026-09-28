[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/NordicRadio.dll' }
if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before the native socket probe.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root '.cache' }
$probeRoot = Join-Path $OutputDirectory ('nordic-radio-native-sockets-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path (Join-Path $probeRoot 'BepInEx/plugins'),(Join-Path $probeRoot 'BepInEx/config'),(Join-Path $probeRoot 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/core') -Destination (Join-Path $probeRoot 'BepInEx/core') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll') -Destination (Join-Path $probeRoot 'BepInEx/plugins/Jotunn.dll')
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/plugins/ValheimModding-YamlDotNet/YamlDotNet.dll') -Destination (Join-Path $probeRoot 'BepInEx/plugins/YamlDotNet.dll')
Copy-Item -LiteralPath $PluginAssembly -Destination (Join-Path $probeRoot 'BepInEx/plugins/NordicRadio.dll')
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
WriteUnityLog = true
LogLevels = All
'@ | Set-Content -LiteralPath (Join-Path $probeRoot 'BepInEx/config/BepInEx.cfg') -Encoding UTF8
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),(Join-Path $root 'Game/BepInEx/core/0Harmony.dll'),(Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'),(Join-Path $probeRoot 'BepInEx/plugins/NordicRadio.dll'))
$refs += @('assembly_valheim.dll','assembly_utils.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','com.rlabrecque.steamworks.net.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$arguments = @('/nologo','/target:library','/codepage:65001',('/out:'+(Join-Path $probeRoot 'BepInEx/plugins/NativeSteamSocketProbe.dll')))
$arguments += $refs | ForEach-Object { '/reference:'+$_ }
$arguments += Join-Path $PSScriptRoot 'NativeSteamSocketProbe.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native socket probe compilation failed.' }
$oldRoot = $env:NORDICRADIO_SOCKET_PROBE_ROOT
$process = $null
try {
    $env:NORDICRADIO_SOCKET_PROBE_ROOT = $probeRoot
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during probe preparation.' }
    $launchArgs = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $probeRoot 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $probeRoot 'Unity.log'),(Join-Path $probeRoot 'Saves')
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $launchArgs -WorkingDirectory $probeRoot -WindowStyle Hidden -PassThru
    Write-Output "Native socket probe PID $($process.Id); isolated files: $probeRoot"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 90) { $process.Kill(); throw 'Native socket probe exceeded 90 seconds.' }
    }
    $result = Join-Path $probeRoot 'result.txt'
    if (-not (Test-Path -LiteralPath $result)) { throw "No native socket result. Inspect $probeRoot/Unity.log" }
    $text = [IO.File]::ReadAllText($result)
    Write-Output $text
    if (-not $text.StartsWith('PASS') -or $text.Contains('FAIL')) { throw 'Native Steam socket probe failed.' }
    Write-Output ('Tested DLL SHA256: '+(Get-FileHash -LiteralPath (Join-Path $probeRoot 'BepInEx/plugins/NordicRadio.dll') -Algorithm SHA256).Hash)
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:NORDICRADIO_SOCKET_PROBE_ROOT = $oldRoot
}
