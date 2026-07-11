"""Quick local test for connectivity store logic (no MCP client required)."""

from pathlib import Path

from connectivity_store import ConnectivityStore


def main() -> None:
    sample = Path(__file__).resolve().parent / "sample" / "connectivity.json"
    store = ConnectivityStore(path=sample)
    store.load(force=True)

    print("Status:", store.status())
    print("Sheets:", store.list_sheets())
    print("Components:", len(store.list_components()))
    print("U1 placement:", store.get_component_placement("U1"))
    print("U1 NRST pin:", store.get_pin_connection("U1", "7"))
    print("Net 3V3:", store.get_net("3V3"))
    print("PCB U1:", store.get_pcb_component("U1"))
    print("PCB NRST net:", store.get_pcb_net("NRST"))
    print("ERC:", store.get_erc_violations())


if __name__ == "__main__":
    main()
