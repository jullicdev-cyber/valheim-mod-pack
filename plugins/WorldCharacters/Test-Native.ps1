[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [switch]$BuildOnly, [string]$PluginAssembly, [switch]$WithGraphics)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $BuildOnly -and (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue)) { throw 'Close Valheim before native verification.' }
if (-not $PluginAssembly) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -GameDirectory $GameDirectory
    $PluginAssembly = Join-Path $root 'local-plugins/WorldCharacters.dll'
}
$PluginAssembly = (Resolve-Path -LiteralPath $PluginAssembly).ProviderPath
$smoke = Join-Path $root ('.cache/worldcharacters-native-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $smoke,(Join-Path $smoke 'Saves') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx') -Destination (Join-Path $smoke 'BepInEx') -Recurse
$target = Join-Path $smoke 'BepInEx/plugins/ValheimModPack-WorldCharacters'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -LiteralPath $PluginAssembly -Destination (Join-Path $target 'WorldCharacters.dll')
$testedHash = (Get-FileHash -LiteralPath (Join-Path $target 'WorldCharacters.dll') -Algorithm SHA256).Hash
$probeSources = @('NativeProbe.cs','AdministrationUiNativeChecks.cs','AdministrationInputNativeChecks.cs') | ForEach-Object {
    $copy = Join-Path $smoke $_
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $_) -Destination $copy
    $copy
}
[IO.File]::WriteAllLines((Join-Path $smoke 'probe-inputs.txt'), @('WorldCharacters SHA256: ' + $testedHash) + @($probeSources | ForEach-Object { (Split-Path $_ -Leaf) + ' SHA256: ' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }), [Text.UTF8Encoding]::new($false))
# Compare the exact plugins copied to this fixture with the native loader result.
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll'))
$expected = New-Object 'System.Collections.Generic.List[string]'
$guids = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $smoke 'BepInEx/plugins') -Filter '*.dll' -File -Recurse) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName)
    try {
        foreach ($type in $assembly.MainModule.GetTypes()) {
            foreach ($attribute in $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' }) {
                $guid = [string]$attribute.ConstructorArguments[0].Value
                if (-not $guids.Add($guid)) { throw "Duplicate plugin GUID in native fixture: $guid" }
                $expected.Add($guid + "`t" + [string]$attribute.ConstructorArguments[2].Value)
            }
        }
    } finally { $assembly.Dispose() }
}
[IO.File]::WriteAllLines((Join-Path $smoke 'expected-plugins.txt'), $expected, [Text.UTF8Encoding]::new($false))
$refs = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll') | ForEach-Object { Join-Path $root $_ }
$refs += $PluginAssembly
$refs += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.ImageConversionModule.dll','netstandard.dll') | ForEach-Object { Join-Path $GameDirectory ('valheim_Data/Managed/'+$_) }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$argsList = @('/nologo','/codepage:65001','/target:library',('/out:'+(Join-Path $smoke 'BepInEx/plugins/WorldCharactersProbe.dll')))
$argsList += $refs | ForEach-Object { '/reference:'+$_ }; $argsList += $probeSources
& $compiler @argsList
if ($LASTEXITCODE -ne 0) { throw 'Native probe compilation failed.' }
if ($BuildOnly) { Write-Output "Native probe compiled, not executed: $smoke"; return }
$previous = $env:VMP_WORLDCHARACTERS_PROBE
$previousGraphics = $env:VMP_WORLDCHARACTERS_GRAPHICS
$process = $null
try {
    $env:VMP_WORLDCHARACTERS_PROBE = $smoke
    $env:VMP_WORLDCHARACTERS_GRAPHICS = if ($WithGraphics) { '1' } else { '0' }
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $smoke 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $smoke 'Unity.log'),(Join-Path $smoke 'Saves')
    if ($WithGraphics) { $arguments = $arguments.Replace('-batchmode -nographics', '-screen-fullscreen 0 -screen-width 1280 -screen-height 720') }
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during test setup.' }
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $smoke -WindowStyle Hidden -PassThru
    Write-Output "Native probe PID $($process.Id), isolated directory: $smoke"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) { if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill(); throw 'Native probe timed out.' } }
    $resultFile = Join-Path $smoke 'result.txt'
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No result; inspect $smoke/Unity.log" }
    $result = [IO.File]::ReadAllText($resultFile); Write-Output $result
    if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS')) { throw 'Native probe failed.' }
    if ((Get-FileHash -LiteralPath (Join-Path $target 'WorldCharacters.dll') -Algorithm SHA256).Hash -ne $testedHash) { throw 'Native fixture DLL changed during verification.' }
    Write-Output "Tested WorldCharacters SHA256: $testedHash"
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:VMP_WORLDCHARACTERS_PROBE = $previous
    $env:VMP_WORLDCHARACTERS_GRAPHICS = $previousGraphics
}
