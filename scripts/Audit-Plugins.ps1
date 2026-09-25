[CmdletBinding()]
param([string]$PackDirectory, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $PackDirectory) { $PackDirectory = Join-Path $root 'Game' }
$pack = [IO.Path]::GetFullPath($PackDirectory)
# Cecil reads metadata; it does not run plugin code or start the game.
Add-Type -Path (Join-Path $pack 'BepInEx/core/Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory((Join-Path $pack 'BepInEx/core'))
$files = @(Get-ChildItem (Join-Path $pack 'BepInEx/plugins') -Recurse -Filter *.dll)
foreach ($directory in ($files.DirectoryName | Sort-Object -Unique)) { $resolver.AddSearchDirectory($directory) }
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$plugins = @()
$assemblies = @()
foreach ($file in $files) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName, $reader)
    try {
        $relative = $file.FullName.Substring($pack.Length + 1).Replace('\', '/')
        $assemblies += [pscustomobject]@{ name=$assembly.Name.Name; version="$($assembly.Name.Version)"; file=$relative }
        foreach ($type in $assembly.MainModule.GetTypes()) {
            $metadata = @($type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' })
            if ($metadata.Count -eq 0) { continue }
            $arguments = $metadata[0].ConstructorArguments
            $dependencies = @()
            $incompatible = @()
            $processes = @()
            foreach ($attribute in $type.CustomAttributes) {
                $values = @($attribute.ConstructorArguments | ForEach-Object { $_.Value })
                switch ($attribute.AttributeType.FullName) {
                    'BepInEx.BepInDependency' {
                        $minimum = $null
                        $hard = $true
                        if ($values.Count -gt 1) {
                            if ($attribute.ConstructorArguments[1].Type.FullName -eq 'System.String') { $minimum = [string]$values[1] }
                            else { $hard = (([int]$values[1] -band 1) -ne 0) }
                        }
                        $dependencies += [pscustomobject]@{ guid=[string]$values[0]; hard=$hard; minimum=$minimum }
                    }
                    'BepInEx.BepInIncompatibility' { $incompatible += [string]$values[0] }
                    'BepInEx.BepInProcess' { $processes += [string]$values[0] }
                }
            }
            $plugins += [pscustomobject]@{
                guid=[string]$arguments[0].Value; name=[string]$arguments[1].Value
                version=[string]$arguments[2].Value; file=$relative
                dependencies=$dependencies; incompatible=$incompatible; processes=$processes
            }
        }
    } finally { $assembly.Dispose() }
}
$errors = @()
foreach ($group in ($plugins | Group-Object guid | Where-Object Count -gt 1)) { $errors += "Duplicate plugin GUID: $($group.Name)" }
foreach ($group in ($assemblies | Group-Object name | Where-Object Count -gt 1)) { $errors += "Duplicate assembly: $($group.Name)" }
foreach ($plugin in $plugins) {
    foreach ($guid in $plugin.incompatible) {
        if ($plugins.guid -contains $guid) { $errors += "$($plugin.guid) is incompatible with $guid" }
    }
    foreach ($dependency in $plugin.dependencies) {
        $found = @($plugins | Where-Object guid -eq $dependency.guid)
        if ($found.Count -eq 0 -and $dependency.hard) { $errors += "$($plugin.guid) requires $($dependency.guid)" }
        if ($found.Count -eq 1 -and $dependency.minimum -and [version]$found[0].version -lt [version]$dependency.minimum) {
            $errors += "$($plugin.guid) requires $($dependency.guid) >= $($dependency.minimum)"
        }
    }
}
$report = [pscustomobject]@{ scope='Static DLL metadata only; not a gameplay or Harmony compatibility test'; assemblies=$assemblies; plugins=$plugins; errors=$errors }
if ($ReportPath) { $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
$resolver.Dispose()
if ($errors.Count) { throw ($errors -join "`n") }
Write-Output "OK: $($plugins.Count) plugin GUIDs, $($assemblies.Count) assemblies; no declared hard conflicts or missing plugin dependencies."
Write-Output $report.scope
