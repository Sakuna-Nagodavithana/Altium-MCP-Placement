# Altium MCP — Online Setup (ngrok)

## What goes where

| What | Where to put it |
|------|-----------------|
| MCP Python files | `EasyEDALoader-main\altium-mcp\` (keep whole folder) |
| Your secrets | `altium-mcp\.env` (create from `.env.example`) |
| Altium extension DLL | `C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\EasyEDA-Loader\` |
| Exported schematic data | `%USERPROFILE%\Documents\AltiumEE\connectivity.json` |
| Cursor local MCP config | `%USERPROFILE%\.cursor\mcp.json` |
| ngrok | Install globally (PATH) via `scripts\install-ngrok.ps1` |

## One-time setup

1. Install Python deps:
   ```powershell
   cd EasyEDALoader-main\altium-mcp
   python -m pip install -r requirements.txt
   ```

2. Create `.env`:
   ```powershell
   copy .env.example .env
   powershell -ExecutionPolicy Bypass -File scripts\generate-api-key.ps1
   ```
   Paste the printed key into `MCP_API_KEY=` in `.env`.

3. Install ngrok:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install-ngrok.ps1
   ngrok config add-authtoken YOUR_NGROK_TOKEN
   ```

4. Install Altium extension (close Altium first):
   - Copy all files from `Deploy\` to your Extensions folder.

## Run online (two terminals)

**Terminal 1 — MCP server**
```powershell
cd EasyEDALoader-main\altium-mcp
powershell -ExecutionPolicy Bypass -File scripts\start-server-online.ps1
```

**Terminal 2 — ngrok tunnel**
```powershell
cd EasyEDALoader-main\altium-mcp
powershell -ExecutionPolicy Bypass -File scripts\start-ngrok.ps1
```

Copy the `https://....ngrok-free.app` URL from ngrok, then:

1. Edit `.env` → set `MCP_PUBLIC_URL=https://YOUR-URL.ngrok-free.app`
2. Restart Terminal 1 (the MCP server)

## Cursor remote MCP config

Merge into `%USERPROFILE%\.cursor\mcp.json` (see `cursor-mcp-remote.example.json`):

```json
{
  "mcpServers": {
    "altium-schematic-online": {
      "url": "https://YOUR-SUBDOMAIN.ngrok-free.app/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_MCP_API_KEY"
      }
    }
  }
}
```

Restart Cursor after editing.

## Altium workflow

1. Open schematic sheet
2. Run **Export Connectivity for MCP**
3. AI calls `reload_connectivity` (or upload via `scripts\upload-connectivity.ps1`)

## Security

- Online mode requires `Authorization: Bearer <MCP_API_KEY>` on `/mcp`
- Upload endpoint `/api/connectivity` also requires the same key
- `/health` is public (status only, no design data)
- Keep `.env` private; never commit it
- ngrok free URLs change each restart — update `MCP_PUBLIC_URL` and Cursor config

## Local-only mode (no ngrok)

Use `cursor-mcp-local.example.json` and `scripts\start-local.ps1` instead.
No API key required for stdio mode.
