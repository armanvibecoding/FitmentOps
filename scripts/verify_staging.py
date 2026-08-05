#!/usr/bin/env python3
"""Fail-closed staging contract checks without logging credentials or response bodies."""

from __future__ import annotations

import argparse
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any


TIMEOUT_SECONDS = 20
REQUIRED_CAPABILITIES = (
    "payment",
    "electronicInvoice",
    "email",
    "outboxDispatch",
    "inventoryReservationExpiry",
    "publicSite",
    "shippingCarrier",
)
FORBIDDEN_FIELD_PARTS = ("secret", "credential", "password", "apiKey", "accessToken")


class GateFailure(RuntimeError):
    pass


def request(
    url: str,
    *,
    method: str = "GET",
    headers: dict[str, str] | None = None,
) -> tuple[int, dict[str, str], bytes]:
    req = urllib.request.Request(url, method=method, headers=headers or {})
    try:
        with urllib.request.urlopen(
            req,
            timeout=TIMEOUT_SECONDS,
            context=ssl.create_default_context(),
        ) as response:
            return response.status, dict(response.headers.items()), response.read()
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers.items()), error.read()
    except (urllib.error.URLError, TimeoutError) as error:
        raise GateFailure(f"request failed for {safe_location(url)}: {type(error).__name__}") from error


def safe_location(url: str) -> str:
    parsed = urllib.parse.urlsplit(url)
    return f"{parsed.scheme}://{parsed.netloc}{parsed.path}"


def json_object(body: bytes, location: str) -> dict[str, Any]:
    try:
        payload = json.loads(body)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise GateFailure(f"{location} did not return valid JSON") from error
    if not isinstance(payload, dict):
        raise GateFailure(f"{location} did not return a JSON object")
    return payload


def header(headers: dict[str, str], name: str) -> str | None:
    lowered = name.lower()
    return next((value for key, value in headers.items() if key.lower() == lowered), None)


def ensure_no_sensitive_fields(value: Any, path: str = "capabilities") -> None:
    if isinstance(value, dict):
        for key, nested in value.items():
            if any(part.lower() in key.lower() for part in FORBIDDEN_FIELD_PARTS):
                raise GateFailure(f"sensitive field name exposed at {path}.{key}")
            ensure_no_sensitive_fields(nested, f"{path}.{key}")
    elif isinstance(value, list):
        for index, nested in enumerate(value):
            ensure_no_sensitive_fields(nested, f"{path}[{index}]")


def verify(args: argparse.Namespace) -> None:
    token = os.environ.get("STAGING_ADMIN_BEARER_TOKEN", "").strip()
    if not token:
        raise GateFailure("STAGING_ADMIN_BEARER_TOKEN is required; provider certification cannot be skipped")

    for name, url in (
        ("API base URL", args.api_base_url),
        ("web base URL", args.web_base_url),
        ("OpenAPI URL", args.openapi_url),
    ):
        if not url.startswith("https://"):
            raise GateFailure(f"{name} must use HTTPS")

    api = args.api_base_url.rstrip("/")
    web = args.web_base_url.rstrip("/")
    web_origin = f"{urllib.parse.urlsplit(web).scheme}://{urllib.parse.urlsplit(web).netloc}"

    web_status, _, _ = request(f"{web}/")
    if web_status != 200:
        raise GateFailure(f"storefront returned HTTP {web_status}")

    live_status, live_headers, live_body = request(f"{api}/health/live")
    live = json_object(live_body, "/health/live")
    if live_status != 200 or live.get("healthy") is not True:
        raise GateFailure("liveness is not healthy")
    if header(live_headers, "X-Content-Type-Options") != "nosniff":
        raise GateFailure("X-Content-Type-Options is missing or invalid")
    if header(live_headers, "X-Frame-Options") != "DENY":
        raise GateFailure("X-Frame-Options is missing or invalid")

    ready_status, _, ready_body = request(f"{api}/health/ready")
    ready = json_object(ready_body, "/health/ready")
    if ready_status != 200 or ready.get("ready") is not True:
        raise GateFailure("readiness is not healthy")

    allowed_status, allowed_headers, _ = request(
        f"{api}/health/live",
        method="OPTIONS",
        headers={
            "Origin": web_origin,
            "Access-Control-Request-Method": "GET",
        },
    )
    if allowed_status >= 400 or header(allowed_headers, "Access-Control-Allow-Origin") != web_origin:
        raise GateFailure("configured storefront origin is not allowed by CORS")

    _, hostile_headers, _ = request(
        f"{api}/health/live",
        method="OPTIONS",
        headers={
            "Origin": "https://hostile.invalid",
            "Access-Control-Request-Method": "GET",
        },
    )
    if header(hostile_headers, "Access-Control-Allow-Origin") is not None:
        raise GateFailure("hostile origin received an Access-Control-Allow-Origin header")

    openapi_status, _, openapi_body = request(args.openapi_url)
    openapi = json_object(openapi_body, "OpenAPI document")
    if openapi_status != 200 or not isinstance(openapi.get("paths"), dict) or not openapi["paths"]:
        raise GateFailure("OpenAPI document is unavailable or contains no paths")

    capabilities_status, _, capabilities_body = request(
        f"{api}/api/admin/integrations/capabilities",
        headers={"Authorization": f"Bearer {token}"},
    )
    capabilities = json_object(capabilities_body, "admin integration capabilities")
    if capabilities_status != 200:
        raise GateFailure(f"admin integration capabilities returned HTTP {capabilities_status}")
    ensure_no_sensitive_fields(capabilities)

    failures: list[str] = []
    for capability_name in REQUIRED_CAPABILITIES:
        capability = capabilities.get(capability_name)
        if not isinstance(capability, dict):
            failures.append(f"{capability_name}:missing")
            continue
        if capability.get("enabled") is not True:
            failures.append(f"{capability_name}:disabled")
        if capability.get("liveReady") is not True:
            failures.append(f"{capability_name}:not-live-ready")
        if capability.get("healthStatus") not in {"Healthy", "Ready"}:
            failures.append(f"{capability_name}:health-not-verified")
    if failures:
        raise GateFailure("provider certification incomplete: " + ", ".join(failures))

    print("staging gate: health, headers, CORS, OpenAPI and provider certification passed")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--api-base-url", required=True)
    parser.add_argument("--web-base-url", required=True)
    parser.add_argument("--openapi-url", required=True)
    args = parser.parse_args()
    try:
        verify(args)
    except GateFailure as error:
        print(f"staging gate failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
