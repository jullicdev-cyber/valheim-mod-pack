$ErrorActionPreference = 'Stop'
$personalTestRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$personalTestOut = Join-Path $personalTestRoot '.cache/nordic-personal-audio-tests'
New-Item -ItemType Directory -Path $personalTestOut -Force | Out-Null
$personalTestExe = Join-Path $personalTestOut 'PersonalAudioTests.exe'
$personalTestArgs = @('/nologo','/codepage:65001','/target:exe','/nowarn:0067,0649',('/out:' + $personalTestExe))
$personalTestArgs += @('PersonalAudioControls.cs','PersonalShortcutCapture.cs','PersonalGameplayInputCache.cs','PersonalAudioTestDoubles.cs','PersonalAudioTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') @personalTestArgs
if ($LASTEXITCODE -ne 0) { throw 'Personal audio hotkey tests compilation failed' }
& $personalTestExe
if ($LASTEXITCODE -ne 0) { throw 'Personal audio hotkey regression failed' }
