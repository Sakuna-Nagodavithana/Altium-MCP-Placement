"""Simple bearer-token auth for the online MCP server."""

from __future__ import annotations

from mcp.server.auth.provider import AccessToken


class StaticApiKeyVerifier:
    """Validate Authorization: Bearer <MCP_API_KEY>."""

    def __init__(self, api_key: str, scopes: list[str] | None = None) -> None:
        self._api_key = api_key
        self._scopes = scopes or ["mcp:read"]

    async def verify_token(self, token: str) -> AccessToken | None:
        if not token or token != self._api_key:
            return None
        return AccessToken(
            token=token,
            client_id="altium-mcp-client",
            scopes=self._scopes,
            subject="altium-mcp-user",
        )


def extract_bearer_token(authorization_header: str | None) -> str | None:
    if not authorization_header:
        return None
    prefix = "Bearer "
    if authorization_header.startswith(prefix):
        return authorization_header[len(prefix) :].strip()
    return None


def is_authorized_request(
    authorization_header: str | None,
    x_api_key_header: str | None,
    expected_api_key: str,
) -> bool:
    bearer = extract_bearer_token(authorization_header)
    if bearer and bearer == expected_api_key:
        return True
    return bool(x_api_key_header and x_api_key_header == expected_api_key)
