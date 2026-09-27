[CmdletBinding()]
param([string]$GameDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GamePath.ps1')
try {
    $root = Split-Path $PSScriptRoot -Parent
    $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory -AskAgain
    Write-Host "Saved: $target"
    Write-Host "Settings: $(Join-Path $root 'local-settings.json')"
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
