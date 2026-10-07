[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly,
    [switch]$BuildOnly,
    [switch]$WithGraphics
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/PartyPrison.dll' }
$PluginAssembly = (Resolve-Path -LiteralPath $PluginAssembly).ProviderPath
if (-not $BuildOnly -and (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue)) { throw 'Close Valheim before the isolated native test.' }
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $root ('.cache/partyprison-native-' + [Guid]::NewGuid().ToString('N').Substring(0,8))))
$cacheRoot = [IO.Path]::GetFullPath((Join-Path $root '.cache'))
if (-not $fixtureRoot.StartsWith($cacheRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture must stay within the workspace cache.' }
New-Item -ItemType Directory -Force -Path $fixtureRoot,(Join-Path $fixtureRoot 'Saves') | Out-Null
# Keep the complete actual pack, including vendor helper assemblies and assets.
Copy-Item -LiteralPath (Join-Path $root 'Game/BepInEx') -Destination (Join-Path $fixtureRoot 'BepInEx') -Recurse
$fixturePlugins = Join-Path $fixtureRoot 'BepInEx/plugins'
foreach ($built in Get-ChildItem -LiteralPath (Join-Path $root 'local-plugins') -Filter '*.dll' -File) {
    foreach ($installed in Get-ChildItem -LiteralPath $fixturePlugins -Filter $built.Name -File -Recurse) {
        Copy-Item -LiteralPath $built.FullName -Destination $installed.FullName -Force
    }
}
$existingPrison = @(Get-ChildItem -LiteralPath $fixturePlugins -Filter 'PartyPrison.dll' -File -Recurse)
if ($existingPrison.Count -gt 1) { throw 'Duplicate PartyPrison assemblies in the isolated pack.' }
$prisonTarget = if ($existingPrison.Count -eq 1) { $existingPrison[0].FullName } else { Join-Path $fixturePlugins 'PartyPrison.dll' }
Copy-Item -LiteralPath $PluginAssembly -Destination $prisonTarget -Force
$testedPrisonHash = (Get-FileHash -LiteralPath $prisonTarget -Algorithm SHA256).Hash
# Freeze the probe source beside its result so parallel source edits cannot
# change which custody/UI assertions this particular fixture was built from.
$probeSource = Join-Path $fixtureRoot 'NativeChecks.cs'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeChecks.cs') -Destination $probeSource
$probeSourceHash = (Get-FileHash -LiteralPath $probeSource -Algorithm SHA256).Hash
$probeHelpers = @('CombatNativeChecks.cs','LayoutNativeChecks.cs','RecoveryNativeChecks.cs','DeathNativeChecks.cs','KitStorageNativeChecks.cs','CustodyInventoryNativeChecks.cs','RandomSpawnNativeChecks.cs','WaveRuntimeNativeChecks.cs') | ForEach-Object {
    $frozen = Join-Path $fixtureRoot $_
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $_) -Destination $frozen
    $frozen
}
[IO.File]::WriteAllLines((Join-Path $fixtureRoot 'probe-inputs.txt'), @(
    'PartyPrison SHA256: ' + $testedPrisonHash
    'NativeChecks.cs SHA256: ' + $probeSourceHash
    'Scope: menu startup, native patches, safe native Chat/Console command routing, detached inventories, synthetic UI and prefab definitions'
), [Text.UTF8Encoding]::new($false))
foreach ($helper in $probeHelpers) {
    [IO.File]::AppendAllText((Join-Path $fixtureRoot 'probe-inputs.txt'),
        ([IO.Path]::GetFileName($helper) + ' SHA256: ' + (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
}
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
WriteUnityLog = true
LogLevels = All
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'BepInEx/config/BepInEx.cfg') -Encoding UTF8

# Build an exact expected GUID list from copied BepInPlugin attributes. This
# catches a silently omitted/incompatible mod without maintaining a stale list.
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll'))
$expectedPlugins = New-Object 'System.Collections.Generic.List[string]'
$expectedGuids = New-Object 'System.Collections.Generic.HashSet[string]'
function Add-PluginTypes($types, [string]$sourceName) {
    foreach ($type in $types) {
        foreach ($attribute in $type.CustomAttributes) {
            if ($attribute.AttributeType.FullName -ne 'BepInEx.BepInPlugin') { continue }
            $guid = [string]$attribute.ConstructorArguments[0].Value
            if (-not $expectedGuids.Add($guid)) { throw "Duplicate BepInPlugin GUID in actual pack: $guid" }
            $expectedPlugins.Add($guid + "`t" + $sourceName)
        }
        if ($type.HasNestedTypes) { Add-PluginTypes $type.NestedTypes $sourceName }
    }
}
foreach ($dll in Get-ChildItem -LiteralPath $fixturePlugins -Filter '*.dll' -File -Recurse) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName)
    try { Add-PluginTypes $assembly.MainModule.Types $dll.Name } finally { $assembly.Dispose() }
}
[IO.File]::WriteAllLines((Join-Path $fixtureRoot 'expected-plugins.txt'), $expectedPlugins, [Text.UTF8Encoding]::new($false))

$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
$references = @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/core/0Harmony.dll','Game/BepInEx/plugins/Jotunn.dll','local-plugins/WorldCharacters.dll') | ForEach-Object { Join-Path $root $_ }
$references += $PluginAssembly
$references += @('assembly_valheim.dll','assembly_guiutils.dll','assembly_utils.dll','SoftReferenceableAssets.dll','Splatform.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.PhysicsModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Native reference missing: $reference" } }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$compileArgs = @('/nologo','/codepage:65001','/target:library',('/out:' + (Join-Path $fixturePlugins 'PartyPrisonNativeProbe.dll')))
$compileArgs += $references | ForEach-Object { '/reference:' + $_ }
$compileArgs += $probeSource
$compileArgs += $probeHelpers
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Party Prison native probe compilation failed.' }
if ($BuildOnly) { Write-Output "Native probe compiled with the complete pack, not launched: $fixtureRoot"; return }

$previousRoot = $env:VMP_PARTYPRISON_PROBE
$process = $null
try {
    $env:VMP_PARTYPRISON_PROBE = $fixtureRoot
    if (Get-Process -Name valheim,valheim_server -ErrorAction SilentlyContinue) { throw 'Valheim started during fixture setup.' }
    $arguments = '--doorstop-target-assembly "{0}" -batchmode -nographics -logFile "{1}" -savedir "{2}"' -f (Join-Path $fixtureRoot 'BepInEx/core/BepInEx.Preloader.dll'),(Join-Path $fixtureRoot 'Unity.log'),(Join-Path $fixtureRoot 'Saves')
    if ($WithGraphics) { $arguments = $arguments.Replace('-batchmode -nographics', '-screen-fullscreen 0 -screen-width 1280 -screen-height 720') }
    $process = Start-Process -FilePath (Join-Path $GameDirectory 'valheim.exe') -ArgumentList $arguments -WorkingDirectory $fixtureRoot -WindowStyle Hidden -PassThru
    Write-Output "Party Prison native probe PID $($process.Id); isolated directory: $fixtureRoot"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $process.WaitForExit(1000)) {
        if ($timer.Elapsed.TotalSeconds -gt 90) { $process.Kill(); throw "Native probe exceeded 90 seconds. Inspect $fixtureRoot/Unity.log" }
    }
    $resultFile = Join-Path $fixtureRoot 'result.txt'
    if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf)) { throw "No native result. Inspect $fixtureRoot/Unity.log and BepInEx/LogOutput.log" }
    $result = [IO.File]::ReadAllText($resultFile)
    Write-Output $result
    if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS') -or $result.Contains('FAIL:')) { throw 'Party Prison native verification failed.' }
    if ((Get-FileHash -LiteralPath $prisonTarget -Algorithm SHA256).Hash -ne $testedPrisonHash) { throw 'PartyPrison changed inside the fixture during verification.' }
    Write-Output ('Tested PartyPrison SHA256: ' + $testedPrisonHash)
    Write-Output ('Native assertion source SHA256: ' + $probeSourceHash)
    Write-Output ('Native result retained: ' + $resultFile)
    Write-Output ('Actual pack expected plugin count: ' + $expectedPlugins.Count)
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    $env:VMP_PARTYPRISON_PROBE = $previousRoot
}
