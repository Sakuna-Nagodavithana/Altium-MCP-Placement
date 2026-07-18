"""Geometric clearance / short-risk review for Duel_Esp_LoRa PCB copper."""
from __future__ import annotations

import json
import math
from collections import Counter, defaultdict
from pathlib import Path

DATA = json.loads(
    (Path.home() / "Documents" / "AltiumEE" / "connectivity.json").read_text(encoding="utf-8")
)
pcb = DATA["pcb"]
tracks = pcb["routing"]["tracks"]
vias = pcb["routing"]["vias"]
comps = pcb.get("components") or []

# Prefer electrical copper only when exporter tagged layers correctly.
if any(t.get("electrical") is True for t in tracks):
    tracks = [t for t in tracks if t.get("electrical") is True]
    print(f"Using electrical tracks only: {len(tracks)}")
else:
    print(f"No electrical flags in export; using all tracks: {len(tracks)}")

POWER = {"GND", "3v3", "+5", "3V3"}


def rail(n: str) -> str | None:
    t = (n or "").strip()
    if not t or t in {"No Net", "no net"}:
        return None
    low = t.casefold()
    if low == "gnd" or (low.startswith("gnd") and len(low) <= 6):
        return "GND"
    if "3v3" in low or "3.3" in low:
        return "3V3"
    if low in {"+5", "5v", "+5v"}:
        return "5V"
    return None


# Collect pads with XY
pads = []
for c in comps:
    des = c.get("designator")
    for p in c.get("pins") or []:
        x, y = p.get("xMils"), p.get("yMils")
        if x is None or y is None:
            continue
        pads.append(
            {
                "des": des,
                "pin": p.get("name"),
                "net": p.get("net") or "",
                "rail": rail(p.get("net") or ""),
                "x": float(x),
                "y": float(y),
            }
        )

print(f"pads with XY: {len(pads)}")
by_rail = defaultdict(list)
for p in pads:
    if p["rail"]:
        by_rail[p["rail"]].append(p)

# Pad-to-pad proximity between different rails (possible copper/solder short risk)
print("\n=== PAD PROXIMITY BETWEEN DIFFERENT POWER RAILS (< 20 mil center distance) ===")
pairs = [("GND", "3V3"), ("GND", "5V"), ("3V3", "5V")]
THRESH = 20.0  # mil centers - pads can be larger; flag close approaches
for a, b in pairs:
    hits = []
    for pa in by_rail[a]:
        for pb in by_rail[b]:
            d = math.hypot(pa["x"] - pb["x"], pa["y"] - pb["y"])
            if d < THRESH and d > 0.01:
                hits.append((d, pa, pb))
    hits.sort(key=lambda t: t[0])
    print(f"{a} vs {b}: {len(hits)} pad pairs < {THRESH} mil")
    for d, pa, pb in hits[:15]:
        print(
            f"  {d:.1f} mil  {pa['des']}.{pa['pin']}({pa['net']}) <-> {pb['des']}.{pb['pin']}({pb['net']}) "
            f"@ ({pa['x']:.0f},{pa['y']:.0f})"
        )

# Via proximity between different rails
print("\n=== VIA PROXIMITY BETWEEN DIFFERENT POWER RAILS ===")
via_rails = defaultdict(list)
for v in vias:
    r = rail(v.get("net") or "")
    if r:
        via_rails[r].append(v)

for a, b in pairs:
    hits = []
    for va in via_rails[a]:
        for vb in via_rails[b]:
            d = math.hypot(va["xMils"] - vb["xMils"], va["yMils"] - vb["yMils"])
            # annular rings: size/2 + size/2 + clearance; flag if centers closer than sum of radii + 6mil
            ra = (va.get("sizeMils") or 20) / 2
            rb = (vb.get("sizeMils") or 20) / 2
            need = ra + rb + 6.0
            if d < need:
                hits.append((d, need, va, vb))
    hits.sort(key=lambda t: t[0])
    print(f"{a} vs {b} vias: {len(hits)} pairs closer than annular+6mil")
    for d, need, va, vb in hits[:12]:
        print(
            f"  dist={d:.1f} need={need:.1f}  {va.get('net')}@{va['xMils']:.0f},{va['yMils']:.0f} "
            f"<-> {vb.get('net')}@{vb['xMils']:.0f},{vb['yMils']:.0f} size={va.get('sizeMils')}/{vb.get('sizeMils')}"
        )

# Track segment proximity (sample power tracks only for speed)
print("\n=== POWER TRACK CLEARANCE (segment midpoint approx) ===")


def segs(net_names):
    out = []
    for t in tracks:
        if (t.get("net") or "") in net_names:
            out.append(t)
    return out


def seg_clearance(ta, tb):
    # distance between two finite segments (approx via endpoints + mid)
    pts_a = [
        (ta["x1Mils"], ta["y1Mils"]),
        (ta["x2Mils"], ta["y2Mils"]),
        ((ta["x1Mils"] + ta["x2Mils"]) / 2, (ta["y1Mils"] + ta["y2Mils"]) / 2),
    ]
    pts_b = [
        (tb["x1Mils"], tb["y1Mils"]),
        (tb["x2Mils"], tb["y2Mils"]),
        ((tb["x1Mils"] + tb["x2Mils"]) / 2, (tb["y1Mils"] + tb["y2Mils"]) / 2),
    ]
    best = 1e9
    for ax, ay in pts_a:
        for bx, by in pts_b:
            best = min(best, math.hypot(ax - bx, ay - by))
    return best


for a_names, b_names, label in [
    ({"GND"}, {"3v3", "3V3"}, "GND-3v3"),
    ({"GND"}, {"+5"}, "GND-+5"),
    ({"3v3", "3V3"}, {"+5"}, "3v3-+5"),
]:
    sa, sb = segs(a_names), segs(b_names)
    hits = []
    for ta in sa:
        wa = (ta.get("widthMils") or 10) / 2
        for tb in sb:
            wb = (tb.get("widthMils") or 10) / 2
            d = seg_clearance(ta, tb)
            edge = d - wa - wb
            if edge < 6.0:  # less than ~6 mil copper-to-copper
                hits.append((edge, d, ta, tb))
    hits.sort(key=lambda t: t[0])
    print(f"{label}: {len(hits)} track pairs with edge clearance < 6 mil (approx)")
    for edge, d, ta, tb in hits[:10]:
        print(
            f"  edge={edge:.1f} mil centers={d:.1f} "
            f"A({ta['x1Mils']:.0f},{ta['y1Mils']:.0f})-({ta['x2Mils']:.0f},{ta['y2Mils']:.0f}) w={ta.get('widthMils')} "
            f"B({tb['x1Mils']:.0f},{tb['y1Mils']:.0f})-({tb['x2Mils']:.0f},{tb['y2Mils']:.0f}) w={tb.get('widthMils')}"
        )

# Via over pad of different net
print("\n=== VIA CENTER OVER PAD OF DIFFERENT RAIL (< size/2) ===")
hits = []
for v in vias:
    vr = rail(v.get("net") or "")
    if not vr:
        continue
    r = (v.get("sizeMils") or 20) / 2
    for p in pads:
        if not p["rail"] or p["rail"] == vr:
            continue
        d = math.hypot(v["xMils"] - p["x"], v["yMils"] - p["y"])
        if d < r + 8:
            hits.append((d, r, v, p))
hits.sort(key=lambda t: t[0])
print(f"count: {len(hits)}")
for d, r, v, p in hits[:20]:
    print(
        f"  dist={d:.1f} viaR={r:.1f} via {v.get('net')}@{v['xMils']:.0f},{v['yMils']:.0f} "
        f"pad {p['des']}.{p['pin']} {p['net']}"
    )

# Empty-net tracks near power pads
print("\n=== EMPTY-NET TRACK ENDPOINTS NEAR POWER PADS (< 15 mil) ===")
empty = [t for t in tracks if not t.get("net")]
hits = []
for t in empty:
    for x, y in ((t["x1Mils"], t["y1Mils"]), (t["x2Mils"], t["y2Mils"])):
        for p in pads:
            if not p["rail"]:
                continue
            d = math.hypot(x - p["x"], y - p["y"])
            if d < 15:
                hits.append((d, t, p, x, y))
hits.sort(key=lambda t: t[0])
print(f"count: {len(hits)} (showing 25)")
# group by pad
seen = set()
shown = 0
for d, t, p, x, y in hits:
    key = (p["des"], p["pin"], p["net"])
    if key in seen:
        continue
    seen.add(key)
    print(
        f"  {d:.1f} mil empty-track near {p['des']}.{p['pin']} ({p['net']}/{p['rail']}) "
        f"track w={t.get('widthMils')} @endpoint ({x:.0f},{y:.0f})"
    )
    shown += 1
    if shown >= 25:
        break

print("\n=== SUMMARY STATS ===")
print("empty tracks", len(empty), "of", len(tracks))
print("planes", pcb.get("planes"))
print("validation", pcb.get("validation"))
