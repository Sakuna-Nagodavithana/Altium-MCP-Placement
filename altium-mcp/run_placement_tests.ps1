# Run internal placement planner tests (pin_near layout + verification)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
python test_placement_planner.py
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Optional: generate a real-board plan (requires connectivity export):"
Write-Host "  python generate_placement_plan.py IC1 `"$env:USERPROFILE\Documents\AltiumEE\connectivity.json`""
Write-Host "  python generate_all_placement_plan.py `"$env:USERPROFILE\Documents\AltiumEE\connectivity.json`""
