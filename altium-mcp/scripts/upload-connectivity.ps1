param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [string]$ServerUrl = "http://127.0.0.1:8787"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

if (-not (Test-Path ".env")) {
    Write-Host ".env not found. Create it from .env.example first."
    exit 1
}

Get-Content ".env" | ForEach-Object {
    if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
    $name, $value = $_.Split('=', 2)
    Set-Item -Path "env:$name" -Value $value
}

if (-not (Test-Path $FilePath)) {
    Write-Host "File not found: $FilePath"
    exit 1
}

$headers = @{
    Authorization = "Bearer $env:MCP_API_KEY"
    "Content-Type" = "application/json"
}

$body = Get-Content -Raw -Path $FilePath
Invoke-RestMethod -Method Post -Uri "$ServerUrl/api/connectivity" -Headers $headers -Body $body
