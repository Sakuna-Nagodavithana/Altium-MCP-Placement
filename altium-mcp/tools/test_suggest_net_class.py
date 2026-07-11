"""Call suggest_net_class on an SD-card residue net via the live MCP server."""
from __future__ import annotations

import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from invoke_mcp_tool import call_tool


async def main() -> None:
    for net in ("CLK", "CMD", "DAT0"):
        result = await call_tool("suggest_net_class", {"net": net})
        print(f"\n=== suggest_net_class({net}) -> {result.get('deterministicClass')} ===")
        for p in (result.get("pins") or [])[:5]:
            print(f"  {p.get('designator')}.{p.get('pin')} ({p.get('pinName')}) [{p.get('componentComment')}]")
        neighbors = result.get("seriesNeighbors") or []
        if neighbors:
            print("  series neighbors:")
            for n in neighbors[:4]:
                print(f"    -> {n.get('net')} via {n.get('viaDesignator')} ({n.get('componentComment')})")


if __name__ == "__main__":
    asyncio.run(main())
