[CmdletBinding()]
param([string]$GameDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not $GameDirectory) {
    $running = Get-Process -Name valheim -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running -and $running.Path) { $GameDirectory = Split-Path $running.Path -Parent }
    else { $GameDirectory = Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/Valheim' }
}
if (-not (Test-Path -LiteralPath $GameDirectory -PathType Container)) { throw 'Specify -GameDirectory with the Valheim installation folder.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Valheim-Diagnostics' }
$folder = Join-Path $OutputDirectory ('Valheim-logs-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$entries = New-Object 'System.Collections.Generic.List[object]'
$sources = @(
    @{Path=(Join-Path $GameDirectory 'BepInEx/LogOutput.log'); Name='LogOutput.log'},
    @{Path=(Join-Path $env:USERPROFILE 'AppData/LocalLow/IronGate/Valheim/Player.log'); Name='Player.log'},
    @{Path=(Join-Path $env:USERPROFILE 'AppData/LocalLow/IronGate/Valheim/Player-prev.log'); Name='Player-prev.log'}
)
foreach ($source in $sources) {
    if (-not (Test-Path -LiteralPath $source.Path -PathType Leaf)) {
        $entries.Add([pscustomobject]@{name=$source.Name;status='missing'})
        continue
    }
    # Bound the read to the opening length: a live log may keep growing.
    $inputStream = [IO.File]::Open($source.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $outputStream = $null
    try {
        $remaining = $inputStream.Length
        $outputStream = [IO.File]::Open((Join-Path $folder $source.Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] 65536
        while ($remaining -gt 0) {
            $read = $inputStream.Read($buffer, 0, [int][Math]::Min($remaining, $buffer.Length))
            if ($read -eq 0) { break }
            $outputStream.Write($buffer, 0, $read)
            $remaining -= $read
        }
        $entries.Add([pscustomobject]@{name=$source.Name;status='copied';bytes=$outputStream.Length;sourceWriteUtc=(Get-Item -LiteralPath $source.Path).LastWriteTimeUtc.ToString('o')})
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        $inputStream.Dispose()
    }
}
if (@($entries | Where-Object status -eq 'copied').Count -eq 0) { throw 'No Valheim logs found. Check -GameDirectory.' }
$plugins = @()
$pluginRoot = Join-Path $GameDirectory 'BepInEx/plugins'
if (Test-Path -LiteralPath $pluginRoot -PathType Container) {
    $pluginRoot = [IO.Path]::GetFullPath($pluginRoot)
    $plugins = @(Get-ChildItem -LiteralPath $pluginRoot -Filter '*.dll' -File -Recurse | ForEach-Object {
        [pscustomobject]@{file=$_.FullName.Substring($pluginRoot.Length).TrimStart('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
$processes = @(Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue | ForEach-Object {
    [pscustomobject]@{name=$_.ProcessName;startedUtc=$_.StartTime.ToUniversalTime().ToString('o');workingSetBytes=$_.WorkingSet64}
})
$report = [pscustomobject]@{capturedUtc=[DateTime]::UtcNow.ToString('o');utcOffsetMinutes=[TimeZoneInfo]::Local.GetUtcOffset([DateTime]::Now).TotalMinutes;logs=$entries.ToArray();processes=$processes;plugins=$plugins}
[IO.File]::WriteAllText((Join-Path $folder 'diagnostics.json'), ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$archive = $folder + '.zip'
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $folder -File | ForEach-Object FullName) -DestinationPath $archive
Write-Output ('Logs collected: ' + $archive)
Write-Output 'The game was not stopped. Saves, worlds, characters and configuration files were not copied or modified.'
