$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out=Join-Path $root '.cache/chest-hotkey-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$runner=Join-Path $out 'Hotkeys.exe'
$argsList=@('/nologo','/target:exe','/codepage:65001','/nowarn:0067,0649',('/out:'+$runner))
$argsList+=@('Plugin.cs','GameplayInputCache.cs','HotkeyTestDoubles.cs','HotkeyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'ShortcutCapture.cs')) { $argsList+=Join-Path $PSScriptRoot 'ShortcutCapture.cs' }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @argsList
if($LASTEXITCODE -ne 0) { throw 'Hotkey test compilation failed.' }
& $runner
if($LASTEXITCODE -ne 0) { throw 'Hotkey regression tests failed.' }
