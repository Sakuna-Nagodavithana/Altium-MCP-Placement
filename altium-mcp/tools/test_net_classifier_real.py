"""Run the net classifier against the user's real connectivity export."""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from net_classifier import classify_nets, suggest_net_class

REAL = Path.home() / "Documents" / "AltiumEE" / "connectivity.json"


def main() -> None:
    if not REAL.exists():
        print(f"Real export not found at {REAL}")
        return
    data = json.loads(REAL.read_text(encoding="utf-8"))
    result = classify_nets(data)
    print("=== classify_nets (Stmcu real export) ===")
    for cls, nets in result.items():
        print(f"  {cls}: {len(nets)}")
        for n in nets[:20]:
            print(f"      {n}")
        if len(nets) > 20:
            print(f"      ... ({len(nets) - 20} more)")

    # Inspect a couple of RF nets to prove pin-name seeding worked.
    rf_nets = result.get("RF", [])
    if rf_nets:
        probe = rf_nets[0]
        sug = suggest_net_class(data, probe)
        print(f"\n=== suggest_net_class({probe}) ===")
        print(json.dumps({
            "net": sug["net"],
            "deterministicClass": sug["deterministicClass"],
            "pins": sug["pins"],
            "seriesNeighbors": sug["seriesNeighbors"][:6],
        }, indent=2)[:1800])

    # Inspect a Logic net to show what the residue looks like.
    logic_nets = result.get("Logic", [])
    if logic_nets:
        probe = logic_nets[0]
        sug = suggest_net_class(data, probe)
        print(f"\n=== suggest_net_class({probe}) [Logic residue] ===")
        print(json.dumps({
            "net": sug["net"],
            "deterministicClass": sug["deterministicClass"],
            "pins": sug["pins"][:4],
            "seriesNeighbors": sug["seriesNeighbors"][:4],
        }, indent=2)[:1200])


if __name__ == "__main__":
    main()
