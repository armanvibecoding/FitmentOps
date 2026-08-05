#!/usr/bin/env python3
"""Conservative repository secret scanner that never prints matched values."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path


PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("private-key", re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----")),
    ("github-token", re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
    ("aws-access-key", re.compile(r"\bAKIA[0-9A-Z]{16}\b")),
    ("stripe-live-key", re.compile(r"\b(?:sk|rk)_live_[A-Za-z0-9]{16,}\b")),
    ("google-api-key", re.compile(r"\bAIza[0-9A-Za-z_-]{35}\b")),
    (
        "hardcoded-credential",
        re.compile(
            r"(?i)(?:password|passwd|secret|api[_-]?key|client[_-]?secret|access[_-]?token)"
            r"\s*(?::|=)\s*[\"'][^\r\n\"'${}<>]{8,}[\"']"
        ),
    ),
)

TEXT_SUFFIXES = {
    ".cs", ".csproj", ".json", ".js", ".jsx", ".ts", ".tsx", ".yml", ".yaml",
    ".xml", ".props", ".targets", ".md", ".txt", ".env", ".sh", ".ps1", ".py",
}

SAFE_CONTEXT = re.compile(
    r"(?i)(example|dummy|placeholder|ci-validation|test-only|sandbox|not-a-secret|redacted)"
)
SAFE_TEST_CREDENTIAL = re.compile(
    r"[\"'](?:sensitive-password-hash|not-a-real-password-hash|strong-password-123|"
    r"another-strong-password|test-password-hash|password-hash)[\"']"
)


def repository_files() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "-co", "--exclude-standard", "-z"],
        check=True,
        stdout=subprocess.PIPE,
    )
    return [Path(item.decode("utf-8")) for item in result.stdout.split(b"\0") if item]


def main() -> int:
    findings: list[tuple[Path, int, str]] = []
    for path in repository_files():
        if path.suffix.lower() not in TEXT_SUFFIXES or not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for line_number, line in enumerate(text.splitlines(), start=1):
            is_test_source = any(part.endswith(".Tests") for part in path.parts)
            if SAFE_CONTEXT.search(line) or (is_test_source and SAFE_TEST_CREDENTIAL.search(line)):
                continue
            for name, pattern in PATTERNS:
                if pattern.search(line):
                    findings.append((path, line_number, name))

    if findings:
        for path, line_number, name in findings:
            print(f"secret scan: {path}:{line_number}: {name}", file=sys.stderr)
        print(f"secret scan failed with {len(findings)} finding(s)", file=sys.stderr)
        return 1
    print("secret scan: no supported credential pattern found")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
