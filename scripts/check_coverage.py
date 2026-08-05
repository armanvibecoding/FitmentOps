#!/usr/bin/env python3
"""Fail a build when Cobertura coverage drops below the approved baseline."""

from __future__ import annotations

import argparse
import glob
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class LineKey:
    filename: str
    number: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--search-root", required=True)
    parser.add_argument("--min-line", type=float, default=70.0)
    parser.add_argument("--min-branch", type=float, default=50.0)
    return parser.parse_args()


def normalized_filename(value: str) -> str:
    return value.replace("\\", "/").lower()


def main() -> int:
    args = parse_args()
    pattern = str(Path(args.search_root) / "**" / "coverage.cobertura.xml")
    reports = [Path(path) for path in glob.glob(pattern, recursive=True)]
    if not reports:
        print(f"coverage gate: no Cobertura report under {args.search_root}", file=sys.stderr)
        return 2

    lines: dict[LineKey, int] = {}
    branches_valid = 0
    branches_covered = 0

    for report in reports:
        root = ET.parse(report).getroot()
        branches_valid += int(root.attrib.get("branches-valid", "0"))
        branches_covered += int(root.attrib.get("branches-covered", "0"))
        for class_node in root.findall("./packages/package/classes/class"):
            filename = normalized_filename(class_node.attrib.get("filename", ""))
            if "/migrations/" in f"/{filename}" or filename.endswith(".designer.cs"):
                continue
            for line_node in class_node.findall("./lines/line"):
                key = LineKey(filename, int(line_node.attrib["number"]))
                hits = int(line_node.attrib.get("hits", "0"))
                lines[key] = max(lines.get(key, 0), hits)

    if not lines:
        print("coverage gate: reports did not contain production lines", file=sys.stderr)
        return 2

    covered_lines = sum(1 for hits in lines.values() if hits > 0)
    line_rate = 100.0 * covered_lines / len(lines)
    branch_rate = (
        100.0 * branches_covered / branches_valid if branches_valid else 100.0
    )
    print(
        "coverage gate: "
        f"line={line_rate:.2f}% ({covered_lines}/{len(lines)}), "
        f"branch={branch_rate:.2f}% ({branches_covered}/{branches_valid}), "
        f"reports={len(reports)}"
    )

    failures: list[str] = []
    if line_rate < args.min_line:
        failures.append(f"line {line_rate:.2f}% < {args.min_line:.2f}%")
    if branch_rate < args.min_branch:
        failures.append(f"branch {branch_rate:.2f}% < {args.min_branch:.2f}%")
    if failures:
        print("coverage gate failed: " + ", ".join(failures), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
