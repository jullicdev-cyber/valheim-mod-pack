[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$managed = Join-Path ([IO.Path]::GetFullPath($GameDirectory)) 'valheim_Data/Managed'
if (-not (Test-Path -LiteralPath $managed -PathType Container)) { throw "Valheim managed directory was not found: $managed" }
if (-not $OutputFile) { $OutputFile = Join-Path $packRoot 'local-plugins/XPortal.dll' }
$OutputFile = [IO.Path]::GetFullPath($OutputFile)

# XPortal's source calls internal Valheim APIs. Build against isolated visibility-adjusted
# references; the real game assemblies must never be replaced with these copies.
$buildCache = Join-Path $packRoot '.cache/anyportal-build'
$publicized = Join-Path $buildCache 'publicized'
New-Item -ItemType Directory -Force -Path $buildCache, $publicized | Out-Null
$archive = Join-Path $buildCache 'compilers.zip'
$compilerDirectory = Join-Path $buildCache 'compiler'
$compiler = Join-Path $compilerDirectory 'tasks/net472/csc.exe'
$compilerUrl = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.12.0/microsoft.net.compilers.toolset.4.12.0.nupkg'
$compilerSha256 = 'fe24ef31a6ffcb7c49383d2fd362763dee291ad9b9d98cc0c19ef80203b99ebc'
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    $download = Join-Path $buildCache ('compiler-download-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $compilerUrl -OutFile $download -UseBasicParsing
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $compilerSha256) { throw 'Pinned Roslyn compiler archive hash mismatch.' }
        Move-Item -LiteralPath $download -Destination $archive
    }
    finally {
        if (Test-Path -LiteralPath $download -PathType Leaf) { Remove-Item -LiteralPath $download -Force }
    }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $compilerSha256) {
    throw "Pinned Roslyn compiler archive hash mismatch: $archive"
}
# Validate every extracted package file before reuse. The stamp avoids reading the ZIP
# again after the initial verified extraction, while still detecting changed dependencies.
$compilerStamp = Join-Path $compilerDirectory '.verified-sha256.json'
$compilerPrefix = [IO.Path]::GetFullPath($compilerDirectory) + [IO.Path]::DirectorySeparatorChar
function Test-CompilerManifest($Manifest) {
    try {
        if ($null -eq $Manifest -or $Manifest.Version -ne 1 -or $Manifest.ArchiveSha256 -ne $compilerSha256 -or @($Manifest.Files).Count -eq 0) { return $false }
        foreach ($file in $Manifest.Files) {
            if ([string]::IsNullOrEmpty($file.Path) -or $file.Sha256 -notmatch '^[0-9a-f]{64}$') { return $false }
            $resolved = [IO.Path]::GetFullPath((Join-Path $compilerDirectory $file.Path))
            if (-not $resolved.StartsWith($compilerPrefix, [StringComparison]::OrdinalIgnoreCase)) { return $false }
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $file.Sha256) { return $false }
        }
        return $true
    }
    catch { return $false }
}
function Get-CompilerManifest {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    $files = @()
    try {
        foreach ($entry in $zip.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path $compilerDirectory $entry.FullName))
            if (-not $resolved.StartsWith($compilerPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe path in pinned compiler archive.' }
            $hasher = [Security.Cryptography.SHA256]::Create()
            $entryStream = $entry.Open()
            try { $digest = [BitConverter]::ToString($hasher.ComputeHash($entryStream)).Replace('-', '').ToLowerInvariant() }
            finally { $entryStream.Dispose(); $hasher.Dispose() }
            $files += [pscustomobject]@{ Path = $entry.FullName; Sha256 = $digest }
        }
    }
    finally { $zip.Dispose() }
    return [pscustomobject]@{ Version = 1; ArchiveSha256 = $compilerSha256; Files = $files }
}
$manifest = $null
if (Test-Path -LiteralPath $compilerStamp -PathType Leaf) {
    try { $manifest = Get-Content -LiteralPath $compilerStamp -Raw | ConvertFrom-Json }
    catch { $manifest = $null }
}
if (-not (Test-CompilerManifest $manifest)) {
    $manifest = Get-CompilerManifest
    # Existing files from the same checked archive can receive a stamp without rewriting
    # a compiler DLL that another process might still have loaded.
    if (-not (Test-CompilerManifest $manifest)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $compilerDirectory -Force
        if (-not (Test-CompilerManifest $manifest)) { throw 'Pinned compiler extraction verification failed.' }
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $compilerStamp -Encoding UTF8
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Pinned compiler is missing: $compiler" }

$frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$cecil = Join-Path $packRoot 'Game/BepInEx/core/Mono.Cecil.dll'
foreach ($required in @($frameworkCompiler, $cecil)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing build dependency: $required" }
}
$publicizer = Join-Path $buildCache 'Publicize.exe'
Copy-Item -LiteralPath $cecil -Destination (Join-Path $buildCache 'Mono.Cecil.dll') -Force
& $frameworkCompiler /nologo /codepage:65001 /target:exe "/reference:$cecil" "/out:$publicizer" (Join-Path $PSScriptRoot 'BuildTools/Publicize.cs')
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ publicizer compilation failed.' }
$internalAssemblies = @('assembly_valheim.dll', 'assembly_guiutils.dll', 'assembly_utils.dll')
& $publicizer $managed $publicized @internalAssemblies
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ isolated reference preparation failed.' }

$references = @(
    (Join-Path $packRoot 'Game/BepInEx/core/BepInEx.dll'),
    (Join-Path $packRoot 'Game/BepInEx/core/0Harmony.dll'),
    (Join-Path $packRoot 'Game/BepInEx/plugins/Jotunn.dll')
)
$references += $internalAssemblies | ForEach-Object { Join-Path $publicized $_ }
$references += @(
    'SoftReferenceableAssets.dll', 'Splatform.dll', 'gui_framework.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll', 'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.TextRenderingModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextCoreFontEngineModule.dll', 'UnityEngine.TextCoreTextEngineModule.dll',
    'Unity.TextMeshPro.dll', 'netstandard.dll', 'mscorlib.dll', 'System.dll',
    'System.Core.dll', 'System.Runtime.dll', 'System.Runtime.Serialization.dll'
) | ForEach-Object { Join-Path $managed $_ }
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference: $reference" }
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.cs' -File -Recurse |
    Where-Object { $_.Name -ne 'Resources.Designer.cs' } | Sort-Object FullName | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw 'AnyPortal+ sources were not found.' }
New-Item -ItemType Directory -Force -Path (Split-Path $OutputFile -Parent) | Out-Null
$arguments = @('/nologo', '/noconfig', '/nostdlib+', '/shared:false', '/codepage:65001', '/target:library', '/optimize+', '/deterministic+', '/langversion:latest', ('/out:' + $OutputFile))
$arguments += $references | ForEach-Object { '/reference:' + $_ }
$arguments += $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'AnyPortal+ compilation failed.' }
Write-Output "Built AnyPortal+: $OutputFile"
