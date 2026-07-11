#!/usr/bin/env python3
"""Diff Python vs C# all-cluster plans on a connectivity export."""

from __future__ import annotations

import json
import subprocess
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXE = ROOT / "tools" / "PlacementPlannerParity" / "bin" / "Release" / "PlacementPlannerParity.exe"


def main() -> int:
    input_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.home() / "Documents" / "AltiumEE" / "connectivity.json"
    data = json.loads(input_path.read_text(encoding="utf-8"))

    from placement_planner import build_all_ic_cluster_plan

    py_plan = build_all_ic_cluster_plan(data)
    py_path = Path(__file__).with_name(".parity_py_plan.json")
    cs_path = Path(__file__).with_name(".parity_cs_plan.json")
    py_path.write_text(json.dumps(py_plan, indent=2), encoding="utf-8")

    proc = subprocess.run(
        [str(EXE), str(input_path), "all", "IC1", str(cs_path)],
        capture_output=True,
        text=True,
        check=False,
    )
    if proc.returncode not in {0, 3} or not cs_path.exists():
        print(proc.stdout)
        print(proc.stderr, file=sys.stderr)
        return 1

    cs_plan = json.loads(cs_path.read_text(encoding="utf-8"))
    py_des = [m["designator"] for m in py_plan.get("moves") or []]
    cs_des = [m["designator"] for m in cs_plan.get("moves") or []]
    print("Python moves:", len(py_des), "unique:", len(set(py_des)))
    print("C# moves:", len(cs_des), "unique:", len(set(cs_des)))

    dup_py = [k for k, v in Counter(py_des).items() if v > 1]
    dup_cs = [k for k, v in Counter(cs_des).items() if v > 1]
    print("Duplicate designators py:", dup_py)
    print("Duplicate designators cs:", dup_cs)

    only_cs = sorted(set(cs_des) - set(py_des))
    only_py = sorted(set(py_des) - set(cs_des))
    print("Only in C#:", len(only_cs), only_cs)
    print("Only in Python:", len(only_py), only_py)
    return 0 if len(py_des) == len(cs_des) and not dup_cs else 1


if __name__ == "__main__":
    raise SystemExit(main())
