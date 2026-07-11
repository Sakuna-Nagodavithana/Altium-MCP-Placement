from net_classifier import classify_nets


def test_bare_positive_rail_names_are_power() -> None:
    payload = {
        "components": [
            {
                "designator": "U1",
                "comment": "MCU",
                "pins": [
                    {"number": "1", "name": "VDD", "net": "+5"},
                    {"number": "2", "name": "GPIO", "net": "NetU1_2"},
                ],
            }
        ],
        "projectNets": [
            {"name": "+5", "connections": [{"designator": "U1", "pin": "1"}]},
            {
                "name": "NetU1_2",
                "connections": [{"designator": "U1", "pin": "2"}],
            },
        ],
    }

    classes = classify_nets(payload)
    assert "+5" in classes["PWR"]
    assert "+5" not in classes["Logic"]
