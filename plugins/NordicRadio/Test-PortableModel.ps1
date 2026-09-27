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
$type = $assembly.GetType('ValheimModPack.NordicRadio.PortableModel', $true)
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
if ($triangles -gt 1500 -or $triangles -lt 100) { throw 'Unexpected idol model complexity.' }
if ($minimum[1] -lt -0.001 -or $maximum[1] -gt 0.50 -or $maximum[0]-$minimum[0] -gt 0.32 -or $maximum[2]-$minimum[2] -gt 0.32) { throw 'Model bounds outside handheld envelope.' }
if ($parts.Count -ne 5) { throw 'Unexpected number of materials.' }
$core = $parts | Where-Object { $_.Name -eq 'IdolSurtlingCore' }
if (-not $core.Glow -or $core.Mesh.Vertices.Count -ne 24) { throw 'Missing faceted emissive surtling heart.' }
if ($ExportPath) {
    $json = @{ triangles=$triangles; minimum=$minimum; maximum=$maximum; parts=$export } | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($ExportPath), $json, (New-Object Text.UTF8Encoding($false)))
}
Write-Output "PASS: $triangles triangles, 5 shared material batches, finite coordinates, flat normals, valid UV counts, emissive heart and handheld bounds."
Write-Output ("Bounds: ({0}) to ({1})" -f ($minimum -join ', '), ($maximum -join ', '))
