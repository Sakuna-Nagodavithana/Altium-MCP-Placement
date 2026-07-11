"""Smoke test for MCP server module and v2 store queries."""

import os
from pathlib import Path

import server


def main() -> None:
    sample = Path(__file__).resolve().parent / "sample" / "connectivity.json"
    os.environ["ALTIUM_CONNECTIVITY_FILE"] = str(sample)
    server.store = server.ConnectivityStore(path=sample)
    server.store.load(force=True)

    assert server.mcp is not None
    status = server.store.status()
    assert status["schema_version"] == 2
    assert status["sheet_count"] == 1
    assert status["pcb_component_count"] == 2

    assert len(server.store.list_sheets()) == 1
    assert server.store.get_sheet("Sheet1.SchDoc") is not None
    assert server.store.get_component_placement("U1")["found"] is True
    assert server.store.get_pcb_component("U1") is not None
    assert server.store.get_pcb_net("NRST") is not None
    assert server.store.get_pcb_component_placement("R1")["found"] is True

    trace = server.store.trace_connection("U1", "R1")
    assert trace["connected"] is True
    assert "R1" in trace["components_along_path"]
    assert trace["mermaid"]

    print("MCP module OK:", status)


if __name__ == "__main__":
    main()
