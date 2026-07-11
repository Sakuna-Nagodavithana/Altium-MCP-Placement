"""Load and query Altium design data exported as JSON."""

from __future__ import annotations

import json
import os
import re
from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


DEFAULT_CONNECTIVITY_PATH = Path.home() / "Documents" / "AltiumEE" / "connectivity.json"
SUPPORTED_SCHEMA_VERSIONS = frozenset({4, 5, 5.1})


def _normalize_schema_version(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def validate_connectivity_schema(payload: dict[str, Any]) -> None:
    version = _normalize_schema_version(payload.get("schemaVersion"))
    if version is None:
        raise ValueError("Connectivity export is missing schemaVersion.")
    if version not in SUPPORTED_SCHEMA_VERSIONS:
        supported = ", ".join(str(item) for item in sorted(SUPPORTED_SCHEMA_VERSIONS))
        raise ValueError(
            f"Unsupported connectivity schemaVersion {payload.get('schemaVersion')}. "
            f"Supported versions: {supported}."
        )


@dataclass
class ConnectivityStore:
    path: Path
    data: dict[str, Any] | None = None
    loaded_at: datetime | None = None
    last_error: str | None = None

    def resolve_path(self, override: str | None = None) -> Path:
        if override:
            return Path(override).expanduser()
        # An explicit constructor path must win over a process-wide environment
        # variable. This also keeps independent stores/tests from accidentally
        # loading whichever file another module placed in the environment.
        if self.path != DEFAULT_CONNECTIVITY_PATH:
            return self.path
        env_path = os.environ.get("ALTIUM_CONNECTIVITY_FILE")
        if env_path:
            return Path(env_path).expanduser()
        return self.path

    def load(self, path: str | None = None, force: bool = False) -> dict[str, Any]:
        target = self.resolve_path(path)
        if not force and self.data is not None and self.loaded_at is not None and target == self.path:
            return self.data

        self.path = target
        self.last_error = None

        if not target.exists():
            self.data = None
            self.loaded_at = None
            self.last_error = f"Connectivity file not found: {target}"
            raise FileNotFoundError(self.last_error)

        with target.open("r", encoding="utf-8") as handle:
            payload = json.load(handle)

        if not isinstance(payload, dict):
            raise ValueError("Connectivity file must contain a JSON object.")

        from sch_net_resolver import apply_pin_aliases_to_export

        payload = apply_pin_aliases_to_export(payload)

        validate_connectivity_schema(payload)

        self.data = payload
        self.loaded_at = datetime.now(timezone.utc)
        return payload

    def ensure_loaded(self) -> dict[str, Any]:
        target = self.resolve_path()
        if self.data is not None and target.exists() and self.loaded_at is not None:
            modified = datetime.fromtimestamp(target.stat().st_mtime, tz=timezone.utc)
            if modified <= self.loaded_at:
                return self.data

        return self.load(force=True)

    def _summary_counts(self, data: dict[str, Any]) -> dict[str, int]:
        summary = data.get("summary") or {}
        schematics = data.get("schematics") or []
        pcb = data.get("pcb") or {}
        return {
            "sheet_count": int(summary.get("sheetCount") or len(schematics)),
            "sch_component_count": int(
                summary.get("schComponentCount") or len(data.get("components") or [])
            ),
            "sch_net_count": int(summary.get("schNetCount") or len(data.get("nets") or [])),
            "pcb_component_count": int(
                summary.get("pcbComponentCount") or len(pcb.get("components") or [])
            ),
            "pcb_net_count": int(summary.get("pcbNetCount") or len(pcb.get("nets") or [])),
        }

    def status(self) -> dict[str, Any]:
        target = self.resolve_path()
        exists = target.exists()
        modified = None
        if exists:
            modified = datetime.fromtimestamp(target.stat().st_mtime, tz=timezone.utc).isoformat()

        counts = {
            "sheet_count": 0,
            "sch_component_count": 0,
            "sch_net_count": 0,
            "pcb_component_count": 0,
            "pcb_net_count": 0,
        }
        if self.data:
            counts = self._summary_counts(self.data)

        coverage = {
            "component_count": 0,
            "pin_count": 0,
            "pins_with_net": 0,
            "has_usable_nets": False,
        }
        if self.data is not None:
            try:
                coverage = self._net_coverage()
            except Exception:
                pass

        return {
            "file": str(target),
            "exists": exists,
            "loaded": self.data is not None,
            "loaded_at": self.loaded_at.isoformat() if self.loaded_at else None,
            "modified_at": modified,
            "last_error": self.last_error,
            "schema_version": (self.data or {}).get("schemaVersion"),
            "component_count": counts["sch_component_count"],
            "net_count": counts["sch_net_count"],
            **counts,
            **coverage,
        }

    def list_sheets(self) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        sheets = []
        for sheet in data.get("schematics") or []:
            sheets.append(
                {
                    "sheet": sheet.get("sheet"),
                    "path": sheet.get("path"),
                    "component_count": len(sheet.get("components") or []),
                    "net_count": len(sheet.get("nets") or []),
                }
            )
        if not sheets and data.get("sheet"):
            sheets.append(
                {
                    "sheet": data.get("sheet"),
                    "path": None,
                    "component_count": len(data.get("components") or []),
                    "net_count": len(data.get("nets") or []),
                }
            )
        return sheets

    def get_sheet(self, sheet_name: str) -> dict[str, Any] | None:
        target = sheet_name.strip().casefold()
        for sheet in self.ensure_loaded().get("schematics") or []:
            if str(sheet.get("sheet", "")).casefold() == target:
                return sheet
        return None

    def list_components(self, query: str | None = None, sheet: str | None = None) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        if sheet:
            sheet_data = self.get_sheet(sheet)
            components = sheet_data.get("components", []) if sheet_data else []
        else:
            components = data.get("components") or []
            if not components and data.get("schematics"):
                components = []
                for sheet_data in data["schematics"]:
                    for component in sheet_data.get("components") or []:
                        enriched = dict(component)
                        enriched.setdefault("sheet", sheet_data.get("sheet"))
                        components.append(enriched)

        if not query:
            return components

        needle = query.casefold()
        results = []
        for component in components:
            haystack = " ".join(
                [
                    str(component.get("designator", "")),
                    str(component.get("comment", "")),
                    str(component.get("jlcpcb", "")),
                    str(component.get("libReference", "")),
                    str(component.get("sheet", "")),
                ]
            ).casefold()
            if needle in haystack:
                results.append(component)
        return results

    def get_component(self, designator: str, sheet: str | None = None) -> dict[str, Any] | None:
        target = designator.strip().upper()
        for component in self.list_components(sheet=sheet):
            if str(component.get("designator", "")).upper() == target:
                return component
        return None

    def list_nets(self, query: str | None = None, sheet: str | None = None) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        if sheet:
            sheet_data = self.get_sheet(sheet)
            nets = sheet_data.get("nets", []) if sheet_data else []
        elif data.get("projectNets"):
            nets = data.get("projectNets") or []
        else:
            nets = self._merge_nets_by_name(data.get("nets") or [])

        if not query:
            return nets

        needle = query.casefold()
        return [net for net in nets if needle in str(net.get("name", "")).casefold()]

    def get_net(self, name: str, sheet: str | None = None) -> dict[str, Any] | None:
        target = name.strip()
        if sheet:
            for net in self.list_nets(sheet=sheet):
                if str(net.get("name", "")).casefold() == target.casefold():
                    return net
            return None

        for net in self._project_nets():
            if str(net.get("name", "")).casefold() == target.casefold():
                return net
        return None

    def get_pin_connection(self, designator: str, pin: str, sheet: str | None = None) -> dict[str, Any]:
        component = self.get_component(designator, sheet=sheet)
        if component is None:
            return {
                "found": False,
                "designator": designator,
                "pin": pin,
                "error": f"Component '{designator}' not found.",
            }

        pin_text = str(pin).strip()
        for pin_info in component.get("pins", []):
            pin_number = str(pin_info.get("number", pin_info.get("name", "")))
            pin_name = str(pin_info.get("name", ""))
            if pin_text == pin_number or (pin_name and pin_text.casefold() == pin_name.casefold()):
                return {
                    "found": True,
                    "designator": component.get("designator"),
                    "sheet": component.get("sheet"),
                    "pin": pin_number,
                    "pin_name": pin_name or None,
                    "net": pin_info.get("net"),
                    "jlcpcb": component.get("jlcpcb"),
                }

        return {
            "found": False,
            "designator": designator,
            "pin": pin,
            "error": f"Pin '{pin}' not found on component '{designator}'.",
        }

    def get_component_placement(self, designator: str, sheet: str | None = None) -> dict[str, Any]:
        component = self.get_component(designator, sheet=sheet)
        if component is None:
            return {"found": False, "designator": designator}
        return {
            "found": True,
            "designator": component.get("designator"),
            "sheet": component.get("sheet"),
            "domain": "schematic",
            "placement": component.get("placement"),
        }

    def list_pcb_components(self, query: str | None = None) -> list[dict[str, Any]]:
        pcb = self.ensure_loaded().get("pcb") or {}
        components = pcb.get("components") or []
        if not query:
            return components
        needle = query.casefold()
        return [
            component
            for component in components
            if needle
            in " ".join(
                [
                    str(component.get("designator", "")),
                    str(component.get("pattern", "")),
                    str(component.get("description", "")),
                ]
            ).casefold()
        ]

    def get_pcb_component(self, designator: str) -> dict[str, Any] | None:
        target = designator.strip().upper()
        for component in self.list_pcb_components():
            if str(component.get("designator", "")).upper() == target:
                return component
        return None

    def list_pcb_nets(self, query: str | None = None) -> list[dict[str, Any]]:
        pcb = self.ensure_loaded().get("pcb") or {}
        nets = pcb.get("nets") or []
        if not query:
            return nets
        needle = query.casefold()
        return [net for net in nets if needle in str(net.get("name", "")).casefold()]

    def get_pcb_net(self, name: str) -> dict[str, Any] | None:
        target = name.strip()
        for net in self.list_pcb_nets():
            if str(net.get("name", "")).casefold() == target.casefold():
                return net
        return None

    def get_pcb_component_placement(self, designator: str) -> dict[str, Any]:
        component = self.get_pcb_component(designator)
        if component is None:
            return {"found": False, "designator": designator}
        return {
            "found": True,
            "designator": component.get("designator"),
            "domain": "pcb",
            "placement": component.get("placement"),
            "layer": component.get("layer"),
        }

    def get_erc_violations(self) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        violations = data.get("ercViolations") or []
        if violations:
            return violations
        combined = []
        for sheet in data.get("schematics") or []:
            for item in sheet.get("ercViolations") or []:
                enriched = dict(item)
                enriched.setdefault("sheet", sheet.get("sheet"))
                combined.append(enriched)
        return combined

    def _real_net_name(self, net: Any) -> str | None:
        if net is None:
            return None
        text = str(net).strip()
        if not text or text.casefold() in {"no net", "nonet", "unconnected"}:
            return None
        return text

    def _iter_export_components(self, sheet: str | None = None) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        if sheet:
            sheet_data = self.get_sheet(sheet)
            return list(sheet_data.get("components") or []) if sheet_data else []

        components = list(data.get("components") or [])
        if components:
            return components

        combined: list[dict[str, Any]] = []
        for sheet_data in data.get("schematics") or []:
            for component in sheet_data.get("components") or []:
                enriched = dict(component)
                enriched.setdefault("sheet", sheet_data.get("sheet"))
                combined.append(enriched)
        return combined

    def _synthesize_nets_from_pins(self, sheet: str | None = None) -> list[dict[str, Any]]:
        grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
        for component in self._iter_export_components(sheet=sheet):
            designator = str(component.get("designator", "")).strip()
            if not designator:
                continue
            for pin_info in component.get("pins") or []:
                net_name = self._real_net_name(pin_info.get("net"))
                if not net_name:
                    continue
                pin_number = str(pin_info.get("number", pin_info.get("name", ""))).strip()
                if not pin_number:
                    continue
                grouped[net_name].append(
                    {
                        "designator": designator,
                        "pin": pin_number,
                        "pin_name": pin_info.get("name"),
                        "sheet": component.get("sheet"),
                    }
                )
        return [{"name": name, "connections": connections} for name, connections in grouped.items()]

    def _merge_nets_by_name(self, nets: list[dict[str, Any]]) -> list[dict[str, Any]]:
        merged: dict[str, dict[str, Any]] = {}
        for net in nets:
            name = str(net.get("name", "")).strip()
            if not name:
                continue
            bucket = merged.setdefault(name, {"name": name, "connections": []})
            seen = {
                (str(c.get("designator", "")).upper(), str(c.get("pin", "")))
                for c in bucket["connections"]
            }
            for conn in net.get("connections") or []:
                key = (str(conn.get("designator", "")).upper(), str(conn.get("pin", "")))
                if key in seen:
                    continue
                bucket["connections"].append(dict(conn))
                seen.add(key)
        return list(merged.values())

    def _project_nets(self) -> list[dict[str, Any]]:
        data = self.ensure_loaded()
        exported = data.get("projectNets")
        if exported:
            return exported
        return self._merge_nets_by_name(data.get("nets") or [])

    def _effective_nets(self, sheet: str | None = None) -> list[dict[str, Any]]:
        if sheet:
            exported = self.list_nets(sheet=sheet)
            if exported and any(self._real_net_name(net.get("name")) for net in exported):
                return exported
            return self._synthesize_nets_from_pins(sheet=sheet)

        project_nets = self._project_nets()
        if project_nets and any(self._real_net_name(net.get("name")) for net in project_nets):
            return project_nets
        return self._synthesize_nets_from_pins(sheet=None)

    def _net_coverage(self, sheet: str | None = None) -> dict[str, Any]:
        components = self._iter_export_components(sheet=sheet)
        total_pins = 0
        named_pins = 0
        for component in components:
            for pin_info in component.get("pins") or []:
                total_pins += 1
                if self._real_net_name(pin_info.get("net")):
                    named_pins += 1
        return {
            "component_count": len(components),
            "pin_count": total_pins,
            "pins_with_net": named_pins,
            "net_count": len(self._effective_nets(sheet=sheet)),
            "has_usable_nets": named_pins > 0,
        }

    def _pin_ref(
        self, designator: str, pin: str, component_index: dict[str, dict[str, Any]]
    ) -> tuple[str, str] | None:
        component = component_index.get(designator.strip().upper())
        if component is None:
            return None
        pin_text = str(pin).strip()
        for pin_info in component.get("pins") or []:
            pin_number = str(pin_info.get("number", "")).strip()
            pin_name = str(pin_info.get("name", "")).strip()
            if pin_text == pin_number or (pin_name and pin_text.casefold() == pin_name.casefold()):
                return designator.strip().upper(), pin_number
        return None

    def _passive_internal_edges(self, component: dict[str, Any]) -> list[tuple[str, str]]:
        pins = component.get("pins") or []
        if len(pins) < 2:
            return []
        designator = str(component.get("designator", "")).upper()
        prefix = re.match(r"^([A-Z]+)", designator)
        passive_prefix = prefix.group(1) if prefix else ""
        if passive_prefix not in {"R", "L", "C", "D", "FB", "CB", "RN", "RV"}:
            return []
        pin_numbers = []
        for pin_info in pins:
            pin_number = str(pin_info.get("number", "")).strip()
            if pin_number and pin_number not in pin_numbers:
                pin_numbers.append(pin_number)
        if len(pin_numbers) < 2 or len(pin_numbers) > 3:
            return []
        edges: list[tuple[str, str]] = []
        for left in pin_numbers:
            for right in pin_numbers:
                if left != right:
                    edges.append((left, right))
        return edges

    def trace_connection(
        self,
        from_designator: str,
        to_designator: str,
        from_pin: str = "",
        to_pin: str = "",
        sheet: str | None = None,
    ) -> dict[str, Any]:
        """Find a schematic signal path between two components using exported net data."""
        from_des = from_designator.strip().upper()
        to_des = to_designator.strip().upper()
        components = self._iter_export_components(sheet=sheet)
        component_index = {
            str(component.get("designator", "")).strip().upper(): component for component in components
        }

        if from_des not in component_index:
            return {"found": False, "error": f"Component '{from_designator}' not found."}
        if to_des not in component_index:
            return {"found": False, "error": f"Component '{to_designator}' not found."}

        coverage = self._net_coverage(sheet=sheet)
        if not coverage["has_usable_nets"]:
            return {
                "found": False,
                "connected": False,
                "from_designator": from_des,
                "to_designator": to_des,
                "coverage": coverage,
                "error": (
                    "Export has no compiled net names (all pins are 'No Net'). "
                    "In Altium: Project -> Compile, then EasyEDA Loader -> Export Project for MCP, "
                    "then call reload_connectivity."
                ),
            }

        pin_to_net: dict[tuple[str, str], str] = {}
        net_to_pins: dict[str, list[tuple[str, str]]] = defaultdict(list)
        for net in self._effective_nets(sheet=sheet):
            net_name = self._real_net_name(net.get("name"))
            if not net_name:
                continue
            for connection in net.get("connections") or []:
                des = str(connection.get("designator", "")).strip().upper()
                pin_number = str(connection.get("pin", "")).strip()
                if not des or not pin_number:
                    continue
                pin_to_net[(des, pin_number)] = net_name
                net_to_pins[net_name].append((des, pin_number))

        for component in components:
            des = str(component.get("designator", "")).strip().upper()
            for pin_info in component.get("pins") or []:
                pin_number = str(pin_info.get("number", "")).strip()
                net_name = self._real_net_name(pin_info.get("net"))
                if not des or not pin_number or not net_name:
                    continue
                pin_to_net.setdefault((des, pin_number), net_name)
                if (des, pin_number) not in net_to_pins[net_name]:
                    net_to_pins[net_name].append((des, pin_number))

        def component_pins(designator: str) -> list[tuple[str, str]]:
            refs: list[tuple[str, str]] = []
            for pin_info in component_index[designator].get("pins") or []:
                pin_number = str(pin_info.get("number", "")).strip()
                if pin_number:
                    refs.append((designator, pin_number))
            return refs

        if from_pin.strip():
            start_refs = [
                ref
                for ref in [self._pin_ref(from_des, from_pin, component_index)]
                if ref is not None
            ]
            if not start_refs:
                return {
                    "found": False,
                    "error": f"Pin '{from_pin}' not found on component '{from_des}'.",
                }
        else:
            start_refs = component_pins(from_des)

        if to_pin.strip():
            goal_refs = {
                ref
                for ref in [self._pin_ref(to_des, to_pin, component_index)]
                if ref is not None
            }
            if not goal_refs:
                return {
                    "found": False,
                    "error": f"Pin '{to_pin}' not found on component '{to_des}'.",
                }
        else:
            goal_refs = set(component_pins(to_des))

        Node = tuple[str, str]
        adjacency: dict[Node, list[Node]] = defaultdict(list)

        for net_name, pins in net_to_pins.items():
            for pin_a in pins:
                for pin_b in pins:
                    if pin_a != pin_b:
                        adjacency[pin_a].append(pin_b)

        for component in components:
            des = str(component.get("designator", "")).strip().upper()
            for pin_a, pin_b in self._passive_internal_edges(component):
                node_a = (des, pin_a)
                node_b = (des, pin_b)
                adjacency[node_a].append(node_b)
                adjacency[node_b].append(node_a)

        queue: deque[tuple[Node, list[Node]]] = deque()
        visited: set[Node] = set()
        for start in start_refs:
            queue.append((start, [start]))
            visited.add(start)

        best_path: list[Node] | None = None
        while queue:
            node, path = queue.popleft()
            if node in goal_refs:
                best_path = path
                break
            for neighbor in adjacency.get(node, []):
                if neighbor in visited:
                    continue
                visited.add(neighbor)
                queue.append((neighbor, path + [neighbor]))

        if best_path is None:
            return {
                "found": True,
                "connected": False,
                "from_designator": from_des,
                "to_designator": to_des,
                "coverage": coverage,
                "message": f"No schematic path found between {from_des} and {to_des}.",
            }

        steps: list[dict[str, Any]] = []
        components_on_path: list[str] = []
        nets_on_path: list[str] = []

        def append_component(designator: str) -> None:
            if not components_on_path or components_on_path[-1] != designator:
                components_on_path.append(designator)

        for index, (des, pin_number) in enumerate(best_path):
            component = component_index[des]
            pin_info = next(
                (
                    pin
                    for pin in component.get("pins") or []
                    if str(pin.get("number", "")).strip() == pin_number
                ),
                {},
            )
            append_component(des)
            steps.append(
                {
                    "step": len(steps) + 1,
                    "type": "pin",
                    "designator": des,
                    "pin": pin_number,
                    "pin_name": pin_info.get("name"),
                    "comment": component.get("comment"),
                    "jlcpcb": component.get("jlcpcb"),
                    "net": pin_to_net.get((des, pin_number)),
                }
            )
            if index + 1 >= len(best_path):
                continue
            next_des, next_pin = best_path[index + 1]
            if des == next_des:
                steps.append(
                    {
                        "step": len(steps) + 1,
                        "type": "through",
                        "designator": des,
                        "from_pin": pin_number,
                        "to_pin": next_pin,
                        "comment": component.get("comment"),
                    }
                )
                continue
            net_name = pin_to_net.get((des, pin_number)) or pin_to_net.get((next_des, next_pin))
            if net_name:
                nets_on_path.append(net_name)
                steps.append({"step": len(steps) + 1, "type": "net", "name": net_name})
            append_component(next_des)

        edge_labels: list[tuple[str, str, str]] = []
        for index in range(len(best_path) - 1):
            left = best_path[index]
            right = best_path[index + 1]
            if left[0] == right[0]:
                continue
            net_name = pin_to_net.get(left) or pin_to_net.get(right) or "signal"
            edge_labels.append((left[0], right[0], net_name))

        deduped_edges: list[tuple[str, str, str]] = []
        for edge in edge_labels:
            if deduped_edges and deduped_edges[-1][0] == edge[0] and deduped_edges[-1][1] == edge[1]:
                continue
            deduped_edges.append(edge)

        mermaid_parts = ["flowchart LR"]
        rendered: set[str] = set()
        for des in components_on_path:
            component = component_index[des]
            comment = str(component.get("comment") or "").replace('"', "'")
            node_id = re.sub(r"[^A-Za-z0-9_]", "_", des)
            label = f'{des}<br/>{comment}' if comment else des
            mermaid_parts.append(f'  {node_id}["{label}"]')
            rendered.add(des)
        for left, right, net_name in deduped_edges:
            left_id = re.sub(r"[^A-Za-z0-9_]", "_", left)
            right_id = re.sub(r"[^A-Za-z0-9_]", "_", right)
            safe_net = str(net_name).replace('"', "'")
            mermaid_parts.append(f"  {left_id} -->|{safe_net}| {right_id}")

        start_node = best_path[0]
        end_node = best_path[-1]
        start_component = component_index[start_node[0]]
        end_component = component_index[end_node[0]]
        start_pin_info = next(
            (
                pin
                for pin in start_component.get("pins") or []
                if str(pin.get("number", "")).strip() == start_node[1]
            ),
            {},
        )
        end_pin_info = next(
            (
                pin
                for pin in end_component.get("pins") or []
                if str(pin.get("number", "")).strip() == end_node[1]
            ),
            {},
        )

        return {
            "found": True,
            "connected": True,
            "from": {
                "designator": start_node[0],
                "pin": start_node[1],
                "pin_name": start_pin_info.get("name"),
                "comment": start_component.get("comment"),
            },
            "to": {
                "designator": end_node[0],
                "pin": end_node[1],
                "pin_name": end_pin_info.get("name"),
                "comment": end_component.get("comment"),
            },
            "nets": nets_on_path,
            "components_along_path": components_on_path,
            "steps": steps,
            "mermaid": "\n".join(mermaid_parts),
            "coverage": coverage,
        }

    def _net_matches_rail(self, net_name: str, source_rail: str) -> bool:
        return str(net_name or "").strip().casefold() == str(source_rail or "").strip().casefold()

    def _is_gnd_net(self, net_name: str) -> bool:
        text = str(net_name or "").strip().casefold()
        return text in {"gnd", "agnd", "dgnd", "pgnd", "vss"} or "gnd" in text

    def _decoupling_caps_on_net(
        self,
        net_name: str,
        component_index: dict[str, dict[str, Any]],
        sheet: str | None = None,
    ) -> list[dict[str, Any]]:
        net = self.get_net(net_name, sheet=sheet)
        if net is None:
            return []

        target = str(net_name).strip()
        entries: list[dict[str, Any]] = []
        seen: set[str] = set()

        for connection in net.get("connections") or []:
            designator = str(connection.get("designator", "")).strip().upper()
            if not designator.startswith("C") or designator in seen:
                continue
            component = component_index.get(designator) or self.get_component(designator, sheet=sheet)
            if component is None:
                continue
            pin_nets = [str(pin.get("net", "")).strip() for pin in component.get("pins") or []]
            if not pin_nets:
                continue
            has_target = any(n == target for n in pin_nets)
            has_gnd = any(self._is_gnd_net(n) for n in pin_nets)
            if has_target and has_gnd:
                seen.add(designator)
                entries.append(
                    {
                        "designator": designator,
                        "comment": component.get("comment"),
                        "jlcpcb": component.get("jlcpcb"),
                        "pins": [
                            {"number": pin.get("number"), "net": pin.get("net")}
                            for pin in component.get("pins") or []
                        ],
                    }
                )
        entries.sort(key=lambda item: str(item.get("designator", "")))
        return entries

    def trace_power_path(
        self,
        designator: str,
        pin: str,
        source_rail: str = "3v3",
        sheet: str | None = None,
    ) -> dict[str, Any]:
        """Trace from a power rail to a specific IC pin and list decoupling caps on the path.

        Start from the destination pin (e.g. VDDP3P), not the shared rail name alone.
        """
        target_des = designator.strip().upper()
        pin_info = self.get_pin_connection(designator, pin, sheet=sheet)
        if not pin_info.get("found"):
            return pin_info

        components = self._iter_export_components(sheet=sheet)
        component_index = {
            str(component.get("designator", "")).strip().upper(): component for component in components
        }
        if target_des not in component_index:
            return {"found": False, "error": f"Component '{designator}' not found."}

        coverage = self._net_coverage(sheet=sheet)
        if not coverage["has_usable_nets"]:
            return {
                "found": False,
                "error": "Export has no compiled net names. Re-export from Altium and reload_connectivity.",
                "coverage": coverage,
            }

        pin_to_net: dict[tuple[str, str], str] = {}
        for component in components:
            des = str(component.get("designator", "")).strip().upper()
            for item in component.get("pins") or []:
                pin_number = str(item.get("number", "")).strip()
                net_name = self._real_net_name(item.get("net"))
                if des and pin_number and net_name:
                    pin_to_net[(des, pin_number)] = net_name

        for net in self._effective_nets(sheet=sheet):
            net_name = self._real_net_name(net.get("name"))
            if not net_name:
                continue
            for connection in net.get("connections") or []:
                des = str(connection.get("designator", "")).strip().upper()
                pin_number = str(connection.get("pin", "")).strip()
                if des and pin_number:
                    pin_to_net.setdefault((des, pin_number), net_name)

        target_pin = str(pin_info.get("pin", "")).strip()
        target_net = str(pin_info.get("net", "")).strip()
        start_node = (target_des, target_pin)

        rail_decoupling = self._decoupling_caps_on_net(
            source_rail, component_index, sheet=sheet
        )

        if self._net_matches_rail(target_net, source_rail):
            net_data = self.get_net(source_rail, sheet=sheet) or {}
            ic_pins_on_rail = [
                {
                    "designator": conn.get("designator"),
                    "pin": conn.get("pin"),
                    "pin_name": next(
                        (
                            p.get("name")
                            for p in component_index.get(
                                str(conn.get("designator", "")).strip().upper(), {}
                            ).get("pins", [])
                            if str(p.get("number", "")).strip() == str(conn.get("pin", "")).strip()
                        ),
                        None,
                    ),
                }
                for conn in net_data.get("connections") or []
                if str(conn.get("designator", "")).strip().upper() == target_des
            ]
            return {
                "found": True,
                "connection_type": "direct_to_rail",
                "source_rail": source_rail,
                "target": {
                    "designator": target_des,
                    "pin": target_pin,
                    "pin_name": pin_info.get("pin_name"),
                    "net": target_net,
                    "comment": component_index[target_des].get("comment"),
                },
                "series_path": [],
                "series_components": [],
                "nets_on_path": [source_rail],
                "local_decoupling": [],
                "local_decoupling_count": 0,
                "rail_decoupling": rail_decoupling,
                "rail_decoupling_count": len(rail_decoupling),
                "total_decoupling_count": len(rail_decoupling),
                "other_pins_on_same_rail": [
                    item
                    for item in ic_pins_on_rail
                    if str(item.get("pin", "")).strip() != target_pin
                ],
                "note": (
                    f"Pin {target_pin} is directly on net '{source_rail}'. "
                    f"All {len(rail_decoupling)} decoupling caps on that rail serve this pin "
                    "(and every other load on the same net). Schematic connectivity cannot "
                    "assign a subset of rail caps to one pin unless a series filter creates a separate net."
                ),
                "coverage": coverage,
            }

        Node = tuple[str, str]
        adjacency: dict[Node, list[Node]] = defaultdict(list)

        net_to_pins: dict[str, list[Node]] = defaultdict(list)
        for node, net_name in pin_to_net.items():
            net_to_pins[net_name].append(node)

        for net_name, nodes in net_to_pins.items():
            for left in nodes:
                for right in nodes:
                    if left != right:
                        adjacency[left].append(right)

        for component in components:
            des = str(component.get("designator", "")).strip().upper()
            for pin_a, pin_b in self._passive_internal_edges(component):
                node_a = (des, pin_a)
                node_b = (des, pin_b)
                adjacency[node_a].append(node_b)
                adjacency[node_b].append(node_a)

        goal_nodes = {
            node for node, net_name in pin_to_net.items() if self._net_matches_rail(net_name, source_rail)
        }

        queue: deque[tuple[Node, list[Node]]] = deque([(start_node, [start_node])])
        visited: set[Node] = {start_node}
        best_path: list[Node] | None = None

        while queue:
            node, path = queue.popleft()
            if node in goal_nodes:
                best_path = path
                break
            for neighbor in adjacency.get(node, []):
                if neighbor in visited:
                    continue
                visited.add(neighbor)
                queue.append((neighbor, path + [neighbor]))

        if best_path is None:
            return {
                "found": True,
                "connected": False,
                "connection_type": "not_reachable",
                "source_rail": source_rail,
                "target": {
                    "designator": target_des,
                    "pin": target_pin,
                    "pin_name": pin_info.get("pin_name"),
                    "net": target_net,
                },
                "message": f"No path found from {target_des}.{target_pin} ({target_net}) to rail '{source_rail}'.",
                "coverage": coverage,
            }

        series_steps: list[dict[str, Any]] = []
        series_components: list[str] = []
        nets_on_path: list[str] = []

        def append_series_component(name: str) -> None:
            if name not in series_components:
                series_components.append(name)

        for index, (des, pin_number) in enumerate(best_path):
            component = component_index[des]
            current_net = pin_to_net.get((des, pin_number))
            series_steps.append(
                {
                    "designator": des,
                    "pin": pin_number,
                    "net": current_net,
                    "comment": component.get("comment"),
                }
            )
            if index + 1 >= len(best_path):
                continue
            next_des, next_pin = best_path[index + 1]
            if des == next_des and des != target_des:
                append_series_component(des)
                series_steps.append(
                    {
                        "type": "through",
                        "designator": des,
                        "from_pin": pin_number,
                        "to_pin": next_pin,
                        "comment": component.get("comment"),
                    }
                )
                continue
            hop_net = pin_to_net.get((des, pin_number)) or pin_to_net.get((next_des, next_pin))
            if hop_net and (not nets_on_path or nets_on_path[-1] != hop_net):
                nets_on_path.append(hop_net)
            if next_des != des:
                append_series_component(next_des)

        if target_net and (not nets_on_path or nets_on_path[0] != target_net):
            nets_on_path.insert(0, target_net)
        if not self._net_matches_rail(nets_on_path[-1] if nets_on_path else "", source_rail):
            nets_on_path.append(source_rail)

        local_decoupling: list[dict[str, Any]] = []
        for net_name in nets_on_path:
            if self._net_matches_rail(net_name, source_rail):
                continue
            for entry in self._decoupling_caps_on_net(net_name, component_index, sheet=sheet):
                tagged = dict(entry)
                tagged["net"] = net_name
                local_decoupling.append(tagged)

        return {
            "found": True,
            "connected": True,
            "connection_type": "filtered_path",
            "source_rail": source_rail,
            "target": {
                "designator": target_des,
                "pin": target_pin,
                "pin_name": pin_info.get("pin_name"),
                "net": target_net,
                "comment": component_index[target_des].get("comment"),
            },
            "series_path": series_steps,
            "series_components": [item for item in series_components if not item.startswith("C")],
            "nets_on_path": nets_on_path,
            "local_decoupling": local_decoupling,
            "local_decoupling_count": len(local_decoupling),
            "rail_decoupling": rail_decoupling,
            "rail_decoupling_count": len(rail_decoupling),
            "total_decoupling_count": len(local_decoupling) + len(rail_decoupling),
            "note": (
                "Local decoupling caps sit on filtered nets between the rail and the pin. "
                "Rail decoupling caps are on the shared source rail and serve all loads on that rail. "
                "Multiple caps in a schematic filter (e.g. C8, C9, C10 before L2) often share the same "
                "rail net name (3v3) — use get_net(3v3) and get_component; do not expect a unique net per cap."
            ),
            "coverage": coverage,
        }

    def get_ic_support_components(
        self,
        designator: str,
        same_sheet_only: bool = True,
        max_schematic_distance_mils: float = 2500.0,
    ) -> dict[str, Any]:
        from placement_planner import get_ic_support_components

        return get_ic_support_components(
            self.ensure_loaded(),
            designator,
            same_sheet_only=same_sheet_only,
            max_schematic_distance_mils=max_schematic_distance_mils,
        )

    def build_ic_placement_plan(
        self,
        designator: str,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
    ) -> dict[str, Any]:
        from placement_planner import build_ic_placement_plan

        return build_ic_placement_plan(
            self.ensure_loaded(),
            designator,
            spacing_mils=spacing_mils,
            layout_mode=layout_mode,
            max_radius_mils=max_radius_mils,
            max_schematic_distance_mils=max_schematic_distance_mils,
            same_sheet_only=same_sheet_only,
        )

    def generate_ic_placement_plan(
        self,
        designator: str,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
        output_path: str | None = None,
    ) -> dict[str, Any]:
        from placement_planner import DEFAULT_PLAN_PATH, write_placement_plan

        plan = self.build_ic_placement_plan(
            designator,
            spacing_mils=spacing_mils,
            layout_mode=layout_mode,
            max_radius_mils=max_radius_mils,
            max_schematic_distance_mils=max_schematic_distance_mils,
            same_sheet_only=same_sheet_only,
        )
        if not plan.get("found"):
            return plan
        target = write_placement_plan(plan, output_path or DEFAULT_PLAN_PATH)
        plan["plan_file"] = str(target)
        return plan

    def build_all_ic_cluster_plan(
        self,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
        min_support_count: int = 1,
    ) -> dict[str, Any]:
        from placement_planner import build_all_ic_cluster_plan

        return build_all_ic_cluster_plan(
            self.ensure_loaded(),
            spacing_mils=spacing_mils,
            layout_mode=layout_mode,
            max_radius_mils=max_radius_mils,
            max_schematic_distance_mils=max_schematic_distance_mils,
            same_sheet_only=same_sheet_only,
            min_support_count=min_support_count,
        )

    def generate_all_ic_cluster_plan(
        self,
        spacing_mils: float = 80.0,
        layout_mode: str = "pin_near",
        max_radius_mils: float = 900.0,
        max_schematic_distance_mils: float = 2500.0,
        same_sheet_only: bool = True,
        min_support_count: int = 1,
        output_path: str | None = None,
    ) -> dict[str, Any]:
        from placement_planner import DEFAULT_PLAN_PATH, write_placement_plan

        plan = self.build_all_ic_cluster_plan(
            spacing_mils=spacing_mils,
            layout_mode=layout_mode,
            max_radius_mils=max_radius_mils,
            max_schematic_distance_mils=max_schematic_distance_mils,
            same_sheet_only=same_sheet_only,
            min_support_count=min_support_count,
        )
        if not plan.get("found"):
            return plan
        target = write_placement_plan(plan, output_path or DEFAULT_PLAN_PATH)
        plan["plan_file"] = str(target)
        return plan

    def get_placement_plan(self, plan_path: str | None = None) -> dict[str, Any]:
        from placement_planner import DEFAULT_PLAN_PATH

        target = Path(plan_path or DEFAULT_PLAN_PATH)
        if not target.exists():
            return {"found": False, "error": f"Placement plan not found: {target}"}
        plan = json.loads(target.read_text(encoding="utf-8"))
        plan["found"] = True
        plan["plan_file"] = str(target)
        return plan

    def update_placement_move(
        self,
        designator: str,
        x_mils: float,
        y_mils: float,
        rotation: float | None = None,
        plan_path: str | None = None,
        reverify: bool = True,
    ) -> dict[str, Any]:
        from placement_planner import DEFAULT_PLAN_PATH, verify_cluster_placement, write_placement_plan

        plan = self.get_placement_plan(plan_path)
        if not plan.get("found"):
            return plan

        target_des = designator.strip().upper()
        moves = plan.get("moves") or []
        updated = False
        for move in moves:
            if str(move.get("designator", "")).strip().upper() != target_des:
                continue
            move["xMils"] = round(float(x_mils), 3)
            move["yMils"] = round(float(y_mils), 3)
            move["method"] = str(move.get("method") or "manual_mcp")
            if rotation is not None:
                move["rotation"] = rotation
            updated = True
            break

        if not updated:
            return {"found": False, "error": f"Designator '{designator}' not found in placement plan."}

        if reverify and plan.get("anchor") and plan.get("anchor") != "ALL":
            anchor = str(plan["anchor"]).strip().upper()
            pcb_components = {
                str(component.get("designator", "")).strip().upper(): component
                for component in (self.ensure_loaded().get("pcb") or {}).get("components") or []
            }
            anchor_pcb = pcb_components.get(anchor) or {}
            placement = anchor_pcb.get("placement") or {}
            anchor_xy = (
                float(placement.get("xMils", 0.0)),
                float(placement.get("yMils", 0.0)),
            )
            grouping = self.get_ic_support_components(anchor)
            verification = verify_cluster_placement(
                anchor=anchor,
                anchor_xy=anchor_xy,
                support_components=grouping.get("support_components") or [],
                moves=moves,
                spacing_mils=float(plan.get("spacingMils") or 80.0),
                max_radius_mils=float(plan.get("maxRadiusMils") or 900.0),
            )
            plan["verification"] = verification

        output = write_placement_plan(plan, plan_path or DEFAULT_PLAN_PATH)
        plan["plan_file"] = str(output)
        plan["updated_designator"] = target_des
        return plan

    def get_pcb_design_summary(self) -> dict[str, Any]:
        data = self.ensure_loaded()
        pcb = data.get("pcb") or {}
        routing = pcb.get("routing") or {}
        planes = pcb.get("planes") or {}
        validation = pcb.get("validation") or {}
        stackup = pcb.get("stackup") or {}

        track_widths: dict[str, int] = {}
        for track in routing.get("tracks") or []:
            width = str(track.get("widthMils", "unknown"))
            track_widths[width] = track_widths.get(width, 0) + 1

        plane_nets = [
            {
                "net": plane.get("net"),
                "layer": plane.get("layer"),
                "kind": plane.get("kind"),
            }
            for plane in (planes.get("polygons") or [])
        ]

        return {
            "found": bool(pcb),
            "document": pcb.get("document"),
            "component_count": len(pcb.get("components") or []),
            "net_count": len(pcb.get("nets") or []),
            "track_count": routing.get("trackCount") or len(routing.get("tracks") or []),
            "via_count": routing.get("viaCount") or len(routing.get("vias") or []),
            "plane_count": planes.get("polygonCount") or len(planes.get("polygons") or []),
            "track_width_histogram_mils": track_widths,
            "ground_planes": [
                p
                for p in plane_nets
                if str(p.get("net", "")).casefold() in {"gnd", "agnd", "dgnd", "pgnd", "vss"}
            ],
            "power_planes": [
                p
                for p in plane_nets
                if any(x in str(p.get("net", "")).casefold() for x in ("3v3", "vcc", "vdd", "5v"))
            ],
            "stackup_layers": stackup.get("layers") or [],
            "validation": validation,
            "schema_version": data.get("schemaVersion"),
        }

    def classify_nets(self) -> dict[str, Any]:
        """Deterministic RF / PWR / HighSpeed / Logic classification with series-chain propagation.

        Mirrors the C# tracer in PcbDesignRulesSetup.cs. Returns {class: [net names]}.
        Use suggest_net_class for per-net trace context when reviewing ambiguous nets.
        """
        from net_classifier import classify_nets

        return classify_nets(self.ensure_loaded())

    def suggest_net_class(self, net_name: str) -> dict[str, Any]:
        """Return trace context for one net so an agent can propose a class.

        Includes the deterministic class, every pin on the net (with pin name and
        component comment), and 1-2 hop series-passive neighbors so the agent can
        see the matching/filter chain without re-tracing it.
        """
        from net_classifier import suggest_net_class

        return suggest_net_class(self.ensure_loaded(), net_name)
