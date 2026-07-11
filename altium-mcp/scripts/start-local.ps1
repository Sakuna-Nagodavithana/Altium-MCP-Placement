$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not (Test-Path ".env")) {
    Copy-Item ".env.example" ".env"
}

Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
    $name, $value = $_.Split('=', 2)
    Set-Item -Path "env:$name" -Value $value
}

$env:MCP_TRANSPORT = "stdio"
Write-Host "Local MCP mode for Cursor (stdio). Add cursor-mcp-local.example.json to Cursor MCP settings."
python server.py
