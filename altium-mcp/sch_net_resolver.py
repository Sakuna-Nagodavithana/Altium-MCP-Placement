"""Resolve schematic port/net-label names to component pins via SchDoc wire graph."""

from __future__ import annotations

import re
from collections import defaultdict, deque
from pathlib import Path
from typing import Any


def _wire_points(block: str) -> list[tuple[int, int]]:
    match = re.search(
        r"LocationCount=\d+\|((?:X\d+=\d+\|Y\d+=\d+(?:\|Y\d+_Frac=\d+)?\|)+)", block
    )
    if not match:
        return []
    return [
        (int(item[1]), int(item[2]))
        for item in re.findall(r"X(\d+)=(\d+)\|Y\1=(\d+)", match.group(1))
    ]


def _stub_points(block: str) -> list[tuple[int, int]]:
    match = re.search(r"LocationCount=\d+\|((?:X\d+=\d+\|Y\d+=\d+\|)+)", block)
    if not match:
        return []
    return [
        (int(item[1]), int(item[2]))
        for item in re.findall(r"X(\d+)=(\d+)\|Y\1=(\d+)", match.group(1))
    ]


def _build_adjacency(data: str) -> dict[tuple[int, int], set[tuple[int, int]]]:
    adj: dict[tuple[int, int], set[tuple[int, int]]] = defaultdict(set)
    for block in data.split("|RECORD=27|")[1:]:
        points = _wire_points(block)
        for left, right in zip(points, points[1:]):
            adj[left].add(right)
            adj[right].add(left)
    for block in data.split("|RECORD=25|")[1:]:
        points = _stub_points(block)
        for left, right in zip(points, points[1:]):
            adj[left].add(right)
            adj[right].add(left)
    return adj


def _record_blocks(data: str) -> list[tuple[str, str]]:
    blocks: list[tuple[str, str]] = []
    for part in data.split("|RECORD=")[1:]:
        record_id, _, body = part.partition("|")
        blocks.append((record_id, body))
    return blocks


def _field(body: str, name: str) -> str | None:
    match = re.search(rf"\|{re.escape(name)}=([^|]*)", "|" + body)
    return match.group(1).strip() if match else None


def _component_pins(data: str) -> dict[str, list[dict[str, Any]]]:
    owner_to_designator: dict[str, str] = {}
    for record_id, body in _record_blocks(data):
        if record_id not in {"34", "41"}:
            continue
        text = _field(body, "Text")
        if _field(body, "Name") != "Designator" or not text:
            continue
        owner = _field(body, "OwnerIndex")
        if owner:
            owner_to_designator[owner] = text

    pins_by_designator: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for record_id, body in _record_blocks(data):
        if record_id != "2":
            continue
        owner = _field(body, "OwnerIndex")
        pin_number = _field(body, "Designator")
        pin_name = _field(body, "Name")
        x_text = _field(body, "Location.X")
        y_text = _field(body, "Location.Y")
        if not owner or not pin_number or not pin_name or not x_text or not y_text:
            continue
        designator = owner_to_designator.get(owner)
        if not designator:
            continue
        pins_by_designator[designator].append(
            {
                "designator": designator,
                "pin": pin_number,
                "pin_name": pin_name,
                "x": int(x_text),
                "y": int(y_text),
            }
        )
    return pins_by_designator


def _named_nodes(data: str) -> dict[tuple[int, int], str]:
    nodes: dict[tuple[int, int], str] = {}

    for record_id, body in _record_blocks(data):
        x_text = _field(body, "Location.X")
        y_text = _field(body, "Location.Y")
        if not x_text or not y_text:
            continue
        point = (int(x_text), int(y_text))

        if record_id == "17":
            name = _field(body, "Text")
            if name and not name.startswith("="):
                nodes[point] = name
            continue

        if record_id == "18":
            name = _field(body, "Name")
            if name and name not in {"Designator", "Comment", "PinUniqueId", "Net", "PinName"}:
                nodes[point] = name

    return nodes


def _nearest_wire_node(
    adj: dict[tuple[int, int], set[tuple[int, int]]],
    x: int,
    y: int,
    tolerance: int = 40,
) -> tuple[int, int] | None:
    best: tuple[int, int] | None = None
    best_distance = tolerance + 1
    for node in adj:
        distance = abs(node[0] - x) + abs(node[1] - y)
        if distance <= tolerance and distance < best_distance:
            best = node
            best_distance = distance
    return best


def _reachable_named_nodes(
    adj: dict[tuple[int, int], set[tuple[int, int]]],
    named_nodes: dict[tuple[int, int], str],
    start: tuple[int, int],
) -> set[str]:
    seen = {start}
    queue: deque[tuple[int, int]] = deque([start])
    hits: set[str] = set()
    while queue:
        point = queue.popleft()
        if point in named_nodes:
            hits.add(named_nodes[point])
        for neighbor in adj.get(point, []):
            if neighbor not in seen:
                seen.add(neighbor)
                queue.append(neighbor)
    return hits


def _looks_auto_generated(name: str) -> bool:
    if name.upper().startswith("NET") and len(name) > 3:
        body = name[3:]
        return all(ch.isalnum() or ch == "_" for ch in body)
    return False


def _is_auto_local_net(name: str) -> bool:
    """True for per-sheet compiled nets like NetIC1_6 that need SchDoc alias resolution."""
    return bool(re.match(r"^NetIC\d+_\d+$", name.strip(), re.IGNORECASE))


def _port_nodes(data: str) -> list[dict[str, Any]]:
    ports: list[dict[str, Any]] = []
    for record_id, body in _record_blocks(data):
        if record_id != "18":
            continue
        name = _field(body, "Name")
        x_text = _field(body, "Location.X")
        y_text = _field(body, "Location.Y")
        if not name or not x_text or not y_text:
            continue
        if name in {"Designator", "Comment", "PinUniqueId", "Net", "PinName"}:
            continue
        if _looks_auto_generated(name):
            continue
        ports.append({"name": name, "x": int(x_text), "y": int(y_text)})
    return ports


def _proximity_port_aliases(pins_by_designator: dict[str, list[dict[str, Any]]], ports: list[dict[str, Any]]) -> dict[tuple[str, str], str]:
    candidates: list[tuple[int, str, str, str]] = []
    for designator, pins in pins_by_designator.items():
        for pin_info in pins:
            for port in ports:
                dx = abs(pin_info["x"] - port["x"])
                dy = abs(pin_info["y"] - port["y"])
                if dy > 10 or dx > 180:
                    continue
                score = dx + dy * 5
                candidates.append((score, designator.upper(), str(pin_info["pin"]), port["name"]))

    candidates.sort(key=lambda item: item[0])
    used_pins: set[tuple[str, str]] = set()
    used_ports: set[str] = set()
    aliases: dict[tuple[str, str], str] = {}
    for _, designator, pin, name in candidates:
        key = (designator, pin)
        if key in used_pins or name in used_ports:
            continue
        aliases[key] = name
        used_pins.add(key)
        used_ports.add(name)
    return aliases


def resolve_sheet_aliases(sch_path: str | Path) -> dict[tuple[str, str], str]:
    """Return {(designator, pin): net_label} discovered from SchDoc ports near pins."""
    path = Path(sch_path)
    if not path.exists():
        return {}

    data = path.read_bytes().decode("latin1", errors="ignore")
    pins_by_designator = _component_pins(data)
    if not pins_by_designator:
        return {}

    ports = _port_nodes(data)
    proximity_aliases = _proximity_port_aliases(pins_by_designator, ports)
    if proximity_aliases:
        return proximity_aliases

    adj = _build_adjacency(data)
    if not adj:
        return {}

    named_nodes = _named_nodes(data)
    aliases: dict[tuple[str, str], str] = {}

    for designator, pins in pins_by_designator.items():
        for pin_info in pins:
            start = _nearest_wire_node(adj, pin_info["x"], pin_info["y"])
            if start is None:
                continue
            names = _reachable_named_nodes(adj, named_nodes, start)
            user_names = {name for name in names if name and not _looks_auto_generated(name)}
            if len(user_names) == 1:
                aliases[(designator.upper(), str(pin_info["pin"]))] = next(iter(user_names))
            elif len(user_names) > 1:
                preferred = sorted(user_names, key=len)[0]
                aliases[(designator.upper(), str(pin_info["pin"]))] = preferred

    return aliases


def apply_pin_aliases_to_export(data: dict[str, Any]) -> dict[str, Any]:
    """Rewrite exported pin nets and projectNets using SchDoc wire/label tracing."""
    pin_aliases: dict[tuple[str, str], str] = {}
    for sheet in data.get("schematics") or []:
        sheet_path = sheet.get("path") or sheet.get("sheet")
        if not sheet_path:
            continue
        pin_aliases.update(resolve_sheet_aliases(sheet_path))

    if not pin_aliases:
        return data

    local_to_global: dict[str, str] = {}
    for component in data.get("components") or []:
        designator = str(component.get("designator", "")).upper()
        for pin_info in component.get("pins") or []:
            pin_number = str(pin_info.get("number", "")).strip()
            key = (designator, pin_number)
            if key not in pin_aliases:
                continue
            old_net = str(pin_info.get("net", "")).strip()
            if not _is_auto_local_net(old_net):
                continue
            new_net = pin_aliases[key]
            pin_info["net"] = new_net
            local_to_global[old_net] = new_net

    for sheet in data.get("schematics") or []:
        for component in sheet.get("components") or []:
            designator = str(component.get("designator", "")).upper()
            for pin_info in component.get("pins") or []:
                pin_number = str(pin_info.get("number", "")).strip()
                key = (designator, pin_number)
                if key not in pin_aliases:
                    continue
                old_net = str(pin_info.get("net", "")).strip()
                if _is_auto_local_net(old_net):
                    pin_info["net"] = pin_aliases[key]

    def rewrite_net_list(nets: list[dict[str, Any]]) -> list[dict[str, Any]]:
        merged: dict[str, dict[str, Any]] = {}
        for net in nets:
            old_name = str(net.get("name", "")).strip()
            if not old_name:
                continue
            new_name = local_to_global.get(old_name, old_name)
            bucket = merged.setdefault(new_name, {"name": new_name, "connections": []})
            seen = {
                (str(item.get("designator", "")).upper(), str(item.get("pin", "")))
                for item in bucket["connections"]
            }
            for conn in net.get("connections") or []:
                des = str(conn.get("designator", "")).upper()
                pin = str(conn.get("pin", "")).strip()
                alias = pin_aliases.get((des, pin))
                target_name = alias or new_name
                target_bucket = merged.setdefault(
                    target_name, {"name": target_name, "connections": []}
                )
                key = (des, pin)
                target_seen = {
                    (str(item.get("designator", "")).upper(), str(item.get("pin", "")))
                    for item in target_bucket["connections"]
                }
                if key in target_seen:
                    continue
                target_bucket["connections"].append({"designator": des, "pin": pin})
        return list(merged.values())

    flat_nets = rewrite_net_list(data.get("nets") or [])
    data["nets"] = flat_nets
    for sheet in data.get("schematics") or []:
        sheet["nets"] = rewrite_net_list(sheet.get("nets") or [])

    project_nets: dict[str, dict[str, Any]] = {}
    for net in flat_nets:
        name = str(net.get("name", "")).strip()
        if not name:
            continue
        bucket = project_nets.setdefault(name, {"name": name, "connections": []})
        seen = {
            (str(item.get("designator", "")).upper(), str(item.get("pin", "")))
            for item in bucket["connections"]
        }
        for conn in net.get("connections") or []:
            key = (str(conn.get("designator", "")).upper(), str(conn.get("pin", "")))
            if key in seen:
                continue
            bucket["connections"].append(dict(conn))
            seen.add(key)

    for (designator, pin), net_name in pin_aliases.items():
        bucket = project_nets.setdefault(net_name, {"name": net_name, "connections": []})
        seen = {
            (str(item.get("designator", "")).upper(), str(item.get("pin", "")))
            for item in bucket["connections"]
        }
        key = (designator, pin)
        if key not in seen:
            bucket["connections"].append({"designator": designator, "pin": pin})

    data["projectNets"] = sorted(
        project_nets.values(), key=lambda item: str(item.get("name", "")).casefold()
    )
    data["schNetAliasesApplied"] = len(pin_aliases)
    return data
