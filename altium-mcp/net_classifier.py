"""Deterministic net-class classifier with series-chain propagation.

Mirrors the C# tracer in PcbDesignRulesSetup.cs so the MCP agent and the
Altium extension produce the same RF / PWR / HighSpeed / Logic assignments.

Priority: PWR (net-name only) -> RF (seed + propagate through 2-pin series
passives) -> HighSpeed (seed + propagate) -> Logic (catch-all).

PWR never propagates through passives (a resistor-divider midpoint is not PWR).
RF wins over HighSpeed when both reach the same net.

The token lists default to the same values as PcbRulesProfile.CreateDefault()
and are overridable by reading %USERPROFILE%\\Documents\\AltiumEE\\pcb-rules-profile.json
when present.
"""

from __future__ import annotations

import json
import re
from collections import deque
from pathlib import Path
from typing import Any

CLASS_ORDER = ("PWR", "RF", "HighSpeed", "Logic")
SERIES_PASSIVE_PREFIXES = {"C", "R", "L", "D", "FB", "BEAD", "FERRITE"}

# Broader RF pin names allowed only when the IC comment already matches an RF transceiver.
RF_COMMENT_GATED_PIN_TOKENS = [
    "RF_TX", "RF_RX", "TX_RF", "RX_RF", "RFP", "RFN", "RF_P", "RF_N",
    "ANTENNA", "PA_OUT", "LNA_IN", "TRX", "RFI", "RFO", "RFIO", "ANT",
]

DEFAULT_PROFILE: dict[str, dict[str, Any]] = {
    "PWR": {
        "netNameTokens": [
            "GND", "AGND", "DGND", "PGND", "VSS", "VCC", "VDD", "3V3", "3.3V",
            "5V", "12V", "VBAT", "VBUS", "VIN", "VOUT", "VDDA", "VDDD", "+3V3", "+5V",
            "+5", "+3", "+12", "+1V8", "+2V5", "1V8", "2V5", "VREF", "VBCKP", "V_BCKP",
            "B+", "BAT+", "BAT-", "BATT",
        ],
        "componentCommentTokens": [],
        "pinNameTokens": [],
    },
    "RF": {
        "netNameTokens": [
            "RF", "ANT", "LORA", "SUBG", "SUB-G", "MATCH", "IFA", "BALUN",
            "FEED", "WLAN", "WIFI", "BLE", "NFC", "GPS_RF", "RFIO", "RFI", "RFO",
        ],
        "componentCommentTokens": [
            "SX126", "SX127", "SX128", "LLCC68", "NRF24", "CC1101", "CC1125",
            "LORA", "TRANSCEIVER", "ANTENNA", "RAK",
        ],
        # Unambiguous RF pin names only. PA/TX/RX excluded: they substring-match
        # MCU GPIO (PA3) and UART (UART2_RX) on combo MCU+RF modules like the RAK family.
        "pinNameTokens": [
            "RF", "RFIO", "RFI", "RFO", "ANT", "LNA", "FEED", "BALUN", "MATCH",
        ],
    },
    "HighSpeed": {
        "netNameTokens": [
            "USB", "D+", "D-", "DP", "DM", "RGMII", "RMII", "MDC", "MDIO",
            "SDIO", "SD_CLK", "SDCMD", "QSPI", "MIPI", "CSI", "DSI", "HDMI",
            "DDR", "DQS", "REFCLK", "XTAL", "OSC", "MCLK", "BCLK", "LRCLK",
            "CANH", "CANL", "CAN_P", "CAN_N", "RS485", "LVDS", "USB_P", "USB_N",
        ],
        "componentCommentTokens": [
            "LAN872", "DP83848", "KSZ808", "KSZ903", "RTL8211",
            "USB330", "USB332", "USB251", "CH340", "CP210", "FT232", "FT223",
            "DDR3", "DDR4", "LPDDR", "MT48", "IS42", "AS4C",
        ],
        "pinNameTokens": [
            "D+", "D-", "DM", "DP", "USB_DM", "USB_DP",
            "MCLK", "BCLK", "LRCLK", "SDCLK", "DQS",
            "RGMII", "RMII", "REFCLK", "XTAL", "OSC",
        ],
    },
    "Logic": {
        "netNameTokens": [],
        "componentCommentTokens": [],
        "pinNameTokens": [],
        "catchAll": True,
    },
}


def _profile_path() -> Path | None:
    path = Path.home() / "Documents" / "AltiumEE" / "pcb-rules-profile.json"
    return path if path.exists() else None


def load_profile() -> dict[str, dict[str, Any]]:
    """Load the token profile from the AltiumEE folder, falling back to defaults.

    Merges missing keys/tokens so a stale 3-class profile still works.
    """
    profile = {cls: dict(cfg) for cls, cfg in DEFAULT_PROFILE.items()}
    path = _profile_path()
    if path is None:
        return profile

    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return profile

    classes = raw.get("netClasses") or []
    for entry in classes:
        name = str(entry.get("name") or "").strip()
        if not name or name not in profile:
            continue
        for field in ("netNameTokens", "componentCommentTokens", "pinNameTokens"):
            value = entry.get(field)
            if value:
                profile[name][field] = [str(t) for t in value if str(t).strip()]
    return profile


def _matches_tokens(value: str, tokens: list[str]) -> bool:
    if not value or not tokens:
        return False
    upper = value.upper()
    return any(t.strip().upper() in upper for t in tokens if t and t.strip())


def _series_passive_prefix(designator: str) -> str:
    return "".join(ch for ch in designator if ch.isalpha()).upper()


def _build_indices(data: dict[str, Any]):
    net_to_pins: dict[str, list[dict[str, str]]] = {}
    component_to_pins: dict[str, list[dict[str, str]]] = {}
    component_comment: dict[str, str] = {}
    component_pin_count: dict[str, int] = {}

    for component in data.get("components") or []:
        designator = str(component.get("designator") or "").strip()
        if not designator:
            continue
        component_comment[designator] = str(component.get("comment") or "").strip()
        pins = component.get("pins") or []
        component_pin_count[designator] = len(pins)
        comp_pins: list[dict[str, str]] = []
        for pin in pins:
            entry = {
                "designator": designator,
                "pinNumber": str(pin.get("number") or "").strip(),
                "pinName": str(pin.get("name") or "").strip(),
                "net": str(pin.get("net") or "").strip(),
            }
            comp_pins.append(entry)
            if entry["net"]:
                net_to_pins.setdefault(entry["net"], []).append(entry)
        component_to_pins[designator] = comp_pins

    # Merge net membership from the projectNets/nets "connections" arrays too.
    # The export sometimes lists a labeled net (e.g. "CLK") separately from the
    # auto-named net on the same node (e.g. "NetU2_5"). The connections array gives
    # us the designator+pin for the labeled net so suggest_net_class can show them.
    for source_key in ("projectNets", "nets"):
        for net_entry in data.get(source_key) or []:
            net_name = str(net_entry.get("name") or "").strip()
            if not net_name or net_name in net_to_pins:
                continue
            for conn in net_entry.get("connections") or []:
                desig = str(conn.get("designator") or "").strip()
                pin_num = str(conn.get("pin") or "").strip()
                if not desig or not pin_num:
                    continue
                pin_name = ""
                for p in component_to_pins.get(desig, []):
                    if p["pinNumber"] == pin_num:
                        pin_name = p["pinName"]
                        break
                net_to_pins.setdefault(net_name, []).append({
                    "designator": desig,
                    "pinNumber": pin_num,
                    "pinName": pin_name,
                    "net": net_name,
                })

    return net_to_pins, component_to_pins, component_comment, component_pin_count


def _is_series_passive(designator: str, pin_count: dict[str, int]) -> bool:
    if not designator:
        return False
    if component_pin_count := pin_count.get(designator):
        if component_pin_count != 2:
            return False
    prefix = _series_passive_prefix(designator)
    return prefix in SERIES_PASSIVE_PREFIXES


def classify_nets(data: dict[str, Any], profile: dict[str, dict[str, Any]] | None = None) -> dict[str, list[str]]:
    """Classify every net in the export. Returns {class: [net names]}.

    Does not mutate the export. Mirrors PcbDesignRulesSetup.ClassifyNets.
    """
    if profile is None:
        profile = load_profile()

    net_to_pins, component_to_pins, component_comment, pin_count = _build_indices(data)

    # Include every net name from the export (projectNets preferred, else net->pins keys).
    net_names: list[str] = []
    seen: set[str] = set()
    for net in data.get("projectNets") or data.get("nets") or []:
        name = str(net.get("name") or "").strip()
        if name and name.casefold() not in seen:
            seen.add(name.casefold())
            net_names.append(name)
    for name in net_to_pins:
        if name.casefold() not in seen:
            seen.add(name.casefold())
            net_names.append(name)

    pwr_tokens = profile["PWR"]["netNameTokens"]
    rf_name = profile["RF"]["netNameTokens"]
    rf_pin = profile["RF"]["pinNameTokens"]
    rf_comment = profile["RF"].get("componentCommentTokens") or []
    hs_name = profile["HighSpeed"]["netNameTokens"]
    hs_pin = profile["HighSpeed"]["pinNameTokens"]
    hs_comment = profile["HighSpeed"].get("componentCommentTokens") or []

    assigned: dict[str, str] = {}
    queue: deque[str] = deque()

    for net in net_names:
        if _matches_tokens(net, pwr_tokens):
            assigned[net] = "PWR"
            continue

        seed = None
        if _matches_tokens(net, rf_name):
            seed = "RF"
        elif _matches_tokens(net, hs_name):
            seed = "HighSpeed"
        else:
            for pin in net_to_pins.get(net, []):
                if _matches_tokens(pin["pinName"], rf_pin):
                    seed = "RF"
                    break
                if _matches_tokens(pin["pinName"], hs_pin):
                    seed = "HighSpeed"
                    break

            # Comment-gated broader pin tokens (RF transceiver IC only).
            if seed is None:
                for pin in net_to_pins.get(net, []):
                    comment = component_comment.get(pin["designator"], "")
                    if not comment:
                        continue
                    if _matches_tokens(comment, rf_comment) and _matches_tokens(
                        pin["pinName"], RF_COMMENT_GATED_PIN_TOKENS
                    ):
                        seed = "RF"
                        break
                    if _matches_tokens(comment, hs_comment) and _matches_tokens(
                        pin["pinName"], hs_pin
                    ):
                        seed = "HighSpeed"
                        break

        if seed:
            assigned[net] = seed
            queue.append(net)

    # Propagate RF + HighSpeed through 2-pin series passives (BFS).
    while queue:
        current = queue.popleft()
        current_class = assigned.get(current)
        if current_class not in ("RF", "HighSpeed"):
            continue
        for pin in net_to_pins.get(current, []):
            if not _is_series_passive(pin["designator"], pin_count):
                continue
            for other in component_to_pins.get(pin["designator"], []):
                other_net = other["net"]
                if not other_net or other_net.casefold() == current.casefold():
                    continue
                if _matches_tokens(other_net, pwr_tokens):
                    assigned[other_net] = "PWR"
                    continue
                existing = assigned.get(other_net)
                if existing == "PWR":
                    continue
                if existing == "RF":
                    continue
                if existing == "HighSpeed" and current_class == "RF":
                    assigned[other_net] = "RF"
                    queue.append(other_net)
                    continue
                if existing:
                    continue
                assigned[other_net] = current_class
                queue.append(other_net)

    buckets: dict[str, list[str]] = {cls: [] for cls in CLASS_ORDER}
    for net in net_names:
        cls = assigned.get(net)
        if cls in buckets:
            buckets[cls].append(net)
        else:
            buckets["Logic"].append(net)

    for cls in buckets:
        buckets[cls].sort(key=str.casefold)
    return buckets


def suggest_net_class(data: dict[str, Any], net_name: str) -> dict[str, Any]:
    """Return the trace context for a net so an agent can propose a class.

    Includes: deterministic classification, every pin on the net (with pin name
    and component comment), and 1-2 hop neighbors through 2-pin series passives
    so the agent can see the series chain without re-tracing it.
    """
    profile = load_profile()
    classification = classify_nets(data, profile)
    net_key = net_name.strip()
    net_cf = net_key.casefold()

    determined = None
    for cls, nets in classification.items():
        if any(n.casefold() == net_cf for n in nets):
            determined = cls
            break

    net_to_pins, component_to_pins, comment, pin_count = _build_indices(data)

    pins_on_net = [
        {
            "designator": p["designator"],
            "pin": p["pinNumber"],
            "pinName": p["pinName"],
            "componentComment": comment.get(p["designator"], ""),
            "net": p["net"],
        }
        for p in net_to_pins.get(net_key, [])
    ]

    # One-hop and two-hop series neighbors.
    neighbors: list[dict[str, Any]] = []
    visited = {net_cf}
    frontier = deque([(net_key, 0)])
    while frontier:
        node, depth = frontier.popleft()
        if depth >= 2:
            continue
        for pin in net_to_pins.get(node, []):
            if not _is_series_passive(pin["designator"], pin_count):
                continue
            for other in component_to_pins.get(pin["designator"], []):
                other_net = other["net"]
                if not other_net or other_net.casefold() in visited:
                    continue
                visited.add(other_net.casefold())
                neighbors.append({
                    "net": other_net,
                    "viaDesignator": pin["designator"],
                    "viaPinName": pin["pinName"],
                    "componentComment": comment.get(pin["designator"], ""),
                    "hops": depth + 1,
                    "classification": _class_of(classification, other_net),
                })
                frontier.append((other_net, depth + 1))

    return {
        "net": net_key,
        "deterministicClass": determined,
        "pins": pins_on_net,
        "seriesNeighbors": neighbors,
        "profileTokens": {
            cls: {
                "netNameTokens": profile[cls].get("netNameTokens", []),
                "pinNameTokens": profile[cls].get("pinNameTokens", []),
            }
            for cls in ("RF", "HighSpeed", "PWR")
        },
        "hint": (
            "If deterministicClass is Logic but a series neighbor is RF/HighSpeed, "
            "the net is probably part of a matching/filter chain and should be "
            "reassigned. Propagate the neighbor's class. If pins include a named "
            "RF/USB/XTAL pin, match by pinNameTokens. If still ambiguous, ask the "
            "user for the signal name or frequency."
        ),
    }


def _class_of(classification: dict[str, list[str]], net: str) -> str | None:
    cf = net.casefold()
    for cls, nets in classification.items():
        if any(n.casefold() == cf for n in nets):
            return cls
    return None
