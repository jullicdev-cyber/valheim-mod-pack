[CmdletBinding()]
param([string]$GameDirectory, [string]$Url)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GamePath.ps1')
. (Join-Path $PSScriptRoot 'Music.ps1')
try {
    $target = Get-GameDirectory -PackRoot $root -GameDirectory $GameDirectory
    if (-not $Url) { $Url = (Get-Content -LiteralPath (Join-Path $root 'music-source.txt') -Raw).Trim() }
    Install-RadioMusic $target $Url
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
