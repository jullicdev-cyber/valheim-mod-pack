param([string]$Repository, [string]$Root, [string]$Remote, [string]$Snapshot, [string]$Mode)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/Download-Pack.ps1')
. (Join-Path $Repository 'scripts/Sync-Pack.ps1')
function Get-LatestPack([string]$PackRoot) { return $Snapshot }
if ($Mode -eq 'rollback') {
    $script:OriginalMove = (Get-Command Move-PackEntry).ScriptBlock
    $script:Injected = $false
    function Move-PackEntry([string]$From, [string]$To, [string]$Root) {
        if (-not $script:Injected -and $From -match '[\\/]staged[\\/]Game$') {
            $script:Injected = $true
            throw 'Injected file replacement failure.'
        }
        & $script:OriginalMove $From $To $Root
    }
}
try {
    $gitPath = if ($Mode -in @('zip','rollback')) { '' } else { 'auto' }
    $result = Sync-PackFolder -Root $Root -Git $gitPath -Repository $Remote
    if ($result -isnot [string] -or $result -ne $Root) { throw 'Updater returned extra pipeline data.' }
    Write-Host 'PASS: folder update completed.'
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
