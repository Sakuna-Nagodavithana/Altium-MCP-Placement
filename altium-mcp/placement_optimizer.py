"""Two-phase PCB placement optimizer (Python mirror of C# ForceDirectedOptimizer).

Phase 1: force-directed HPWL springs + overlap repulsion
Phase 2: simulated annealing with translate / ±90° rotate / local swap

Reads connectivity.json, writes a placement_plan.json-compatible dict.
"""

from __future__ import annotations

import json
import math
import random
from pathlib import Path
from typing import Any


DEFAULT_SPACING_MILS = 28.0
LOCKED_PREFIXES = {"J", "H", "MH", "TP", "FID", "SW", "BTN", "CN", "P", "MP"}


def _prefix(designator: str) -> str:
    text = (designator or "").strip().upper()
    i = 0
    while i < len(text) and text[i].isalpha():
        i += 1
    return text[:i] if i else text


def _is_locked(designator: str, lock_ics: bool) -> bool:
    prefix = _prefix(designator)
    if prefix in LOCKED_PREFIXES:
        return True
    if lock_ics and prefix in {"U", "IC", "Q", "D", "Y", "X"}:
        return True
    return False


def _layer(value: Any) -> str:
    return "bottom" if "bottom" in str(value or "").casefold() else "top"


def _bbox_half(component: dict[str, Any]) -> tuple[float, float]:
    bbox = component.get("bboxMils") or {}
    hw = bbox.get("halfWidthMils")
    hh = bbox.get("halfHeightMils")
    if hw is None and bbox.get("widthMils") is not None:
        hw = float(bbox["widthMils"]) * 0.5
    if hh is None and bbox.get("heightMils") is not None:
        hh = float(bbox["heightMils"]) * 0.5
    return (max(float(hw or 40.0), 12.0), max(float(hh or 40.0), 12.0))


def _is_ignored_net(name: str) -> bool:
    text = (name or "").strip().upper()
    if text in {
        "GND", "AGND", "DGND", "PGND", "VCC", "VDD", "VSS",
        "3V3", "5V", "1V8", "12V",
    }:
        return True
    return text.startswith("GND") or text.startswith("VCC") or text.startswith("VDD")


def _norm_rot(rot: float) -> float:
    rot = rot % 360.0
    if rot < 0:
        rot += 360.0
    nearest = round(rot / 90.0) * 90.0
    if abs(rot - nearest) < 0.5:
        rot = nearest % 360.0
    return rot


def optimize_board(
    data: dict[str, Any],
    *,
    iterations: int = 220,
    spring_k: float = 0.045,
    repulsion_k: float = 9200.0,
    grid_mils: float = 10.0,
    spacing_mils: float = DEFAULT_SPACING_MILS,
    lock_ics: bool = True,
    anneal_steps: int = 90,
    anneal_trials: int = 28,
) -> dict[str, Any]:
    components = {
        str(c.get("designator") or "").strip().upper(): c
        for c in (data.get("pcb") or {}).get("components") or []
        if str(c.get("designator") or "").strip()
    }
    if not components:
        return {
            "found": False,
            "error": "No PCB components in connectivity export. Open the PCB and re-export.",
        }

    states: dict[str, dict[str, Any]] = {}
    initial: dict[str, tuple[float, float, float]] = {}
    for des, comp in components.items():
        placement = comp.get("placement") or {}
        if placement.get("xMils") is None or placement.get("yMils") is None:
            continue
        hw, hh = _bbox_half(comp)
        rot = _norm_rot(float(placement.get("rotation") or 0.0))
        states[des] = {
            "designator": des,
            "x": float(placement["xMils"]),
            "y": float(placement["yMils"]),
            "half_width": hw,
            "half_height": hh,
            "layer": _layer(comp.get("layer") or placement.get("layer")),
            "rotation": rot,
            "locked": _is_locked(des, lock_ics),
            "component": comp,
        }
        initial[des] = (states[des]["x"], states[des]["y"], rot)

    if not states:
        return {"found": False, "error": "No placeable components with coordinates."}

    nets: dict[str, list[dict[str, Any]]] = {}
    for st in states.values():
        for pin in st["component"].get("pins") or []:
            net = str(pin.get("net") or "").strip()
            if not net or net.casefold() == "no net" or _is_ignored_net(net):
                continue
            dx = dy = 0.0
            if pin.get("xMils") is not None and pin.get("yMils") is not None:
                dx = float(pin["xMils"]) - st["x"]
                dy = float(pin["yMils"]) - st["y"]
            nets.setdefault(net, []).append(
                {"designator": st["designator"], "dx": dx, "dy": dy}
            )
    nets = {
        name: pins
        for name, pins in nets.items()
        if len({p["designator"] for p in pins}) >= 2
    }

    def total_cost() -> float:
        cost = 0.0
        for pins in nets.values():
            if len(pins) < 2:
                continue
            xs, ys = [], []
            for pin in pins:
                st = states.get(pin["designator"])
                if not st:
                    continue
                xs.append(st["x"] + pin["dx"])
                ys.append(st["y"] + pin["dy"])
            if len(xs) >= 2:
                cost += (max(xs) - min(xs)) + (max(ys) - min(ys))

        items = list(states.values())
        preferred = spacing_mils
        for i, a in enumerate(items):
            for b in items[i + 1 :]:
                if a["layer"] != b["layer"]:
                    continue
                gap_x = abs(a["x"] - b["x"]) - (a["half_width"] + b["half_width"])
                gap_y = abs(a["y"] - b["y"]) - (a["half_height"] + b["half_height"])
                gap = max(gap_x, gap_y)
                if gap < 0:
                    cost += 180000.0 + (gap * gap) * 90.0
                elif gap < preferred:
                    d = preferred - gap
                    cost += 0.35 * 2.2 * d * d
                elif gap < preferred * 3.5:
                    over = gap - preferred
                    cost += 0.35 * 0.04 * over * over
        return cost

    def apply_rotation_turns(st: dict[str, Any], turns: int) -> None:
        turns %= 4
        if turns < 0:
            turns += 4
        for _ in range(turns):
            des = st["designator"]
            for pins in nets.values():
                for p in pins:
                    if p["designator"] != des:
                        continue
                    p["dx"], p["dy"] = -p["dy"], p["dx"]
            st["half_width"], st["half_height"] = st["half_height"], st["half_width"]
            st["rotation"] = _norm_rot(st["rotation"] + 90.0)

    cost_before = total_cost()
    movable = [s for s in states.values() if not s["locked"]]

    # Phase 1 — force-directed
    vel = {s["designator"]: [0.0, 0.0] for s in movable}
    damping = 0.82
    for it in range(iterations):
        cool = 0.35 + 0.65 * (1.0 - it / max(iterations - 1, 1))
        forces = {s["designator"]: [0.0, 0.0] for s in movable}

        for pins in nets.values():
            if len(pins) < 2:
                continue
            pts = []
            for pin in pins:
                st = states.get(pin["designator"])
                if not st:
                    continue
                pts.append((st["x"] + pin["dx"], st["y"] + pin["dy"], pin["designator"]))
            if len(pts) < 2:
                continue
            cx = sum(p[0] for p in pts) / len(pts)
            cy = sum(p[1] for p in pts) / len(pts)
            for px, py, des in pts:
                st = states[des]
                if st["locked"]:
                    continue
                forces[des][0] += spring_k * (cx - px)
                forces[des][1] += spring_k * (cy - py)

        for a in movable:
            for b in states.values():
                if a is b or a["layer"] != b["layer"]:
                    continue
                dx = a["x"] - b["x"]
                dy = a["y"] - b["y"]
                dist = math.hypot(dx, dy)
                if dist < 1e-3:
                    dx, dy, dist = 1.0, 0.0, 1.0
                min_dist = (
                    a["half_width"] + b["half_width"] + spacing_mils * 0.55
                    + abs(a["half_height"] + b["half_height"]) * 0.15
                )
                if dist >= min_dist:
                    continue
                push = repulsion_k * (min_dist - dist) / (dist * dist)
                forces[a["designator"]][0] += push * dx / dist
                forces[a["designator"]][1] += push * dy / dist

        max_step = 18.0 + 55.0 * cool
        for st in movable:
            des = st["designator"]
            vel[des][0] = damping * vel[des][0] + forces[des][0] * cool
            vel[des][1] = damping * vel[des][1] + forces[des][1] * cool
            vx, vy = vel[des]
            mag = math.hypot(vx, vy)
            if mag > max_step and mag > 1e-9:
                vx *= max_step / mag
                vy *= max_step / mag
                vel[des] = [vx, vy]
            st["x"] += vx
            st["y"] += vy

    def resolve_overlaps(gap: float) -> None:
        occupied: list[dict[str, Any]] = [
            {
                "x": st["x"],
                "y": st["y"],
                "half_width": st["half_width"],
                "half_height": st["half_height"],
                "layer": st["layer"],
            }
            for st in states.values()
            if st["locked"]
        ]

        def blocked(box: dict[str, Any]) -> bool:
            for other in occupied:
                if box["layer"] != other["layer"]:
                    continue
                if (
                    abs(box["x"] - other["x"])
                    < box["half_width"] + other["half_width"] + gap
                    and abs(box["y"] - other["y"])
                    < box["half_height"] + other["half_height"] + gap
                ):
                    return True
            return False

        for st in states.values():
            if st["locked"]:
                continue
            box = {
                "x": st["x"],
                "y": st["y"],
                "half_width": st["half_width"],
                "half_height": st["half_height"],
                "layer": st["layer"],
            }
            if blocked(box):
                for ring in range(1, 40):
                    radius = ring * max(gap, 24.0)
                    found = False
                    for angle_i in range(16):
                        angle = angle_i * (2 * math.pi / 16)
                        box["x"] = st["x"] + radius * math.cos(angle)
                        box["y"] = st["y"] + radius * math.sin(angle)
                        if not blocked(box):
                            st["x"], st["y"] = box["x"], box["y"]
                            found = True
                            break
                    if found:
                        break
            occupied.append(
                {
                    "x": st["x"],
                    "y": st["y"],
                    "half_width": st["half_width"],
                    "half_height": st["half_height"],
                    "layer": st["layer"],
                }
            )

    resolve_overlaps(spacing_mils * 0.85)

    # Phase 2 — simulated annealing with rotation
    rng = random.Random(42)
    current_cost = total_cost()
    temperature = max(800.0, current_cost * 0.012)
    cooling = 0.02 ** (1.0 / max(anneal_steps, 1))

    def accept(old: float, new: float, temp: float) -> bool:
        if new <= old:
            return True
        if temp < 1e-9:
            return False
        return rng.random() < math.exp(-(new - old) / temp)

    for step in range(anneal_steps):
        step_scale = 1.0 - step / max(anneal_steps - 1, 1)
        move_amp = 8.0 + 40.0 * step_scale
        for _ in range(anneal_trials):
            if not movable:
                break
            pick = movable[rng.randint(0, len(movable) - 1)]
            move_type = rng.randint(0, 99)
            old = (
                pick["x"],
                pick["y"],
                pick["rotation"],
                pick["half_width"],
                pick["half_height"],
            )
            pin_snap = [
                (p, p["dx"], p["dy"])
                for pins in nets.values()
                for p in pins
                if p["designator"] == pick["designator"]
            ]

            if move_type < 55:
                pick["x"] += (rng.random() * 2 - 1) * move_amp
                pick["y"] += (rng.random() * 2 - 1) * move_amp
            elif move_type < 88:
                apply_rotation_turns(pick, 1 if rng.random() < 0.5 else -1)
            else:
                near = [
                    o
                    for o in movable
                    if o is not pick
                    and o["layer"] == pick["layer"]
                    and math.hypot(o["x"] - pick["x"], o["y"] - pick["y"]) <= 350.0
                ]
                if near:
                    other = near[rng.randint(0, len(near) - 1)]
                    ox, oy = other["x"], other["y"]
                    other["x"], other["y"] = pick["x"], pick["y"]
                    pick["x"], pick["y"] = ox, oy
                    new_cost = total_cost()
                    if accept(current_cost, new_cost, temperature):
                        current_cost = new_cost
                        continue
                    pick["x"], pick["y"] = other["x"], other["y"]
                    other["x"], other["y"] = ox, oy
                    continue
                pick["x"] += (rng.random() * 2 - 1) * move_amp * 0.5
                pick["y"] += (rng.random() * 2 - 1) * move_amp * 0.5

            new_cost = total_cost()
            if accept(current_cost, new_cost, temperature):
                current_cost = new_cost
            else:
                pick["x"], pick["y"], pick["rotation"], pick["half_width"], pick["half_height"] = old
                for p, dx, dy in pin_snap:
                    p["dx"], p["dy"] = dx, dy

        temperature *= cooling

    resolve_overlaps(spacing_mils)
    if grid_mils > 0:
        for st in states.values():
            if st["locked"]:
                continue
            st["x"] = round(st["x"] / grid_mils) * grid_mils
            st["y"] = round(st["y"] / grid_mils) * grid_mils
    resolve_overlaps(spacing_mils)

    cost_after = total_cost()
    moves = []
    for st in states.values():
        before = initial.get(st["designator"])
        if not before:
            continue
        rot_delta = abs(_norm_rot(st["rotation"]) - _norm_rot(before[2]))
        if rot_delta > 180:
            rot_delta = 360 - rot_delta
        if abs(before[0] - st["x"]) < 0.5 and abs(before[1] - st["y"]) < 0.5 and rot_delta < 0.5:
            continue
        moves.append(
            {
                "designator": st["designator"],
                "anchor": "BOARD",
                "comment": st["component"].get("description") or st["component"].get("pattern"),
                "xMils": round(st["x"], 3),
                "yMils": round(st["y"], 3),
                "rotation": round(st["rotation"], 3),
                "layer": st["layer"],
                "mirror": st["layer"] == "bottom",
                "method": "force_directed_sa",
                "roles": ["optimizer"],
                "nets": [],
                "current": {
                    "xMils": round(before[0], 3),
                    "yMils": round(before[1], 3),
                    "rotation": round(before[2], 3),
                    "layer": st["layer"],
                },
            }
        )

    return {
        "found": True,
        "schemaVersion": 5.1,
        "mode": "force_directed_sa",
        "layoutMode": "force_directed_sa",
        "optimizer": "force_directed_sa",
        "anchor": "BOARD",
        "anchors": ["BOARD"],
        "cluster_count": 1,
        "iterations": iterations,
        "anneal_steps": anneal_steps,
        "spacing_mils": spacing_mils,
        "grid_mils": grid_mils,
        "cost_before": round(cost_before, 1),
        "cost_after": round(cost_after, 1),
        "component_count": len(states),
        "locked_count": sum(1 for s in states.values() if s["locked"]),
        "movable_count": sum(1 for s in states.values() if not s["locked"]),
        "move_count": len(moves),
        "moves": moves,
        "note": (
            "Two-phase: force-directed (HPWL) then simulated annealing with 90° rotations. "
            f"Target clearance ~{spacing_mils:.0f} mil (middle-ground packing)."
        ),
    }


def optimize_connectivity_file(
    connectivity_path: str | Path,
    output_path: str | Path | None = None,
    **kwargs: Any,
) -> dict[str, Any]:
    path = Path(connectivity_path)
    data = json.loads(path.read_text(encoding="utf-8"))
    plan = optimize_board(data, **kwargs)
    if output_path is not None:
        Path(output_path).write_text(json.dumps(plan, indent=2), encoding="utf-8")
    return plan


if __name__ == "__main__":
    import sys

    src = sys.argv[1] if len(sys.argv) > 1 else str(
        Path.home() / "Documents" / "AltiumEE" / "connectivity.json"
    )
    dst = sys.argv[2] if len(sys.argv) > 2 else str(
        Path.home() / "Documents" / "AltiumEE" / "placement_plan.json"
    )
    result = optimize_connectivity_file(src, dst)
    print(json.dumps({k: result[k] for k in result if k != "moves"}, indent=2))
    print(f"Wrote {result.get('move_count', 0)} moves -> {dst}")
