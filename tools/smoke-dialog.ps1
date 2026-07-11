$ErrorActionPreference = "Stop"
$base = Join-Path (Split-Path -Parent $PSScriptRoot) "EasyEDA-Loader"

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([System.Reflection.AssemblyName]$eventArgs.Name).Name + ".dll"
    $paths = @(
        (Join-Path $base "Assemblies\$name"),
        (Join-Path $base "bin\Release\$name"),
        (Join-Path $base "packages\Newtonsoft.Json.13.0.3\lib\net45\$name")
    )
    foreach ($path in $paths) {
        if (Test-Path $path) {
            return [System.Reflection.Assembly]::LoadFrom($path)
        }
    }
    return $null
})

Add-Type -AssemblyName PresentationFramework
$assembly = [System.Reflection.Assembly]::LoadFrom(
    (Join-Path $base "bin\Release\Altium-MCP-Placement.dll"))
$window = $assembly.CreateInstance("EasyEDA_Loader.DialogWindow")
if ($null -eq $window) {
    throw "DialogWindow instance is null"
}
Write-Output "DialogWindow constructed: $($window.Title)"
$window.Close()
