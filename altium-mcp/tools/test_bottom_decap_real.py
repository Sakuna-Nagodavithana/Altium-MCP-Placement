"""Verify decoupling caps target the bottom side, on the real Stmcu board."""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from placement_planner import build_all_ic_cluster_plan

data = json.loads((Path.home() / "Documents" / "AltiumEE" / "connectivity.json").read_text(encoding="utf-8"))
plan = build_all_ic_cluster_plan(data, layout_mode="pin_accurate")
moves = plan.get("moves", [])

bottom = [m for m in moves if m.get("layer") == "bottom"]
top = [m for m in moves if m.get("layer") != "bottom"]
print(f"Total moves: {len(moves)}")
print(f"Bottom-side (decoupling) moves: {len(bottom)}")
print(f"Top-side moves: {len(top)}")
print("\nBottom-side caps (should be decoupling, directly under pin):")
for m in bottom[:8]:
    print(f"  {m['designator']}: {m.get('comment')} layer={m.get('layer')!r} mirror={m.get('mirror')} "
          f"method={m.get('method')!r} standoff={m.get('standoffMils')}mils "
          f"pin={m.get('primary_ic_pin')} net={m.get('primary_net')}")
print("\nTop-side sample (non-decoupling):")
for m in top[:4]:
    print(f"  {m['designator']}: {m.get('comment')} layer={m.get('layer')!r} mirror={m.get('mirror')} method={m.get('method')!r}")
