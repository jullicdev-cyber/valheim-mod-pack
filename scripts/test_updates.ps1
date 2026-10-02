param([string]$Repository, [string]$Fixture, [string]$Commit)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/GamePath.ps1')
. (Join-Path $Repository 'scripts/Download-Pack.ps1')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$packRoot = Join-Path $Fixture 'pack'
$game = (Get-ChildItem -LiteralPath $Fixture -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'valheim.exe') }).FullName
$pasted = 'Valheim directory (contains valheim.exe): Valheim directory (contains valheim.exe): "' + $game + '"'
$saved = Get-GameDirectory -PackRoot $packRoot -GameDirectory $pasted
Assert ($saved -eq $game) 'Pasted path not normalized.'
function Read-Host { throw 'Unexpected prompt; saved path should be reused.' }
Assert ((Get-GameDirectory -PackRoot $packRoot) -eq $game) 'Saved path not reused.'
$before = [IO.File]::ReadAllText((Join-Path $packRoot 'local-settings.json'))
$failed = $false
try { Get-GameDirectory -PackRoot $packRoot -GameDirectory $packRoot } catch { $failed = $true }
Assert $failed 'Invalid game directory accepted.'
Assert ([IO.File]::ReadAllText((Join-Path $packRoot 'local-settings.json')) -eq $before) 'Invalid path overwrote settings.'
$pack = Expand-PackArchive (Join-Path $Fixture 'good.zip') (Join-Path $Fixture 'windows-extracted') $Commit
& (Join-Path $Repository 'scripts/Verify.ps1') -PackDirectory (Join-Path $pack 'Game') -ManifestDirectory $pack
$failed = $false
try { Expand-PackArchive (Join-Path $Fixture 'escape.zip') (Join-Path $Fixture 'windows-unsafe') $Commit } catch { $failed = $true }
Assert $failed 'Archive traversal accepted.'
Assert (-not (Test-Path -LiteralPath (Join-Path $Fixture 'windows-unsafe'))) 'Unsafe archive partially extracted.'
$badPack = Expand-PackArchive (Join-Path $Fixture 'bad.zip') (Join-Path $Fixture 'windows-bad') $Commit
$failed = $false
try { & (Join-Path $Repository 'scripts/Verify.ps1') -PackDirectory (Join-Path $badPack 'Game') -ManifestDirectory $badPack } catch { $failed = $true }
Assert $failed 'Bad payload hash accepted.'
# Exercise the real download workflow with deterministic network responses.
function Invoke-RestMethod { param($Uri,$Headers,$TimeoutSec); return @{sha=$Commit} }
function Save-PackArchive { param($Uri,$OutFile); Copy-Item -LiteralPath (Join-Path $Fixture 'good.zip') -Destination $OutFile }
$download = Get-LatestPack $packRoot
Assert ($download -is [string] -and (Test-Path -LiteralPath (Join-Path $download 'VERSION'))) 'Downloader returned extra pipeline output.'
Assert (Test-Path -LiteralPath (Join-Path $packRoot '.updates/latest.json')) 'Verified snapshot was not recorded.'
$last = [IO.File]::ReadAllText((Join-Path $packRoot '.updates/latest.json'))
$firstJob = Split-Path $download -Parent
function Save-PackArchive { param($Uri,$OutFile); Copy-Item -LiteralPath (Join-Path $Fixture 'bad.zip') -Destination $OutFile }
$jobsBefore = @(Get-ChildItem -LiteralPath (Join-Path $packRoot '.updates') -Directory).Count
$failed = $false
try { Get-LatestPack $packRoot } catch { $failed = $true }
Assert $failed 'Corrupt download accepted.'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $packRoot '.updates') -Directory).Count -eq $jobsBefore) 'Failed download left temporary data.'
Assert ([IO.File]::ReadAllText((Join-Path $packRoot '.updates/latest.json')) -eq $last) 'Failed download replaced previous record.'
function Save-PackArchive { param($Uri,$OutFile); Copy-Item -LiteralPath (Join-Path $Fixture 'good.zip') -Destination $OutFile }
$current = Get-LatestPack $packRoot
Remove-PackDownloadJob $packRoot (Split-Path $current -Parent)
Assert (-not (Test-Path -LiteralPath $current)) 'Current download job was not cleaned.'
Assert (Test-Path -LiteralPath $download) 'Cleanup removed a previous archive.'
$unrelated = Join-Path $packRoot 'personal'
New-Item -ItemType Directory -Path $unrelated | Out-Null
$failed = $false
try { Remove-PackDownloadJob $packRoot $unrelated } catch { $failed = $true }
Assert ($failed -and (Test-Path -LiteralPath $unrelated)) 'Cleanup accepted an unrelated directory.'
Remove-PackDownloadJob $packRoot $firstJob
Write-Output 'OK: Windows saved paths, safe extraction, payload verification and temporary download cleanup.'
