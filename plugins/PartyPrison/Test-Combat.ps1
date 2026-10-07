[CmdletBinding()]
param([string]$GameDirectory)
$ErrorActionPreference = 'Stop'
$packRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$fixtureDirectory = Join-Path $packRoot ('.cache/party-prison-combat-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $fixtureDirectory | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$runner = Join-Path $fixtureDirectory 'CombatPolicyTests.exe'
$sources = @('CombatCatalog.cs','CombatPolicyTests.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /codepage:65001 /target:exe "/out:$runner" @sources
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison arena combat policy test build failed.' }
& $runner
if ($LASTEXITCODE -ne 0) { throw 'PartyPrison arena combat policy checks failed.' }
if ($GameDirectory) {
    # Check canonical prefab identities against this installed game's native
    # asset manifest without starting Unity or allocating its world/prefabs.
    $manifest = Join-Path $GameDirectory 'valheim_Data/StreamingAssets/SoftRef/manifest_extended'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'Native asset manifest is missing.' }
    $known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadLines($manifest)) {
        if ($line.StartsWith('  path in bundle: ') -and $line.EndsWith('.prefab')) {
            [void]$known.Add([IO.Path]::GetFileNameWithoutExtension($line.Substring(18)))
        }
    }
    $assembly = [Reflection.Assembly]::LoadFrom($runner)
    $sources = $assembly.GetType('ValheimModPack.PartyPrison.CombatCatalog').GetMethod('AllFoodSources').Invoke($null, $null)
    foreach ($name in $sources) { if (-not $known.Contains($name)) { throw "Missing canonical native arena food prefab: $name" } }
    if (-not $known.Contains('fire_pit')) { throw 'Native campfire prefab is missing.' }
    Write-Output "PASS: $($sources.Length) arena food sources resolve in the installed native game asset manifest."
    [void][Reflection.Assembly]::LoadFrom((Join-Path $packRoot 'Game/BepInEx/core/Mono.Cecil.dll'))
    $native = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
    try {
        $fire = $native.MainModule.GetType('Fireplace')
        $fields = @{
            m_infiniteFuel = 'System.Boolean'; m_disableCoverCheck = 'System.Boolean';
            m_canRefill = 'System.Boolean'; m_canTurnOff = 'System.Boolean';
            m_startFuel = 'System.Single'; m_maxFuel = 'System.Single'; m_secPerFuel = 'System.Single';
            m_igniteInterval = 'System.Single'; m_smokeSpawner = 'SmokeSpawner'
        }
        foreach ($entry in $fields.GetEnumerator()) {
            $field = @($fire.Fields | Where-Object Name -eq $entry.Key)
            if ($field.Count -ne 1 -or -not $field[0].IsPublic -or $field[0].FieldType.FullName -ne $entry.Value) {
                throw "Native campfire ABI changed: $($entry.Key)"
            }
        }
        $update = @($fire.Methods | Where-Object Name -eq 'UpdateFireplace')[0]
        $period = @($update.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'm_secPerFuel' })[0]
        $time = @($update.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.Name -eq 'GetTimeSinceLastUpdate' })[0]
        $gate = @($update.Body.Instructions | Where-Object {
            $_.Offset -gt $period.Offset -and $_.Offset -lt $time.Offset -and $_.OpCode.Name.StartsWith('ble') -and
                $_.Operand.Offset -gt $time.Offset
        })
        if ($gate.Count -ne 1) { throw 'Zero campfire fuel period no longer bypasses native fuel-time ZDO writes.' }
        $burn = @($fire.Methods | Where-Object Name -eq 'IsBurning')[0]
        if (-not @($burn.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'm_infiniteFuel' }).Count) {
            throw 'Native campfire no longer supports infinite fuel when its initial fuel is zero.'
        }
        $cover = @($fire.Methods | Where-Object Name -eq 'CheckUnderTerrain')[0]
        $disable = @($cover.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldfld' -and $_.Operand.Name -eq 'm_disableCoverCheck' })[0]
        $ground = @($cover.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'call' -and $_.Operand.DeclaringType.Name -eq 'Heightmap' })[0]
        if (-not @($cover.Body.Instructions | Where-Object { $_.Offset -gt $disable.Offset -and $_.Offset -lt $ground.Offset -and $_.OpCode.Name -eq 'ret' }).Count) {
            throw 'Native campfire cover bypass no longer exits before terrain and roof checks.'
        }
        Write-Output 'PASS: native cell campfire ABI, infinite fuel, cover bypass and fuel-time write gate.'
    }
    finally { $native.Dispose() }
}
