# Altium Schematic MCP — Reference

## Topology (read first for connectivity questions)

**`trace_connection` is one path only.** For decoupling caps, shunt caps, and parallel branches, follow the mandatory **net fanout audit** in [topology.md](topology.md).

## Server endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /health` | No | Liveness check |
| `GET /api/info` | No | Connection metadata, tool list, export status (no secrets) |
| `POST /mcp` | Bearer required | MCP streamable-http (all tools) |
| `POST /api/connectivity` | Bearer required | Upload JSON export (optional) |

Default host: `127.0.0.1:8787`

## MCP tools

| Tool | Args | Returns |
|------|------|---------|
| `get_connectivity_status` | — | Export file path, schema version, net counts, `has_usable_nets` |
| `reload_connectivity` | `file_path?` | Reload JSON from disk |
| `list_sheets` | — | All schematic sheets |
| `get_sheet` | `sheet_name` | Sheet components + nets |
| `list_components` | `query?` | Filter by designator/comment/JLCPCB |
| `get_component` | `designator` | Full component + pin nets |
| `list_nets` | `query?` | Net names |
| `get_net` | `name` | **All pins on a net** — use for parallel/shunt/decoupling discovery |
| `get_pin_connection` | `designator`, `pin` | Single pin net — starting point for fanout audit |
| `trace_connection` | `from_designator`, `to_designator`, `from_pin?`, `to_pin?`, `sheet?` | **Shortest series path only** — then run get_net on every net in path |
| `trace_power_path` | `designator`, `pin`, `source_rail?`, `sheet?` | **Rail → IC pin** decoupling audit; use for 3v3→VDDP3P/VDDA questions |
| `get_erc_violations` | — | ERC from export |
| `get_component_placement` | `designator`, `sheet?` | Schematic XY |
| `list_pcb_components` | `query?` | PCB side (if exported) |
| `get_pcb_component` | `designator` | PCB component |
| `list_pcb_nets` | `query?` | PCB nets |
| `get_pcb_net` | `name` | PCB net |
| `get_pcb_component_placement` | `designator` | PCB XY |

## Cursor MCP config (local)

File: `%USERPROFILE%\.cursor\mcp.json`

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

API key: `Documents/AltiumEE/mcp-settings.json` → `ApiKey`

## Cursor MCP config (remote / ngrok)

```json
{
  "mcpServers": {
    "altium-schematic-online": {
      "url": "https://YOUR-SUBDOMAIN.ngrok-free.dev/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_MCP_API_KEY"
      }
    }
  }
}
```

After ngrok starts, update `MCP_PUBLIC_URL` in `altium-mcp/.env` and restart the MCP server.

## Generic AI agent (HTTP MCP client)

Any agent supporting **MCP streamable-http**:

1. **URL:** `http://127.0.0.1:8787/mcp` (local) or ngrok HTTPS URL
2. **Header:** `Authorization: Bearer <MCP_API_KEY>`
3. **Accept:** `application/json, text/event-stream`
4. Discover tools via MCP `tools/list`, call via `tools/call`

Preflight without auth:

```bash
curl http://127.0.0.1:8787/api/info
curl http://127.0.0.1:8787/health
```

## Files on disk

| File | Role |
|------|------|
| `Documents/AltiumEE/connectivity.json` | Exported schematic (auto-refreshed) |
| `Documents/AltiumEE/mcp-settings.json` | Python path, API key, port |
| `Documents/AltiumEE/mcp-server.log` | Server + export log |
| `altium-mcp/.env` | MCP server secrets |

## Altium side

- Extension auto-exports on startup and every ~45s while server runs.
- Manual: **Tools → EasyEDA Loader → MCP Control Panel** (optional).
- Project must be compiled (export calls `DM_Compile` automatically).
