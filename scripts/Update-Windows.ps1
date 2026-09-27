[CmdletBinding()]
param([string]$GameDirectory, [switch]$DownloadOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')
. (Join-Path $PSScriptRoot 'Download-Pack.ps1')
. (Join-Path $PSScriptRoot 'Sync-Pack.ps1')
try {
    $target = $null
    if (-not $DownloadOnly) {
        $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory
        if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Close Valheim before updating, or use -DownloadOnly.' }
    }
    $pack = Sync-PackFolder $root
    if ($DownloadOnly) { Write-Host 'Pack folder updated. Game files were not changed.'; return }
    # A fresh process preserves the downloaded installer's failure exit code.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $pack 'scripts/Install-Windows.ps1') -GameDirectory $target -SettingsDirectory $root
    if ($LASTEXITCODE -ne 0) { throw "Installation failed. Download retained at $pack" }
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
