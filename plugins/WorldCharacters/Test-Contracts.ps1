[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDirectory, [string]$PluginAssembly)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Add-Type -Path (Join-Path $root 'Game/BepInEx/core/Mono.Cecil.dll')
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDirectory 'valheim_Data/Managed/assembly_valheim.dll'))
if (-not $PluginAssembly) { $PluginAssembly = Join-Path $root 'local-plugins/WorldCharacters.dll' }
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginAssembly)
$count = 0
try {
    foreach ($type in $plugin.MainModule.GetTypes()) {
        $attribute = $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }
        if (-not $attribute) { continue }
        $targetTypeName = $attribute.ConstructorArguments[0].Value.FullName
        $methodName = [string]$attribute.ConstructorArguments[1].Value
        $targetType = $game.MainModule.GetTypes() | Where-Object FullName -eq $targetTypeName
        $targets = @($targetType.Methods | Where-Object Name -eq $methodName)
        if ($targets.Count -ne 1) { throw "Missing or ambiguous native target: $targetTypeName.$methodName" }
        $target = $targets[0]
        foreach ($patch in $type.Methods | Where-Object Name -in 'Prefix','Postfix','Finalizer') {
            foreach ($parameter in $patch.Parameters) {
                $name = $parameter.Name
                if ($name -in '__instance','__runOriginal','__exception') { continue }
                $expected = $null
                if ($name -eq '__result') { $expected = $target.ReturnType.FullName }
                elseif ($name -match '^__(\d+)$') { $expected = $target.Parameters[[int]$Matches[1]].ParameterType.FullName }
                else { $expected = ($target.Parameters | Where-Object Name -eq $name).ParameterType.FullName }
                $actual = $parameter.ParameterType.FullName.TrimEnd('&')
                if (-not $expected -or $actual -ne $expected) { throw "Invalid patch parameter $($type.Name).$name ($actual vs $expected)" }
            }
        }
        $count++
    }
    foreach ($spec in @(@('Player',33),@('Inventory',109))) {
        $type = $game.MainModule.Types | Where-Object Name -eq $spec[0]
        $save = $type.Methods | Where-Object Name -eq 'Save'
        $constant = $save.Body.Instructions | Where-Object { $_.OpCode.Name -like 'ldc.i4*' } | Select-Object -First 1
        if ([int]$constant.Operand -ne $spec[1]) { throw "Unsupported native $($spec[0]) save version" }
        $count++
    }
    $profile = $game.MainModule.Types | Where-Object Name -eq 'PlayerProfile'
    if (-not ($profile.Fields | Where-Object { $_.Name -eq 'm_playerData' -and $_.FieldType.FullName -eq 'System.Byte[]' })) { throw 'Player data field changed' }
    if (-not ($profile.Methods | Where-Object { $_.Name -eq 'GetWorldData' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'System.Int64' })) { throw 'World profile getter changed' }
    $count += 2
    Write-Output "PASS: $count native contracts (patch signatures and supported serialization versions). No game process started."
} finally { $plugin.Dispose(); $game.Dispose() }
