"""Tests for IC support grouping, pin-near placement, and verification."""

import json
import math
import tempfile
from pathlib import Path

from connectivity_store import ConnectivityStore
from placement_planner import (
    build_all_ic_cluster_plan,
    build_ic_placement_plan,
    detect_support_chains,
    expand_series_chain_support,
    get_ic_support_components,
    verify_cluster_placement,
)


def _sample_payload() -> dict:
    return {
        "schemaVersion": 4,
        "projectNets": [
            {
                "name": "3v3",
                "connections": [
                    {"designator": "IC1", "pin": "3"},
                    {"designator": "IC2", "pin": "3"},
                    {"designator": "C10", "pin": "1"},
                    {"designator": "C99", "pin": "1"},
                ],
            },
            {
                "name": "GND",
                "connections": [
                    {"designator": "IC1", "pin": "1"},
                    {"designator": "C10", "pin": "2"},
                    {"designator": "C99", "pin": "2"},
                ],
            },
            {
                "name": "NetIC1_25",
                "connections": [
                    {"designator": "IC1", "pin": "25"},
                    {"designator": "R29", "pin": "2"},
                ],
            },
            {
                "name": "NetChain_A",
                "connections": [
                    {"designator": "IC1", "pin": "26"},
                    {"designator": "R30", "pin": "2"},
                ],
            },
            {
                "name": "NetChain_B",
                "connections": [
                    {"designator": "R30", "pin": "1"},
                    {"designator": "C20", "pin": "1"},
                ],
            },
            {
                "name": "NetChain_C",
                "connections": [
                    {"designator": "C20", "pin": "2"},
                    {"designator": "R31", "pin": "1"},
                ],
            },
        ],
        "components": [
            {
                "designator": "IC1",
                "comment": "ESP32-S3",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "3", "name": "VDD", "net": "3v3"},
                    {"number": "25", "name": "GPIO19", "net": "NetIC1_25"},
                    {"number": "26", "name": "GPIO20", "net": "NetChain_A"},
                ],
                "placement": {"xMils": 1000, "yMils": 2000, "rotation": "0"},
            },
            {
                "designator": "IC2",
                "comment": "ESP32-S3",
                "sheet": "ESP32_B.SchDoc",
                "pins": [{"number": "3", "name": "VDD", "net": "3v3"}],
                "placement": {"xMils": 9000, "yMils": 9000, "rotation": "0"},
            },
            {
                "designator": "C10",
                "comment": "0.1 uf",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "3v3"},
                    {"number": "2", "name": "2", "net": "GND"},
                ],
                "placement": {"xMils": 1200, "yMils": 2000, "rotation": "0"},
            },
            {
                "designator": "C99",
                "comment": "far cap",
                "sheet": "ESP32_B.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "3v3"},
                    {"number": "2", "name": "2", "net": "GND"},
                ],
                "placement": {"xMils": 9000, "yMils": 9200, "rotation": "0"},
            },
            {
                "designator": "R29",
                "comment": "10K",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "3v3"},
                    {"number": "2", "name": "2", "net": "NetIC1_25"},
                ],
                "placement": {"xMils": 1100, "yMils": 2100, "rotation": "90"},
            },
            {
                "designator": "R31",
                "comment": "10K",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "NetChain_C"},
                    {"number": "2", "name": "2", "net": "GND"},
                ],
                "placement": {"xMils": 1050, "yMils": 2150, "rotation": "0"},
            },
            {
                "designator": "C20",
                "comment": "15pf",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "NetChain_B"},
                    {"number": "2", "name": "2", "net": "NetChain_C"},
                ],
                "placement": {"xMils": 1040, "yMils": 2140, "rotation": "0"},
            },
            {
                "designator": "R30",
                "comment": "33R",
                "sheet": "ESP32_A.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "NetChain_B"},
                    {"number": "2", "name": "2", "net": "NetChain_A"},
                ],
                "placement": {"xMils": 1030, "yMils": 2130, "rotation": "0"},
            },
        ],
        "pcb": {
            "components": [
                {
                    "designator": "IC1",
                    "layer": "Top",
                    "placement": {"xMils": 5000, "yMils": 4000, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "IC2",
                    "layer": "Top",
                    "placement": {"xMils": 12000, "yMils": 12000, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "C10",
                    "layer": "Top",
                    "placement": {"xMils": 100, "yMils": 100, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "C99",
                    "layer": "Top",
                    "placement": {"xMils": 8000, "yMils": 8000, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "R29",
                    "layer": "Top",
                    "placement": {"xMils": 200, "yMils": 200, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "R30",
                    "layer": "Top",
                    "placement": {"xMils": 250, "yMils": 250, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "C20",
                    "layer": "Top",
                    "placement": {"xMils": 300, "yMils": 300, "rotation": 0, "layer": "Top"},
                },
                {
                    "designator": "R31",
                    "layer": "Top",
                    "placement": {"xMils": 350, "yMils": 350, "rotation": 0, "layer": "Top"},
                },
            ]
        },
    }


def test_grouping_excludes_far_and_global_parts() -> None:
    payload = _sample_payload()
    grouping = get_ic_support_components(payload, "IC1")
    assert grouping["found"] is True
    designators = {item["designator"] for item in grouping["support_components"]}
    assert "C10" in designators
    assert "R29" in designators
    assert "C99" not in designators


def test_pin_near_plan_separates_parts_by_pin_ray() -> None:
    payload = _sample_payload()
    plan = build_ic_placement_plan(
        payload, "IC1", spacing_mils=80, max_radius_mils=900, layout_mode="pin_near"
    )
    assert plan["found"] is True
    assert plan["schemaVersion"] == 5.1
    assert plan["layoutMode"] == "pin_near"
    assert plan["move_count"] >= 2

    ic = next(c for c in payload["pcb"]["components"] if c["designator"] == "IC1")
    ax, ay = ic["placement"]["xMils"], ic["placement"]["yMils"]

    moves_by_des = {move["designator"]: move for move in plan["moves"]}
    c10 = moves_by_des["C10"]
    r29 = moves_by_des["R29"]

    assert c10["method"].startswith("pin_near")
    assert r29["method"].startswith("pin_near")
    assert c10.get("primary_ic_pin")
    assert r29.get("primary_ic_pin")
    assert c10["primary_ic_pin"] != r29["primary_ic_pin"]

    for move in plan["moves"]:
        dist = math.hypot(move["xMils"] - ax, move["yMils"] - ay)
        assert dist <= 900.1, move
        assert move.get("targetPinAngleDeg") is not None
        assert move.get("standoffMils", 0) > 0

    separation = math.hypot(c10["xMils"] - r29["xMils"], c10["yMils"] - r29["yMils"])
    assert separation >= 60.0, f"parts stacked too close: {separation:.1f} mils"

    angle_delta = abs(c10["placementAngleDeg"] - r29["placementAngleDeg"])
    angle_delta = min(angle_delta, 360.0 - angle_delta)
    assert angle_delta >= 10.0, f"expected different pin rays, got {angle_delta:.1f} deg"


def test_series_chain_detection_and_layout() -> None:
    payload = _sample_payload()
    grouping = get_ic_support_components(payload, "IC1")
    expanded = expand_series_chain_support(payload, "IC1", grouping["support_components"])
    chains = detect_support_chains(payload, "IC1", expanded)
    chain_members = [member for chain in chains for member in chain["members"]]
    assert "R30" in chain_members
    assert "C20" in chain_members
    assert "R31" in chain_members

    plan = build_ic_placement_plan(
        payload, "IC1", spacing_mils=80, max_radius_mils=900, layout_mode="pin_chain"
    )
    chain_moves = [
        move
        for move in plan["moves"]
        if str(move.get("method") or "").startswith("pin_chain")
    ]
    assert len(chain_moves) >= 3
    ordered = sorted(chain_moves, key=lambda move: move.get("chainIndex", 0))
    assert ordered[0]["designator"] == "R30"
    assert ordered[1]["designator"] == "C20"
    assert ordered[2]["designator"] == "R31"
    for left, right in zip(ordered, ordered[1:]):
        assert math.hypot(
            right["xMils"] - left["xMils"],
            right["yMils"] - left["yMils"],
        ) >= 40.0


def test_verification_passes_for_pin_near_plan() -> None:
    payload = _sample_payload()
    plan = build_ic_placement_plan(
        payload, "IC1", spacing_mils=80, max_radius_mils=900, layout_mode="pin_near"
    )
    verification = plan.get("verification") or {}
    assert verification.get("verified_count") == plan["move_count"]
    assert verification.get("ok_count", 0) >= 1

    ok_items = [item for item in verification.get("items") or [] if item["status"] == "ok"]
    assert len(ok_items) >= 1


def test_compact_mode_still_available() -> None:
    payload = _sample_payload()
    plan = build_ic_placement_plan(
        payload,
        "IC1",
        spacing_mils=80,
        max_radius_mils=900,
        layout_mode="compact",
    )
    assert plan["layoutMode"] == "compact"
    for move in plan["moves"]:
        assert move["method"].startswith("compact_net_pin")


def test_all_clusters_plan_includes_verification() -> None:
    payload = _sample_payload()
    plan = build_all_ic_cluster_plan(
        payload, spacing_mils=80, max_radius_mils=900, layout_mode="pin_accurate"
    )
    assert plan["found"] is True
    assert plan["schemaVersion"] == 5.1
    assert plan["layoutMode"] == "pin_accurate"
    assert plan["move_count"] >= 2
    verification = plan.get("verification") or {}
    assert verification.get("cluster_count", 0) >= 1
    assert verification.get("ok_count", 0) >= 1


def test_connectivity_store_exports_pin_near_plan() -> None:
    payload = _sample_payload()
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as handle:
        json.dump(payload, handle)
        path = Path(handle.name)

    store = ConnectivityStore(path=path)
    store.load(force=True)
    exported = store.generate_ic_placement_plan("IC1")
    assert exported.get("plan_file")
    assert exported["move_count"] >= 2
    assert exported.get("layoutMode") == "pin_near"


def test_verify_cluster_placement_detects_overlap() -> None:
    payload = _sample_payload()
    grouping = get_ic_support_components(payload, "IC1")
    ic = next(c for c in payload["pcb"]["components"] if c["designator"] == "IC1")
    anchor_xy = (ic["placement"]["xMils"], ic["placement"]["yMils"])
    bad_moves = [
        {
            "designator": "C10",
            "xMils": 5100.0,
            "yMils": 4100.0,
            "method": "pin_near",
            "primary_ic_pin": "3",
            "primary_net": "3v3",
            "nets": ["3v3", "GND"],
            "linked_ic_pins": [{"pin": "3", "net": "3v3"}],
            "targetPinAngleDeg": 0.0,
            "roles": ["decoupling"],
        },
        {
            "designator": "R29",
            "xMils": 5105.0,
            "yMils": 4105.0,
            "method": "pin_near",
            "primary_ic_pin": "25",
            "primary_net": "NetIC1_25",
            "nets": ["3v3", "NetIC1_25"],
            "linked_ic_pins": [{"pin": "25", "net": "NetIC1_25"}],
            "targetPinAngleDeg": 45.0,
            "roles": ["signal"],
        },
    ]
    result = verify_cluster_placement(
        anchor="IC1",
        anchor_xy=anchor_xy,
        support_components=grouping["support_components"],
        moves=bad_moves,
        spacing_mils=80.0,
        max_radius_mils=900.0,
    )
    assert result["all_ok"] is False
    assert result["warn_count"] >= 1
    overlap = next(item for item in result["items"] if "too_close_to_other_part" in item["issues"])
    assert overlap["designator"] in {"C10", "R29"}


def test_decoupling_cap_sort_prefers_smaller_value_over_pin_angle() -> None:
    """100nF must sort before 10uF even when schematic angle favors the bulk cap."""
    data = {
        "schemaVersion": 5.1,
        "components": [
            {
                "designator": "U1",
                "comment": "MCU",
                "sheet": "Main.SchDoc",
                "pins": [
                    {"number": "9", "name": "VDDA", "net": "3V3"},
                    {"number": "8", "name": "VSSA", "net": "GND"},
                ],
                "placement": {"xMils": 1200.0, "yMils": 800.0, "rotation": "0"},
            },
            {
                "designator": "C1",
                "comment": "100nF",
                "sheet": "Main.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "3V3"},
                    {"number": "2", "name": "2", "net": "GND"},
                ],
                "placement": {"xMils": 1050.0, "yMils": 700.0, "rotation": "90"},
            },
            {
                "designator": "C2",
                "comment": "10uF",
                "sheet": "Main.SchDoc",
                "pins": [
                    {"number": "1", "name": "1", "net": "3V3"},
                    {"number": "2", "name": "2", "net": "GND"},
                ],
                "placement": {"xMils": 1080.0, "yMils": 760.0, "rotation": "90"},
            },
        ],
        "projectNets": [
            {
                "name": "3V3",
                "connections": [
                    {"designator": "U1", "pin": "9"},
                    {"designator": "C1", "pin": "1"},
                    {"designator": "C2", "pin": "1"},
                ],
            },
            {
                "name": "GND",
                "connections": [
                    {"designator": "U1", "pin": "8"},
                    {"designator": "C1", "pin": "2"},
                    {"designator": "C2", "pin": "2"},
                ],
            },
        ],
        "pcb": {
            "components": [
                {
                    "designator": "U1",
                    "pattern": "LQFP48",
                    "placement": {"xMils": 1200.0, "yMils": 800.0, "rotation": 0.0},
                    "pads": [{"name": "9", "net": "3V3", "xMils": 1180.0, "yMils": 790.0}],
                },
                {
                    "designator": "C1",
                    "pattern": "0402",
                    "placement": {"xMils": 1050.0, "yMils": 700.0, "rotation": 90.0},
                    "pads": [],
                },
                {
                    "designator": "C2",
                    "pattern": "0603",
                    "placement": {"xMils": 1080.0, "yMils": 760.0, "rotation": 90.0},
                    "pads": [],
                },
            ],
        },
    }
    grouping = get_ic_support_components(data, "U1")
    decaps = [
        item["designator"]
        for item in grouping["support_components"]
        if item["designator"] in {"C1", "C2"}
    ]
    assert decaps == ["C1", "C2"]

    plan = build_ic_placement_plan(data, "U1", layout_mode="pin_accurate")
    assert plan["found"] is True
    moves = {move["designator"]: move for move in plan["moves"]}
    assert moves["C1"]["pinSlot"] < moves["C2"]["pinSlot"]

    pin_x, pin_y = 1180.0, 790.0
    c1_dist = math.hypot(moves["C1"]["xMils"] - pin_x, moves["C1"]["yMils"] - pin_y)
    c2_dist = math.hypot(moves["C2"]["xMils"] - pin_x, moves["C2"]["yMils"] - pin_y)
    assert c1_dist < c2_dist


def main() -> None:
    test_grouping_excludes_far_and_global_parts()
    test_pin_near_plan_separates_parts_by_pin_ray()
    test_series_chain_detection_and_layout()
    test_verification_passes_for_pin_near_plan()
    test_compact_mode_still_available()
    test_all_clusters_plan_includes_verification()
    test_connectivity_store_exports_pin_near_plan()
    test_verify_cluster_placement_detects_overlap()
    test_decoupling_cap_sort_prefers_smaller_value_over_pin_angle()
    print("placement planner tests OK (pin_near + verification)")


if __name__ == "__main__":
    main()
