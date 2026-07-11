"""Independently validate a placement plan against exported PCB bounds."""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path


def _layer(value: object) -> str:
    return "bottom" if "bottom" in str(value or "").lower() else "top"


def _half_size(component: dict, target_rotation: float | None) -> tuple[float, float]:
    bbox = component.get("bboxMils") or {}
    half_width = float(bbox.get("halfWidthMils") or 40.0)
    half_height = float(bbox.get("halfHeightMils") or 40.0)
    current_rotation = float((component.get("placement") or {}).get("rotation") or 0.0)
    if target_rotation is None:
        return half_width, half_height
    radians = math.radians(target_rotation - current_rotation)
    cosine = abs(math.cos(radians))
    sine = abs(math.sin(radians))
    return (
        half_width * cosine + half_height * sine,
        half_width * sine + half_height * cosine,
    )


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: audit_placement_plan.py CONNECTIVITY_JSON PLAN_JSON [GAP_MILS]")
        return 2

    connectivity = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    plan = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
    gap = float(sys.argv[3]) if len(sys.argv) > 3 else 20.0

    components = {
        str(component.get("designator") or "").upper(): component
        for component in (connectivity.get("pcb") or {}).get("components") or []
    }
    moves = {
        str(move.get("designator") or "").upper(): move
        for move in plan.get("moves") or []
    }

    final_boxes: list[dict] = []
    for designator, component in components.items():
        placement = component.get("placement") or {}
        move = moves.get(designator)
        target_rotation = (
            float(move["rotation"])
            if move and move.get("rotation") is not None
            else float(placement.get("rotation") or 0.0)
        )
        half_width, half_height = _half_size(component, target_rotation)
        final_boxes.append(
            {
                "designator": designator,
                "moved": move is not None,
                "x": float(move.get("xMils") if move else placement.get("xMils") or 0.0),
                "y": float(move.get("yMils") if move else placement.get("yMils") or 0.0),
                "layer": _layer(move.get("layer") if move else component.get("layer")),
                "half_width": half_width,
                "half_height": half_height,
            }
        )

    overlaps: list[tuple[str, str, str]] = []
    for index, left in enumerate(final_boxes):
        for right in final_boxes[index + 1 :]:
            if not (left["moved"] or right["moved"]):
                continue
            if left["layer"] != right["layer"]:
                continue
            if (
                abs(left["x"] - right["x"])
                < left["half_width"] + right["half_width"] + gap
                and abs(left["y"] - right["y"])
                < left["half_height"] + right["half_height"] + gap
            ):
                overlaps.append(
                    (left["designator"], right["designator"], left["layer"])
                )

    validation = plan.get("collision_validation") or {}
    print(f"moves={len(moves)}")
    print(f"collision_adjusted={validation.get('adjusted_count', 0)}")
    print(f"planner_unresolved={validation.get('unresolved_count', 0)}")
    print(f"independent_same_layer_overlaps={len(overlaps)}")
    for left, right, layer in overlaps[:50]:
        print(f"  {left} overlaps {right} on {layer}")

    return 1 if overlaps else 0


if __name__ == "__main__":
    raise SystemExit(main())
