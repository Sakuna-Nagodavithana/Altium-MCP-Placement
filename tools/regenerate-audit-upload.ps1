# Regenerates audit-upload/ from current EasyEDA-Loader + altium-mcp sources.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$loader = Join-Path $root "EasyEDA-Loader"
$mcp = Join-Path $root "altium-mcp"
$out = Join-Path $root "audit-upload"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Merge-Files {
    param(
        [string]$Header,
        [string]$OutputPath,
        [hashtable[]]$Sections
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine($Header)
    [void]$sb.AppendLine("")
    foreach ($section in $Sections) {
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("// =============================================================================")
        [void]$sb.AppendLine("// FILE: $($section.Name)")
        [void]$sb.AppendLine("// =============================================================================")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine((Get-Content -Raw -LiteralPath $section.Path))
    }
    [System.IO.File]::WriteAllText($OutputPath, $sb.ToString())
}

function Merge-PyFiles {
    param(
        [string]$Header,
        [string]$OutputPath,
        [hashtable[]]$Sections
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine($Header)
    [void]$sb.AppendLine("")
    foreach ($section in $Sections) {
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("# =============================================================================")
        [void]$sb.AppendLine("# FILE: $($section.Name)")
        [void]$sb.AppendLine("# =============================================================================")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine((Get-Content -Raw -LiteralPath $section.Path))
    }
    [System.IO.File]::WriteAllText($OutputPath, $sb.ToString())
}

$csPairs = @(
    @{ Src = "Placement\PlacementConstants.cs"; Dst = "cs-PlacementConstants.cs" },
    @{ Src = "Placement\PlacementSupport.cs"; Dst = "cs-PlacementSupport.cs" },
    @{ Src = "Placement\PlacementChains.cs"; Dst = "cs-PlacementChains.cs" },
    @{ Src = "Placement\PlacementLayout.cs"; Dst = "cs-PlacementLayout.cs" },
    @{ Src = "Placement\PlacementPlanBuilder.cs"; Dst = "cs-PlacementPlanBuilder.cs" },
    @{ Src = "Placement\PlacementPlannerService.cs"; Dst = "cs-PlacementPlannerService.cs" },
    @{ Src = "CoordUtils.cs"; Dst = "cs-CoordUtils.cs" },
    @{ Src = "PcbDocumentHelper.cs"; Dst = "cs-PcbDocumentHelper.cs" },
    @{ Src = "PlacementPlanGenerator.cs"; Dst = "cs-PlacementPlanGenerator.cs" },
    @{ Src = "DesignExporter.cs"; Dst = "cs-DesignExporter.cs" },
    @{ Src = "IcClusterRunner.cs"; Dst = "cs-IcClusterRunner.cs" }
)

$pyPairs = @(
    @{ Src = "placement_planner.py"; Dst = "py-placement_planner.py" },
    @{ Src = "sch_net_resolver.py"; Dst = "py-sch_net_resolver.py" },
    @{ Src = "connectivity_store.py"; Dst = "py-connectivity_store.py" }
)

foreach ($pair in $csPairs) {
    Copy-Item -Force (Join-Path $loader $pair.Src) (Join-Path $out $pair.Dst)
}
foreach ($pair in $pyPairs) {
    $srcPath = Join-Path $mcp $pair.Src
    if (Test-Path $srcPath) {
        Copy-Item -Force $srcPath (Join-Path $out $pair.Dst)
    }
}

Copy-Item -Force (Join-Path $root "build-and-deploy.ps1") (Join-Path $out "build-and-deploy.ps1")
Copy-Item -Force (Join-Path $mcp "test_placement_planner.py") (Join-Path $out "test-placement_planner.py") -ErrorAction SilentlyContinue
Copy-Item -Force (Join-Path $mcp "compare_placement_parity.py") (Join-Path $out "test-compare_placement_parity.py") -ErrorAction SilentlyContinue
Copy-Item -Force (Join-Path $mcp "diff_planner_parity.py") (Join-Path $out "test-diff_planner_parity.py") -ErrorAction SilentlyContinue

$csSections = $csPairs | ForEach-Object {
    @{ Name = $_.Dst; Path = (Join-Path $out $_.Dst) }
}
Merge-Files -Header @"
// =============================================================================
// ALTIUM MCP PLACEMENT — ALL C# SOURCE (merged for audit upload)
// Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
// Sections separated by FILE headers. Includes CoordUtils + PcbDocumentHelper.
// =============================================================================
"@ -OutputPath (Join-Path $out "ALL-CS-PLACEMENT.cs") -Sections $csSections

$pySections = @(
    @{ Name = "py-placement_planner.py"; Path = (Join-Path $out "py-placement_planner.py") },
    @{ Name = "py-sch_net_resolver.py"; Path = (Join-Path $out "py-sch_net_resolver.py") }
)
Merge-PyFiles -Header @"
# =============================================================================
# ALTIUM MCP PLACEMENT — PYTHON REFERENCE (merged for audit upload)
# Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
# Sections separated by FILE headers.
# =============================================================================
"@ -OutputPath (Join-Path $out "ALL-PY-REFERENCE.py") -Sections $pySections

Write-Host "Regenerated audit-upload at: $out"
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
    $kb = [math]::Round($_.Length / 1KB, 1)
    Write-Host ("  {0,-32} {1,8} KB" -f $_.Name, $kb)
}
