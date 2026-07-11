"""Verify project-level net merge and cross-sheet trace_connection."""

import json
import tempfile
from pathlib import Path

from connectivity_store import ConnectivityStore


def main() -> None:
    payload = {
        "schemaVersion": 4,
        "projectNets": [
            {
                "name": "RX_ESP",
                "connections": [
                    {"designator": "IC1", "pin": "6"},
                    {"designator": "IC2", "pin": "6"},
                ],
            },
            {
                "name": "TX_ESP",
                "connections": [
                    {"designator": "IC1", "pin": "7"},
                    {"designator": "IC2", "pin": "7"},
                ],
            },
        ],
        "components": [
            {
                "designator": "IC1",
                "comment": "ESP32-S3FH4R2",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "6", "name": "GPIO1", "net": "RX_ESP"},
                    {"number": "7", "name": "GPIO2", "net": "TX_ESP"},
                ],
            },
            {
                "designator": "IC2",
                "comment": "ESP32-S3FH4R2",
                "sheet": "ESP32_B.SchDoc",
                "pins": [
                    {"number": "6", "name": "GPIO1", "net": "RX_ESP"},
                    {"number": "7", "name": "GPIO2", "net": "TX_ESP"},
                ],
            },
        ],
        "nets": [],
    }

    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as handle:
        json.dump(payload, handle)
        path = Path(handle.name)

    store = ConnectivityStore(path=path)
    store.load(force=True)

    rx = store.get_net("RX_ESP")
    assert rx is not None
    assert len(rx["connections"]) == 2

    trace = store.trace_connection("IC1", "IC2", from_pin="6", to_pin="6")
    assert trace["connected"] is True, trace

    trace_tx = store.trace_connection("IC1", "IC2", from_pin="7", to_pin="7")
    assert trace_tx["connected"] is True, trace_tx

    listed = store.list_nets(query="ESP")
    assert len(listed) == 2

    print("cross-sheet net tests OK")


if __name__ == "__main__":
    main()
