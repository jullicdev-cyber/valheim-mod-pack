[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GameDirectory,
    [string]$PluginAssembly,
    [string]$ExportPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/NordicRadio.dll' }
$managed = Join-Path $GameDirectory 'valheim_Data/Managed'
foreach ($ref in @('UnityEngine.CoreModule.dll','UnityEngine.dll','UnityEngine.PhysicsModule.dll','UnityEngine.AssetBundleModule.dll','SoftReferenceableAssets.dll','assembly_utils.dll','assembly_guiutils.dll','assembly_valheim.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $managed $ref)) | Out-Null
}
foreach ($ref in @('Game/BepInEx/core/BepInEx.dll','Game/BepInEx/plugins/Jotunn.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $root $ref)) | Out-Null
}
$assembly = [Reflection.Assembly]::LoadFrom($PluginAssembly)
$type = $assembly.GetType('ValheimModPack.NordicRadio.HornModel', $true)
$method = $type.GetMethod('BuildGeometry', [Reflection.BindingFlags]'Static,NonPublic')
# This invokes the exact production mesh generation, containing managed math
# only. No prefab registration, Unity objects, asset loading or game startup.
$parts = $method.Invoke($null, @())
$minimum = [double[]]@(1e9,1e9,1e9)
$maximum = [double[]]@(-1e9,-1e9,-1e9)
$triangles = 0
$export = @()
foreach ($part in $parts) {
    $vertices = $part.Mesh.Vertices
    $normals = $part.Mesh.Normals
    $uv = $part.Mesh.GetType().GetField('uv', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($part.Mesh)
    if ($vertices.Count % 3 -ne 0 -or $normals.Count -ne $vertices.Count -or $uv.Count -ne $vertices.Count) { throw 'Mismatched mesh arrays.' }
    $points = @()
    $normalData = @()
    for ($i = 0; $i -lt $vertices.Count; $i += 3) {
        $a = $vertices[$i]; $b = $vertices[$i+1]; $c = $vertices[$i+2]
        $normal = [UnityEngine.Vector3]::Cross(($b - $a), ($c - $a))
        if ($normal.sqrMagnitude -lt 1e-13) { throw "Degenerate triangle in $($part.Name) at $i" }
        if ([UnityEngine.Vector3]::Dot($normal.normalized, $normals[$i]) -lt 0.999) { throw 'Normal/winding mismatch.' }
        for ($j = 0; $j -lt 3; ++$j) {
            $p = $vertices[$i+$j]; $n = $normals[$i+$j]
            $values = [double[]]@($p.x,$p.y,$p.z)
            for ($axis = 0; $axis -lt 3; ++$axis) {
                if ([double]::IsNaN($values[$axis]) -or [double]::IsInfinity($values[$axis])) { throw 'Nonfinite coordinate.' }
                $minimum[$axis] = [Math]::Min($minimum[$axis], $values[$axis])
                $maximum[$axis] = [Math]::Max($maximum[$axis], $values[$axis])
            }
            $points += ,$values
            $normalData += ,([double[]]@($n.x,$n.y,$n.z))
        }
        ++$triangles
    }
    $export += @{ name=$part.Name; color=@($part.Color.r,$part.Color.g,$part.Color.b); glow=$part.Glow; vertices=$points; normals=$normalData }
}
if ($triangles -gt 4000 -or $triangles -lt 100) { throw 'Unexpected model complexity.' }
if ($minimum[1] -lt -0.001 -or $maximum[1] -gt 1.5 -or $maximum[0]-$minimum[0] -gt 1.5 -or $maximum[2]-$minimum[2] -gt 0.8) { throw 'Model bounds outside furniture envelope.' }
if ($parts.Count -ne 7) { throw 'Unexpected number of materials.' }
# Inspect the actual outer and inner horn faces, not only recomputed normals.
$horn = $parts | Where-Object { $_.Name -eq 'HornShell' }
$recesses = $parts | Where-Object { $_.Name -eq 'CarvedRecesses' }
if ($horn.Mesh.Vertices.Count -ne 7*14*6) { throw 'Horn topology is no longer the expected open tube.' }
$centers = @(@(-0.34,0.63,0),@(-0.49,0.74,0),@(-0.48,0.89,0),@(-0.33,0.99,0),@(-0.10,1.015,0),@(0.16,1.015,0),@(0.38,1.04,0),@(0.56,1.105,0))
$innerStart = 36 # The backing box precedes the inner horn in the same material.
for ($ring = 0; $ring -lt 7; ++$ring) {
    for ($side = 0; $side -lt 14; ++$side) {
        $index = ($ring*14+$side)*6
        $p = $horn.Mesh.Vertices[$index]
        $center = New-Object UnityEngine.Vector3 -ArgumentList $centers[$ring]
        if ([UnityEngine.Vector3]::Dot(($p-$center),$horn.Mesh.Normals[$index]) -le 0) { throw 'Inverted outer horn.' }
        $innerIndex = $innerStart+$index
        $p = $recesses.Mesh.Vertices[$innerIndex]
        $center = New-Object UnityEngine.Vector3 -ArgumentList $centers[$ring+1]
        if ([UnityEngine.Vector3]::Dot(($p-$center),$recesses.Mesh.Normals[$innerIndex]) -ge 0) { throw 'Inverted inner horn.' }
    }
}
if ($ExportPath) {
    $json = @{ triangles=$triangles; minimum=$minimum; maximum=$maximum; parts=$export } | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ExportPath), $json, (New-Object Text.UTF8Encoding($false)))
}
Write-Output "PASS: $triangles triangles, 7 shared material batches, finite coordinates, flat normals, valid UV counts, open horn and reversed inner surface."
Write-Output ("Bounds: ({0}) to ({1})" -f ($minimum -join ', '), ($maximum -join ', '))
