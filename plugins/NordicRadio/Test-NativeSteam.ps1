[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before the native Steam check.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$probeRoot = Join-Path $root ('.cache/radio-native-steam-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path (Join-Path $probeRoot 'BepInEx/plugins'),(Join-Path $probeRoot 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx/core') -Destination (Join-Path $probeRoot 'BepInEx/core') -Recurse
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$refs = @((Join-Path $root 'Game/BepInEx/core/BepInEx.dll'))
$refs += @('assembly_valheim.dll','assembly_utils.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','com.rlabrecque.steamworks.net.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$argsList = @('/nologo','/target:library','/codepage:65001',('/out:'+(Join-Path $probeRoot 'BepInEx/plugins/SteamProbe.dll')))
$argsList += $refs | ForEach-Object { '/reference:'+$_ }
$argsList += Join-Path $PSScriptRoot 'NativeSteamProbe.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Steam probe compilation failed.' }
$previous = $env:NORDICRADIO_STEAM_PROBE
$process = $null
try {
    $env:NORDICRADIO_STEAM_PROBE = $probeRoot
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during setup.' }
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $probeRoot 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $probeRoot 'Unity.log'),(Join-Path $probeRoot 'Saves')
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $probeRoot -WindowStyle Hidden -PassThru
    Write-Output "Native Steam probe PID $($process.Id); isolated logs: $probeRoot"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 90) { $process.Kill(); throw 'Native Steam probe timed out.' }
    }
    $result = Get-Content -LiteralPath (Join-Path $probeRoot 'result.txt') -Raw
    Write-Output $result
    if (-not $result.StartsWith('PASS')) { throw 'Native Steam probe failed.' }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:NORDICRADIO_STEAM_PROBE = $previous
}
