"""Focused power-rail review for Duel_Esp_LoRa."""
from __future__ import annotations

import json
from collections import Counter, defaultdict
from pathlib import Path

data = json.loads(
    (Path.home() / "Documents" / "AltiumEE" / "connectivity.json").read_text(encoding="utf-8")
)


def rail(name: str) -> str | None:
    low = str(name or "").casefold().replace(" ", "")
    if low in {"gnd", "agnd", "dgnd", "pgnd", "vss"} or ("gnd" in low and len(low) <= 6):
        return "GND"
    if "3v3" in low or "3.3" in low:
        return "3V3"
    if low in {"5v", "+5", "+5v", "vcc5"} or (low.endswith("5v") and "3" not in low):
        return "5V"
    return None


pn = data.get("projectNets") or data.get("nets") or []
print("PROJECT:", data.get("project"))
print("PCB:", (data.get("pcb") or {}).get("path"))
print("summary:", data.get("summary"))

print("\n=== PROJECT POWER NETS ===")
for n in pn:
    name = str(n.get("name") or "")
    r = rail(name)
    if r or name in {"+5", "3v3", "3V3", "GND", "5V"}:
        conns = n.get("connections") or []
        des = sorted({str(c.get("designator")) for c in conns})
        print(f"{name!r} rail={r} conns={len(conns)} parts={des[:25]}")

names = {str(n.get("name")) for n in pn}
print("\nExact name presence:")
for t in ["GND", "3v3", "3V3", "+5", "5V", "5v"]:
    print(f"  {t!r}: {t in names}")

n_lower: dict[str, list[str]] = defaultdict(list)
for n in pn:
    n_lower[str(n.get("name") or "").casefold()].append(str(n.get("name")))
print("\nCase-only duplicate net names:")
for k, v in sorted(n_lower.items()):
    uniq = sorted(set(v))
    if len(uniq) > 1:
        print(f"  {k}: {uniq}")

# Component pin nets
net_names: Counter[str] = Counter()
for c in data.get("components") or []:
    for p in c.get("pins") or []:
        net = str(p.get("net") or "").strip()
        if net:
            net_names[net] += 1
print("\nComponent pin counts for rail-like nets:")
for k, v in sorted(net_names.items(), key=lambda kv: (-kv[1], kv[0])):
    if rail(k) or any(x in k.casefold() for x in ("gnd", "3v3", "+5", "5v", "vbus")):
        print(f"  {k!r}: {v}")

print("\n=== SAME-NAME SHORT? ===")
gnd = {n for n in names if rail(n) == "GND"}
v3 = {n for n in names if rail(n) == "3V3"}
v5 = {n for n in names if rail(n) == "5V"}
print("GND nets:", sorted(gnd))
print("3V3 nets:", sorted(v3))
print("5V nets:", sorted(v5))
print("GND & 3V3 overlap:", sorted(gnd & v3))
print("GND & 5V overlap:", sorted(gnd & v5))
print("3V3 & 5V overlap:", sorted(v3 & v5))

print("\n=== PARTS TOUCHING 2+ RAILS (non-cap) ===")
for c in data.get("components") or []:
    des = str(c.get("designator") or "").upper()
    pref = "".join(ch for ch in des if ch.isalpha())
    rails: dict[str, set[str]] = {}
    nets: list[str] = []
    for p in c.get("pins") or []:
        net = str(p.get("net") or "").strip()
        if not net:
            continue
        nets.append(net)
        r = rail(net)
        if r:
            rails.setdefault(r, set()).add(net)
    if len(rails) >= 2 and pref not in {"C", "CB"}:
        print(
            des,
            c.get("comment"),
            {k: sorted(v) for k, v in rails.items()},
            "nets",
            sorted(set(nets)),
        )

print("\n=== 0R / DNP BETWEEN RAILS ===")
for c in data.get("components") or []:
    des = str(c.get("designator") or "").upper()
    comment = str(c.get("comment") or "").upper().replace(" ", "")
    nets = [
        str(p.get("net") or "").strip()
        for p in (c.get("pins") or [])
        if str(p.get("net") or "").strip()
    ]
    nets = list(dict.fromkeys(nets))
    if len(nets) < 2:
        continue
    rails = {rail(n) for n in nets}
    rails.discard(None)
    is0 = any(x in comment for x in ("0OHM", "0R", "0OHM", "DNP")) or comment in {"0", "0R"}
    if ("0" in comment and "OHM" in comment) or comment.startswith("0") or "DNP" in comment:
        if len(rails) >= 2:
            print(des, c.get("comment"), nets, rails)

# Sheet net naming split
print("\n=== SHEET NET LABEL SPLIT (3v3 vs 3V3) ===")
for sheet in data.get("schematics") or []:
    path = sheet.get("path") or sheet.get("sheet")
    local = Counter()
    for c in sheet.get("components") or []:
        for p in c.get("pins") or []:
            net = str(p.get("net") or "").strip()
            if rail(net) in {"3V3", "5V", "GND"} or net in {"+5", "3v3", "3V3"}:
                local[net] += 1
    print(Path(str(path)).name, dict(local))

# PCB net list if present
pcb = data.get("pcb") or {}
print("\n=== PCB STRUCTURE ===")
print("keys", list(pcb.keys()))
for key in ("nets", "netClasses", "planes", "layers"):
    val = pcb.get(key)
    if val is None:
        continue
    print(key, type(val), len(val) if hasattr(val, "__len__") else val)
    if isinstance(val, list) and val and isinstance(val[0], dict):
        # find power
        for n in val:
            name = str(n.get("name") or n.get("net") or "")
            if rail(name) or name in {"+5", "3v3", "3V3", "GND"}:
                print(" ", name, {k: n.get(k) for k in list(n.keys())[:8]})

# Suspicious: U4 GND pin not on GND
print("\n=== IC GND/POWER PIN MISMATCHES ===")
for c in data.get("components") or []:
    des = str(c.get("designator") or "").upper()
    pref = "".join(ch for ch in des if ch.isalpha())
    if pref not in {"U", "IC", "J", "P", "H", "Q", "X", "Y"} and not des.startswith(("U", "IC")):
        continue
    for p in c.get("pins") or []:
        pname = str(p.get("name") or "").upper()
        net = str(p.get("net") or "").strip()
        r = rail(net)
        if any(t in pname for t in ("GND", "VSS", "AGND", "PGND")) and r != "GND":
            print(f"  {des}.{p.get('number')} {pname} -> {net!r} (expected GND)")
        if any(t in pname for t in ("3V3", "VDD3", "VDD3P3")) and r not in ("3V3", None) and r == "5V":
            print(f"  {des}.{p.get('number')} {pname} -> {net!r} (3V3 pin on 5V)")
        if pname in {"5V", "VBUS", "+5V"} and r not in ("5V", None) and r == "GND":
            print(f"  {des}.{p.get('number')} {pname} -> {net!r} (5V pin on GND)")

print("\n=== ERC ===")
print("top-level erc:", len(data.get("ercViolations") or []))
for sheet in data.get("schematics") or []:
    ev = sheet.get("ercViolations") or []
    print(Path(str(sheet.get("path") or "")).name, "erc", len(ev))

# Duplicate designators
print("\n=== DUPLICATE DESIGNATORS IN COMPONENTS[] ===")
des_count = Counter(str(c.get("designator") or "").upper() for c in data.get("components") or [])
for d, n in des_count.most_common():
    if n > 1:
        sheets = [
            Path(str(c.get("sheet") or "")).name
            for c in data.get("components") or []
            if str(c.get("designator") or "").upper() == d
        ]
        print(d, n, sheets)
