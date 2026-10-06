[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/party-prison-withdrawal-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$runner = Join-Path $fixtureDirectory 'WithdrawalPolicyTests.exe'
$sources = @('SentenceState.cs','CustodyStore.cs','CustodyWithdrawal.cs','WithdrawalPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe') /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison withdrawal policy test build failed.' }
& $runner (Join-Path $fixtureDirectory 'state')
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison withdrawal durability checks failed.' }
