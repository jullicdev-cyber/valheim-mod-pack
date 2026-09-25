$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('.cache/audit-tests-' + [guid]::NewGuid().ToString('N'))
$core = Join-Path $fixture 'BepInEx/core'
$plugins = Join-Path $fixture 'BepInEx/plugins'
New-Item -ItemType Directory -Force $core,$plugins | Out-Null
Copy-Item -Path (Join-Path $root 'Game/BepInEx/core/*') -Destination $core -Recurse
$testDll = Join-Path $plugins 'Fixture.dll'
$audit = Join-Path $PSScriptRoot 'Audit-Plugins.ps1'
function Assert-AuditFailure([string]$Expected) {
    $failure = $null
    try { & $audit -PackDirectory $fixture | Out-Null } catch { $failure = $_.Exception.Message }
    if (-not $failure -or $failure -notmatch $Expected) { throw "Expected $Expected; received: $failure" }
}
Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($core)
$assembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly([Mono.Cecil.AssemblyNameDefinition]::new('AuditFixture',[version]'1.0.0.0'),'AuditFixture',[Mono.Cecil.ModuleKind]::Dll)
$assembly.MainModule.AssemblyResolver.AddSearchDirectory($core)
$bep = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $core 'BepInEx.dll'))
function New-Attribute([string]$Name, [object[]]$Values) {
    $attrType = $bep.MainModule.Types | Where-Object FullName -eq ('BepInEx.'+$Name)
    $ctor = $attrType.Methods | Where-Object { $_.Name -eq '.ctor' -and $_.Parameters.Count -eq $Values.Count -and ($Name -ne 'BepInDependency' -or $_.Parameters[1].ParameterType.FullName -ne 'System.String') } | Select-Object -First 1
    $attr = [Mono.Cecil.CustomAttribute]::new($assembly.MainModule.ImportReference($ctor))
    for($i=0; $i -lt $Values.Count; $i++) {
        $attr.ConstructorArguments.Add([Mono.Cecil.CustomAttributeArgument]::new($assembly.MainModule.ImportReference($ctor.Parameters[$i].ParameterType),$Values[$i]))
    }
    return $attr
}
try {
    $type = [Mono.Cecil.TypeDefinition]::new('Fixture','Plugin',[Mono.Cecil.TypeAttributes]::Public,$assembly.MainModule.TypeSystem.Object)
    $assembly.MainModule.Types.Add($type)
    $type.CustomAttributes.Add((New-Attribute 'BepInPlugin' @('fixture.plugin','Test fixture','1.0.0')))
    $dep = New-Attribute 'BepInDependency' @('fixture.absent',[int]2)
    $type.CustomAttributes.Add($dep)
    $assembly.Write($testDll)
    & $audit -PackDirectory $fixture | Out-Null
    Copy-Item -LiteralPath $testDll -Destination (Join-Path $plugins 'Fixture-copy.dll')
    Assert-AuditFailure 'Duplicate plugin GUID'
    Move-Item -LiteralPath (Join-Path $plugins 'Fixture-copy.dll') -Destination (Join-Path $fixture 'Fixture-copy.dll')
    $oldFlag = $dep.ConstructorArguments[1]
    $dep.ConstructorArguments[1] = [Mono.Cecil.CustomAttributeArgument]::new($oldFlag.Type, [int]1)
    $assembly.Write($testDll)
    Assert-AuditFailure 'requires fixture.absent'
    $dep.ConstructorArguments[1] = $oldFlag
    $type.CustomAttributes.Add((New-Attribute 'BepInIncompatibility' @('fixture.plugin')))
    $assembly.Write($testDll)
    Assert-AuditFailure 'is incompatible with fixture.plugin'
} finally { $bep.Dispose(); $assembly.Dispose(); $resolver.Dispose() }
Write-Output 'OK: optional dependencies, duplicate GUID/assembly, missing hard dependency and explicit incompatibility fixtures.'
