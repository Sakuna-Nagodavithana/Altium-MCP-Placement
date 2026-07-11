"""Quick MCP board verification for Stmcu project."""
from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from invoke_mcp_tool import call_tool


async def main() -> None:
    status = await call_tool("get_connectivity_status", {})
    print("=== get_connectivity_status ===")
    print(json.dumps(status, indent=2))

    for ic in ("U1", "U3", "U7"):
        result = await call_tool("get_ic_support_components", {"designator": ic})
        print(f"\n=== get_ic_support_components {ic} ===")
        summary = {
            "found": result.get("found"),
            "anchor_comment": result.get("anchor_comment"),
            "support_count": result.get("support_count"),
            "support": [
                {
                    "designator": item.get("designator"),
                    "comment": item.get("comment"),
                    "role": item.get("primary_role"),
                }
                for item in (result.get("support_components") or [])[:10]
            ],
        }
        print(json.dumps(summary, indent=2))

    comps = await call_tool("list_components", {"query": "U"})
    seen: set[str] = set()
    modules = []
    for item in comps.get("components") or []:
        des = str(item.get("designator") or "")
        if des in seen:
            continue
        seen.add(des)
        if int(item.get("pin_count") or 0) >= 8:
            modules.append(item)

    print("\n=== Module-class parts (U*, >=8 pins) ===")
    for item in modules:
        print(
            f"  {item.get('designator')}: {item.get('comment')} "
            f"({item.get('pin_count')} pins, JLC {item.get('jlcpcb')})"
        )


if __name__ == "__main__":
    asyncio.run(main())
