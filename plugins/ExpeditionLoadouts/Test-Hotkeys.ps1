$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$output = Join-Path $root '.cache/expedition-hotkey-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$runner = Join-Path $output 'Hotkeys.exe'
$argsList = @('/nologo','/target:exe','/langversion:4','/codepage:65001','/nowarn:0067,0649',('/out:' + $runner))
$argsList += @('Plugin.cs','ShortcutCapture.cs','GameplayInputCache.cs','HotkeyTestDoubles.cs','HotkeyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'ExpeditionLoadouts hotkey test compilation failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'ExpeditionLoadouts hotkey regression tests failed.' }
