"""Geometric clearance DRC from exported connectivity (mirrors C# PcbClearanceDrc)."""
from __future__ import annotations

import json
import math
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPORT_PATH = Path.home() / "Documents" / "AltiumEE" / "drc_clearance.json"
CONNECTIVITY_PATH = Path.home() / "Documents" / "AltiumEE" / "connectivity.json"

DEFAULT_MIN = 6.0
POWER_MIN = 8.0


def power_rail(net: str | None) -> str | None:
    if not net:
        return None
    low = net.strip().casefold()
    if low in {"gnd", "gndnet", "dgnd", "agnd", "vss"} or low.startswith("gnd"):
        return "GND"
    if "3v3" in low or "3.3" in low:
        return "3V3"
    if low in {"+5", "5v", "+5v", "vbus", "+5v0"}:
        return "5V"
    if "vcc" in low or "vdd" in low or low.startswith("+"):
        return "PWR"
    return None


def is_copper_layer(layer: str | None) -> bool:
    if not layer:
        return False
    n = layer.casefold()
    bad = (
        "mechanical", "overlay", "paste", "solder", "keep", "assembly",
        "courtyard", "dimension", "drill", "3d", "component center", "pcb.v7_layer",
    )
    if any(b in n for b in bad):
        return False
    return any(k in n for k in ("top", "bottom", "mid", "signal", "plane", "inner"))


def edge_clearance(a: dict, b: dict, samples: int = 10) -> float:
    best = 1e18
    for i in range(samples + 1):
        ua = i / samples
        ax = a["x1Mils"] + (a["x2Mils"] - a["x1Mils"]) * ua
        ay = a["y1Mils"] + (a["y2Mils"] - a["y1Mils"]) * ua
        for j in range(samples + 1):
            ub = j / samples
            bx = b["x1Mils"] + (b["x2Mils"] - b["x1Mils"]) * ub
            by = b["y1Mils"] + (b["y2Mils"] - b["y1Mils"]) * ub
            best = min(best, math.hypot(ax - bx, ay - by))
    return best - 0.5 * float(a.get("widthMils") or 0) - 0.5 * float(b.get("widthMils") or 0)


def analyze_connectivity(
    data: dict[str, Any] | None = None,
    min_clearance_mils: float = DEFAULT_MIN,
    power_min_clearance_mils: float = POWER_MIN,
) -> dict[str, Any]:
    if data is None:
        data = json.loads(CONNECTIVITY_PATH.read_text(encoding="utf-8"))

    # Prefer embedded mcpDrc from C# export when present (full or legacy clearance).
    embedded = data.get("mcpDrc") or ((data.get("pcb") or {}).get("mcpDrc"))
    if isinstance(embedded, dict) and (
        embedded.get("violations") is not None or embedded.get("issues") is not None
    ):
        return embedded

    # Prefer on-disk full DRC report written by the Altium extension.
    full_path = Path.home() / "Documents" / "AltiumEE" / "drc_full_report.json"
    if full_path.is_file():
        try:
            full = json.loads(full_path.read_text(encoding="utf-8"))
            if isinstance(full, dict) and full.get("issues") is not None:
                return full
        except Exception:
            pass

    if REPORT_PATH.is_file():
        try:
            disk = json.loads(REPORT_PATH.read_text(encoding="utf-8"))
            if isinstance(disk, dict) and (
                disk.get("violations") is not None or disk.get("issues") is not None
            ):
                return disk
        except Exception:
            pass

    routing = (data.get("pcb") or {}).get("routing") or {}
    tracks = routing.get("tracks") or []
    vias = routing.get("vias") or []

    copper = []
    for t in tracks:
        if t.get("electrical") is False:
            continue
        layer = t.get("layer") or ""
        if not is_copper_layer(layer):
            continue
        if not t.get("net"):
            continue
        copper.append(t)

    violations: list[dict[str, Any]] = []
    by_layer: dict[str, list] = defaultdict(list)
    for t in copper:
        by_layer[t.get("layer") or "?"].append(t)

    for layer, list_t in by_layer.items():
        for i, a in enumerate(list_t):
            for b in list_t[i + 1 :]:
                if (a.get("net") or "").casefold() == (b.get("net") or "").casefold():
                    continue
                ra, rb = power_rail(a.get("net")), power_rail(b.get("net"))
                both_power = ra and rb and ra != rb
                limit = power_min_clearance_mils if both_power else min_clearance_mils
                edge = edge_clearance(a, b)
                if edge >= limit:
                    continue
                violations.append(
                    {
                        "severity": "critical" if both_power else "warning",
                        "kind": "power_rail_clearance" if both_power else "clearance",
                        "layer": layer,
                        "netA": a.get("net"),
                        "netB": b.get("net"),
                        "edgeClearanceMils": round(edge, 2),
                        "requiredMils": limit,
                        "xMils": round((a["x1Mils"] + a["x2Mils"] + b["x1Mils"] + b["x2Mils"]) / 4, 1),
                        "yMils": round((a["y1Mils"] + a["y2Mils"] + b["y1Mils"] + b["y2Mils"]) / 4, 1),
                        "message": (
                            f"{a.get('net')} vs {b.get('net')} on {layer}: "
                            f"edge {edge:.2f} mil < {limit:.1f} mil"
                            + (" (fab short risk)" if both_power else "")
                        ),
                    }
                )

    for i, a in enumerate(vias):
        for b in vias[i + 1 :]:
            ra, rb = power_rail(a.get("net")), power_rail(b.get("net"))
            if not ra or not rb or ra == rb:
                continue
            center = math.hypot(a["xMils"] - b["xMils"], a["yMils"] - b["yMils"])
            edge = center - 0.5 * float(a.get("sizeMils") or 0) - 0.5 * float(b.get("sizeMils") or 0)
            if edge >= power_min_clearance_mils:
                continue
            violations.append(
                {
                    "severity": "critical",
                    "kind": "via_power_clearance",
                    "layer": f"{a.get('lowLayer')}->{a.get('highLayer')}",
                    "netA": a.get("net"),
                    "netB": b.get("net"),
                    "edgeClearanceMils": round(edge, 2),
                    "requiredMils": power_min_clearance_mils,
                    "xMils": round((a["xMils"] + b["xMils"]) / 2, 1),
                    "yMils": round((a["yMils"] + b["yMils"]) / 2, 1),
                    "message": (
                        f"Via {a.get('net')} vs {b.get('net')}: "
                        f"annular edge {edge:.2f} mil < {power_min_clearance_mils:.1f} mil"
                    ),
                }
            )

    violations.sort(
        key=lambda v: (0 if v.get("severity") == "critical" else 1, float(v.get("edgeClearanceMils") or 0))
    )
    violations = violations[:200]
    critical = sum(1 for v in violations if v.get("severity") == "critical")
    warning = len(violations) - critical
    report = {
        "checkedAt": datetime.now(timezone.utc).isoformat(),
        "minClearanceMils": min_clearance_mils,
        "powerMinClearanceMils": power_min_clearance_mils,
        "electricalTrackCount": len(copper),
        "viaCount": len(vias),
        "violationCount": len(violations),
        "criticalCount": critical,
        "warningCount": warning,
        "pass": critical == 0,
        "violations": violations,
        "summary": (
            f"MCP DRC PASS — {len(copper)} tracks, {len(vias)} vias checked."
            if critical == 0 and warning == 0
            else (
                f"MCP DRC PASS with {warning} warning(s) — no critical power-rail shorts."
                if critical == 0
                else f"MCP DRC FAIL — {critical} critical clearance issue(s)."
            )
        ),
    }
    try:
        REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)
        REPORT_PATH.write_text(json.dumps(report, indent=2), encoding="utf-8")
    except OSError:
        pass
    return report
