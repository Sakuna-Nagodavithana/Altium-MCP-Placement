# Start ngrok -> local MCP, then write PublicUrl into Documents\AltiumEE\mcp-settings.json
# and Cursor ~/.cursor/mcp.json so other agents can reach the same server.
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$SettingsPath = Join-Path $env:USERPROFILE "Documents\AltiumEE\mcp-settings.json"
$CursorMcp = Join-Path $env:USERPROFILE ".cursor\mcp.json"
$NgrokDir = Join-Path $env:LOCALAPPDATA "ngrok"
$NgrokExe = Join-Path $NgrokDir "ngrok.exe"
$port = 8787

if (Test-Path $SettingsPath) {
    $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    if ($settings.Port) { $port = [int]$settings.Port }
}

function Ensure-Ngrok {
    $existing = Get-Command ngrok -ErrorAction SilentlyContinue
    if ($existing) { return $existing.Source }
    if (Test-Path $NgrokExe) { return $NgrokExe }

    New-Item -ItemType Directory -Force -Path $NgrokDir | Out-Null
    $zip = Join-Path $env:TEMP "ngrok.zip"
    Write-Host "Downloading ngrok..."
    Invoke-WebRequest -Uri "https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $NgrokDir -Force
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $NgrokExe)) {
        throw "ngrok download failed. Install from https://ngrok.com/download and re-run."
    }
    return $NgrokExe
}

# Health check local MCP first
try {
    $null = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health" -UseBasicParsing -TimeoutSec 5
} catch {
    throw "Local MCP is not running on port $port. Start it first: scripts/start-server-online.ps1"
}

$ngrok = Ensure-Ngrok
Get-Process ngrok -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "Starting ngrok http $port ..."
Start-Process -FilePath $ngrok -ArgumentList @("http", "$port", "--log=stdout") -WindowStyle Minimized
Start-Sleep -Seconds 3

$publicUrl = $null
for ($i = 0; $i -lt 20; $i++) {
    try {
        $api = Invoke-RestMethod -Uri "http://127.0.0.1:4040/api/tunnels" -TimeoutSec 3
        $https = $api.tunnels | Where-Object { $_.public_url -like "https://*" } | Select-Object -First 1
        if ($https) {
            $publicUrl = $https.public_url
            break
        }
    } catch {
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 1
}

if (-not $publicUrl) {
    throw "ngrok started but no public HTTPS URL appeared. Run 'ngrok config add-authtoken YOUR_TOKEN' then retry."
}

Write-Host "Public URL: $publicUrl"

if (Test-Path $SettingsPath) {
    $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    $settings.PublicUrl = $publicUrl
    $settings | ConvertTo-Json -Depth 8 | Set-Content $SettingsPath -Encoding UTF8
    Write-Host "Updated $SettingsPath PublicUrl"
}

if (Test-Path $CursorMcp) {
    $mcp = Get-Content $CursorMcp -Raw | ConvertFrom-Json
    $apiKey = $null
    if (Test-Path $SettingsPath) {
        $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
        $apiKey = $settings.ApiKey
    }

    foreach ($name in @("altium-schematic", "assignment-workspace")) {
        if ($mcp.mcpServers.$name) {
            $mcp.mcpServers.$name.url = "$publicUrl/mcp"
            if ($apiKey) {
                if (-not $mcp.mcpServers.$name.headers) {
                    $mcp.mcpServers.$name | Add-Member -NotePropertyName headers -NotePropertyValue (@{}) -Force
                }
                $mcp.mcpServers.$name.headers.Authorization = "Bearer $apiKey"
            }
        }
    }
    $mcp | ConvertTo-Json -Depth 8 | Set-Content $CursorMcp -Encoding UTF8
    Write-Host "Updated Cursor mcp.json URLs + Authorization to match AltiumEE ApiKey"
}

Write-Host ""
Write-Host "Done. Reload MCP servers in Cursor (or restart Cursor)."
Write-Host "Remote MCP endpoint: $publicUrl/mcp"
Write-Host "Keep both the Python MCP server and ngrok running."
