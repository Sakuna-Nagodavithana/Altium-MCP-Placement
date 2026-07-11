"""Verify decoupling caps now target the bottom side directly under the IC pin."""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from placement_planner import build_ic_placement_plan

data = json.loads((Path(__file__).resolve().parent.parent / "sample" / "connectivity.json").read_text(encoding="utf-8"))
plan = build_ic_placement_plan(data, "U1", layout_mode="pin_accurate")
moves = plan.get("moves", [])

decap = [m for m in moves if m.get("method", "").endswith("_bottom") or "decoupling" in (m.get("roles") or [])]
print(f"Total moves: {len(moves)}")
print(f"Decoupling / bottom-side moves: {len(decap)}")
for m in decap[:6]:
    print(f"  {m['designator']}: layer={m.get('layer')!r} mirror={m.get('mirror')} "
          f"method={m.get('method')!r} standoff={m.get('standoffMils')}mils "
          f"role={m.get('roles')}")

print("\nNon-decoupling sample (should stay top, mirror=False):")
for m in moves[:4]:
    if not m.get("method", "").endswith("_bottom"):
        print(f"  {m['designator']}: layer={m.get('layer')!r} mirror={m.get('mirror')} method={m.get('method')!r}")
