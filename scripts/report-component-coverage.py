#!/usr/bin/env python3
"""Measured Cobertura coverage by source assembly, not a repository-wide proxy.

Ignores tests and generated files. Counts each source line once even when
multiple classes in a source file report the same source line. Distinguishes
unmeasured components from 0% measured coverage. No coverage is fabricated.
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

COMPONENTS = (
    "Domain", "Api", "Web", "Mcp", "ServiceDefaults", "SyntheticDemo",
)
SOURCE = re.compile(r"(?:^|/)src/FamilyTree\.([A-Za-z]+)/")
CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def component_of(filename, package):
    clean = filename.replace("\\", "/")
    if "/obj/" in clean or "/bin/" in clean or "/tests/" in clean:
        return None
    match = SOURCE.search(clean)
    if match and match.group(1) in COMPONENTS:
        return match.group(1)
    for part in COMPONENTS:
        if package == f"FamilyTree.{part}" or package.startswith(f"FamilyTree.{part}."):
            return part
    return None


def count(source):
    root = ET.parse(source).getroot()
    if root.tag != "coverage":
        raise ValueError("Cobertura root missing")
    # Distinct source lines, not classes: C# nested/partial classes overlap.
    observations = defaultdict(dict)
    branch_counts = defaultdict(dict)
    for package in root.findall("./packages/package"):
        package_name = package.get("name", "")
        for cls in package.findall("./classes/class"):
            filename = cls.get("filename", "")
            component = component_of(filename, package_name)
            if component is None:
                continue
            for line in cls.findall("./lines/line"):
                try:
                    number = int(line.attrib["number"])
                    hits = int(line.attrib["hits"])
                except (ValueError, KeyError) as exc:
                    raise ValueError("Invalid source line observation") from exc
                if number < 1 or hits < 0:
                    raise ValueError("Invalid source line number or hit count")
                key = (filename.replace("\\", "/"), number)
                observations[component][key] = (
                    observations[component].get(key, False) or hits > 0)
                if line.get("branch", "false") == "true":
                    match = CONDITION.search(line.get("condition-coverage", ""))
                    if match:
                        covered, total = map(int, match.groups())
                        if total < 1 or covered < 0 or covered > total:
                            raise ValueError("Invalid branch condition observation")
                        previous = branch_counts[component].get(key)
                        if previous is None:
                            branch_counts[component][key] = (covered, total)
                        elif previous[1] != total:
                            raise ValueError("Conflicting branch totals on source line")
                        else:
                            branch_counts[component][key] = (max(covered, previous[0]), total)
    return observations, branch_counts


def summarize(observations, branch_counts):
    rows = []
    for component in COMPONENTS:
        lines = observations.get(component, {})
        branches = branch_counts.get(component, {})
        rate = sum(lines.values()) / len(lines) if lines else None
        covered = sum(x[0] for x in branches.values())
        total = sum(x[1] for x in branches.values())
        branch_rate = covered / total if total else None
        rows.append((component, rate, len(lines), branch_rate, total))
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path)
    parser.add_argument("--min-component", action="append", default=[],
                        metavar="NAME=RATE", help="Require NAME and its measured line rate >= RATE percent")
    args = parser.parse_args()
    if not args.path.is_file():
        raise ValueError("Coverage XML file missing")
    observations, branch_counts = count(args.path)
    rows = summarize(observations, branch_counts)
    requirements = {}
    for rule in args.min_component:
        component, sep, rate_text = rule.partition("=")
        if not sep or component not in COMPONENTS or component in requirements:
            raise ValueError("Invalid or duplicate component coverage gate")
        try:
            rate = float(rate_text)
        except ValueError as exc:
            raise ValueError("Invalid coverage threshold") from exc
        if not 0 <= rate <= 100:
            raise ValueError("Coverage threshold outside 0..100")
        requirements[component] = rate

    print("Source-component coverage (distinct observed source lines):")
    failure = False
    for component, rate, count_lines, branch, count_branches in rows:
        line_text = f"{rate*100:.1f}%" if rate is not None else "NOT MEASURED"
        branch_text = f"{branch*100:.1f}%" if branch is not None else "NOT MEASURED"
        print(f"  FamilyTree.{component}: lines={line_text} ({count_lines} lines), "
              f"branches={branch_text} ({count_branches} conditions)")
        threshold = requirements.get(component)
        if threshold is not None and (rate is None or rate * 100 < threshold):
            print(f"  FAIL: FamilyTree.{component} lacks measured {threshold:.1f}% line coverage",
                  file=sys.stderr)
            failure = True

    summary = os.getenv("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write("\n### Measured source-component coverage\n\n")
            output.write("| Component | Source lines | Line coverage | Branch coverage |\n")
            output.write("|---|---:|---:|---:|\n")
            for component, rate, count_lines, branch, _ in rows:
                line_text = f"{rate*100:.1f}%" if rate is not None else "Not measured"
                branch_text = f"{branch*100:.1f}%" if branch is not None else "Not measured"
                output.write(f"| FamilyTree.{component} | {count_lines} | {line_text} | {branch_text} |\n")
            output.write("\nUnmeasured means no instrumented source lines, not 100%. "
                         "Smoke-test coverage is not included in these percentages.\n")
    return 1 if failure else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"ERROR: invalid coverage data: {type(error).__name__}", file=sys.stderr)
        sys.exit(2)
