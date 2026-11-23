#!/usr/bin/env python3
"""Report measured Cobertura coverage; never infer coverage from test counts.

The optional command-line thresholds support enforcing a measured 90% line
coverage gate. Branch coverage is reported separately.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def parse_ratio(text):
    try:
        value = float(text)
    except (TypeError, ValueError) as error:
        raise ValueError("Cobertura rate missing or malformed") from error
    if not 0 <= value <= 1:
        raise ValueError("Cobertura ratio outside 0..1")
    return value


def summarize(path):
    root = ET.parse(path).getroot()
    if root.tag != "coverage":
        raise ValueError("Expected Cobertura coverage root")
    overall_line = parse_ratio(root.attrib.get("line-rate"))
    overall_branch = parse_ratio(root.attrib.get("branch-rate"))
    packages = root.findall("./packages/package")
    domain_lines = []
    domain_branches = []
    for package in packages:
        package_name = package.attrib.get("name", "")
        for cls in package.findall("./classes/class"):
            filename = cls.attrib.get("filename", "")
            # Match assembly or project file paths; do not count tests as domain.
            if "FamilyTree.Domain" not in package_name and "FamilyTree.Domain" not in filename:
                continue
            for line in cls.findall("./lines/line"):
                domain_lines.append(int(line.attrib.get("hits", "0")) > 0)
                if line.attrib.get("branch", "false") == "true":
                    # Cobertura condition-coverage typically says "(x/y)".
                    import re
                    found = re.search(r"\((\d+)/(\d+)\)", line.attrib.get("condition-coverage", ""))
                    if found:
                        hit, total = int(found.group(1)), int(found.group(2))
                        if total <= 0 or not 0 <= hit <= total:
                            raise ValueError("Invalid branch coverage counts")
                        domain_branches.extend([True] * hit + [False] * (total - hit))
    domain_ratio = sum(domain_lines) / len(domain_lines) if domain_lines else None
    branch_ratio = sum(domain_branches) / len(domain_branches) if domain_branches else None
    return overall_line, overall_branch, domain_ratio, branch_ratio, len(domain_lines)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path)
    parser.add_argument("--min-overall", type=float, default=0)
    parser.add_argument("--min-domain", type=float, default=0)
    args = parser.parse_args()
    if not args.path.is_file():
        raise ValueError("Missing coverage report")
    overall, branches, domain, domain_branches, count = summarize(args.path)
    rows = [
        ("All instrumented assemblies, lines", overall),
        ("All instrumented assemblies, branches", branches),
        ("FamilyTree.Domain, lines", domain),
        ("FamilyTree.Domain, branches (when supplied)", domain_branches),
    ]
    for title, value in rows:
        print(f"{title}: {value * 100:.1f}%" if value is not None else f"{title}: not measured")
    print(f"Domain source line observations: {count}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write("\n### FamilyTree measured test coverage\n\n")
            stream.write("| Coverage scope | Measured |\n|---|---:|\n")
            for title, value in rows:
                value_text = f"{value * 100:.1f}%" if value is not None else "Not measured"
                stream.write(f"| {title} | {value_text} |\n")
            stream.write("\n90% line coverage floor is enabled on CI; branch coverage remains a separate target.\n")
    if overall * 100 < args.min_overall:
        print(f"ERROR: overall line coverage below {args.min_overall:.1f}% floor", file=sys.stderr)
        return 1
    if args.min_domain > 0 and (domain is None or domain * 100 < args.min_domain):
        print(f"ERROR: Domain line coverage missing or below {args.min_domain:.1f}% floor", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ET.ParseError, OSError, ValueError) as exc:
        print(f"ERROR: invalid coverage report: {type(exc).__name__}", file=sys.stderr)
        sys.exit(2)
