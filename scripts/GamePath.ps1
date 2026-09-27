function Read-PackSettings([string]$Directory) {
    $file = Join-Path $Directory 'local-settings.json'
    $settings = @{}
    if (Test-Path -LiteralPath $file) {
        if ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Settings file must not be a link.' }
        $value = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $value -or $value -is [array] -or $value -is [string]) { throw 'Invalid local-settings.json. Fix or rename it before continuing.' }
        foreach ($property in $value.PSObject.Properties) { $settings[$property.Name] = $property.Value }
    }
    return $settings
}

function Save-GameDirectory([string]$SettingsDirectory, [string]$GameDirectory) {
    $settings = Read-PackSettings $SettingsDirectory
    $settings['windowsGameDirectory'] = $GameDirectory
    New-Item -ItemType Directory -Path $SettingsDirectory -Force | Out-Null
    $file = Join-Path $SettingsDirectory 'local-settings.json'
    [IO.File]::WriteAllText($file, (($settings | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}

function Get-GameDirectory {
    param([string]$PackRoot, [string]$GameDirectory, [string]$SettingsDirectory, [switch]$AskAgain)
    if (-not $SettingsDirectory) { $SettingsDirectory = $PackRoot }
    if (-not $GameDirectory -and -not $AskAgain) {
        $saved = (Read-PackSettings $SettingsDirectory)['windowsGameDirectory']
        if ($saved -is [string] -and $saved) {
            if (Test-Path -LiteralPath (Join-Path $saved 'valheim.exe') -PathType Leaf) {
                Write-Host "Using saved game directory: $saved"
                $GameDirectory = $saved
            } else { Write-Host 'Saved game directory no longer exists. Enter its new location.' }
        }
    }
    if (-not $GameDirectory) { $GameDirectory = Read-Host 'Valheim directory (contains valheim.exe)' }
    $GameDirectory = $GameDirectory.Trim()
    while ($GameDirectory -match '^Valheim directory \(contains valheim\.exe\):\s*') {
        $GameDirectory = $GameDirectory.Substring($Matches[0].Length).Trim()
    }
    $GameDirectory = $GameDirectory.Trim().Trim('"')
    if (-not $GameDirectory) { throw 'No directory specified.' }
    $target = (Resolve-Path -LiteralPath $GameDirectory -ErrorAction Stop).ProviderPath
    if (-not (Test-Path -LiteralPath (Join-Path $target 'valheim.exe') -PathType Leaf)) { throw 'valheim.exe not found. Select the game directory.' }
    $pack = [IO.Path]::GetFullPath($PackRoot).TrimEnd('\','/')
    $game = $target.TrimEnd('\','/')
    if ($game -eq $pack -or $game.StartsWith($pack + '\', [StringComparison]::OrdinalIgnoreCase) -or $pack.StartsWith($game + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep the pack repository and game directory separate.'
    }
    Save-GameDirectory $SettingsDirectory $target
    return $target
}
