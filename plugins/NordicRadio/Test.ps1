[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $root '.cache/nordic-radio-tests'
New-Item -ItemType Directory -Force $out | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $out 'PlaybackTests.exe'
& $compiler /nologo /codepage:65001 /target:exe "/out:$exe" (Join-Path $PSScriptRoot 'PlaybackMath.cs') (Join-Path $PSScriptRoot 'PlaybackTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Playback tests compilation failed' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Playback timeline tests failed' }
& (Join-Path $PSScriptRoot 'Test-PlaybackTransport.ps1')
$gainExe = Join-Path $out 'AudioGainTests.exe'
& $compiler /nologo /codepage:65001 /target:exe "/out:$gainExe" (Join-Path $PSScriptRoot 'PlaybackMath.cs') (Join-Path $PSScriptRoot 'AudioGainProcessor.cs') (Join-Path $PSScriptRoot 'AudioGainTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Audio gain tests compilation failed' }
& $gainExe
if ($LASTEXITCODE -ne 0) { throw 'Audio gain tests failed' }
& (Join-Path $PSScriptRoot 'Test-MusicDucking.ps1') -GameDirectory $GameDirectory
& (Join-Path $PSScriptRoot 'Test-Network.ps1')
& (Join-Path $PSScriptRoot 'Test-SteamTransport.ps1')
& (Join-Path $PSScriptRoot 'Test-SteamSockets.ps1')
& (Join-Path $PSScriptRoot 'Test-Recovery.ps1')
& (Join-Path $PSScriptRoot 'Test-Portable.ps1')
& (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory -OutputFile (Join-Path $out 'NordicRadio.dll')
& (Join-Path $PSScriptRoot 'Test-UI.ps1') -GameDirectory $GameDirectory
& (Join-Path $PSScriptRoot 'Test-Model.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $out 'NordicRadio.dll') -ExportPath (Join-Path $out 'model.json')
& (Join-Path $PSScriptRoot 'Test-PortableModel.ps1') -GameDirectory $GameDirectory -PluginAssembly (Join-Path $out 'NordicRadio.dll')
Write-Output 'Full plugin compiled against installed Valheim and packaged Jotunn. This does not test rendering or multiplayer gameplay.'
