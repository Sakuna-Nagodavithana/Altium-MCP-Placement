"""Smoke-test the net classifier on the sample connectivity export."""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from net_classifier import classify_nets, suggest_net_class

SAMPLE = Path(__file__).resolve().parent.parent / "sample" / "connectivity.json"


def main() -> None:
    data = json.loads(SAMPLE.read_text(encoding="utf-8"))
    result = classify_nets(data)
    print("=== classify_nets (sample) ===")
    for cls, nets in result.items():
        print(f"  {cls}: {len(nets)} -> {nets[:12]}{' ...' if len(nets) > 12 else ''}")

    # Find a net on the RF IC (SX1262 in sample) to inspect suggestion.
    rf_pin_net = None
    for comp in data.get("components", []):
        comment = str(comp.get("comment") or "").upper()
        if "SX126" in comment or "MCU" in comment:
            for pin in comp.get("pins", []):
                name = str(pin.get("name") or "").upper()
                if "RF" in name or "D+" in name or "XTAL" in name or "USB" in name:
                    rf_pin_net = pin.get("net")
                    print(f"\nSeed pick: {comp['designator']}.{pin['number']} ({pin['name']}) -> net {rf_pin_net}")
                    break
            if rf_pin_net:
                break

    if rf_pin_net:
        sug = suggest_net_class(data, rf_pin_net)
        print(f"\n=== suggest_net_class({rf_pin_net}) ===")
        print(json.dumps({
            "net": sug["net"],
            "deterministicClass": sug["deterministicClass"],
            "pins": sug["pins"],
            "seriesNeighbors": sug["seriesNeighbors"],
        }, indent=2)[:1500])


if __name__ == "__main__":
    main()
