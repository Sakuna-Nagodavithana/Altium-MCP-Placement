"""Full connectivity review for Duel_Esp_LoRa — power-rail shorts + ERC."""
from __future__ import annotations

import json
from collections import Counter, defaultdict
from pathlib import Path

PATH = Path.home() / "Documents" / "AltiumEE" / "connectivity.json"


def classify_rail(name: str) -> str | None:
    low = str(name or "").casefold().replace(" ", "")
    if low in {"gnd", "agnd", "dgnd", "pgnd", "vss"} or (
        "gnd" in low and len(low) <= 6
    ):
        return "GND"
    if "3v3" in low or "3.3" in low or low in {"vcc3v3"}:
        return "3V3"
    if low in {"5v", "+5v", "vcc5"} or (
        low.endswith("5v") and "3" not in low and "1" not in low and "12" not in low
    ):
        return "5V"
    if low in {"vcc", "vdd"} or low.startswith("vcc") or low.startswith("vdd"):
        return "VCC_GENERIC"
    if "vbat" in low or "vbus" in low or low in {"vin", "12v", "+12v"}:
        return "OTHER_PWR"
    return None


def passive_prefix(des: str) -> str:
    return "".join(ch for ch in des if ch.isalpha())


def main() -> None:
    data = json.loads(PATH.read_text(encoding="utf-8"))
    print("PROJECT:", data.get("project"))
    print("exportedAt:", data.get("exportedAt"))
    print("summary:", json.dumps(data.get("summary"), indent=2)[:2500])
    print("schematics:", data.get("schematics"))
    pcb = data.get("pcb") or {}
    print("pcb:", {k: pcb.get(k) for k in ("path", "name", "file", "layerCount") if k in pcb or True})
    print("pcb components:", len(pcb.get("components") or []))
    erc = data.get("ercViolations") or []
    print("erc count:", len(erc))

    net_to_pins: dict[str, list[dict]] = defaultdict(list)
    comp_by_des: dict[str, dict] = {}
    for c in data.get("components") or []:
        des = str(c.get("designator") or "").strip().upper()
        comp_by_des[des] = c
        for p in c.get("pins") or []:
            net = str(p.get("net") or "").strip()
            if not net or net.casefold() in {"no net", "nonet", "unconnected"}:
                continue
            net_to_pins[net].append(
                {
                    "des": des,
                    "pin": str(p.get("number") or ""),
                    "name": str(p.get("name") or ""),
                    "comment": str(c.get("comment") or ""),
                    "sheet": str(c.get("sheet") or ""),
                }
            )

    for n in data.get("projectNets") or data.get("nets") or []:
        name = str(n.get("name") or "").strip()
        if not name:
            continue
        existing = {(x["des"], x["pin"]) for x in net_to_pins[name]}
        for conn in n.get("connections") or []:
            des = str(conn.get("designator") or "").strip().upper()
            pin = str(conn.get("pin") or "")
            if (des, pin) in existing:
                continue
            c = comp_by_des.get(des) or {}
            net_to_pins[name].append(
                {
                    "des": des,
                    "pin": pin,
                    "name": str(conn.get("pinName") or ""),
                    "comment": str(c.get("comment") or ""),
                    "sheet": str(c.get("sheet") or ""),
                }
            )

    rail_nets: dict[str, list[str]] = defaultdict(list)
    for name in net_to_pins:
        rail = classify_rail(name)
        if rail:
            rail_nets[rail].append(name)

    print("\n=== RAIL GROUPING ===")
    for rail, names in sorted(rail_nets.items()):
        print(f"{rail}: {sorted(names, key=str.casefold)}")
        for name in sorted(names, key=str.casefold):
            print(f"  {name}: {len(net_to_pins[name])} pins")

    gnd_names = set(rail_nets.get("GND") or [])
    v3_names = set(rail_nets.get("3V3") or [])
    v5_names = set(rail_nets.get("5V") or [])
    print("\n=== DIRECT SHORT BY SAME NET NAME? ===")
    print("GND ∩ 3V3:", sorted(gnd_names & v3_names))
    print("GND ∩ 5V:", sorted(gnd_names & v5_names))
    print("3V3 ∩ 5V:", sorted(v3_names & v5_names))

    shorts = []
    for des, c in comp_by_des.items():
        pins = c.get("pins") or []
        pin_nets = []
        for p in pins:
            net = str(p.get("net") or "").strip()
            if net and net.casefold() not in {"no net", "nonet", "unconnected"}:
                pin_nets.append(
                    (str(p.get("number") or ""), str(p.get("name") or ""), net)
                )
        rails: dict[str, list] = {}
        for num, pname, net in pin_nets:
            r = classify_rail(net)
            if r:
                rails.setdefault(r, []).append((num, pname, net))
        if len(rails) >= 2:
            shorts.append(
                {
                    "des": des,
                    "comment": str(c.get("comment") or ""),
                    "prefix": passive_prefix(des),
                    "rails": rails,
                    "all_nets": [n for _, _, n in pin_nets],
                }
            )

    decaps = []
    suspicious = []
    for s in shorts:
        rails = set(s["rails"].keys())
        is_cap = s["prefix"] in {"C", "CB"}
        is_gnd_power = "GND" in rails and len(rails) == 2
        if is_cap and is_gnd_power:
            decaps.append(s)
        else:
            suspicious.append(s)

    print(f"\n=== PARTS TOUCHING 2+ POWER RAILS ({len(shorts)}) ===")
    print(f"Normal-looking decoupling C to GND: {len(decaps)}")
    print(f"Other multi-rail parts: {len(suspicious)}")
    for s in sorted(suspicious, key=lambda x: x["des"]):
        rail_summary = {
            r: sorted({n for _, _, n in pins}) for r, pins in s["rails"].items()
        }
        print(f"  {s['des']} ({s['comment']}): {rail_summary} all={s['all_nets']}")

    bridge_35 = [s for s in suspicious if {"3V3", "5V"} <= set(s["rails"].keys())]
    bridge_gnd = [
        s
        for s in suspicious
        if "GND" in s["rails"]
        and s["prefix"] in {"R", "L", "FB", "JP", "J", "P", "W", "SJ", "F", "BEAD"}
    ]
    print("\n3V3<->5V bridges:", [(s["des"], s["comment"], s["all_nets"]) for s in bridge_35])
    print(
        "Suspicious GND bridges:",
        [(s["des"], s["comment"], s["all_nets"], list(s["rails"])) for s in bridge_gnd],
    )

    # Pin-name vs net mismatches on non-passives
    miswired = []
    for name, pins in net_to_pins.items():
        rail = classify_rail(name)
        if not rail:
            continue
        for p in pins:
            des = p["des"]
            prefix = passive_prefix(des)
            if prefix in {"R", "C", "L", "FB", "D", "RN", "RV", "CB", "BEAD"}:
                continue
            pname = p["name"].upper()
            if any(t in pname for t in ("GND", "VSS", "AGND", "DGND", "PGND")):
                if rail != "GND":
                    miswired.append((des, p["pin"], pname, name, rail, "GND pin on non-GND net"))
            elif any(t in pname for t in ("3V3", "3.3", "VDD3")):
                if rail not in ("3V3", "VCC_GENERIC"):
                    miswired.append((des, p["pin"], pname, name, rail, "3V3 pin on wrong rail"))
            elif pname in {"5V", "+5V", "VCC5"} or ("5V" in pname and "3" not in pname):
                if rail != "5V":
                    miswired.append((des, p["pin"], pname, name, rail, "5V pin on wrong rail"))

    print(f"\n=== PIN-NAME vs NET MISMATCHES ({len(miswired)}) ===")
    for row in miswired[:50]:
        print(" ", row)

    # Unconnected / floating power pins on ICs
    print("\n=== IC / MODULE POWER PINS ===")
    for des, c in sorted(comp_by_des.items()):
        prefix = passive_prefix(des)
        if prefix not in {"U", "IC", "ESP", "MOD"} and not des.startswith(("U", "IC")):
            # keep connectors with power too
            if prefix not in {"J", "P", "H"}:
                continue
        powerish = []
        for p in c.get("pins") or []:
            pname = str(p.get("name") or "")
            net = str(p.get("net") or "").strip()
            up = pname.upper()
            if any(
                t in up
                for t in (
                    "GND",
                    "VSS",
                    "VDD",
                    "VCC",
                    "3V3",
                    "5V",
                    "VIN",
                    "VBAT",
                    "VBUS",
                    "AGND",
                )
            ):
                powerish.append((p.get("number"), pname, net or "(NO NET)"))
        if powerish:
            print(f"  {des} ({c.get('comment')}):")
            for row in powerish:
                print(f"    pin {row[0]} {row[1]} -> {row[2]}")

    # ERC dump
    print(f"\n=== ERC VIOLATIONS ({len(erc)}) ===")
    by_type = Counter(str(v.get("type") or v.get("kind") or v.get("message") or "unknown") for v in erc)
    print("by type:", dict(by_type.most_common(20)))
    for v in erc[:40]:
        print(" ", {k: v.get(k) for k in list(v.keys())[:8]})

    # Net membership sample for GND/3V3/5V
    print("\n=== SAMPLE MEMBERSHIP ===")
    for label, names in (("GND", gnd_names), ("3V3", v3_names), ("5V", v5_names)):
        for name in sorted(names):
            pins = net_to_pins[name]
            des_counts = Counter(p["des"] for p in pins)
            print(f"{label}/{name}: {len(pins)} pins, top parts={des_counts.most_common(12)}")


if __name__ == "__main__":
    main()
