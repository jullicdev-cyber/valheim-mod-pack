[CmdletBinding()]
param([string]$GameDirectory, [switch]$DownloadOnly, [string]$MusicUrl, [switch]$SkipMusic)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')
. (Join-Path $PSScriptRoot 'Download-Pack.ps1')
. (Join-Path $PSScriptRoot 'Sync-Pack.ps1')
$script:PackDownloadJobs = [Collections.Generic.List[string]]::new()
$synced = $false
try {
    $target = $null
    if (-not $DownloadOnly) {
        $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory
        if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before updating, or use -DownloadOnly.' }
    }
    $pack = Sync-PackFolder $root
    $synced = $true
    if ($DownloadOnly) { Write-Host 'Pack folder updated. Game files were not changed.'; return }
    # A fresh process preserves the downloaded installer's failure exit code.
    $installArgs = @('-GameDirectory', $target, '-SettingsDirectory', $root)
    if ($MusicUrl) { $installArgs += @('-MusicUrl', $MusicUrl) }
    if ($SkipMusic) { $installArgs += '-SkipMusic' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pack 'scripts/Install-Windows.ps1') @installArgs
    if ($LASTEXITCODE -ne 0) { throw 'Installation failed. The updated pack remains in this folder; retry Install-Windows.cmd.' }
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
finally {
    $cleanupDownloads = $true
    $latest = Join-Path $root '.updates/latest.json'
    if ($script:PackDownloadJobs.Count -gt 0 -and (Test-Path -LiteralPath $latest)) {
        try {
            if ((Get-Item -LiteralPath $latest -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked download record refused.' }
            $record = Get-Content -LiteralPath $latest -Raw -Encoding UTF8 | ConvertFrom-Json
            $owned = @($script:PackDownloadJobs | Where-Object { $record.directory -eq (Join-Path $_ 'pack') }).Count -gt 0
            if ($owned) {
                if ($synced) {
                    $recordPath = Join-Path (Split-Path $record.directory -Parent) 'latest.json'
                    $record.directory = $root
                    $record | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding UTF8
                    [IO.File]::Replace($recordPath, $latest, [NullString]::Value)
                }
                else { Remove-Item -LiteralPath $latest -Force }
            }
        } catch {
            $cleanupDownloads = $false
            Write-Warning "Could not update download bookkeeping: $_. Temporary downloads retained to keep the previous record valid."
        }
    }
    if ($cleanupDownloads) {
        foreach ($job in $script:PackDownloadJobs) {
            try { Remove-PackDownloadJob $root $job }
            catch { Write-Warning "Could not remove temporary download $job`: $_" }
        }
    }
}
