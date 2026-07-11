# Build Altium-MCP-Placement from source and copy into Altium Extensions folder.
# Requires: Visual Studio 2022 Build Tools with ".NET desktop build tools" workload.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Join-Path $Root "EasyEDA-Loader"
$DeployDir = Join-Path $Root "Deploy"
$ExtensionName = "Altium-MCP-Placement"
$LegacyExtensionName = "EasyEDA-Loader"
$AltiumRoot = if (Test-Path "E:\Altium") { "E:\Altium" } else { "C:\Program Files\Altium\AD24" }
$AltiumSystem = Join-Path $AltiumRoot "System"
$AltiumDevex = Join-Path $AltiumSystem "DotNet\DevExpress.Wpf"
$AssembliesDir = Join-Path $ProjectDir "Assemblies"
$AltiumGuid = "Altium Designer {2E34A225-0C0D-424C-B915-02F461E29B71}"
$ExtensionsRoot = "C:\ProgramData\Altium\$AltiumGuid\Extensions"
$ExtensionsDir = Join-Path $ExtensionsRoot $ExtensionName
$LegacyExtensionsDir = Join-Path $ExtensionsRoot $LegacyExtensionName
$ExtensionsDisabledDir = Join-Path $ExtensionsRoot "$ExtensionName.disabled"

function Find-MsBuild {
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

Write-Host "==> Preparing Altium reference assemblies..."
New-Item -ItemType Directory -Force -Path $AssembliesDir | Out-Null
Copy-Item (Join-Path $AltiumSystem "Altium.Controls.dll") $AssembliesDir -Force
Copy-Item (Join-Path $AltiumRoot "Altium.Controls.Skins.dll") $AssembliesDir -Force
Copy-Item (Join-Path $AltiumSystem "Altium.SDK.dll") $AssembliesDir -Force
Copy-Item (Join-Path $AltiumSystem "Altium.SDK.Interfaces.dll") $AssembliesDir -Force
$devexFiles = @(
    "DevExpress.Data.v24.1.dll",
    "DevExpress.Mvvm.v24.1.dll",
    "DevExpress.Printing.v24.1.Core.dll",
    "DevExpress.Utils.v24.1.dll",
    "DevExpress.Xpf.Core.v24.1.dll",
    "DevExpress.Xpf.Grid.v24.1.Core.dll",
    "DevExpress.Xpf.Grid.v24.1.dll"
)
foreach ($file in $devexFiles) {
    Copy-Item (Join-Path $AltiumDevex $file) $AssembliesDir -Force
}

$msbuild = Find-MsBuild
if ($msbuild) {
    Write-Host "==> Building with $msbuild"
    & $msbuild (Join-Path $ProjectDir "EasyEDA-Loader.csproj") /p:Configuration=Release /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
    $builtDll = Join-Path $ProjectDir "bin\Release\$ExtensionName.dll"
} else {
    Write-Host "==> Visual Studio MSBuild not found. Trying dotnet SDK build..."
    Push-Location $Root
    dotnet build (Join-Path $ProjectDir "EasyEDA-Loader.csproj") -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
    Pop-Location
    $builtDll = Join-Path $ProjectDir "bin\Release\$ExtensionName.dll"
}

if (-not (Test-Path $builtDll)) {
    $builtDll = Join-Path $ProjectDir "bin\Release\net48\$ExtensionName.dll"
}

if (-not (Test-Path $builtDll)) {
    throw "Build failed. Install Visual Studio 2022 Build Tools with '.NET desktop build tools', then run this script again."
}

$dllBytes = [IO.File]::ReadAllBytes($builtDll)
$dllText = [Text.Encoding]::ASCII.GetString($dllBytes)
if ($dllText -match 'net8\.0') {
    throw "Built DLL targets .NET 8, but Altium extensions require .NET Framework 4.8. Do not deploy this DLL."
}
if ($dllText -notmatch 'v4\.0\.30319' -and $dllText -notmatch '\.NETFramework,Version=v4\.8') {
    Write-Warning "Could not confirm net48 target for $builtDll. Verify before installing into Altium."
}

Write-Host "==> Staging deploy package..."
New-Item -ItemType Directory -Force -Path $DeployDir | Out-Null
Copy-Item $builtDll $DeployDir -Force
$builtConfig = Join-Path (Split-Path $builtDll -Parent) "$ExtensionName.dll.config"
if (Test-Path $builtConfig) {
    Copy-Item $builtConfig $DeployDir -Force
} else {
    Copy-Item (Join-Path $ProjectDir "app.config") (Join-Path $DeployDir "$ExtensionName.dll.config") -Force
}
Copy-Item (Join-Path $Root "Deploy\$ExtensionName.Ins") $DeployDir -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Root "Deploy\$ExtensionName.rcs") $DeployDir -Force -ErrorAction SilentlyContinue

$packagesDir = Join-Path $ProjectDir "packages"
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$deps = @{
    "Newtonsoft.Json.dll" = @(
        (Join-Path $packagesDir "Newtonsoft.Json.13.0.3\lib\net45\Newtonsoft.Json.dll")
        (Join-Path $nugetRoot "newtonsoft.json\13.0.3\lib\net45\Newtonsoft.Json.dll")
        (Join-Path (Split-Path $builtDll -Parent) "Newtonsoft.Json.dll")
    )
}
foreach ($entry in $deps.GetEnumerator()) {
    $source = $null
    foreach ($candidate in $entry.Value) {
        if (Test-Path $candidate) {
            $source = $candidate
            break
        }
    }
    if (-not $source) {
        throw "Missing dependency source for $($entry.Key)"
    }
    Copy-Item $source (Join-Path $DeployDir $entry.Key) -Force
}

$staleDeps = @(
    "System.Text.Json.dll",
    "System.Text.Encodings.Web.dll",
    "System.Drawing.Common.dll",
    "Microsoft.Bcl.AsyncInterfaces.dll",
    "System.Buffers.dll",
    "System.IO.Pipelines.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Threading.Tasks.Extensions.dll",
    "System.ValueTuple.dll",
    "EasyEDA-Loader.dll"
)
foreach ($stale in $staleDeps) {
    Remove-Item (Join-Path $DeployDir $stale) -Force -ErrorAction SilentlyContinue
}

# Ensure the extension auto-starts in DXP.RAF (StartupMode 1 = enabled). Earlier
# builds disabled this, which prevented the extension from loading after registration.
$dxpRaf = Join-Path $env:APPDATA "Altium\$AltiumGuid\DXP.RAF"
if (Test-Path $dxpRaf) {
    $raf = Get-Content $dxpRaf -Raw
    $rafChanged = $false
    foreach ($serverName in @($ExtensionName, $LegacyExtensionName)) {
        if ($raf -match "Server '$serverName'[\s\S]*?StartupMode\s+0") {
            $raf = $raf -replace "(Server '$serverName'[\s\S]*?StartupMode\s+)0", '${1}1'
            $rafChanged = $true
            Write-Host "==> Enabled $serverName auto-start in DXP.RAF (StartupMode 1)"
        }
    }
    if ($rafChanged) {
        Set-Content $dxpRaf $raf -NoNewline
    }
}

if (Test-Path $ExtensionsDisabledDir) {
    if (Test-Path $ExtensionsDir) {
        Remove-Item $ExtensionsDir -Recurse -Force
    }
    Rename-Item $ExtensionsDisabledDir (Split-Path $ExtensionsDir -Leaf)
    Write-Host "==> Re-enabled extension folder (renamed .disabled -> $ExtensionName)"
}

if (-not (Test-Path $ExtensionsDir)) {
    New-Item -ItemType Directory -Force -Path $ExtensionsDir | Out-Null
}

if (Test-Path $LegacyExtensionsDir) {
    Write-Host "==> Removing legacy extension folder: $LegacyExtensionsDir"
    Remove-Item $LegacyExtensionsDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Installing into Altium Extensions folder..."
Copy-Item (Join-Path $DeployDir "*") $ExtensionsDir -Force
foreach ($stale in $staleDeps) {
    Remove-Item (Join-Path $ExtensionsDir $stale) -Force -ErrorAction SilentlyContinue
}

# Register the extension in ExtensionsRegistry.xml if it is not already there.
# Altium ignores extension folders that are not registered -- this is what makes
# the extension appear in the Extensions panel and load on startup.
$registryPath = Join-Path $ExtensionsRoot "ExtensionsRegistry.xml"
if (Test-Path $registryPath) {
    $registryContent = Get-Content $registryPath -Raw
    if ($registryContent -notmatch "HRID=""$ExtensionName""") {
        $extensionGuid = "6FE3A9E3-36F1-4B94-967E-772357D2C44F"
        $itemXml = @"
    <Item HRID="$ExtensionName" Guid="$extensionGuid">
        <Path>$ExtensionsDir</Path>
        <Status>0</Status>
        <VaultGuid></VaultGuid>
        <CreatedBy>Altium MCP Placement</CreatedBy>
        <CategoryGuid>793A1F67-0B22-4E01-A5DE-3176A1E8C60D</CategoryGuid>
        <CategoryName></CategoryName>
        <ReadMe></ReadMe>
        <Help></Help>
        <Requirements></Requirements>
        <Title>$ExtensionName</Title>
        <ShortDescription>Altium MCP export, pin-accurate passive placement, and IC clustering</ShortDescription>
        <LongDescription>Exports schematic/PCB data for MCP, places support passives pin-accurate around ICs with decoupling caps on the bottom side, and groups clusters as Unions.</LongDescription>
        <SmallImage></SmallImage>
        <LargeImage></LargeImage>
        <Version>1.2.0.0</Version>
        <VersionGuid>$extensionGuid</VersionGuid>
        <ReleasedDate>46183.7189423495</ReleasedDate>
        <ReleaseNotes></ReleaseNotes>
        <DateInstalled>46183.7189423495</DateInstalled>
        <PlatformVersions>
          <DXP BuildNumber="1.0.16.20"/>
          <EDP BuildNumber="10.0.16.20"/>
          <MaxDXP BuildNumber="0.0.0.0"/>
          <MaxEDP BuildNumber="0.0.0.0"/>
        </PlatformVersions>
      </Item>
</Extensions>
"@
        Copy-Item $registryPath "$registryPath.bak" -Force
        $updated = $registryContent -replace "</Extensions>\s*$", $itemXml
        Set-Content $registryPath $updated -NoNewline
        Write-Host "==> Registered $ExtensionName in ExtensionsRegistry.xml"
    }
}

Write-Host "Done. Restart Altium Designer and enable '$ExtensionName' if prompted."

Write-Host "Built DLL: $builtDll"
