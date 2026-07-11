"""Check if PCB pin coordinates exist in the export (causes overlap if missing)."""
import json
from pathlib import Path

data = json.loads((Path.home() / "Documents" / "AltiumEE" / "connectivity.json").read_text(encoding="utf-8"))
pcb = data.get("pcb", {})
comps = pcb.get("components", [])
print(f"PCB components: {len(comps)}")
if comps:
    c = comps[0]
    pins = c.get("pins", [])
    des = c.get("designator", "?")
    print(f"First comp {des}: {len(pins)} pins")
    if pins:
        p = pins[0]
        print(f"  pin keys: {list(p.keys())}")
        print(f"  pin xMils: {p.get('xMils')}, yMils: {p.get('yMils')}")
    else:
        print("  NO pins on PCB component")
else:
    print("NO PCB components in export")

plan_path = Path.home() / "Documents" / "AltiumEE" / "placement_plan.json"
if plan_path.exists():
    plan = json.loads(plan_path.read_text(encoding="utf-8"))
    moves = plan.get("moves", [])
    print(f"\nPlan moves: {len(moves)}")
    if moves:
        for m in moves[:5]:
            print(f"  {m.get('designator')}: method={m.get('method')} layer={m.get('layer')} "
                  f"x={m.get('xMils')} y={m.get('yMils')} pin={m.get('primary_ic_pin')}")
else:
    print("\nNo placement_plan.json found")
