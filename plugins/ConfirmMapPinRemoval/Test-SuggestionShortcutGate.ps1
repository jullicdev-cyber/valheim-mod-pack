[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $root '.cache/suggestion-shortcut-tests' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output=Join-Path $OutputDirectory 'SuggestionShortcutGateTests.exe'
$arguments=@('/nologo','/codepage:65001','/langversion:4','/target:exe','/optimize+',('/out:'+$output))
$arguments+=@('SuggestionShortcutGate.cs','MapControls.cs','SuggestionShortcutGateTestDoubles.cs','SuggestionShortcutGateTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if($LASTEXITCODE -ne 0) { throw 'Suggestion shortcut gate test compilation failed.' }
& $output
if($LASTEXITCODE -ne 0) { throw 'Suggestion shortcut gate regression failed.' }
