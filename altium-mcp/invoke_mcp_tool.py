"""Call a tool on the running Altium MCP HTTP server."""

from __future__ import annotations

import asyncio
import json
import os
import sys
from pathlib import Path

from dotenv import load_dotenv

load_dotenv(Path(__file__).resolve().parent / ".env")

try:
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client
except ImportError as exc:
    raise SystemExit("Install requirements: pip install mcp httpx") from exc


async def call_tool(name: str, arguments: dict) -> object:
    host = os.environ.get("MCP_HOST", "127.0.0.1")
    port = os.environ.get("MCP_PORT", "8787")
    api_key = os.environ.get("MCP_API_KEY", "")
    url = f"http://{host}:{port}/mcp"
    headers = {"Authorization": f"Bearer {api_key}"} if api_key else {}

    async with streamablehttp_client(url, headers=headers) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            result = await session.call_tool(name, arguments)
            if result.content:
                for block in result.content:
                    text = getattr(block, "text", None)
                    if text:
                        return json.loads(text)
            return {"raw": result.model_dump()}


def main() -> None:
    tool = sys.argv[1] if len(sys.argv) > 1 else "get_connectivity_status"
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    payload = asyncio.run(call_tool(tool, args))
    print(json.dumps(payload, indent=2))


if __name__ == "__main__":
    main()
