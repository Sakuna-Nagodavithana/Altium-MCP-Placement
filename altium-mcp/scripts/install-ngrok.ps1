$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "Installing ngrok via winget..."
try {
    winget install --id Ngrok.Ngrok -e --accept-source-agreements --accept-package-agreements
} catch {
    Write-Host "winget install failed. Download manually from https://ngrok.com/download"
    exit 1
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Create a free ngrok account: https://dashboard.ngrok.com/signup"
Write-Host "2. Copy your authtoken from: https://dashboard.ngrok.com/get-started/your-authtoken"
Write-Host "3. Run: ngrok config add-authtoken YOUR_TOKEN"
