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
    "Domain", "Persistence", "Api", "Web", "Mcp", "ServiceDefaults", "SyntheticDemo",
)
SOURCE = re.compile(r"(?:^|/)src/FamilyTree\.([A-Za-z]+)/")
CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def is_true(value):
    normalized = value.strip().lower()
    if normalized in {"true", "1", "yes"}:
        return True
    if normalized in {"false", "0", "no"}:
        return False
    raise ValueError("Invalid branch flag")


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
    missing_branch_counts = defaultdict(int)
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
                # Each C# class/lambda may have independent decision paths
                # mapped to the same source line. Deduplicate line hits by
                # file/line but branch counts by class/file/line.
                branch_key = (cls.get("name", ""), *key)
                observations[component][key] = (
                    observations[component].get(key, False) or hits > 0)
                if is_true(line.get("branch", "false")):
                    match = CONDITION.search(line.get("condition-coverage", ""))
                    if not match:
                        missing_branch_counts[component] += 1
                        continue
                    covered, total = map(int, match.groups())
                    if total < 1 or covered < 0 or covered > total:
                        raise ValueError("Invalid branch condition observation")
                    previous = branch_counts[component].get(branch_key)
                    if previous is None:
                        branch_counts[component][branch_key] = (covered, total)
                    elif previous[1] != total:
                        raise ValueError("Conflicting branch totals on source line")
                    else:
                        branch_counts[component][branch_key] = (max(covered, previous[0]), total)
    # Some Cobertura producers supply only a global branch-rate, not per-line
    # condition counts. Mark an entire component NOT MEASURED in that case,
    # but never publish an inflated partial rate from a damaged mixed report.
    if missing_branch_counts:
        raise ValueError("Incomplete branch conditions for component")
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
    parser.add_argument("--min-component-branch", action="append", default=[],
                        metavar="NAME=RATE", help="Require NAME and its measured branch rate >= RATE percent")
    parser.add_argument("--min-overall-line", type=float, default=None,
                        help="Require measured source line coverage across all components")
    parser.add_argument("--min-overall-branch", type=float, default=None,
                        help="Require measured source branch coverage across all components")
    parser.add_argument("--allow-branchless", action="append", default=[],
                        metavar="NAME", help="Explicitly allow a measured component with no branches")
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
    branch_requirements = {}
    for rule in args.min_component_branch:
        component, sep, rate_text = rule.partition("=")
        if not sep or component not in COMPONENTS or component in branch_requirements:
            raise ValueError("Invalid or duplicate component branch coverage gate")
        try:
            rate = float(rate_text)
        except ValueError as exc:
            raise ValueError("Invalid branch coverage threshold") from exc
        if not 0 <= rate <= 100:
            raise ValueError("Branch coverage threshold outside 0..100")
        branch_requirements[component] = rate
    for component in args.allow_branchless:
        if component not in COMPONENTS:
            raise ValueError("Invalid branchless component justification")
    if args.min_overall_line is not None and not 0 <= args.min_overall_line <= 100:
        raise ValueError("Overall line coverage threshold outside 0..100")
    if args.min_overall_branch is not None and not 0 <= args.min_overall_branch <= 100:
        raise ValueError("Overall branch coverage threshold outside 0..100")

    print("Source-component coverage (distinct observed source lines):")
    failure = False
    all_lines = [value for component in COMPONENTS
                 for value in observations.get(component, {}).values()]
    all_branches = [value for component in COMPONENTS
                    for value in branch_counts.get(component, {}).values()]
    overall_line = sum(all_lines) / len(all_lines) if all_lines else None
    overall_branch_total = sum(value[1] for value in all_branches)
    overall_branch = (
        sum(value[0] for value in all_branches) / overall_branch_total
        if overall_branch_total else None)
    if args.min_overall_line is not None and (
            overall_line is None or overall_line * 100 < args.min_overall_line):
        print(f"  FAIL: overall source line coverage below {args.min_overall_line:.1f}%",
              file=sys.stderr)
        failure = True
    if args.min_overall_branch is not None and (
            overall_branch is None or overall_branch * 100 < args.min_overall_branch):
        print(f"  FAIL: overall source branch coverage below {args.min_overall_branch:.1f}%",
              file=sys.stderr)
        failure = True
    print(f"  Overall source coverage: lines="
          f"{overall_line * 100:.1f}% ({len(all_lines)} lines), branches="
          f"{overall_branch * 100:.1f}% ({overall_branch_total} conditions)"
          if overall_line is not None and overall_branch is not None
          else "  Overall source coverage: not measured")
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
        branch_threshold = branch_requirements.get(component)
        if branch_threshold is not None:
            if branch is None:
                if rate is None or component not in args.allow_branchless:
                    print(f"  FAIL: FamilyTree.{component} has no measured branches; "
                          "component source lines and an explicit branchless justification are required",
                          file=sys.stderr)
                    failure = True
                else:
                    print(f"  FamilyTree.{component}: branchless exemption (explicitly allowed)")
            elif branch * 100 < branch_threshold:
                print(f"  FAIL: FamilyTree.{component} lacks measured "
                      f"{branch_threshold:.1f}% branch coverage", file=sys.stderr)
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
                if (branch is None and rate is not None and component in args.allow_branchless
                        and component in branch_requirements):
                    branch_text += " (branchless exemption: explicitly allowed)"
                output.write(f"| FamilyTree.{component} | {count_lines} | {line_text} | {branch_text} |\n")
            output.write("\nUnmeasured means no instrumented source lines or branches, not 100%. "
                         "Smoke-test coverage is not included in these percentages.\n")
    return 1 if failure else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ET.ParseError, ValueError) as error:
        print(f"ERROR: invalid coverage data: {type(error).__name__}", file=sys.stderr)
        sys.exit(2)
