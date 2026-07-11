#!/usr/bin/env python3
"""MCP server for querying Altium schematic connectivity exported to JSON."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path

try:
    from dotenv import load_dotenv

    load_dotenv(Path(__file__).resolve().parent / ".env")
except ImportError:
    pass

from mcp.server.auth.settings import AuthSettings
from mcp.server.fastmcp import FastMCP
from mcp.server.transport_security import TransportSecuritySettings
from pydantic import AnyHttpUrl
from starlette.requests import Request
from starlette.responses import JSONResponse, Response

from auth import StaticApiKeyVerifier, is_authorized_request
from config import (
    get_connectivity_path,
    get_host,
    get_port,
    get_public_url,
    get_transport,
    is_online_mode,
    public_hostname,
    public_scheme,
    require_api_key,
)
from connectivity_store import ConnectivityStore

SAMPLE_PATH = Path(__file__).resolve().parent / "sample" / "connectivity.json"
store = ConnectivityStore(path=Path(get_connectivity_path()))

if not Path(get_connectivity_path()).exists() and SAMPLE_PATH.exists():
    os.environ.setdefault("ALTIUM_CONNECTIVITY_FILE", str(SAMPLE_PATH))
    store = ConnectivityStore(path=SAMPLE_PATH)


def _build_transport_security() -> TransportSecuritySettings:
    hostname = public_hostname()
    scheme = public_scheme()
    hosts = [
        "127.0.0.1:*",
        "localhost:*",
        "[::1]:*",
        hostname,
        f"{hostname}:*",
        "*.ngrok-free.app",
        "*.ngrok-free.app:*",
        "*.ngrok-free.dev",
        "*.ngrok-free.dev:*",
        "*.ngrok.app",
        "*.ngrok.app:*",
    ]
    origins = [
        "http://127.0.0.1:*",
        "http://localhost:*",
        "http://[::1]:*",
        f"{scheme}://{hostname}",
        f"{scheme}://{hostname}:*",
        "https://*.ngrok-free.app",
        "https://*.ngrok-free.app:*",
        "https://*.ngrok-free.dev",
        "https://*.ngrok-free.dev:*",
        "https://*.ngrok.app",
        "https://*.ngrok.app:*",
    ]
    return TransportSecuritySettings(
        enable_dns_rebinding_protection=True,
        allowed_hosts=hosts,
        allowed_origins=origins,
    )


def _create_server() -> FastMCP:
    online = is_online_mode()
    kwargs: dict = {
        "name": "altium-schematic",
        "instructions": (
            "Query real Altium schematic connectivity from a JSON export. "
            "Use these tools to verify net connections instead of guessing from images. "
            "Use trace_connection to follow the signal path between two components (returns steps and a mermaid diagram). "
            "For power/decoupling questions (3v3 to VDDP3P, VDDA, etc.), use trace_power_path starting from the IC pin, not trace_connection from the rail. "
            "If data looks stale or has_usable_nets is false, ask the user to run "
            "'Export Project for MCP' in Altium, then call reload_connectivity."
        ),
    }

    if online:
        api_key = require_api_key()
        public_url = get_public_url()
        kwargs.update(
            {
                "host": get_host(),
                "port": get_port(),
                "stateless_http": True,
                "transport_security": _build_transport_security(),
                "token_verifier": StaticApiKeyVerifier(api_key),
                "auth": AuthSettings(
                    issuer_url=AnyHttpUrl(f"{public_url}/"),
                    resource_server_url=AnyHttpUrl(f"{public_url}/mcp"),
                    required_scopes=["mcp:read"],
                ),
            }
        )

    server = FastMCP(**kwargs)
    _register_tools(server)

    if online:
        _register_online_routes(server, require_api_key())

    return server


def _json(data: object) -> str:
    return json.dumps(data, indent=2)


def _register_tools(server: FastMCP) -> None:
    @server.tool()
    def get_connectivity_status() -> str:
        """Return whether connectivity JSON is loaded, its path, and basic counts."""
        try:
            return _json(store.status())
        except Exception as exc:
            return _json({"error": str(exc), **store.status()})

    @server.tool()
    def reload_connectivity(file_path: str = "") -> str:
        """Reload connectivity JSON from disk. Optionally pass a custom file path."""
        try:
            data = store.load(file_path or None, force=True)
            return _json(
                {
                    "reloaded": True,
                    "status": store.status(),
                    "sheet": data.get("sheet"),
                    "project": data.get("project"),
                }
            )
        except Exception as exc:
            return _json({"reloaded": False, "error": str(exc), "status": store.status()})

    @server.tool()
    def list_components(query: str = "") -> str:
        """List schematic components. Optional query matches designator, lib ref, or comment."""
        try:
            components = store.list_components(query or None)
            summary = [
                {
                    "designator": item.get("designator"),
                    "comment": item.get("comment"),
                    "jlcpcb": item.get("jlcpcb"),
                    "pin_count": len(item.get("pins", [])),
                }
                for item in components
            ]
            return _json({"count": len(summary), "components": summary})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_component(designator: str) -> str:
        """Get full details for one component, including all pin nets."""
        try:
            component = store.get_component(designator)
            if component is None:
                return _json({"found": False, "designator": designator})
            return _json({"found": True, "component": component})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_nets(query: str = "") -> str:
        """List schematic nets. Optional query filters by net name."""
        try:
            nets = store.list_nets(query or None)
            summary = [
                {
                    "name": net.get("name"),
                    "connection_count": len(net.get("connections", [])),
                }
                for net in nets
            ]
            return _json({"count": len(summary), "nets": summary})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_net(name: str) -> str:
        """Get all connections on a net."""
        try:
            net = store.get_net(name)
            if net is None:
                return _json({"found": False, "name": name})
            return _json({"found": True, "net": net})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_pin_connection(designator: str, pin: str) -> str:
        """Get the net connected to a specific component pin (pin number or pin name)."""
        try:
            return _json(store.get_pin_connection(designator, pin))
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def trace_connection(
        from_designator: str,
        to_designator: str,
        from_pin: str = "",
        to_pin: str = "",
        sheet: str = "",
    ) -> str:
        """Trace the schematic signal path between two components and return hops plus a mermaid diagram."""
        try:
            return _json(
                store.trace_connection(
                    from_designator,
                    to_designator,
                    from_pin=from_pin,
                    to_pin=to_pin,
                    sheet=sheet or None,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def trace_power_path(
        designator: str,
        pin: str,
        source_rail: str = "3v3",
        sheet: str = "",
    ) -> str:
        """Trace from a power rail to a specific IC pin; list local + rail decoupling caps. Use pin name (VDDP3P) or pin number."""
        try:
            return _json(
                store.trace_power_path(
                    designator,
                    pin,
                    source_rail=source_rail or "3v3",
                    sheet=sheet or None,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_erc_violations() -> str:
        """Return ERC violations included in the export, if any."""
        try:
            violations = store.get_erc_violations()
            return _json({"count": len(violations), "violations": violations})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_sheets() -> str:
        """List all schematic sheets exported from the project."""
        try:
            sheets = store.list_sheets()
            return _json({"count": len(sheets), "sheets": sheets})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_sheet(sheet_name: str) -> str:
        """Get one schematic sheet with its components and nets."""
        try:
            sheet = store.get_sheet(sheet_name)
            if sheet is None:
                return _json({"found": False, "sheet": sheet_name})
            return _json({"found": True, "sheet": sheet})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_component_placement(designator: str, sheet: str = "") -> str:
        """Get schematic X/Y placement for a component."""
        try:
            return _json(store.get_component_placement(designator, sheet=sheet or None))
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_pcb_components(query: str = "") -> str:
        """List PCB components with pattern, layer, and pad counts."""
        try:
            components = store.list_pcb_components(query or None)
            summary = [
                {
                    "designator": item.get("designator"),
                    "pattern": item.get("pattern"),
                    "layer": item.get("layer"),
                    "pin_count": len(item.get("pins", [])),
                }
                for item in components
            ]
            return _json({"count": len(summary), "components": summary})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_pcb_component(designator: str) -> str:
        """Get full PCB component details including pads and nets."""
        try:
            component = store.get_pcb_component(designator)
            if component is None:
                return _json({"found": False, "designator": designator})
            return _json({"found": True, "component": component})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_pcb_nets(query: str = "") -> str:
        """List PCB nets from the exported board data."""
        try:
            nets = store.list_pcb_nets(query or None)
            summary = [
                {
                    "name": net.get("name"),
                    "connection_count": len(net.get("connections", [])),
                }
                for net in nets
            ]
            return _json({"count": len(summary), "nets": summary})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_pcb_net(name: str) -> str:
        """Get all pad connections on a PCB net."""
        try:
            net = store.get_pcb_net(name)
            if net is None:
                return _json({"found": False, "name": name})
            return _json({"found": True, "net": net})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_pcb_component_placement(designator: str) -> str:
        """Get PCB X/Y placement and rotation for a component."""
        try:
            return _json(store.get_pcb_component_placement(designator))
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_ic_support_components(
        designator: str,
        same_sheet_only: bool = True,
        max_schematic_distance_mils: float = 2500.0,
    ) -> str:
        """List local passives near an anchor IC (same sheet, nearby schematic, not whole-board 3v3)."""
        try:
            return _json(
                store.get_ic_support_components(
                    designator,
                    same_sheet_only=same_sheet_only,
                    max_schematic_distance_mils=max_schematic_distance_mils,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def build_ic_placement_plan(
        designator: str,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
    ) -> str:
        """Preview compact PCB cluster targets around an IC (default: tight ring, not 1:1 schematic mirror)."""
        try:
            return _json(
                store.build_ic_placement_plan(
                    designator,
                    spacing_mils=spacing_mils,
                    layout_mode=layout_mode,
                    max_radius_mils=max_radius_mils,
                    max_schematic_distance_mils=max_schematic_distance_mils,
                    same_sheet_only=same_sheet_only,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def generate_ic_placement_plan(
        designator: str,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
    ) -> str:
        """Write placement_plan.json for Altium Apply button (compact local cluster by default)."""
        try:
            return _json(
                store.generate_ic_placement_plan(
                    designator,
                    spacing_mils=spacing_mils,
                    layout_mode=layout_mode,
                    max_radius_mils=max_radius_mils,
                    max_schematic_distance_mils=max_schematic_distance_mils,
                    same_sheet_only=same_sheet_only,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def generate_all_ic_cluster_plan(
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
    ) -> str:
        """Write one combined placement_plan.json for every IC/U cluster (pin_near + chains)."""
        try:
            return _json(
                store.generate_all_ic_cluster_plan(
                    spacing_mils=spacing_mils,
                    layout_mode=layout_mode,
                    max_radius_mils=max_radius_mils,
                    max_schematic_distance_mils=max_schematic_distance_mils,
                    same_sheet_only=same_sheet_only,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_placement_plan() -> str:
        """Read the current placement_plan.json (moves, chains, verification)."""
        try:
            return _json(store.get_placement_plan())
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def update_placement_move(
        designator: str,
        x_mils: float,
        y_mils: float,
        rotation: float | None = None,
        reverify: bool = True,
    ) -> str:
        """Adjust one planned move in placement_plan.json (use before Apply in Altium)."""
        try:
            return _json(
                store.update_placement_move(
                    designator,
                    x_mils=x_mils,
                    y_mils=y_mils,
                    rotation=rotation,
                    reverify=reverify,
                )
            )
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def get_pcb_design_summary() -> str:
        """Summarize PCB routing, trace widths, planes, stackup, and validation export."""
        try:
            return _json(store.get_pcb_design_summary())
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_pcb_tracks(limit: int = 100) -> str:
        """List exported PCB track segments with width and net (requires fresh export)."""
        try:
            data = store.ensure_loaded()
            tracks = ((data.get("pcb") or {}).get("routing") or {}).get("tracks") or []
            return _json({"count": len(tracks), "tracks": tracks[: max(1, min(limit, 500))]})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def list_pcb_planes(limit: int = 50) -> str:
        """List exported copper pours / planes including ground and power nets."""
        try:
            data = store.ensure_loaded()
            planes = ((data.get("pcb") or {}).get("planes") or {}).get("polygons") or []
            return _json({"count": len(planes), "planes": planes[: max(1, min(limit, 200))]})
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def classify_nets() -> str:
        """Deterministic RF / PWR / HighSpeed / Logic net classification with series-chain propagation.

        Seeds from net-name tokens and pin-name tokens (RF/ANT/D+/XTAL...), then propagates
        RF and HighSpeed through 2-pin series passives (caps, inductors, resistors, ferrites,
        diodes) so unnamed nodes inside Pi filters / matching networks / series terminations
        inherit the right class. PWR is net-name-only and never propagates. Logic is the catch-all.
        Mirrors the C# tracer used by the Altium 'Setup Net Classes & Rules' button.
        """
        try:
            return _json(store.classify_nets())
        except Exception as exc:
            return _json({"error": str(exc)})

    @server.tool()
    def suggest_net_class(net: str) -> str:
        """Return trace context for one net so an agent can propose a class for the residue.

        Use after classify_nets when a net looks misclassified. Returns the deterministic class,
        every pin on the net (pin name + component comment), and 1-2 hop series-passive neighbors
        so you can see the matching/filter chain. Propagate a neighbor's class if the net sits on
        the chain. Ask the user for the signal name or frequency if still ambiguous.
        """
        try:
            return _json(store.suggest_net_class(net))
        except Exception as exc:
            return _json({"error": str(exc)})


def _register_online_routes(server: FastMCP, api_key: str) -> None:
    @server.custom_route("/health", methods=["GET"])
    async def health(_: Request) -> Response:
        return JSONResponse({"status": "ok", "service": "altium-schematic-mcp"})

    @server.custom_route("/api/info", methods=["GET"])
    async def agent_info(_: Request) -> Response:
        """Public connection metadata for AI agents (no design data, no secrets)."""
        public_url = get_public_url().rstrip("/")
        host = get_host()
        port = get_port()
        local_base = f"http://{host}:{port}"
        try:
            status = store.status()
        except Exception as exc:
            status = {"loaded": False, "error": str(exc), "file": str(store.resolve_path())}

        return JSONResponse(
            {
                "service": "altium-schematic-mcp",
                "transport": "streamable-http",
                "endpoints": {
                    "mcp": f"{local_base}/mcp",
                    "mcp_public": f"{public_url}/mcp",
                    "health": f"{local_base}/health",
                    "info": f"{local_base}/api/info",
                },
                "authentication": {
                    "required_for_mcp": True,
                    "header": "Authorization",
                    "format": "Bearer <MCP_API_KEY>",
                    "alternate_header": "X-Api-Key",
                },
                "tools": [
                    "get_connectivity_status",
                    "reload_connectivity",
                    "list_sheets",
                    "get_sheet",
                    "list_components",
                    "get_component",
                    "list_nets",
                    "get_net",
                    "get_pin_connection",
                    "trace_connection",
                    "trace_power_path",
                    "get_erc_violations",
                    "get_component_placement",
                    "list_pcb_components",
                    "get_pcb_component",
                    "list_pcb_nets",
                    "get_pcb_net",
                    "get_pcb_component_placement",
                    "get_ic_support_components",
                    "build_ic_placement_plan",
                    "generate_ic_placement_plan",
                    "generate_all_ic_cluster_plan",
                    "get_placement_plan",
                    "update_placement_move",
                    "get_pcb_design_summary",
                    "list_pcb_tracks",
                    "list_pcb_planes",
                    "classify_nets",
                    "suggest_net_class",
                ],
                "workflow": [
                    "Open project in Altium Designer with EasyEDA Loader extension installed.",
                    "Extension auto-exports connectivity.json and starts this MCP server.",
                    "Agent calls reload_connectivity if data may be stale.",
                    "Use trace_power_path for rail-to-pin decoupling (e.g. 3v3 to IC1 VDDP3P); use trace_connection for signal paths.",
                    "Use build_ic_placement_plan / generate_all_ic_cluster_plan for pin_near + chain placement; update_placement_move to fix positions.",
                    "Use get_pcb_design_summary / list_pcb_tracks / list_pcb_planes for PCB validation after export.",
                    "Do not guess connectivity from schematic PDFs when MCP data is available.",
                ],
                "connectivity": status,
            }
        )

    @server.custom_route("/api/connectivity", methods=["POST"])
    async def upload_connectivity(request: Request) -> Response:
        if not is_authorized_request(
            request.headers.get("authorization"),
            request.headers.get("x-api-key"),
            api_key,
        ):
            return JSONResponse({"error": "Unauthorized"}, status_code=401)

        try:
            payload = await request.json()
        except Exception:
            return JSONResponse({"error": "Invalid JSON body"}, status_code=400)

        if not isinstance(payload, dict):
            return JSONResponse({"error": "Expected a JSON object"}, status_code=400)

        target = store.resolve_path()
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        store.load(str(target), force=True)

        return JSONResponse(
            {
                "uploaded": True,
                "file": str(target),
                "status": store.status(),
            }
        )


mcp = _create_server()


if __name__ == "__main__":
    transport = get_transport()
    if transport in {"http", "streamable-http", "online"}:
        print(f"Starting secure online MCP on http://{get_host()}:{get_port()}/mcp", file=sys.stderr)
        print(f"Public URL (set MCP_PUBLIC_URL): {get_public_url()}/mcp", file=sys.stderr)
        mcp.run(transport="streamable-http")
    else:
        mcp.run(transport="stdio")
