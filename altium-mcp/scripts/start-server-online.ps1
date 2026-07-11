$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not (Test-Path ".env")) {
    Copy-Item ".env.example" ".env"
    Write-Host "Created .env from .env.example"
    Write-Host "Run scripts/generate-api-key.ps1 and paste the key into MCP_API_KEY in .env"
    exit 1
}

Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
    $name, $value = $_.Split('=', 2)
    Set-Item -Path "env:$name" -Value $value
}

if (-not $env:MCP_API_KEY -or $env:MCP_API_KEY -like "replace-with*") {
    Write-Host "Set MCP_API_KEY in .env first."
    exit 1
}

$env:MCP_TRANSPORT = "streamable-http"
$port = if ($env:MCP_PORT) { $env:MCP_PORT } else { "8787" }

Write-Host "Starting secure MCP server on http://127.0.0.1:$port/mcp"
python server.py
