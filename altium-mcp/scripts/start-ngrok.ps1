$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not (Test-Path ".env")) {
    if (Test-Path ".env.example") {
        Copy-Item ".env.example" ".env"
    }
}

Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
    $name, $value = $_.Split('=', 2)
    Set-Item -Path "env:$name" -Value $value
}

$port = if ($env:MCP_PORT) { $env:MCP_PORT } else { "8787" }

Write-Host "Starting ngrok tunnel to http://127.0.0.1:$port"
Write-Host "Keep the MCP server running in another terminal: scripts/start-server-online.ps1"
Write-Host ""
Write-Host "After ngrok prints the HTTPS URL, update MCP_PUBLIC_URL in .env and restart the MCP server."
Write-Host ""

ngrok http $port
