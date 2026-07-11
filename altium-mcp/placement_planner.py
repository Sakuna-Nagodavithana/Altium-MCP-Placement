"""Group schematic support parts per IC and build PCB cluster placement plans."""

from __future__ import annotations

import json
import math
import re
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

PLAN_SCHEMA_VERSION = 5.1

PASSIVE_PREFIXES = frozenset({"R", "C", "L", "FB", "CB", "RN", "RV", "D"})
GLOBAL_RAIL_NAMES = frozenset(
    {
        "3v3",
        "3.3v",
        "3v3_a",
        "vcc",
        "vcc3v3",
        "5v",
        "gnd",
        "agnd",
        "dgnd",
        "pgnd",
        "vss",
    }
)
IC_DESIGNATOR = re.compile(r"^(IC|U)\d+", re.IGNORECASE)
POWER_PIN_NAME = re.compile(
    r"(^VDD|^VCC|^VDDA|^VDDD|^VDD3|^VBAT|^VIN|^3V3|3\.3|SUPPLY|POWER)",
    re.IGNORECASE,
)
DEFAULT_PLAN_PATH = Path.home() / "Documents" / "AltiumEE" / "placement_plan.json"


def is_passive_designator(designator: str) -> bool:
    match = re.match(r"^([A-Z]+)", str(designator or "").strip().upper())
    return bool(match and match.group(1) in PASSIVE_PREFIXES)


def _placement_xy(placement: dict[str, Any] | None) -> tuple[float, float] | None:
    if not placement:
        return None
    for x_key, y_key in (("xMils", "yMils"), ("xMm", "yMm")):
        x_val = placement.get(x_key)
        y_val = placement.get(y_key)
        if x_val is not None and y_val is not None:
            return float(x_val), float(y_val)
    return None


def _sheet_name(sheet: str | None) -> str:
    if not sheet:
        return ""
    return Path(str(sheet)).name.casefold()


def _is_global_rail(net_name: str) -> bool:
    text = str(net_name or "").strip().casefold()
    if text in GLOBAL_RAIL_NAMES:
        return True
    return "gnd" in text and len(text) <= 6


def _is_local_net_name(net_name: str) -> bool:
    text = str(net_name or "").strip()
    if not text:
        return False
    if _is_global_rail(text):
        return False
    upper = text.upper()
    return upper.startswith("NETIC") or upper.startswith("NETC") or upper.startswith("NETR")


def _is_decoupling_cap(component: dict[str, Any], net_name: str) -> bool:
    designator = str(component.get("designator", "")).strip().upper()
    if not designator.startswith("C"):
        return False
    pin_nets = [str(pin.get("net", "")).strip() for pin in component.get("pins") or []]
    if not pin_nets:
        return False
    target = str(net_name).strip()
    has_target = any(net == target for net in pin_nets)
    has_gnd = any(_is_global_rail(net) and "gnd" in net.casefold() for net in pin_nets) or any(
        pin.casefold() in {"gnd", "vss", "agnd"} for pin in pin_nets
    )
    return has_target and has_gnd


def _looks_like_power_net(net_name: str) -> bool:
    text = str(net_name or "").strip().casefold()
    if not text or _is_global_rail(text):
        return False
    return any(hint in text for hint in ("vcc", "vdd", "vbat", "vin", "3v3", "1v8", "2v5"))


def _net_members(net: dict[str, Any] | None) -> list[dict[str, Any]]:
    return list((net or {}).get("connections") or [])


def _net_ic_count(net: dict[str, Any] | None, anchor: str) -> int:
    seen: set[str] = set()
    for conn in _net_members(net):
        designator = str(conn.get("designator", "")).strip().upper()
        if not designator or designator == anchor:
            continue
        if IC_DESIGNATOR.match(designator):
            seen.add(designator)
    return len(seen)


def _exclusive_to_anchor(net: dict[str, Any] | None, anchor: str) -> bool:
    others: set[str] = set()
    for conn in _net_members(net):
        designator = str(conn.get("designator", "")).strip().upper()
        if not designator or designator == anchor:
            continue
        if not is_passive_designator(designator):
            others.add(designator)
    return len(others) == 0


def collect_ic_nets(data: dict[str, Any], ic_designator: str) -> set[str]:
    target = ic_designator.strip().upper()
    nets: set[str] = set()
    for component in data.get("components") or []:
        if str(component.get("designator", "")).strip().upper() != target:
            continue
        for pin in component.get("pins") or []:
            net = str(pin.get("net", "")).strip()
            if net and net.casefold() not in {"no net", "nonet", "unconnected"}:
                nets.add(net)
    return nets


def _net_specificity(net_name: str) -> int:
    text = str(net_name or "").strip()
    if not text or _is_global_rail(text):
        if "gnd" in text.casefold():
            return 0
        return 20
    if _is_local_net_name(text):
        return 100
    return 80


def _passive_pin_nets(component: dict[str, Any]) -> set[str]:
    nets: set[str] = set()
    for pin in component.get("pins") or []:
        net = str(pin.get("net", "")).strip()
        if net and net.casefold() not in {"no net", "nonet", "unconnected"}:
            nets.add(net)
    return nets


def _build_ic_pin_index(anchor: dict[str, Any]) -> dict[str, dict[str, Any]]:
    index: dict[str, dict[str, Any]] = {}
    for pin in anchor.get("pins") or []:
        pin_number = str(pin.get("number", "")).strip()
        if not pin_number:
            continue
        index[pin_number] = {
            "pin": pin_number,
            "pin_name": str(pin.get("name", "")).strip(),
            "net": str(pin.get("net", "")).strip(),
        }
    return index


def _load_ic_pin_layout(anchor: dict[str, Any]) -> dict[str, dict[str, Any]]:
    """Merge export pin nets with schematic pin coordinates from SchDoc."""
    from sch_net_resolver import _component_pins

    pin_index = _build_ic_pin_index(anchor)
    sheet_path = anchor.get("sheet") or anchor.get("path")
    if not sheet_path:
        return {}

    path = Path(str(sheet_path))
    if not path.exists():
        return {}

    data = path.read_bytes().decode("latin1", errors="ignore")
    designator = str(anchor.get("designator", "")).strip().upper()
    sch_pins = _component_pins(data).get(designator, [])

    layout: dict[str, dict[str, Any]] = {}
    for sch_pin in sch_pins:
        pin_number = str(sch_pin.get("pin", "")).strip()
        if not pin_number:
            continue
        export_pin = pin_index.get(pin_number, {})
        layout[pin_number] = {
            "pin": pin_number,
            "pin_name": export_pin.get("pin_name") or sch_pin.get("pin_name"),
            "net": export_pin.get("net") or "",
            "xMils": float(sch_pin["x"]),
            "yMils": float(sch_pin["y"]),
        }
    for pin_number, export_pin in pin_index.items():
        layout.setdefault(
            pin_number,
            {
                "pin": pin_number,
                "pin_name": export_pin.get("pin_name"),
                "net": export_pin.get("net"),
                "xMils": None,
                "yMils": None,
            },
        )
    return layout


def _pin_angle_from_anchor(
    anchor_xy: tuple[float, float] | None,
    pin_layout: dict[str, Any] | None,
) -> float | None:
    if not anchor_xy or not pin_layout:
        return None
    px = pin_layout.get("xMils")
    py = pin_layout.get("yMils")
    if px is None or py is None:
        return None
    return round(math.degrees(math.atan2(float(py) - anchor_xy[1], float(px) - anchor_xy[0])), 2)


def _resolve_primary_ic_link(
    component: dict[str, Any],
    ic_pin_index: dict[str, dict[str, Any]],
    pin_layout: dict[str, dict[str, Any]],
    anchor_xy: tuple[float, float] | None,
) -> dict[str, Any]:
    """Pick the IC pin + net that best explains why this passive belongs near the IC."""
    passive_nets = _passive_pin_nets(component)
    comp_xy = _placement_xy(component.get("placement"))

    direct_links: list[dict[str, Any]] = []
    for pin_number, pin_info in ic_pin_index.items():
        net = str(pin_info.get("net", "")).strip()
        if not net or net not in passive_nets:
            continue
        if _is_global_rail(net) and "gnd" in net.casefold() and len(passive_nets - {net}) > 0:
            continue
        layout = pin_layout.get(pin_number, {})
        direct_links.append(
            {
                "pin": pin_number,
                "pin_name": pin_info.get("pin_name"),
                "net": net,
                "link_type": "direct_net",
                "pin_angle_deg": _pin_angle_from_anchor(anchor_xy, layout),
                "net_specificity": _net_specificity(net),
            }
        )

    if direct_links:
        direct_links.sort(
            key=lambda item: (
                -item["net_specificity"],
                item["pin_angle_deg"] is None,
                str(item["pin"]),
            )
        )
        primary = direct_links[0]
        return {
            "primary_net": primary["net"],
            "primary_ic_pin": primary["pin"],
            "primary_ic_pin_name": primary.get("pin_name"),
            "pin_angle_deg": primary.get("pin_angle_deg"),
            "linked_ic_pins": direct_links,
        }

    if str(component.get("designator", "")).upper().startswith("C") and "3v3" in {
        n.casefold() for n in passive_nets
    }:
        power_candidates: list[dict[str, Any]] = []
        for pin_number, pin_info in ic_pin_index.items():
            net = str(pin_info.get("net", "")).strip()
            pin_name = str(pin_info.get("pin_name", "")).strip()
            if net.casefold() not in {"3v3", "3.3v", "vcc", "vcc3v3"} and not POWER_PIN_NAME.search(
                pin_name
            ):
                continue
            layout = pin_layout.get(pin_number, {})
            distance = None
            if comp_xy and layout.get("xMils") is not None and layout.get("yMils") is not None:
                distance = math.hypot(
                    float(layout["xMils"]) - comp_xy[0],
                    float(layout["yMils"]) - comp_xy[1],
                )
            power_candidates.append(
                {
                    "pin": pin_number,
                    "pin_name": pin_name,
                    "net": net or "3v3",
                    "link_type": "power_decoupling",
                    "pin_angle_deg": _pin_angle_from_anchor(anchor_xy, layout),
                    "distance_mils": distance,
                }
            )
        if power_candidates:
            power_candidates.sort(
                key=lambda item: (
                    item["distance_mils"] is None,
                    item["distance_mils"] or 0.0,
                    str(item["pin"]),
                )
            )
            primary = power_candidates[0]
            return {
                "primary_net": "3v3",
                "primary_ic_pin": primary["pin"],
                "primary_ic_pin_name": primary.get("pin_name"),
                "pin_angle_deg": primary.get("pin_angle_deg"),
                "linked_ic_pins": power_candidates[:4],
            }

    return {
        "primary_net": sorted(passive_nets, key=lambda n: (-_net_specificity(n), n))[0]
        if passive_nets
        else None,
        "primary_ic_pin": None,
        "primary_ic_pin_name": None,
        "pin_angle_deg": None,
        "linked_ic_pins": [],
    }


def _should_include_passive(
    *,
    net_name: str,
    net: dict[str, Any],
    component: dict[str, Any],
    anchor: str,
    anchor_sheet: str,
    anchor_xy: tuple[float, float] | None,
    same_sheet_only: bool,
    max_schematic_distance_mils: float,
    exclude_global_nets: bool,
) -> tuple[bool, str]:
    designator = str(component.get("designator", "")).strip().upper()
    comp_xy = _placement_xy(component.get("placement"))
    sch_distance = None
    if anchor_xy and comp_xy:
        sch_distance = math.hypot(comp_xy[0] - anchor_xy[0], comp_xy[1] - anchor_xy[1])

    if same_sheet_only and _sheet_name(component.get("sheet")) != _sheet_name(anchor_sheet):
        return False, "other_sheet"

    if _exclusive_to_anchor(net, anchor):
        return True, "exclusive_net"

    if _is_local_net_name(net_name):
        return True, "local_net"

    if exclude_global_nets and _is_global_rail(net_name):
        if not designator.startswith("C"):
            return False, "global_rail_non_cap"
        if sch_distance is None or sch_distance > max_schematic_distance_mils:
            return False, "global_rail_far"
        return True, "nearby_decoupling"

    if sch_distance is not None and sch_distance <= max_schematic_distance_mils:
        if _net_ic_count(net, anchor) == 0:
            return True, "nearby_signal"
        return False, "nearby_but_shared_ic"

    return False, "too_far_or_shared"


def get_ic_support_components(
    data: dict[str, Any],
    ic_designator: str,
    *,
    same_sheet_only: bool = True,
    max_schematic_distance_mils: float = 2500.0,
    exclude_global_nets: bool = True,
) -> dict[str, Any]:
    """Return local passives that belong near an anchor IC."""
    target = ic_designator.strip().upper()
    anchor = next(
        (
            component
            for component in data.get("components") or []
            if str(component.get("designator", "")).strip().upper() == target
        ),
        None,
    )
    if anchor is None:
        return {
            "found": False,
            "anchor": target,
            "error": f"Component '{ic_designator}' not found in export.",
        }

    ic_nets = collect_ic_nets(data, target)
    project_nets = {
        str(net.get("name", "")).strip(): net
        for net in (data.get("projectNets") or data.get("nets") or [])
        if str(net.get("name", "")).strip()
    }

    component_index = {
        str(component.get("designator", "")).strip().upper(): component
        for component in data.get("components") or []
    }

    anchor_xy = _placement_xy(anchor.get("placement"))
    anchor_sheet = str(anchor.get("sheet") or "")
    ic_pin_index = _build_ic_pin_index(anchor)
    pin_layout = _load_ic_pin_layout(anchor)

    grouped: dict[str, dict[str, Any]] = {}
    rejected_counts: dict[str, int] = {}

    for net_name in sorted(ic_nets):
        net = project_nets.get(net_name)
        if not net:
            continue
        for connection in net.get("connections") or []:
            designator = str(connection.get("designator", "")).strip().upper()
            if not designator or designator == target or not is_passive_designator(designator):
                continue
            component = component_index.get(designator)
            if component is None:
                continue

            include, reason = _should_include_passive(
                net_name=net_name,
                net=net,
                component=component,
                anchor=target,
                anchor_sheet=anchor_sheet,
                anchor_xy=anchor_xy,
                same_sheet_only=same_sheet_only,
                max_schematic_distance_mils=max_schematic_distance_mils,
                exclude_global_nets=exclude_global_nets,
            )
            if not include:
                rejected_counts[reason] = rejected_counts.get(reason, 0) + 1
                continue

            entry = grouped.setdefault(
                designator,
                {
                    "designator": designator,
                    "comment": component.get("comment"),
                    "jlcpcb": component.get("jlcpcb"),
                    "sheet": component.get("sheet"),
                    "nets": [],
                    "roles": set(),
                    "include_reasons": set(),
                },
            )
            if net_name not in entry["nets"]:
                entry["nets"].append(net_name)
            entry["include_reasons"].add(reason)
            if _is_decoupling_cap(component, net_name):
                entry["roles"].add("decoupling")
            elif _looks_like_power_net(net_name) or _is_global_rail(net_name):
                entry["roles"].add("power")
            else:
                entry["roles"].add("signal")

    support_list: list[dict[str, Any]] = []
    for designator, entry in grouped.items():
        component = component_index[designator]
        comp_xy = _placement_xy(component.get("placement"))
        offset_x = None
        offset_y = None
        distance = None
        angle_deg = None
        if anchor_xy and comp_xy:
            offset_x = round(comp_xy[0] - anchor_xy[0], 3)
            offset_y = round(comp_xy[1] - anchor_xy[1], 3)
            distance = round(math.hypot(offset_x, offset_y), 3)
            angle_deg = round(math.degrees(math.atan2(offset_y, offset_x)), 2)

        roles = sorted(entry["roles"]) or ["support"]
        primary_role = "decoupling" if "decoupling" in roles else roles[0]
        net_link = _resolve_primary_ic_link(component, ic_pin_index, pin_layout, anchor_xy)
        pin_angle = net_link.get("pin_angle_deg")
        if pin_angle is None:
            pin_angle = angle_deg
        support_list.append(
            {
                "designator": designator,
                "comment": entry["comment"],
                "jlcpcb": entry["jlcpcb"],
                "sheet": entry["sheet"],
                "nets": entry["nets"],
                "roles": roles,
                "primary_role": primary_role,
                "primary_net": net_link.get("primary_net"),
                "primary_ic_pin": net_link.get("primary_ic_pin"),
                "primary_ic_pin_name": net_link.get("primary_ic_pin_name"),
                "linked_ic_pins": net_link.get("linked_ic_pins") or [],
                "include_reasons": sorted(entry["include_reasons"]),
                "schematic": {
                    "offsetXMils": offset_x,
                    "offsetYMils": offset_y,
                    "distanceMils": distance,
                    "angleDeg": angle_deg,
                    "pinAngleDeg": pin_angle,
                    "placement": component.get("placement"),
                },
            }
        )

    role_rank = {"decoupling": 0, "power": 1, "signal": 2, "support": 3}
    support_list.sort(
        key=lambda item: (
            role_rank.get(item["primary_role"], 9),
            str(item.get("primary_net") or ""),
            _cap_proximity_rank(item),
            item["schematic"]["pinAngleDeg"] is None,
            item["schematic"]["pinAngleDeg"] or 0.0,
            item["designator"],
        )
    )

    return {
        "found": True,
        "anchor": target,
        "anchor_comment": anchor.get("comment"),
        "anchor_sheet": anchor.get("sheet"),
        "ic_net_count": len(ic_nets),
        "support_count": len(support_list),
        "support_components": support_list,
        "has_schematic_coords": anchor_xy is not None,
        "has_pin_layout": bool(pin_layout),
        "anchor_placement": anchor.get("placement"),
        "pin_layout": pin_layout,
        "filters": {
            "same_sheet_only": same_sheet_only,
            "max_schematic_distance_mils": max_schematic_distance_mils,
            "exclude_global_nets": exclude_global_nets,
        },
        "rejected_counts": rejected_counts,
    }


def _passive_internal_pin_pairs(component: dict[str, Any]) -> list[tuple[str, str]]:
    pin_numbers = [
        str(pin.get("number", "")).strip()
        for pin in component.get("pins") or []
        if str(pin.get("number", "")).strip()
    ]
    if len(pin_numbers) < 2 or len(pin_numbers) > 3:
        return []
    pairs: list[tuple[str, str]] = []
    for left in pin_numbers:
        for right in pin_numbers:
            if left != right:
                pairs.append((left, right))
    return pairs


def _build_support_connectivity_graph(
    data: dict[str, Any],
    anchor: str,
    allowed_passives: set[str],
) -> tuple[dict[tuple[str, str], list[tuple[str, str]]], list[tuple[str, str]]]:
    """Build pin-level adjacency for IC + local support passives only."""
    from collections import defaultdict

    anchor = anchor.strip().upper()
    allowed = set(allowed_passives)
    adjacency: dict[tuple[str, str], list[tuple[str, str]]] = defaultdict(list)

    component_index = {
        str(component.get("designator", "")).strip().upper(): component
        for component in (data.get("components") or [])
    }

    def link_nodes(left: tuple[str, str], right: tuple[str, str]) -> None:
        if left == right:
            return
        if right not in adjacency[left]:
            adjacency[left].append(right)
        if left not in adjacency[right]:
            adjacency[right].append(left)

    seen_net_links: set[tuple[tuple[str, str], tuple[str, str]]] = set()
    for net in data.get("projectNets") or []:
        pins: list[tuple[str, str]] = []
        for connection in net.get("connections") or []:
            designator = str(connection.get("designator", "")).strip().upper()
            pin_number = str(connection.get("pin", "")).strip()
            if not designator or not pin_number:
                continue
            if designator != anchor and designator not in allowed:
                continue
            pins.append((designator, pin_number))
        for left in pins:
            for right in pins:
                if left == right:
                    continue
                key = (left, right) if left < right else (right, left)
                if key in seen_net_links:
                    continue
                seen_net_links.add(key)
                link_nodes(left, right)

    for designator in allowed:
        component = component_index.get(designator)
        if not component:
            continue
        for left, right in _passive_internal_pin_pairs(component):
            link_nodes((designator, left), (designator, right))

    ic_starts: list[tuple[str, str]] = []
    anchor_component = component_index.get(anchor)
    if anchor_component:
        for pin_info in anchor_component.get("pins") or []:
            pin_number = str(pin_info.get("number", "")).strip()
            if pin_number:
                ic_starts.append((anchor, pin_number))

    return adjacency, ic_starts


def detect_support_chains(
    data: dict[str, Any],
    anchor: str,
    support_components: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    """Detect ordered R-C-R (etc.) series chains from the IC pin through support passives."""
    from collections import deque

    allowed = {str(item.get("designator", "")).strip().upper() for item in support_components}
    allowed.discard("")
    if len(allowed) < 2:
        return []

    support_by_des = {
        str(item.get("designator", "")).strip().upper(): item for item in support_components
    }
    adjacency, ic_starts = _build_support_connectivity_graph(data, anchor, allowed)

    max_chain_passives = max(8, min(12, len(allowed)))
    active_ic_starts = [start for start in ic_starts if adjacency.get(start)]
    path_cache: dict[str, list[str]] = {}

    def passive_path_to(target_des: str) -> list[str]:
        target_des = target_des.strip().upper()
        cached = path_cache.get(target_des)
        if cached is not None:
            return cached

        best: list[str] = []
        for start in active_ic_starts:
            queue: deque[tuple[tuple[str, str], list[str]]] = deque([(start, [])])
            visited: set[tuple[str, str]] = {start}
            while queue:
                node, passives = queue.popleft()
                designator, _ = node
                if designator in allowed:
                    if not passives or passives[-1] != designator:
                        passives = passives + [designator]
                if designator == target_des:
                    if len(passives) > len(best):
                        best = list(passives)
                    continue
                if len(passives) >= max_chain_passives:
                    continue
                for neighbor in adjacency.get(node, []):
                    if neighbor in visited:
                        continue
                    neighbor_des, _ = neighbor
                    if neighbor_des != anchor and neighbor_des not in allowed:
                        continue
                    visited.add(neighbor)
                    queue.append((neighbor, passives))

        path_cache[target_des] = best
        return best

    remaining = set(allowed)
    chains: list[dict[str, Any]] = []
    chain_number = 0
    while remaining:
        best_path: list[str] = []
        for designator in list(remaining):
            path = passive_path_to(designator)
            if len(path) > len(best_path):
                best_path = path
        if len(best_path) < 2:
            break

        chain_number += 1
        first_item = support_by_des.get(best_path[0], {})
        chains.append(
            {
                "chain_id": f"chain_{chain_number}",
                "members": best_path,
                "length": len(best_path),
                "primary_ic_pin": first_item.get("primary_ic_pin"),
                "primary_net": first_item.get("primary_net"),
            }
        )
        for member in best_path:
            remaining.discard(member)

    return chains


def expand_series_chain_support(
    data: dict[str, Any],
    anchor: str,
    support_components: list[dict[str, Any]],
    *,
    max_schematic_distance_mils: float = 2500.0,
) -> list[dict[str, Any]]:
    """Include middle parts of R-C-R chains even when they do not touch an IC net directly."""
    anchor = anchor.strip().upper()
    if not support_components:
        return support_components

    component_index = {
        str(component.get("designator", "")).strip().upper(): component
        for component in (data.get("components") or [])
    }
    anchor_component = component_index.get(anchor) or {}
    anchor_sheet = _sheet_name(anchor_component.get("sheet"))
    anchor_xy = _placement_xy(anchor_component.get("placement") or {})

    support_by_des = {
        str(item.get("designator", "")).strip().upper(): dict(item) for item in support_components
    }
    allowed = set(support_by_des)

    def passive_near_enough(component: dict[str, Any]) -> bool:
        if not anchor_xy:
            return True
        passive_xy = _placement_xy(component.get("placement") or {})
        if not passive_xy:
            return False
        return math.hypot(passive_xy[0] - anchor_xy[0], passive_xy[1] - anchor_xy[1]) <= max_schematic_distance_mils

    changed = True
    while changed:
        changed = False
        for net in data.get("projectNets") or []:
            net_name = str(net.get("name", "")).strip()
            if not net_name or _is_global_rail(net_name):
                continue
            members: list[str] = []
            touches_allowed = False
            for connection in net.get("connections") or []:
                designator = str(connection.get("designator", "")).strip().upper()
                if not designator:
                    continue
                if designator == anchor or designator in allowed:
                    touches_allowed = True
                if is_passive_designator(designator):
                    members.append(designator)
            if not touches_allowed:
                continue
            for designator in members:
                if designator in allowed:
                    continue
                component = component_index.get(designator)
                if not component:
                    continue
                if anchor_sheet and _sheet_name(component.get("sheet")) != anchor_sheet:
                    continue
                if not passive_near_enough(component):
                    continue
                allowed.add(designator)
                changed = True
                support_by_des[designator] = {
                    "designator": designator,
                    "comment": component.get("comment"),
                    "roles": ["signal"],
                    "nets": sorted(_passive_pin_nets(component)),
                    "schematic": {},
                }

    original_designators = {item["designator"] for item in support_components}
    added_designators = [des for des in allowed if des not in original_designators]
    if added_designators:
        grouping_seed = get_ic_support_components(data, anchor)
        enriched_by_des = {
            str(item.get("designator", "")).strip().upper(): item
            for item in grouping_seed.get("support_components") or []
        }
        for designator in added_designators:
            enriched = enriched_by_des.get(designator)
            if enriched:
                support_by_des[designator] = enriched

    ordered = []
    seen: set[str] = set()
    for item in support_components:
        des = item["designator"]
        if des not in seen:
            ordered.append(support_by_des.get(des, item))
            seen.add(des)
    for des, item in support_by_des.items():
        if des not in seen:
            ordered.append(item)
            seen.add(des)
    return ordered


def _chain_target_xy(
    item: dict[str, Any],
    chain_index: int,
    chain_length: int,
    anchor_xy: tuple[float, float],
    *,
    spacing_mils: float,
    max_radius_mils: float,
    placed_points: list[tuple[float, float]],
    previous_xy: tuple[float, float] | None = None,
    pin_xy: tuple[float, float] | None = None,
) -> tuple[float, float, str, float | None, float, int, float]:
    """Place chain members in signal order along the IC pin ray.

    The first member is placed relative to the IC pin (or IC center as fallback).
    Subsequent members are placed relative to the PREVIOUS member so the chain
    forms a natural signal path (each part close to the one before it), not all
    at a fixed step from the IC center.
    """
    sch = item.get("schematic") or {}
    target_pin_angle = sch.get("pinAngleDeg")
    if target_pin_angle is None:
        target_pin_angle = sch.get("angleDeg")
    if target_pin_angle is None:
        target_pin_angle = 0.0

    role = item.get("primary_role") or "support"
    angle_deg = float(target_pin_angle)
    angle_offset_deg = 0.0
    min_sep = max(spacing_mils * 0.85, 70.0)
    body_radius = _passive_body_radius_mils(item, None)
    step = max(spacing_mils * 0.9, body_radius * 2.0 + max(spacing_mils * 0.4, 30.0))

    origin = previous_xy if (chain_index > 0 and previous_xy is not None) else (pin_xy or anchor_xy)
    standoff = _role_standoff_mils(role, spacing_mils) if chain_index == 0 else step

    x = origin[0]
    y = origin[1]
    for attempt in range(14):
        standoff = min(max_radius_mils, standoff)
        angle = math.radians(angle_deg)
        x = origin[0] + standoff * math.cos(angle)
        y = origin[1] + standoff * math.sin(angle)

        too_close = any(math.hypot(x - px, y - py) < min_sep for px, py in placed_points)
        if not too_close:
            break
        standoff += spacing_mils * 0.4

    placed_points.append((x, y))
    return (
        x,
        y,
        "pin_chain_rel" if chain_index > 0 else "pin_chain",
        float(target_pin_angle),
        float(standoff),
        chain_index,
        float(angle_offset_deg),
    )


def _normalize_angle_deg(angle: float) -> float:
    return float(angle) % 360.0


def _angle_delta_deg(a: float, b: float) -> float:
    delta = abs(_normalize_angle_deg(a) - _normalize_angle_deg(b))
    return min(delta, 360.0 - delta)


def _role_standoff_mils(role: str, spacing_mils: float) -> float:
    return {
        "decoupling": max(spacing_mils * 1.15, 110.0),
        "power": max(spacing_mils * 1.8, 180.0),
        "signal": max(spacing_mils * 2.4, 240.0),
    }.get(role or "support", max(spacing_mils * 2.0, 160.0))


def _pin_near_target_xy(
    item: dict[str, Any],
    index: int,
    anchor_xy: tuple[float, float],
    *,
    spacing_mils: float,
    max_radius_mils: float,
    pin_slot_counts: dict[tuple[str, str], int],
    placed_points: list[tuple[float, float]],
) -> tuple[float, float, str, float | None, float, int, float]:
    """Place each support part outward from the IC toward its linked pin ray."""
    sch = item.get("schematic") or {}
    target_pin_angle = sch.get("pinAngleDeg")
    if target_pin_angle is None:
        target_pin_angle = sch.get("angleDeg")
    if target_pin_angle is None:
        target_pin_angle = (index * 137.508) % 360.0

    role = item.get("primary_role") or "support"
    pin_key = (
        str(item.get("primary_ic_pin") or "unknown"),
        str(item.get("primary_net") or "unknown"),
    )
    slot = pin_slot_counts.get(pin_key, 0)
    pin_slot_counts[pin_key] = slot + 1

    standoff = _role_standoff_mils(role, spacing_mils) + slot * spacing_mils * 0.75
    angle_deg = float(target_pin_angle) + slot * 14.0
    angle_offset_deg = slot * 14.0
    min_sep = max(spacing_mils * 0.85, 70.0)

    x = anchor_xy[0]
    y = anchor_xy[1]
    for attempt in range(16):
        standoff = min(max_radius_mils, standoff)
        angle = math.radians(angle_deg)
        x = anchor_xy[0] + standoff * math.cos(angle)
        y = anchor_xy[1] + standoff * math.sin(angle)

        too_close = any(math.hypot(x - px, y - py) < min_sep for px, py in placed_points)
        if not too_close:
            break

        if attempt < 10:
            standoff += spacing_mils * 0.55
        else:
            standoff += spacing_mils * 0.35
            angle_deg += 9.0
            angle_offset_deg += 9.0

    placed_points.append((x, y))
    return x, y, "pin_near", float(target_pin_angle), float(standoff), slot, float(angle_offset_deg)


def verify_cluster_placement(
    *,
    anchor: str,
    anchor_xy: tuple[float, float],
    support_components: list[dict[str, Any]],
    moves: list[dict[str, Any]],
    spacing_mils: float,
    max_radius_mils: float,
) -> dict[str, Any]:
    """Verify each planned move is pin-aligned, net-linked, and not stacked on other parts."""
    support_index = {
        str(item.get("designator", "")).strip().upper(): item for item in support_components
    }
    min_sep = max(spacing_mils * 0.75, 60.0)
    min_standoff = max(spacing_mils * 0.8, 70.0)
    items: list[dict[str, Any]] = []
    ok_count = 0

    for move in moves:
        designator = str(move.get("designator", "")).strip().upper()
        support = support_index.get(designator, {})
        issues: list[str] = []
        x = float(move.get("xMils", 0.0))
        y = float(move.get("yMils", 0.0))
        standoff = math.hypot(x - anchor_xy[0], y - anchor_xy[1])
        placement_angle = math.degrees(math.atan2(y - anchor_xy[1], x - anchor_xy[0]))

        primary_pin = move.get("primary_ic_pin")
        primary_net = move.get("primary_net")
        if not primary_pin:
            issues.append("missing_primary_ic_pin")
        if not primary_net:
            issues.append("missing_primary_net")

        move_nets = {str(net).strip() for net in (move.get("nets") or []) if str(net).strip()}
        if primary_net and move_nets and str(primary_net) not in move_nets:
            linked_pins = move.get("linked_ic_pins") or []
            if linked_pins:
                issues.append("primary_net_not_on_part")

        linked_pins = move.get("linked_ic_pins") or []
        if primary_pin and linked_pins:
            linked_numbers = {str(entry.get("pin", "")).strip() for entry in linked_pins}
            if str(primary_pin).strip() not in linked_numbers:
                issues.append("primary_pin_not_in_linked_ic_pins")

        if standoff > max_radius_mils + 1.0:
            issues.append("outside_max_radius")
        if standoff < min_standoff:
            issues.append("too_close_to_anchor")

        target_pin_angle = move.get("targetPinAngleDeg")
        if target_pin_angle is not None:
            angle_delta = _angle_delta_deg(float(placement_angle), float(target_pin_angle))
            pin_slot = int(move.get("pinSlot") or 0)
            angle_offset = float(move.get("angleOffsetDeg") or pin_slot * 14.0)
            allowed_delta = max(28.0, angle_offset + 18.0)
            if angle_delta > allowed_delta:
                issues.append("not_aligned_to_pin_ray")

        for other in moves:
            other_des = str(other.get("designator", "")).strip().upper()
            if other_des == designator:
                continue
            other_dist = math.hypot(x - float(other.get("xMils", 0.0)), y - float(other.get("yMils", 0.0)))
            if other_dist < min_sep:
                issues.append("too_close_to_other_part")
                break

        status = "ok" if not issues else "warn"
        if status == "ok":
            ok_count += 1

        items.append(
            {
                "designator": designator,
                "status": status,
                "issues": issues,
                "anchor": anchor,
                "primary_ic_pin": primary_pin,
                "primary_ic_pin_name": move.get("primary_ic_pin_name"),
                "primary_net": primary_net,
                "method": move.get("method"),
                "standoffMils": round(standoff, 3),
                "placementAngleDeg": round(placement_angle, 2),
                "targetPinAngleDeg": target_pin_angle,
                "roles": move.get("roles") or support.get("roles") or [],
            }
        )

    return {
        "anchor": anchor,
        "verified_count": len(items),
        "ok_count": ok_count,
        "warn_count": len(items) - ok_count,
        "all_ok": ok_count == len(items) and len(items) > 0,
        "items": items,
    }


def _compact_target_xy(
    item: dict[str, Any],
    index: int,
    anchor_xy: tuple[float, float],
    *,
    spacing_mils: float,
    max_radius_mils: float,
    net_slot_counts: dict[str, int],
) -> tuple[float, float, str]:
    """Place support parts in tight rings, oriented toward the linked IC pin/net."""
    role = item.get("primary_role") or "support"
    base_radius = {
        "decoupling": max(spacing_mils * 1.5, 180.0),
        "power": max(spacing_mils * 2.5, 280.0),
        "signal": max(spacing_mils * 3.5, 380.0),
    }.get(role, spacing_mils * 3.0)

    sch = item.get("schematic") or {}
    angle_deg = sch.get("pinAngleDeg")
    if angle_deg is None:
        angle_deg = sch.get("angleDeg")
    if angle_deg is None:
        angle_deg = (index * 137.508) % 360.0

    primary_net = str(item.get("primary_net") or "unknown")
    slot = net_slot_counts.get(primary_net, 0)
    net_slot_counts[primary_net] = slot + 1
    angle_deg += slot * 8.0

    angle = math.radians(angle_deg)
    ring = index // 8
    radius = min(max_radius_mils, base_radius + ring * spacing_mils * 1.5)

    x = anchor_xy[0] + radius * math.cos(angle)
    y = anchor_xy[1] + radius * math.sin(angle)
    return x, y, "compact_net_pin"


def _mirror_target_xy(
    item: dict[str, Any],
    anchor_xy: tuple[float, float],
    *,
    schematic_scale: float,
    max_radius_mils: float,
) -> tuple[float, float, str]:
    sch = item.get("schematic") or {}
    offset_x = sch.get("offsetXMils")
    offset_y = sch.get("offsetYMils")
    if offset_x is None or offset_y is None:
        return anchor_xy[0], anchor_xy[1], "mirror_missing"

    scaled_x = offset_x * schematic_scale
    scaled_y = offset_y * schematic_scale
    dist = math.hypot(scaled_x, scaled_y)
    if dist > max_radius_mils > 0:
        scale = max_radius_mils / dist
        scaled_x *= scale
        scaled_y *= scale

    return anchor_xy[0] + scaled_x, anchor_xy[1] + scaled_y, "schematic_mirror"


def _auto_schematic_scale(
    support_components: list[dict[str, Any]],
    max_radius_mils: float,
    *,
    min_scale: float = 0.06,
    max_scale: float = 0.32,
    fallback: float = 0.12,
) -> float:
    """Scale schematic offsets so the support cluster fits near the IC on the PCB."""
    max_dist = 0.0
    for item in support_components:
        sch = item.get("schematic") or {}
        offset_x = sch.get("offsetXMils")
        offset_y = sch.get("offsetYMils")
        if offset_x is None or offset_y is None:
            continue
        max_dist = max(max_dist, math.hypot(float(offset_x), float(offset_y)))
    if max_dist <= 0:
        return fallback
    target = max_radius_mils * 0.88
    return max(min_scale, min(max_scale, target / max_dist))


def _build_pcb_pin_index(pcb_component: dict[str, Any] | None) -> dict[str, dict[str, Any]]:
    """Map IC pin numbers to absolute PCB pad coordinates from the export."""
    if not pcb_component:
        return {}
    index: dict[str, dict[str, Any]] = {}
    for pin in pcb_component.get("pins") or []:
        pin_number = str(pin.get("name") or pin.get("number") or "").strip()
        if not pin_number:
            continue
        x_mils = pin.get("xMils")
        y_mils = pin.get("yMils")
        if x_mils is None or y_mils is None:
            continue
        index[pin_number] = {
            "pin": pin_number,
            "net": pin.get("net"),
            "xMils": float(x_mils),
            "yMils": float(y_mils),
        }
    return index


def _resolve_ic_pin_pcb_xy(
    pin_number: str | None,
    pcb_pin_index: dict[str, dict[str, Any]],
    pin_layout: dict[str, dict[str, Any]],
    anchor_sch_xy: tuple[float, float] | None,
    anchor_pcb_xy: tuple[float, float],
    scale: float,
) -> tuple[float, float] | None:
    """Prefer exported PCB pad coordinates; fall back to schematic pin mapping."""
    if pin_number:
        entry = pcb_pin_index.get(str(pin_number).strip())
        if entry is not None:
            return float(entry["xMils"]), float(entry["yMils"])
    return _sch_pin_to_pcb_xy(
        str(pin_number) if pin_number else None,
        pin_layout,
        anchor_sch_xy,
        anchor_pcb_xy,
        scale,
    )


def _pin_outward_angle_deg(
    pin_xy: tuple[float, float],
    anchor_xy: tuple[float, float],
) -> float:
    dx = pin_xy[0] - anchor_xy[0]
    dy = pin_xy[1] - anchor_xy[1]
    if math.hypot(dx, dy) < 1e-6:
        return 0.0
    return math.degrees(math.atan2(dy, dx))


def _parse_cap_value_nf(comment: str | None) -> float | None:
    """Parse common capacitor values (100nF, 0.1uF, 10uF) into nanofarads."""
    text = str(comment or "").strip().upper().replace(" ", "")
    if not text:
        return None
    match = re.search(r"([\d.]+)\s*(UF|NF|PF|U|N|P)?", text)
    if not match:
        return None
    value = float(match.group(1))
    unit = (match.group(2) or "").upper()
    if unit in {"UF", "U"}:
        return value * 1000.0
    if unit in {"NF", "N"}:
        return value
    if unit in {"PF", "P"}:
        return value / 1000.0
    if value < 1.0:
        return value * 1000.0
    return value


def _cap_proximity_rank(item: dict[str, Any]) -> int:
    """Lower rank = place closer to the linked IC pin (100nF before 10uF)."""
    if item.get("primary_role") != "decoupling":
        return 50
    nf = _parse_cap_value_nf(str(item.get("comment") or ""))
    if nf is None:
        return 25
    if nf <= 150.0:
        return 0
    if nf <= 1500.0:
        return 1
    if nf <= 12000.0:
        return 2
    return 3


def _passive_body_radius_mils(
    item: dict[str, Any] | None,
    pcb_component: dict[str, Any] | None,
) -> float:
    pattern = str((pcb_component or {}).get("pattern") or "").upper()
    comment = str((item or {}).get("comment") or "").upper()
    designator = str((item or {}).get("designator") or "").upper()
    for token, radius in (("0402", 12.0), ("0603", 18.0), ("0805", 22.0), ("1206", 28.0)):
        if token in pattern or token in comment:
            return radius
    if designator.startswith("L"):
        return 20.0
    return 16.0


def _courtyard_half_size_mils(
    item: dict[str, Any] | None,
    pcb_component: dict[str, Any] | None,
) -> float:
    """Courtyard half-size (body + clearance) for collision avoidance. Larger than
    the body radius so parts don't overlap. Matches DesignExporter estimates."""
    pattern = str((pcb_component or {}).get("pattern") or "").upper()
    comment = str((item or {}).get("comment") or "").upper()
    for token, radius in (("0402", 24.0), ("0603", 32.0), ("0805", 40.0), ("1206", 52.0)):
        if token in pattern or token in comment:
            return radius
    if "SOT" in pattern or "SOIC" in pattern or "SOT" in comment or "SOIC" in comment:
        return 70.0
    return 30.0


def _normalize_layer(value: Any) -> str:
    return "bottom" if "bottom" in str(value or "").casefold() else "top"


def _bbox_half_size(
    component: dict[str, Any],
    target_rotation: float | None = None,
) -> tuple[float, float]:
    bbox = component.get("bboxMils") or {}
    half_width = float(bbox.get("halfWidthMils") or 40.0)
    half_height = float(bbox.get("halfHeightMils") or 40.0)
    if target_rotation is None:
        return half_width, half_height
    current_rotation = float((component.get("placement") or {}).get("rotation") or 0.0)
    radians = math.radians(float(target_rotation) - current_rotation)
    cosine = abs(math.cos(radians))
    sine = abs(math.sin(radians))
    return (
        half_width * cosine + half_height * sine,
        half_width * sine + half_height * cosine,
    )


def _boxes_overlap(left: dict[str, Any], right: dict[str, Any], gap: float) -> bool:
    if _normalize_layer(left.get("layer")) != _normalize_layer(right.get("layer")):
        return False
    return (
        abs(float(left["x"]) - float(right["x"]))
        < float(left["half_width"]) + float(right["half_width"]) + gap
        and abs(float(left["y"]) - float(right["y"]))
        < float(left["half_height"]) + float(right["half_height"]) + gap
    )


def _resolve_final_move_overlaps(
    data: dict[str, Any],
    moves: list[dict[str, Any]],
    *,
    spacing_mils: float,
    max_radius_mils: float,
) -> dict[str, Any]:
    pcb_components = {
        str(component.get("designator") or "").strip().upper(): component
        for component in (data.get("pcb") or {}).get("components") or []
    }
    move_designators = {
        str(move.get("designator") or "").strip().upper() for move in moves
    }
    occupied: list[dict[str, Any]] = []
    for designator, component in pcb_components.items():
        if designator in move_designators:
            continue
        placement = component.get("placement") or {}
        xy = _placement_xy(placement)
        if xy is None:
            continue
        half_width, half_height = _bbox_half_size(
            component, float(placement.get("rotation") or 0.0)
        )
        occupied.append(
            {
                "designator": designator,
                "layer": _normalize_layer(component.get("layer")),
                "x": xy[0],
                "y": xy[1],
                "half_width": half_width,
                "half_height": half_height,
            }
        )

    keepouts = _collect_keepout_boxes(data)
    resolved_by_designator: dict[str, dict[str, Any]] = {}
    adjusted_count = 0
    unresolved: list[str] = []
    gap = max(spacing_mils * 0.25, 20.0)

    for move in moves:
        designator = str(move.get("designator") or "").strip().upper()
        component = pcb_components.get(designator)
        if not component:
            continue
        desired_x = float(move.get("xMils") or 0.0)
        desired_y = float(move.get("yMils") or 0.0)
        rotation = (
            float(move["rotation"]) if move.get("rotation") is not None else None
        )
        half_width, half_height = _bbox_half_size(component, rotation)
        moving = {
            "designator": designator,
            "layer": _normalize_layer(move.get("layer")),
            "x": desired_x,
            "y": desired_y,
            "half_width": half_width,
            "half_height": half_height,
        }

        anchor = str(move.get("anchor") or "").strip().upper()
        anchor_xy = _placement_xy(
            (pcb_components.get(anchor) or {}).get("placement") or {}
        ) or (desired_x, desired_y)
        chain_index = int(move.get("chainIndex") or 0)
        chain_members = move.get("chainMembers") or []
        if 0 < chain_index <= len(chain_members) - 1:
            previous = resolved_by_designator.get(
                str(chain_members[chain_index - 1]).strip().upper()
            )
            if previous:
                anchor_xy = (float(previous["x"]), float(previous["y"]))

        def blocked(test_x: float, test_y: float) -> bool:
            moving["x"] = test_x
            moving["y"] = test_y
            if any(_boxes_overlap(moving, box, gap) for box in occupied):
                return True
            for xmin, ymin, xmax, ymax in keepouts:
                if (
                    test_x + half_width >= xmin
                    and test_x - half_width <= xmax
                    and test_y + half_height >= ymin
                    and test_y - half_height <= ymax
                ):
                    return True
            return False

        adjusted = False
        if blocked(desired_x, desired_y):
            dx = desired_x - anchor_xy[0]
            dy = desired_y - anchor_xy[1]
            base_radius = max(math.hypot(dx, dy), spacing_mils)
            base_angle = math.atan2(dy, dx)
            radial_step = max(
                spacing_mils * 0.55, max(half_width, half_height) + gap
            )
            found = None
            for ring in range(64):
                radius = base_radius + ring * radial_step
                for angle_step in range(24):
                    signed_step = (
                        0
                        if angle_step == 0
                        else (1 if angle_step % 2 else -1) * ((angle_step + 1) // 2)
                    )
                    angle = base_angle + math.radians(signed_step * 15.0)
                    candidate = (
                        anchor_xy[0] + radius * math.cos(angle),
                        anchor_xy[1] + radius * math.sin(angle),
                    )
                    if not blocked(*candidate):
                        found = candidate
                        break
                if found:
                    break
            if found:
                desired_x, desired_y = found
                adjusted = True

        moving["x"] = desired_x
        moving["y"] = desired_y
        if adjusted:
            move["xMils"] = round(desired_x, 3)
            move["yMils"] = round(desired_y, 3)
            move["collisionAdjusted"] = True
            move["method"] = str(move.get("method") or "") + "_deconflict"
            adjusted_count += 1

        if any(_boxes_overlap(moving, box, gap) for box in occupied):
            move["collisionUnresolved"] = True
            unresolved.append(designator)
        occupied.append(moving)
        resolved_by_designator[designator] = moving

    return {
        "adjusted_count": adjusted_count,
        "unresolved_count": len(unresolved),
        "unresolved_designators": unresolved,
        "all_clear": not unresolved,
    }


def _collect_keepout_boxes(
    data: dict[str, Any],
) -> list[tuple[float, float, float, float]]:
    keepouts = ((data.get("pcb") or {}).get("keepouts") or {}).get("regions") or []
    boxes: list[tuple[float, float, float, float]] = []
    for region in keepouts:
        # Component courtyards are diagnostic snapshots at the old component
        # positions, not fixed board keepouts. Moving components are handled by the
        # final bounding-box occupancy pass.
        if str(region.get("kind") or "").casefold() == "component_courtyard":
            continue
        bbox = region.get("bboxMils")
        if isinstance(bbox, list) and len(bbox) >= 4:
            boxes.append(
                (float(bbox[0]), float(bbox[1]), float(bbox[2]), float(bbox[3]))
            )
            continue
        x_val = region.get("xMils")
        y_val = region.get("yMils")
        if x_val is None or y_val is None:
            continue
        half = float(region.get("radiusMils") or region.get("halfSizeMils") or 80.0)
        x_pos = float(x_val)
        y_pos = float(y_val)
        boxes.append((x_pos - half, y_pos - half, x_pos + half, y_pos + half))
    return boxes


def _collect_pcb_obstacles(
    pcb_components: dict[str, dict[str, Any]],
    *,
    skip: set[str],
) -> list[tuple[float, float, float]]:
    obstacles: list[tuple[float, float, float]] = []
    for designator, component in pcb_components.items():
        if designator in skip:
            continue
        xy = _placement_xy(component.get("placement") or {})
        if not xy:
            continue
        half_width, half_height = _bbox_half_size(component)
        radius = max(half_width, half_height)
        obstacles.append((xy[0], xy[1], radius))
    return obstacles


def _compute_passive_anchor_ownership(
    data: dict[str, Any],
    anchor_designators: list[str],
    pcb_components: dict[str, dict[str, Any]],
    *,
    same_sheet_only: bool,
    max_schematic_distance_mils: float,
    exclude_global_nets: bool,
    schematic_scale: float,
) -> dict[str, str]:
    """Assign each shared passive to the IC whose pin it should decouple/serve."""
    scores: dict[str, list[tuple[float, str]]] = defaultdict(list)

    for anchor in anchor_designators:
        grouping = get_ic_support_components(
            data,
            anchor,
            same_sheet_only=same_sheet_only,
            max_schematic_distance_mils=max_schematic_distance_mils,
            exclude_global_nets=exclude_global_nets,
        )
        if not grouping.get("found"):
            continue

        anchor_pcb = pcb_components.get(anchor) or {}
        anchor_xy = _placement_xy(anchor_pcb.get("placement") or {})
        if not anchor_xy:
            continue

        pin_layout = grouping.get("pin_layout") or {}
        pin_index = _build_pcb_pin_index(anchor_pcb)
        anchor_sch = _placement_xy(grouping.get("anchor_placement") or {})

        for item in grouping.get("support_components") or []:
            designator = item["designator"]
            pin_num = item.get("primary_ic_pin")
            pin_xy = _resolve_ic_pin_pcb_xy(
                str(pin_num) if pin_num else None,
                pin_index,
                pin_layout,
                anchor_sch,
                anchor_xy,
                schematic_scale,
            )
            passive_pcb = pcb_components.get(designator) or {}
            passive_xy = _placement_xy(passive_pcb.get("placement") or {})

            if pin_xy and passive_xy:
                score = math.hypot(passive_xy[0] - pin_xy[0], passive_xy[1] - pin_xy[1])
            elif pin_xy:
                score = 120.0
            else:
                score = 5000.0

            if item.get("primary_role") == "decoupling":
                score *= 0.82
            rank = _cap_proximity_rank(item)
            score += rank * 8.0
            scores[designator].append((score, anchor))

    ownership: dict[str, str] = {}
    for designator, options in scores.items():
        options.sort(key=lambda entry: (entry[0], _anchor_sort_key(entry[1])))
        ownership[designator] = options[0][1]
    return ownership


def _suggest_passive_rotation_deg(
    item: dict[str, Any],
    passive_xy: tuple[float, float],
    pin_xy: tuple[float, float] | None,
    pcb_component: dict[str, Any] | None = None,
) -> float | None:
    """Orient passives along the escape route; GND caps face signal pad toward the IC pin."""
    if not pin_xy:
        return None
    dx = passive_xy[0] - pin_xy[0]
    dy = passive_xy[1] - pin_xy[1]
    if math.hypot(dx, dy) < 1e-6:
        return None
    angle = math.degrees(math.atan2(dy, dx))
    snapped = round(angle / 90.0) * 90.0 % 360.0

    nets = {str(net).strip().upper() for net in (item.get("nets") or [])}
    if "GND" in nets and len(nets) >= 2:
        # Flip so the non-GND pad sits closer to the IC pin (long axis unchanged).
        snapped = (snapped + 180.0) % 360.0

    current = (pcb_component or {}).get("placement") or {}
    current_rot = current.get("rotation")
    if current_rot is not None and abs(float(current_rot) - snapped) <= 45.0:
        return float(current_rot)
    return snapped


def _is_rf_matching_anchor(data: dict[str, Any], anchor: str) -> bool:
    """True when anchor IC comment matches a known RF transceiver part."""
    target = str(anchor or "").strip().upper()
    tokens = (
        "SX126", "SX127", "SX128", "LLCC68", "RF4463", "SI4463", "NRF24", "NRF91",
        "AT86RF", "CC1101", "CC1125", "SUB-GHZ", "SUBGHZ", "LORA TRANSCEIVER",
    )
    for component in data.get("components") or []:
        designator = str(component.get("designator", "")).strip().upper()
        if designator != target:
            continue
        comment = str(component.get("comment") or "").upper()
        return any(token in comment for token in tokens)
    return False


def _should_use_rf_pi_t_placement(
    data: dict[str, Any],
    anchor: str,
    item: dict[str, Any],
    chain_lookup: dict[str, dict[str, Any]],
) -> bool:
    """RF transceiver matching networks: Pi-T series/shunt layout from the RF pin."""
    if not _is_rf_matching_anchor(data, anchor):
        return False
    designator = str(item.get("designator", "")).strip().upper()
    if designator not in chain_lookup:
        return False
    if item.get("primary_role") == "decoupling":
        return False
    primary_net = str(item.get("primary_net") or "").strip().upper()
    if primary_net in {"3V3", "3.3V", "GND", "XTA"} or _is_global_rail(primary_net):
        return False
    return True


def _is_rf_shunt_to_gnd(item: dict[str, Any]) -> bool:
    nets = {str(net).strip().upper() for net in (item.get("nets") or [])}
    return "GND" in nets and len(nets) >= 2


def _rf_pi_t_target_xy(
    item: dict[str, Any],
    chain_index: int,
    pin_xy: tuple[float, float],
    anchor_xy: tuple[float, float],
    *,
    spacing_mils: float,
    max_radius_mils: float,
    placed_points: list[tuple[float, float]],
    pcb_component: dict[str, Any] | None = None,
    keepout_boxes: list[tuple[float, float, float, float]] | None = None,
    pcb_obstacles: list[tuple[float, float, float]] | None = None,
) -> tuple[float, float, str, float, float, int, float, float | None]:
    """Place SX1262 matching as Pi-T: series on RF ray, shunts perpendicular to GND."""
    angle_deg = _pin_outward_angle_deg(pin_xy, anchor_xy)
    series_rad = math.radians(angle_deg)
    base = max(spacing_mils * 0.75, 50.0)
    body_radius = _passive_body_radius_mils(item, pcb_component)

    if chain_index == 0 or not _is_rf_shunt_to_gnd(item):
        along = base + (chain_index // 2) * max(spacing_mils * 1.0, 65.0)
        x = pin_xy[0] + along * math.cos(series_rad)
        y = pin_xy[1] + along * math.sin(series_rad)
        method = "rf_pi_t_series"
        standoff = along
    else:
        along = base + ((chain_index - 1) // 2) * max(spacing_mils * 1.0, 65.0)
        perp_sign = 1.0 if chain_index % 2 else -1.0
        perp = max(spacing_mils * 0.85, 55.0) * perp_sign
        center_x = pin_xy[0] + along * math.cos(series_rad)
        center_y = pin_xy[1] + along * math.sin(series_rad)
        perp_rad = series_rad + math.pi / 2.0
        x = center_x + perp * math.cos(perp_rad)
        y = center_y + perp * math.sin(perp_rad)
        method = "rf_pi_t_shunt"
        standoff = math.hypot(x - pin_xy[0], y - pin_xy[1])

    x, y = _resolve_collision(
        x,
        y,
        spacing_mils=spacing_mils,
        max_radius_mils=max_radius_mils,
        anchor_xy=anchor_xy,
        placed_points=placed_points,
        body_radius_mils=body_radius,
        keepout_boxes=keepout_boxes,
        pcb_obstacles=pcb_obstacles,
    )
    rotation = _suggest_passive_rotation_deg(item, (x, y), pin_xy, pcb_component)
    return (
        x,
        y,
        method,
        angle_deg,
        standoff,
        chain_index,
        0.0,
        rotation,
    )


def _sch_pin_to_pcb_xy(
    pin_number: str | None,
    pin_layout: dict[str, dict[str, Any]],
    anchor_sch_xy: tuple[float, float] | None,
    anchor_pcb_xy: tuple[float, float],
    scale: float,
) -> tuple[float, float] | None:
    if not pin_number or not anchor_sch_xy:
        return None
    layout = pin_layout.get(str(pin_number).strip())
    if not layout:
        return None
    px = layout.get("xMils")
    py = layout.get("yMils")
    if px is None or py is None:
        return None
    dx = float(px) - anchor_sch_xy[0]
    dy = float(py) - anchor_sch_xy[1]
    return (
        anchor_pcb_xy[0] + dx * scale,
        anchor_pcb_xy[1] + dy * scale,
    )


def _pin_edge_standoff_mils(role: str, spacing_mils: float) -> float:
    """Distance from the linked IC pin pad to the passive center (tight for routing)."""
    return {
        "decoupling": max(spacing_mils * 0.55, 40.0),
        "power": max(spacing_mils * 0.85, 55.0),
        "signal": max(spacing_mils * 1.05, 65.0),
    }.get(role or "support", max(spacing_mils * 1.2, 75.0))


def _resolve_collision(
    x: float,
    y: float,
    *,
    spacing_mils: float,
    max_radius_mils: float,
    anchor_xy: tuple[float, float],
    placed_points: list[tuple[float, float]],
    body_radius_mils: float = 16.0,
    keepout_boxes: list[tuple[float, float, float, float]] | None = None,
    pcb_obstacles: list[tuple[float, float, float]] | None = None,
) -> tuple[float, float]:
    min_sep = max(spacing_mils * 0.7, 55.0) + body_radius_mils

    def blocked(test_x: float, test_y: float) -> bool:
        if any(math.hypot(test_x - px, test_y - py) < min_sep for px, py in placed_points):
            return True
        for ox, oy, orad in pcb_obstacles or []:
            if math.hypot(test_x - ox, test_y - oy) < min_sep + orad:
                return True
        for xmin, ymin, xmax, ymax in keepout_boxes or []:
            if xmin <= test_x <= xmax and ymin <= test_y <= ymax:
                return True
        return False

    for attempt in range(16):
        if not blocked(x, y):
            break
        angle = math.radians(attempt * 31.0)
        bump = spacing_mils * (0.35 + attempt * 0.08)
        x += bump * math.cos(angle)
        y += bump * math.sin(angle)
        dist = math.hypot(x - anchor_xy[0], y - anchor_xy[1])
        if dist > max_radius_mils > 0:
            scale = max_radius_mils / dist
            x = anchor_xy[0] + (x - anchor_xy[0]) * scale
            y = anchor_xy[1] + (y - anchor_xy[1]) * scale
    placed_points.append((x, y))
    return x, y


def _pin_accurate_target_xy(
    item: dict[str, Any],
    anchor_pcb_xy: tuple[float, float],
    anchor_sch_xy: tuple[float, float] | None,
    pin_layout: dict[str, dict[str, Any]],
    pcb_pin_index: dict[str, dict[str, Any]],
    *,
    scale: float,
    spacing_mils: float,
    max_radius_mils: float,
    pin_slot_counts: dict[tuple[str, str], int],
    placed_points: list[tuple[float, float]],
    pcb_component: dict[str, Any] | None = None,
    keepout_boxes: list[tuple[float, float, float, float]] | None = None,
    pcb_obstacles: list[tuple[float, float, float]] | None = None,
) -> tuple[float, float, str, float | None, float, int, float, float | None, str, bool]:
    """Place each passive using schematic layout + PCB pad coordinates when available.

    Returns the usual layout tuple plus (layer, mirror) so decoupling caps can be
    flagged for the bottom side of the board.
    """
    sch = item.get("schematic") or {}
    role = item.get("primary_role") or "support"
    pin_number = item.get("primary_ic_pin")
    pin_key = (
        str(pin_number or "unknown"),
        str(item.get("primary_net") or "unknown"),
    )
    slot = pin_slot_counts.get(pin_key, 0)
    pin_slot_counts[pin_key] = slot + 1

    target_pin_angle = sch.get("pinAngleDeg")
    if target_pin_angle is None:
        target_pin_angle = sch.get("angleDeg")

    offset_x = sch.get("offsetXMils")
    offset_y = sch.get("offsetYMils")
    has_mirror = (
        anchor_sch_xy is not None
        and offset_x is not None
        and offset_y is not None
    )

    if has_mirror:
        scaled_x = float(offset_x) * scale
        scaled_y = float(offset_y) * scale
        dist = math.hypot(scaled_x, scaled_y)
        if dist > max_radius_mils > 0:
            clamp = max_radius_mils / dist
            scaled_x *= clamp
            scaled_y *= clamp
        x = anchor_pcb_xy[0] + scaled_x
        y = anchor_pcb_xy[1] + scaled_y
        method = "pin_accurate_mirror"
    else:
        x = anchor_pcb_xy[0]
        y = anchor_pcb_xy[1]
        method = "pin_accurate_fallback"

    pin_xy = _resolve_ic_pin_pcb_xy(
        str(pin_number) if pin_number else None,
        pcb_pin_index,
        pin_layout,
        anchor_sch_xy,
        anchor_pcb_xy,
        scale,
    )
    used_pcb_pad = bool(
        pin_number
        and pcb_pin_index.get(str(pin_number).strip())
    )
    cap_rank = _cap_proximity_rank(item)
    is_decoupling = role == "decoupling"
    body_radius = _passive_body_radius_mils(item, pcb_component)

    # Decoupling caps go on the BOTTOM side, directly under the IC power pin, with a
    # minimal standoff so the cap's near pad sits at the pin pad edge -- shortest
    # via-to-pin current loop for high-frequency decoupling. Other support passives
    # stay on top at the usual pin-edge standoff.
    if is_decoupling:
        standoff = (
            body_radius
            + max(spacing_mils * 0.15, 8.0)
            + slot * spacing_mils * 0.45
            - cap_rank * spacing_mils * 0.15
        )
        standoff = max(standoff, body_radius + 4.0)
    else:
        standoff = (
            _pin_edge_standoff_mils(role, spacing_mils)
            + slot * spacing_mils * 0.45
            - cap_rank * spacing_mils * 0.22
        )
        standoff = max(standoff, spacing_mils * 0.35)

    if pin_xy is not None:
        if target_pin_angle is None:
            target_pin_angle = _pin_outward_angle_deg(pin_xy, anchor_pcb_xy)
        angle_deg = float(target_pin_angle) + slot * 11.0
        angle = math.radians(angle_deg)
        pin_x = pin_xy[0] + standoff * math.cos(angle)
        pin_y = pin_xy[1] + standoff * math.sin(angle)

        if has_mirror and not used_pcb_pad:
            blend = 0.72 if role == "decoupling" else 0.58 if role == "signal" else 0.5
            x = (1.0 - blend) * x + blend * pin_x
            y = (1.0 - blend) * y + blend * pin_y
            method = "pin_accurate_blend"
        else:
            pcb_blend = 0.88 if used_pcb_pad else 0.72
            if has_mirror:
                x = (1.0 - pcb_blend) * x + pcb_blend * pin_x
                y = (1.0 - pcb_blend) * y + pcb_blend * pin_y
                method = "pin_accurate_pcb_pad"
            else:
                x, y = pin_x, pin_y
                method = "pin_accurate_pcb_pad" if used_pcb_pad else "pin_accurate_pin"

        dist = math.hypot(x - anchor_pcb_xy[0], y - anchor_pcb_xy[1])
        if dist > max_radius_mils > 0:
            clamp = max_radius_mils / dist
            x = anchor_pcb_xy[0] + (x - anchor_pcb_xy[0]) * clamp
            y = anchor_pcb_xy[1] + (y - anchor_pcb_xy[1]) * clamp
    elif target_pin_angle is None:
        target_pin_angle = (slot * 137.508) % 360.0

    angle_offset_deg = slot * 11.0
    x, y = _resolve_collision(
        x,
        y,
        spacing_mils=spacing_mils,
        max_radius_mils=max_radius_mils,
        anchor_xy=anchor_pcb_xy,
        placed_points=placed_points,
        body_radius_mils=_courtyard_half_size_mils(item, pcb_component),
        keepout_boxes=keepout_boxes,
        pcb_obstacles=pcb_obstacles,
    )
    standoff_mils = (
        math.hypot(x - pin_xy[0], y - pin_xy[1])
        if pin_xy is not None
        else math.hypot(x - anchor_pcb_xy[0], y - anchor_pcb_xy[1])
    )
    rotation = _suggest_passive_rotation_deg(item, (x, y), pin_xy, pcb_component)
    # Bottom-side decoupling caps are flipped via FlipXY at apply time, which mirrors
    # the component and swaps pad positions. Adding 180 deg compensates so the VCC pad
    # still faces the IC pin (short via) and the GND pad faces the ground plane.
    if is_decoupling and rotation is not None:
        rotation = (rotation + 180.0) % 360.0
    return (
        x,
        y,
        method + ("_bottom" if is_decoupling else ""),
        float(target_pin_angle) if target_pin_angle is not None else None,
        float(standoff_mils),
        slot,
        float(angle_offset_deg),
        rotation,
        "bottom" if is_decoupling else "top",
        bool(is_decoupling),
    )


def _anchor_sort_key(designator: str) -> tuple[int, int, str]:
    match = re.match(r"^(IC|U)(\d+)", str(designator or "").strip().upper())
    if not match:
        return (9, 9999, designator)
    prefix_rank = 0 if match.group(1) == "IC" else 1
    return (prefix_rank, int(match.group(2)), designator.upper())


def list_cluster_anchor_designators(
    data: dict[str, Any],
    *,
    same_sheet_only: bool = True,
    max_schematic_distance_mils: float = 2500.0,
    exclude_global_nets: bool = True,
    min_support_count: int = 1,
) -> list[str]:
    """Return IC/U designators on the PCB that have local support parts to cluster."""
    pcb_components = {
        str(component.get("designator", "")).strip().upper(): component
        for component in (data.get("pcb") or {}).get("components") or []
    }

    seen: set[str] = set()
    candidates: list[tuple[str, int]] = []
    for component in data.get("components") or []:
        designator = str(component.get("designator", "")).strip().upper()
        if not designator or not IC_DESIGNATOR.match(designator) or designator in seen:
            continue
        seen.add(designator)
        if designator not in pcb_components:
            continue

        grouping = get_ic_support_components(
            data,
            designator,
            same_sheet_only=same_sheet_only,
            max_schematic_distance_mils=max_schematic_distance_mils,
            exclude_global_nets=exclude_global_nets,
        )
        support_count = int(grouping.get("support_count") or 0)
        if grouping.get("found") and support_count >= min_support_count:
            candidates.append((designator, support_count))

    candidates.sort(key=lambda item: (_anchor_sort_key(item[0]), -item[1]))
    return [designator for designator, _ in candidates]


def _build_moves_for_grouping(
    grouping: dict[str, Any],
    pcb_components: dict[str, dict[str, Any]],
    *,
    spacing_mils: float = 80.0,
    layout_mode: str = "pin_accurate",
    max_radius_mils: float = 900.0,
    schematic_scale: float = 0.12,
    grid_cols: int = 6,
) -> tuple[list[dict[str, Any]], str | None]:
    target = grouping["anchor"]
    anchor_pcb = pcb_components.get(target)
    if not anchor_pcb:
        return [], f"PCB placement for '{target}' not found."

    anchor_xy = _placement_xy(anchor_pcb.get("placement") or {})
    if not anchor_xy:
        return [], f"PCB coordinates missing for anchor '{target}'."

    anchor_sch_xy = _placement_xy(grouping.get("anchor_placement") or {})
    pin_layout = grouping.get("pin_layout") or {}
    pcb_pin_index = _build_pcb_pin_index(anchor_pcb)

    mode = str(layout_mode or "pin_accurate").casefold()
    if mode in {"pin", "pin_proximity", "pin-proximity", "pinproximity"}:
        mode = "pin_near"
    if mode in {"accurate", "schematic_pin", "pin-accurate"}:
        mode = "pin_accurate"

    effective_scale = schematic_scale
    if mode == "pin_accurate":
        effective_scale = _auto_schematic_scale(
            grouping.get("support_components") or [],
            max_radius_mils,
            fallback=schematic_scale,
        )

    support_des = {
        str(item.get("designator", "")).strip().upper()
        for item in grouping.get("support_components") or []
    }
    keepout_boxes = (
        _collect_keepout_boxes(grouping["data"])
        if isinstance(grouping.get("data"), dict)
        else []
    )
    pcb_obstacles = _collect_pcb_obstacles(
        pcb_components,
        skip=support_des | {target.upper()},
    )

    moves: list[dict[str, Any]] = []
    fallback_index = 0
    net_slot_counts: dict[str, int] = {}
    pin_slot_counts: dict[tuple[str, str], int] = {}
    placed_points: list[tuple[float, float]] = []

    # Seed the anchor IC as a fixed obstacle so local placement never overlaps it.
    placed_points.append(anchor_xy)

    chains = grouping.get("chains") or []
    chain_lookup: dict[str, dict[str, Any]] = {}
    support_by_des = {
        str(item.get("designator", "")).strip().upper(): item
        for item in grouping.get("support_components") or []
    }
    for chain in chains:
        members = [str(member).strip().upper() for member in chain.get("members") or []]
        for index, member in enumerate(members):
            chain_lookup[member] = {
                "chain_id": chain.get("chain_id"),
                "chain_index": index,
                "chain_length": len(members),
                "chain_members": members,
            }

    chained_designators: set[str] = set()
    move_index = 0

    def append_move(
        item: dict[str, Any],
        target_x: float,
        target_y: float,
        method: str,
        *,
        target_pin_angle: float | None,
        standoff_mils: float | None,
        pin_slot: int,
        angle_offset_deg: float,
        chain_meta: dict[str, Any] | None = None,
        rotation_deg: float | None = None,
        layer: str = "top",
        mirror: bool = False,
    ) -> None:
        nonlocal move_index
        designator = item["designator"]
        pcb_component = pcb_components.get(designator)
        if not pcb_component:
            return

        current = pcb_component.get("placement") or {}
        current_xy = _placement_xy(current)
        placement_angle = math.degrees(
            math.atan2(float(target_y) - anchor_xy[1], float(target_x) - anchor_xy[0])
        )
        rotation = rotation_deg
        if rotation is None:
            rotation = current.get("rotation")
        if rotation is None and item["schematic"].get("placement"):
            rotation = item["schematic"]["placement"].get("rotation")

        move: dict[str, Any] = {
            "designator": designator,
            "anchor": target,
            "comment": item.get("comment"),
            "xMils": round(target_x, 3),
            "yMils": round(target_y, 3),
            "rotation": rotation,
            # Target board side: decoupling caps target the bottom side directly under
            # the IC pin; everything else stays on top.
            "layer": layer or "top",
            "mirror": bool(mirror),
            "method": method,
            "roles": item.get("roles") or [],
            "nets": item.get("nets") or [],
            "primary_net": item.get("primary_net"),
            "primary_ic_pin": item.get("primary_ic_pin"),
            "primary_ic_pin_name": item.get("primary_ic_pin_name"),
            "linked_ic_pins": item.get("linked_ic_pins") or [],
            "targetPinAngleDeg": target_pin_angle
            if target_pin_angle is not None
            else item.get("schematic", {}).get("pinAngleDeg"),
            "pinSlot": pin_slot,
            "angleOffsetDeg": round(angle_offset_deg, 2),
            "standoffMils": round(standoff_mils, 3)
            if standoff_mils is not None
            else round(math.hypot(target_x - anchor_xy[0], target_y - anchor_xy[1]), 3),
            "placementAngleDeg": round(placement_angle, 2),
            "current": {
                "xMils": current_xy[0] if current_xy else None,
                "yMils": current_xy[1] if current_xy else None,
                "rotation": current.get("rotation"),
                "layer": pcb_component.get("layer") or current.get("layer") or "Top",
            },
        }
        if chain_meta:
            move["chainId"] = chain_meta.get("chain_id")
            move["chainIndex"] = chain_meta.get("chain_index")
            move["chainLength"] = chain_meta.get("chain_length")
            move["chainMembers"] = chain_meta.get("chain_members")
        moves.append(move)
        move_index += 1

    if mode in {"pin_near", "pin_chain", "pin_accurate"} and chains:
        for chain in chains:
            members = [str(member).strip().upper() for member in chain.get("members") or []]
            chain_items = [
                support_by_des[member]
                for member in members
                if member in support_by_des
            ]
            has_decoupling = any(
                str(item.get("primary_role") or "").casefold() == "decoupling"
                for item in chain_items
            )
            uses_rf_pi_t = mode == "pin_accurate" and any(
                _should_use_rf_pi_t_placement(
                    grouping.get("data") or {},
                    target,
                    item,
                    chain_lookup,
                )
                for item in chain_items
            )
            if has_decoupling or uses_rf_pi_t:
                continue

            previous_xy: tuple[float, float] | None = None
            chain_pin_xy: tuple[float, float] | None = None
            for index, member in enumerate(members):
                item = support_by_des.get(member)
                if not item:
                    continue
                meta = chain_lookup.get(member, {})
                chained_designators.add(member)
                if index == 0:
                    pin_number = item.get("primary_ic_pin")
                    chain_pin_xy = _resolve_ic_pin_pcb_xy(
                        str(pin_number) if pin_number else None,
                        pcb_pin_index,
                        pin_layout,
                        anchor_sch_xy,
                        anchor_xy,
                        effective_scale,
                    )
                target_x, target_y, method, target_pin_angle, standoff_mils, pin_slot, angle_offset_deg = _chain_target_xy(
                    item,
                    index,
                    len(members),
                    anchor_xy,
                    spacing_mils=spacing_mils,
                    max_radius_mils=max_radius_mils,
                    placed_points=placed_points,
                    previous_xy=previous_xy,
                    pin_xy=chain_pin_xy,
                )
                previous_xy = (target_x, target_y)
                append_move(
                    item,
                    target_x,
                    target_y,
                    method,
                    target_pin_angle=target_pin_angle,
                    standoff_mils=standoff_mils,
                    pin_slot=pin_slot,
                    angle_offset_deg=angle_offset_deg,
                    chain_meta=meta,
                )

    for index, item in enumerate(grouping["support_components"]):
        designator = item["designator"]
        if designator in chained_designators:
            continue
        pcb_component = pcb_components.get(designator)
        if not pcb_component:
            continue

        current = pcb_component.get("placement") or {}
        current_xy = _placement_xy(current)
        target_pin_angle = None
        standoff_mils = None
        pin_slot = 0
        angle_offset_deg = 0.0
        rotation_deg = None
        move_layer = "top"
        move_mirror = False

        if (
            mode == "pin_accurate"
            and _should_use_rf_pi_t_placement(
                grouping.get("data") or {},
                target,
                item,
                chain_lookup,
            )
        ):
            pin_number = item.get("primary_ic_pin")
            pin_xy = _resolve_ic_pin_pcb_xy(
                str(pin_number) if pin_number else None,
                pcb_pin_index,
                pin_layout,
                anchor_sch_xy,
                anchor_xy,
                effective_scale,
            )
            chain_meta = chain_lookup.get(designator, {})
            chain_index = int(chain_meta.get("chain_index") or 0)
            if pin_xy is not None:
                (
                    target_x,
                    target_y,
                    method,
                    target_pin_angle,
                    standoff_mils,
                    pin_slot,
                    angle_offset_deg,
                    rotation_deg,
                ) = _rf_pi_t_target_xy(
                    item,
                    chain_index,
                    pin_xy,
                    anchor_xy,
                    spacing_mils=spacing_mils,
                    max_radius_mils=max_radius_mils,
                    placed_points=placed_points,
                    pcb_component=pcb_component,
                    keepout_boxes=keepout_boxes,
                    pcb_obstacles=pcb_obstacles,
                )
                append_move(
                    item,
                    target_x,
                    target_y,
                    method,
                    target_pin_angle=target_pin_angle,
                    standoff_mils=standoff_mils,
                    pin_slot=pin_slot,
                    angle_offset_deg=angle_offset_deg,
                    chain_meta=chain_meta,
                    rotation_deg=rotation_deg,
                )
                continue

        if mode == "schematic_mirror":
            target_x, target_y, method = _mirror_target_xy(
                item,
                anchor_xy,
                schematic_scale=effective_scale,
                max_radius_mils=max_radius_mils,
            )
        elif mode == "pin_accurate":
            (
                target_x,
                target_y,
                method,
                target_pin_angle,
                standoff_mils,
                pin_slot,
                angle_offset_deg,
                rotation_deg,
                pin_accurate_layer,
                pin_accurate_mirror,
            ) = _pin_accurate_target_xy(
                item,
                anchor_xy,
                anchor_sch_xy,
                pin_layout,
                pcb_pin_index,
                scale=effective_scale,
                spacing_mils=spacing_mils,
                max_radius_mils=max_radius_mils,
                pin_slot_counts=pin_slot_counts,
                placed_points=placed_points,
                pcb_component=pcb_component,
                keepout_boxes=keepout_boxes,
                pcb_obstacles=pcb_obstacles,
            )
            move_layer = pin_accurate_layer
            move_mirror = pin_accurate_mirror
        elif mode in {"pin_near", "pin_chain"}:
            target_x, target_y, method, target_pin_angle, standoff_mils, pin_slot, angle_offset_deg = _pin_near_target_xy(
                item,
                index,
                anchor_xy,
                spacing_mils=spacing_mils,
                max_radius_mils=max_radius_mils,
                pin_slot_counts=pin_slot_counts,
                placed_points=placed_points,
            )
        elif mode == "compact":
            target_x, target_y, method = _compact_target_xy(
                item,
                index,
                anchor_xy,
                spacing_mils=spacing_mils,
                max_radius_mils=max_radius_mils,
                net_slot_counts=net_slot_counts,
            )
        else:
            row = fallback_index // grid_cols
            col = fallback_index % grid_cols
            target_x = anchor_xy[0] + (col - grid_cols / 2.0) * spacing_mils
            target_y = anchor_xy[1] - spacing_mils * (1.5 + row)
            method = "grid_fallback"
            fallback_index += 1

        append_move(
            item,
            target_x,
            target_y,
            method,
            target_pin_angle=target_pin_angle,
            standoff_mils=standoff_mils,
            pin_slot=pin_slot,
            angle_offset_deg=angle_offset_deg,
            rotation_deg=rotation_deg,
            layer=move_layer,
            mirror=move_mirror,
        )

    return moves, None


def build_all_ic_cluster_plan(
    data: dict[str, Any],
    *,
    spacing_mils: float = 80.0,
    layout_mode: str = "pin_accurate",
    max_radius_mils: float = 900.0,
    schematic_scale: float = 0.12,
    same_sheet_only: bool = True,
    max_schematic_distance_mils: float = 2500.0,
    exclude_global_nets: bool = True,
    min_support_count: int = 1,
) -> dict[str, Any]:
    """Build one combined PCB plan that clusters support parts around every IC/U module."""
    anchor_designators = list_cluster_anchor_designators(
        data,
        same_sheet_only=same_sheet_only,
        max_schematic_distance_mils=max_schematic_distance_mils,
        exclude_global_nets=exclude_global_nets,
        min_support_count=min_support_count,
    )
    if not anchor_designators:
        return {
            "found": False,
            "mode": "all_clusters",
            "error": "No IC/U modules with local support parts were found on the PCB export.",
        }

    pcb_components = {
        str(component.get("designator", "")).strip().upper(): component
        for component in (data.get("pcb") or {}).get("components") or []
    }

    passive_owner = _compute_passive_anchor_ownership(
        data,
        anchor_designators,
        pcb_components,
        same_sheet_only=same_sheet_only,
        max_schematic_distance_mils=max_schematic_distance_mils,
        exclude_global_nets=exclude_global_nets,
        schematic_scale=schematic_scale,
    )

    assigned_passives: set[str] = set()
    all_moves: list[dict[str, Any]] = []
    cluster_summaries: list[dict[str, Any]] = []
    cluster_verifications: list[dict[str, Any]] = []

    for anchor in anchor_designators:
        grouping = get_ic_support_components(
            data,
            anchor,
            same_sheet_only=same_sheet_only,
            max_schematic_distance_mils=max_schematic_distance_mils,
            exclude_global_nets=exclude_global_nets,
        )
        grouping["data"] = data
        grouping["pcb_keepouts"] = (data.get("pcb") or {}).get("keepouts")
        if not grouping.get("found"):
            cluster_summaries.append(
                {
                    "anchor": anchor,
                    "move_count": 0,
                    "support_count": 0,
                    "note": grouping.get("error") or "not_found",
                }
            )
            continue

        available_support = [
            item
            for item in grouping["support_components"]
            if passive_owner.get(item["designator"], anchor) == anchor
        ]
        if not available_support:
            cluster_summaries.append(
                {
                    "anchor": anchor,
                    "move_count": 0,
                    "support_count": 0,
                    "note": "support_parts_owned_by_other_ics",
                }
            )
            continue

        filtered_grouping = dict(grouping)
        filtered_grouping["support_components"] = expand_series_chain_support(
            data,
            anchor,
            available_support,
            max_schematic_distance_mils=max_schematic_distance_mils,
        )
        filtered_grouping["chains"] = detect_support_chains(
            data,
            anchor,
            filtered_grouping["support_components"],
        )
        moves, error = _build_moves_for_grouping(
            filtered_grouping,
            pcb_components,
            spacing_mils=spacing_mils,
            layout_mode=layout_mode,
            max_radius_mils=max_radius_mils,
            schematic_scale=schematic_scale,
        )
        if error:
            cluster_summaries.append(
                {
                    "anchor": anchor,
                    "move_count": 0,
                    "support_count": len(available_support),
                    "note": error,
                }
            )
            continue

        for move in moves:
            des = str(move.get("designator", "")).strip()
            if not des or des in assigned_passives:
                continue
            assigned_passives.add(des)
            all_moves.append(move)

        anchor_pcb = pcb_components.get(anchor) or {}
        anchor_xy = _placement_xy(anchor_pcb.get("placement") or {})
        verification = verify_cluster_placement(
            anchor=anchor,
            anchor_xy=anchor_xy or (0.0, 0.0),
            support_components=available_support,
            moves=moves,
            spacing_mils=spacing_mils,
            max_radius_mils=max_radius_mils,
        )
        cluster_verifications.append(verification)

        cluster_summaries.append(
            {
                "anchor": anchor,
                "anchor_comment": grouping.get("anchor_comment"),
                "move_count": len(moves),
                "support_count": len(available_support),
                "verification_ok": verification.get("all_ok", False),
                "verification_warn_count": verification.get("warn_count", 0),
            }
        )

    active_clusters = [summary for summary in cluster_summaries if summary.get("move_count", 0) > 0]
    if not all_moves:
        return {
            "found": False,
            "mode": "all_clusters",
            "anchors": anchor_designators,
            "clusters": cluster_summaries,
            "error": "No cluster moves were generated for the available IC/U modules.",
        }

    collision_validation = _resolve_final_move_overlaps(
        data,
        all_moves,
        spacing_mils=spacing_mils,
        max_radius_mils=max_radius_mils,
    )

    return {
        "found": True,
        "schemaVersion": PLAN_SCHEMA_VERSION,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "mode": "all_clusters",
        "anchor": "ALL",
        "anchors": anchor_designators,
        "cluster_count": len(active_clusters),
        "clusterName": "ALL_ICU_CLUSTERS",
        "layoutMode": str(layout_mode or "pin_near").casefold(),
        "spacingMils": spacing_mils,
        "maxRadiusMils": max_radius_mils,
        "move_count": len(all_moves),
        "support_count": len(assigned_passives),
        "collision_validation": collision_validation,
        "verification": {
            "all_ok": all(item.get("all_ok") for item in cluster_verifications),
            "cluster_count": len(cluster_verifications),
            "ok_count": sum(item.get("ok_count", 0) for item in cluster_verifications),
            "warn_count": sum(item.get("warn_count", 0) for item in cluster_verifications),
            "clusters": cluster_verifications,
        },
        "filters": {
            "same_sheet_only": same_sheet_only,
            "max_schematic_distance_mils": max_schematic_distance_mils,
            "exclude_global_nets": exclude_global_nets,
            "min_support_count": min_support_count,
        },
        "clusters": cluster_summaries,
        "moves": all_moves,
    }


def build_ic_placement_plan(
    data: dict[str, Any],
    ic_designator: str,
    *,
    spacing_mils: float = 80.0,
    layout_mode: str = "pin_accurate",
    max_radius_mils: float = 900.0,
    schematic_scale: float = 0.12,
    same_sheet_only: bool = True,
    max_schematic_distance_mils: float = 2500.0,
    exclude_global_nets: bool = True,
    grid_cols: int = 6,
) -> dict[str, Any]:
    """Build PCB move targets near each linked IC pin for the anchor's support parts."""
    grouping = get_ic_support_components(
        data,
        ic_designator,
        same_sheet_only=same_sheet_only,
        max_schematic_distance_mils=max_schematic_distance_mils,
        exclude_global_nets=exclude_global_nets,
    )
    if not grouping.get("found"):
        return grouping

    target = grouping["anchor"]
    pcb_components = {
        str(component.get("designator", "")).strip().upper(): component
        for component in (data.get("pcb") or {}).get("components") or []
    }

    chains = detect_support_chains(
        data,
        target,
        expand_series_chain_support(
            data,
            target,
            grouping["support_components"],
            max_schematic_distance_mils=max_schematic_distance_mils,
        ),
    )
    expanded_support = expand_series_chain_support(
        data,
        target,
        grouping["support_components"],
        max_schematic_distance_mils=max_schematic_distance_mils,
    )
    grouping = dict(grouping)
    grouping["support_components"] = expanded_support
    grouping["chains"] = chains
    grouping["data"] = data

    moves, error = _build_moves_for_grouping(
        grouping,
        pcb_components,
        spacing_mils=spacing_mils,
        layout_mode=layout_mode,
        max_radius_mils=max_radius_mils,
        schematic_scale=schematic_scale,
        grid_cols=grid_cols,
    )
    if error:
        return {
            "found": False,
            "anchor": target,
            "error": error,
        }

    collision_validation = _resolve_final_move_overlaps(
        data,
        moves,
        spacing_mils=spacing_mils,
        max_radius_mils=max_radius_mils,
    )

    anchor_pcb = pcb_components.get(target) or {}
    anchor_xy = _placement_xy(anchor_pcb.get("placement") or {})
    verification = verify_cluster_placement(
        anchor=target,
        anchor_xy=anchor_xy or (0.0, 0.0),
        support_components=expanded_support,
        moves=moves,
        spacing_mils=spacing_mils,
        max_radius_mils=max_radius_mils,
    )

    return {
        "found": True,
        "schemaVersion": PLAN_SCHEMA_VERSION,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "anchor": target,
        "clusterName": target,
        "layoutMode": str(layout_mode or "pin_near").casefold(),
        "spacingMils": spacing_mils,
        "maxRadiusMils": max_radius_mils,
        "support_count": len(grouping["support_components"]),
        "move_count": len(moves),
        "has_schematic_coords": grouping.get("has_schematic_coords", False),
        "has_pin_layout": grouping.get("has_pin_layout", False),
        "filters": grouping.get("filters"),
        "rejected_counts": grouping.get("rejected_counts"),
        "chains": chains,
        "chain_count": len(chains),
        "collision_validation": collision_validation,
        "verification": verification,
        "moves": moves,
    }


def write_placement_plan(plan: dict[str, Any], path: Path | str | None = None) -> Path:
    target = Path(path or DEFAULT_PLAN_PATH)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(plan, indent=2), encoding="utf-8")
    return target
