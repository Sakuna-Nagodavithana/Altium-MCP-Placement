"""Fetch a real resistor from the EasyEDA API and trace the new symbol logic.

Simulates: LayoutPins -> IsPassivePrefix -> DrawStandardPassive -> DrawResistorBox
to verify the extension would emit a proper resistor symbol, not a block.
"""
from __future__ import annotations

import json
import re
import sys
import urllib.request

LCSC = sys.argv[1] if len(sys.argv) > 1 else "C25744"  # default: 10K 0603 resistor
URL = f"https://easyeda.com/api/products/{LCSC}/components?version=6.4.19.5"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36"


def fetch() -> dict:
    req = urllib.request.Request(URL, headers={"User-Agent": UA, "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))


def parse_shape(raw: str) -> dict:
    """Return {type, ...} for one EasyEDA shape string. Mirrors EeSymbolShape.FromString."""
    if raw.startswith("R~"):  # Rectangle
        p = raw.split("~")
        return {"type": "rect", "x": float(p[1]), "y": float(p[2]),
                "w": float(p[5]), "h": float(p[6])}
    if raw.startswith("PL~"):  # Polyline
        p = raw.split("~")
        return {"type": "poly", "points": p[1]}
    if raw.startswith("PG~"):  # Polygon
        p = raw.split("~")
        return {"type": "polygon", "points": p[1]}
    if raw.startswith("E~"):  # Ellipse
        p = raw.split("~")
        return {"type": "ellipse", "cx": float(p[1]), "cy": float(p[2]),
                "rx": float(p[3]), "ry": float(p[4])}
    if raw.startswith("C~"):  # Circle
        p = raw.split("~")
        return {"type": "circle", "cx": float(p[1]), "cy": float(p[2]), "r": float(p[3])}
    if raw.startswith("A~"):  # Arc
        p = raw.split("~")
        return {"type": "arc", "path": p[1] if len(p) > 1 else ""}
    if raw.startswith("P~"):  # Path
        p = raw.split("~")
        return {"type": "path", "path": p[1] if len(p) > 1 else ""}
    if raw.startswith("P~"):  # Pin (P~...^^...^^...)
        pass
    # Pin starts with "P~" but uses ^^ separators; check after the generic prefix.
    return {"type": "unknown", "raw": raw[:40]}


def parse_pin(raw: str) -> dict:
    """Parse an EasyEDA pin shape (P~...^^...^^...). Mirrors EeSymbolPin.FromString."""
    segs = raw.split("^^")
    s0 = segs[0].split("~")
    name_seg = segs[3].split("~") if len(segs) > 3 else [""] * 9
    return {
        "type": "pin",
        "display": s0[1] if len(s0) > 1 else "",
        "pin_type": s0[2] if len(s0) > 2 else "",
        "spice_pin": s0[3] if len(s0) > 3 else "",
        "pos_x": float(s0[4]) if len(s0) > 4 else 0.0,
        "pos_y": float(s0[5]) if len(s0) > 5 else 0.0,
        "rotation": int(s0[6]) if len(s0) > 6 else 0,
        "name_text": name_seg[4] if len(name_seg) > 4 else "",
        "name_anchor": name_seg[5] if len(name_seg) > 5 else "",
        "name_rotation": int(name_seg[3]) if len(name_seg) > 3 else 0,
    }


def categorize(pin: dict) -> str:
    """Mirror SymbolDrawing.LayoutPins categorization."""
    r = pin["name_rotation"]
    a = pin["name_anchor"]
    if r == 270 and a == "end":
        return "Top"
    if r == 0 and a == "start":
        return "Left"
    if r == 0 and a == "end":
        return "Right"
    if r == 270 and a == "start":
        return "Bottom"
    return "uncategorized"


def main() -> None:
    print(f"Fetching {LCSC} from {URL}\n")
    data = fetch()
    # The result structure: usually { result: { ... } } or the component at top level.
    result = data.get("result", data)
    # Find the symbol data. EasyEDA returns the component with a "dataStr" or "shape" field.
    # The extension's Root -> Component -> Symbol parsing expects a specific structure.
    # Let's dump the keys and find the symbol shapes.
    if isinstance(result, dict):
        print("Top-level keys:", list(result.keys())[:15])
        # dataStr is usually already a dict (not a JSON string) in the current API.
        ds = result.get("dataStr")
        if isinstance(ds, str) and ds.startswith("{"):
            ds = json.loads(ds)
        if isinstance(ds, dict):
            head = ds.get("head", {}) if isinstance(ds.get("head"), dict) else {}
            cpara = head.get("c_para", {}) if isinstance(head.get("c_para"), dict) else {}
            pre = cpara.get("pre", "")
            print(f"  head.c_para.pre = {pre!r}")
            shapes = ds.get("shape", []) or ds.get("canvas", {}).get("shape", [])
            print(f"  shape count = {len(shapes)}")
            inspect_shapes(shapes, pre)
            return

    # Fallback: dump the raw structure to understand it.
    print("\nRaw (first 2000 chars):")
    print(json.dumps(data, indent=2)[:2000])


def inspect_shapes(shapes: list, pre: str) -> None:
    pins = []
    bodies = []
    for s in shapes:
        if not isinstance(s, str):
            continue
        if s.startswith("P~") and "^^" in s:
            pins.append(parse_pin(s))
        else:
            bodies.append(parse_shape(s))

    print(f"\nParsed: {len(pins)} pin(s), {len(bodies)} body shape(s)")
    for i, p in enumerate(pins):
        cat = categorize(p)
        print(f"  pin {i+1}: spice={p['spice_pin']!r} name={p['name_text']!r} "
              f"pos=({p['pos_x']},{p['pos_y']}) rot={p['rotation']} "
              f"nameRot={p['name_rotation']} anchor={p['name_anchor']!r} -> {cat}")
    for b in bodies:
        print(f"  body: {b['type']} {b}")

    # --- Simulate the extension's decision ---
    # EasyEDA sends "R?" — normalize to leading letters only, like the C# fix does.
    raw_prefix = (pre or "").strip()
    prefix = "".join(c for c in raw_prefix if c.isalpha()).upper()
    print(f"\n--- Extension logic trace ---")
    print(f"  ee_symbol.Head.Parameters.Pre = {raw_prefix!r} -> normalized prefix = {prefix!r}")
    print(f"  pins.Count = {len(pins)}")
    is_passive = prefix in ("R", "C", "CP", "CPOL", "L", "D", "LED", "TVS", "FB", "BEAD", "FERRITE", "F")
    print(f"  IsPassivePrefix({prefix!r}) = {is_passive}")
    if len(pins) == 2 and is_passive:
        print(f"  -> CreateComponent takes the DrawStandardPassive path")
        # Determine horizontal/vertical from the categorized pin sides.
        cats = [categorize(p) for p in pins]
        print(f"  pin categories: {cats}")
        if "Left" in cats and "Right" in cats:
            orient = "horizontal (Left + Right)"
        elif "Top" in cats and "Bottom" in cats:
            orient = "vertical (Top + Bottom)"
        elif cats.count("Left") == 2 or cats.count("Right") == 2:
            orient = "vertical (both on same side -> stacked)"
        elif cats.count("Top") == 2 or cats.count("Bottom") == 2:
            orient = "horizontal (both on same side -> stacked)"
        else:
            orient = f"as-is ({cats})"
        print(f"  orientation: {orient}")
        symbol_kind = {
            "R": "resistor box", "FB": "resistor box", "BEAD": "resistor box",
            "FERRITE": "resistor box", "F": "resistor box",
            "C": "capacitor plates", "CP": "capacitor plates", "CPOL": "capacitor plates (polarized)",
            "D": "diode triangle + bar", "LED": "diode triangle + bar", "TVS": "diode triangle + bar",
            "L": "inductor loops",
        }.get(prefix, "resistor box (default)")
        print(f"  DrawStandardPassive -> {symbol_kind}")
        print(f"\n  RESULT: Altium will draw a {symbol_kind} between the two pin endpoints, with leads.")
        print(f"  No fallback rectangle, no EasyEDA body art -> NOT a block.")
    else:
        print(f"  -> CreateComponent takes the DrawEasyEdaShapes / fallback path (IC or multi-pin)")


if __name__ == "__main__":
    main()
