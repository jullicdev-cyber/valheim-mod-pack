[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly,
    [string]$TestMp3,
    [switch]$BuildOnly,
    # TestMp3 must be the three-tone fixture from Generate-SeekProbe.py.
    [switch]$CheckPlaybackTransport
)
$ErrorActionPreference = 'Stop'
if ($CheckPlaybackTransport -and -not $TestMp3) { throw 'Playback transport test requires the generated tone MP3.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/NordicRadio.dll' }
if (-not $BuildOnly -and (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue)) { throw 'Close Valheim before running the native smoke check.' }
$smokeRoot = Join-Path $root ('.cache/nordic-radio-native-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path (Join-Path $smokeRoot 'BepInEx/plugins'),(Join-Path $smokeRoot 'BepInEx/config'),(Join-Path $smokeRoot 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/core') -Destination (Join-Path $smokeRoot 'BepInEx/core') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll') -Destination (Join-Path $smokeRoot 'BepInEx/plugins/Jotunn.dll')
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/plugins/ValheimModding-YamlDotNet/YamlDotNet.dll') -Destination (Join-Path $smokeRoot 'BepInEx/plugins/YamlDotNet.dll')
Copy-Item -LiteralPath $PluginAssembly -Destination (Join-Path $smokeRoot 'BepInEx/plugins/NordicRadio.dll')
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
WriteUnityLog = true
LogLevels = All
'@ | Set-Content -LiteralPath (Join-Path $smokeRoot 'BepInEx/config/BepInEx.cfg') -Encoding UTF8
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/BepInEx.dll'),(Join-Path $root 'Game/BepInEx/plugins/Jotunn.dll'),(Join-Path $smokeRoot 'BepInEx/plugins/NordicRadio.dll'))
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.AssetBundleModule.dll','UnityEngine.AudioModule.dll','UnityEngine.UnityWebRequestModule.dll','UnityEngine.UnityWebRequestAudioModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/target:library','/codepage:65001',('/out:'+(Join-Path $smokeRoot 'BepInEx/plugins/SmokeProbe.dll')))
$argsList += $refs | ForEach-Object { '/reference:'+$_ }
$argsList += Join-Path $PSScriptRoot 'NativeSmokeProbe.cs'
$argsList += Join-Path $PSScriptRoot 'NativePlaybackProbe.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native smoke probe compilation failed.' }
if ($BuildOnly) { Write-Output "Native smoke probe compiled, not executed: $smokeRoot"; return }
$oldRoot = $env:NORDICRADIO_SMOKE_ROOT
$oldMp3 = $env:NORDICRADIO_SMOKE_MP3
$oldSeek = $env:NORDICRADIO_SMOKE_SEEK
$process = $null
try {
    $env:NORDICRADIO_SMOKE_ROOT = $smokeRoot
    $env:NORDICRADIO_SMOKE_SEEK = if ($CheckPlaybackTransport) { '1' } else { '' }
    $env:NORDICRADIO_SMOKE_MP3 = if ($TestMp3) { (Resolve-Path -LiteralPath $TestMp3).ProviderPath } else { '' }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during setup; smoke launch cancelled.' }
    # Windows Doorstop supports this CLI override. The Linux DOORSTOP_TARGET_ASSEMBLY
    # environment variable is not supported by this Windows loader build.
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $smokeRoot 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $smokeRoot 'Unity.log'),(Join-Path $smokeRoot 'Saves')
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $smokeRoot -WindowStyle Hidden -PassThru
    Write-Output "Native smoke PID $($process.Id); isolated logs: $smokeRoot"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill(); throw 'Native smoke process exceeded 120 seconds.' }
    }
    $resultFile = Join-Path $smokeRoot 'result.txt'
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No native probe result. Inspect $smokeRoot/Unity.log" }
    $result = [IO.File]::ReadAllText($resultFile)
    Write-Output $result
    if ($result.Contains('FAIL') -or -not $result.StartsWith('PASS')) { throw 'Native smoke assertions failed.' }
    Write-Output ('Tested DLL SHA256: '+(Get-FileHash -LiteralPath (Join-Path $smokeRoot 'BepInEx/plugins/NordicRadio.dll') -Algorithm SHA256).Hash)
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:NORDICRADIO_SMOKE_ROOT = $oldRoot
    $env:NORDICRADIO_SMOKE_MP3 = $oldMp3
    $env:NORDICRADIO_SMOKE_SEEK = $oldSeek
}
