param([string]$Repository, [string]$Root, [string]$Remote, [string]$Snapshot, [string]$Mode)
$ErrorActionPreference = 'Stop'
. (Join-Path $Repository 'scripts/Download-Pack.ps1')
. (Join-Path $Repository 'scripts/Sync-Pack.ps1')
function Get-LatestPack([string]$PackRoot) { return $Snapshot }
if ($Mode -in @('rollback','rollback-failed','interrupt')) {
    $script:OriginalMove = (Get-Command Move-PackEntry).ScriptBlock
    $script:Injected = $false
    function Move-PackEntry([string]$From, [string]$To, [string]$Root) {
        if (-not $script:Injected -and $From -match '[\\/]staged[\\/]Game$') {
            $script:Injected = $true
            throw 'Injected file replacement failure.'
        }
        if ($Mode -eq 'rollback-failed' -and $From -match '[\\/]original[\\/]Game$') { throw 'Injected rollback failure.' }
        & $script:OriginalMove $From $To $Root
    }
}
if ($Mode -in @('pull-verify-failed','pull-rollback-failed')) {
    $script:OriginalVerify = (Get-Command Test-UpdatePack).ScriptBlock
    function Test-UpdatePack([string]$Pack) {
        if ($Pack -eq $Root) { throw 'Injected verification failure after Git pull.' }
        & $script:OriginalVerify $Pack
    }
    if ($Mode -eq 'pull-rollback-failed') {
        $script:OriginalGit = (Get-Command Invoke-PackGit).ScriptBlock
        function Invoke-PackGit([string]$Git, [string]$Root, [string[]]$Arguments) {
            if ($Arguments[0] -eq 'reset') { throw 'Injected Git rollback failure.' }
            & $script:OriginalGit $Git $Root $Arguments
        }
    }
}
try {
    $gitPath = if ($Mode -in @('zip','rollback','rollback-failed','interrupt')) { '' } else { 'auto' }
    $result = Sync-PackFolder -Root $Root -Git $gitPath -Repository $Remote
    if ($result -isnot [string] -or $result -ne $Root) { throw 'Updater returned extra pipeline data.' }
    Write-Host 'PASS: folder update completed.'
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
