#!/usr/bin/env python3
"""CLI: generate placement_plan.json for an anchor IC (e.g. IC1)."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from connectivity_store import ConnectivityStore, DEFAULT_CONNECTIVITY_PATH
from placement_planner import DEFAULT_PLAN_PATH


def _planner_cli_summary(plan: dict) -> dict:
    moves = plan.get("moves") or []
    return {
        "found": bool(plan.get("found")),
        "error": plan.get("error"),
        "anchor": plan.get("anchor"),
        "move_count": plan.get("move_count", len(moves)),
        "output_path": plan.get("outputPath"),
    }


def main() -> int:
    if len(sys.argv) < 2:
        print(
            "Usage: generate_placement_plan.py IC1 [spacing_mils] [max_radius_mils] "
            "[max_schematic_distance_mils] [output_path]",
            file=sys.stderr,
        )
        return 1

    designator = sys.argv[1]
    spacing = float(sys.argv[2]) if len(sys.argv) > 2 else 80.0
    max_radius = float(sys.argv[3]) if len(sys.argv) > 3 else 900.0
    max_sch_distance = float(sys.argv[4]) if len(sys.argv) > 4 else 2500.0
    output = sys.argv[5] if len(sys.argv) > 5 else str(DEFAULT_PLAN_PATH)
    layout_mode = sys.argv[6] if len(sys.argv) > 6 else "pin_accurate"

    store = ConnectivityStore(path=DEFAULT_CONNECTIVITY_PATH)
    try:
        store.load(force=True)
    except FileNotFoundError as exc:
        print(json.dumps({"found": False, "error": str(exc)}), file=sys.stderr)
        return 2

    plan = store.generate_ic_placement_plan(
        designator,
        spacing_mils=spacing,
        layout_mode=layout_mode,
        max_radius_mils=max_radius,
        max_schematic_distance_mils=max_sch_distance,
        same_sheet_only=True,
        output_path=output,
    )
    print(json.dumps(_planner_cli_summary(plan)))
    return 0 if plan.get("found") else 3


if __name__ == "__main__":
    raise SystemExit(main())
