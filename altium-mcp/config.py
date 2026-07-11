"""Environment configuration for local and online MCP modes."""

from __future__ import annotations

import os
import secrets
from urllib.parse import urlparse


DEFAULT_PORT = 8787
DEFAULT_CONNECTIVITY_PATH = os.path.join(
    os.path.expanduser("~"),
    "Documents",
    "AltiumEE",
    "connectivity.json",
)


def get_transport() -> str:
    return os.environ.get("MCP_TRANSPORT", "stdio").strip().lower()


def is_online_mode() -> bool:
    return get_transport() in {"http", "streamable-http", "online"}


def get_port() -> int:
    return int(os.environ.get("MCP_PORT", str(DEFAULT_PORT)))


def get_host() -> str:
    return os.environ.get("MCP_HOST", "127.0.0.1").strip()


def get_public_url() -> str:
    url = os.environ.get("MCP_PUBLIC_URL", "").strip()
    if url:
        return url.rstrip("/")
    return f"http://{get_host()}:{get_port()}"


def get_connectivity_path() -> str:
    return os.environ.get("ALTIUM_CONNECTIVITY_FILE", DEFAULT_CONNECTIVITY_PATH)


def require_api_key() -> str:
    api_key = os.environ.get("MCP_API_KEY", "").strip()
    if not api_key:
        raise RuntimeError(
            "MCP_API_KEY is not set. Run scripts/generate-api-key.ps1 and copy .env.example to .env"
        )
    if len(api_key) < 24:
        raise RuntimeError("MCP_API_KEY is too short. Use at least 24 characters.")
    return api_key


def generate_api_key() -> str:
    return secrets.token_urlsafe(48)


def public_hostname() -> str:
    parsed = urlparse(get_public_url())
    return parsed.hostname or "127.0.0.1"


def public_scheme() -> str:
    parsed = urlparse(get_public_url())
    return parsed.scheme or "http"
