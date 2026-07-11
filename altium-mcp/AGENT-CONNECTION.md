# AI Agent — Altium MCP Connection Details

Copy the blocks below into your AI agent (Cursor, Claude Desktop, custom agent, etc.).

**Keep your API key private.** Do not commit it to git or paste it into public chats.

---

## Quick status (no auth)

```text
Health:  http://127.0.0.1:8787/health
Info:    http://127.0.0.1:8787/api/info
```

Open `/api/info` in a browser or `curl` to see tool names and export status.

---

## Local connection (same PC as Altium)

| Setting | Value |
|---------|--------|
| **MCP URL** | `http://127.0.0.1:8787/mcp` |
| **Transport** | streamable-http |
| **Auth header** | `Authorization: Bearer <YOUR_API_KEY>` |
| **API key location** | `%USERPROFILE%\Documents\AltiumEE\mcp-settings.json` → field `ApiKey` |
| **Export file** | `%USERPROFILE%\Documents\AltiumEE\connectivity.json` |

### Cursor (`%USERPROFILE%\.cursor\mcp.json`)

```json
{
  "mcpServers": {
    "altium-schematic": {
      "url": "http://127.0.0.1:8787/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_MCP_API_KEY"
      }
    }
  }
}
```

Replace `YOUR_MCP_API_KEY` with the value from `mcp-settings.json`.

---

## Remote connection (AI agent on another machine / cloud)

1. Start MCP server (Altium extension auto-starts it, or run `altium-mcp/scripts/start-server-online.ps1`).
2. Start ngrok: `altium-mcp/scripts/start-ngrok.ps1`
3. Copy the `https://....ngrok-free.app` URL from ngrok.
4. Set in `altium-mcp/.env`: `MCP_PUBLIC_URL=https://YOUR-URL.ngrok-free.app`
5. Restart MCP server.

| Setting | Value |
|---------|--------|
| **MCP URL** | `https://YOUR-SUBDOMAIN.ngrok-free.app/mcp` |
| **Auth header** | `Authorization: Bearer <YOUR_API_KEY>` |

### Cursor remote example

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

---

## First tools to call

1. `reload_connectivity`
2. `get_connectivity_status` — confirm `has_usable_nets: true`
3. `trace_connection` — e.g. connector → IC
4. `get_net` — for GND shunts and full net membership

---

## Requirements

- Altium Designer running with a project open
- Altium-MCP-Placement extension installed
- MCP server running on port **8787**
