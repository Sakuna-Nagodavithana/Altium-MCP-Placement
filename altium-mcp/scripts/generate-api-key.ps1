$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

python -c "from config import generate_api_key; print(generate_api_key())"
