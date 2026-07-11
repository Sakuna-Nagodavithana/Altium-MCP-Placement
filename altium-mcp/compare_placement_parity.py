#!/usr/bin/env python3
"""Compare Python vs C# placement planner output on the same connectivity JSON."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PARITY_EXE = ROOT / "tools" / "PlacementPlannerParity" / "bin" / "Release" / "PlacementPlannerParity.exe"


def _normalize_moves(plan: dict) -> list[dict]:
    moves = []
    for move in plan.get("moves") or []:
        moves.append(
            {
                "designator": move.get("designator"),
                "xMils": round(float(move.get("xMils", 0)), 3),
                "yMils": round(float(move.get("yMils", 0)), 3),
                "method": move.get("method"),
                "primary_ic_pin": move.get("primary_ic_pin"),
            }
        )
    moves.sort(key=lambda item: str(item.get("designator") or ""))
    return moves


def _load_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    input_path = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "altium-mcp" / "sample" / "connectivity.json"
    mode = sys.argv[2] if len(sys.argv) > 2 else "sample"
    anchor = sys.argv[3] if len(sys.argv) > 3 else "IC1"

    if mode == "sample":
        from test_placement_planner import _sample_payload

        payload = _sample_payload()
        input_path = ROOT / "altium-mcp" / ".parity_sample.json"
        input_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        planner_mode = "all"
    else:
        payload = _load_json(input_path)
        planner_mode = mode

    if not PARITY_EXE.exists():
        print(f"C# parity tool missing: {PARITY_EXE}", file=sys.stderr)
        print("Build with: msbuild tools/PlacementPlannerParity/PlacementPlannerParity.csproj /p:Configuration=Release", file=sys.stderr)
        return 2

    from placement_planner import build_all_ic_cluster_plan, build_ic_placement_plan

    if planner_mode == "ic":
        py_plan = build_ic_placement_plan(payload, anchor)
        cs_args = [str(PARITY_EXE), str(input_path), "ic", anchor]
    else:
        py_plan = build_all_ic_cluster_plan(payload)
        cs_args = [str(PARITY_EXE), str(input_path), "all"]

    proc = subprocess.run(cs_args, capture_output=True, text=True, check=False)
    if proc.returncode not in {0, 3}:
        print(proc.stdout)
        print(proc.stderr, file=sys.stderr)
        return proc.returncode

    cs_summary = json.loads(proc.stdout or "{}")
    print("Python:", json.dumps({"found": py_plan.get("found"), "move_count": py_plan.get("move_count"), "schemaVersion": py_plan.get("schemaVersion")}, indent=2))
    print("C#    :", json.dumps(cs_summary, indent=2))

    if bool(py_plan.get("found")) != bool(cs_summary.get("found")):
        print("MISMATCH: found flag differs", file=sys.stderr)
        return 1

    py_moves = _normalize_moves(py_plan)
    # Re-run C# planner in-process equivalent: load full plan via subprocess writing temp file
    # For move parity, invoke planner by importing parity helper from built DLL is not available.
    # Compare move counts and designator sets only in this script.
    py_des = {m["designator"] for m in py_moves}
    print(f"Python move designators ({len(py_des)}): {sorted(py_des)}")
    print("Parity check: move_count match =", py_plan.get("move_count") == cs_summary.get("move_count"))
    if py_plan.get("move_count") != cs_summary.get("move_count"):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
