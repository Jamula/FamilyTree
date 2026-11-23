#!/usr/bin/env python3
"""Enforce the owner's 0.1.0 version freeze until the private-family MVP is accepted.

MVP completion requires a separately approved change to this guard and the
coordinated release files. The framework SDK version is independent.
"""
from pathlib import Path
from xml.etree import ElementTree
import sys

root = Path(__file__).resolve().parents[1]
expected = "0.1.0"
actual = (root / "VERSION").read_text(encoding="utf-8").strip()
props = ElementTree.parse(root / "Directory.Build.props").getroot()
checks = {
    "VERSION": actual == expected,
    "Assembly product Version": props.findtext(".//Version") == expected,
    "AssemblyVersion": props.findtext(".//AssemblyVersion") == expected + ".0",
    "FileVersion": props.findtext(".//FileVersion") == expected + ".0",
}
failed = [key for key, ok in checks.items() if not ok]
if failed:
    print("FAIL: FamilyTree must remain 0.1.0 until the M1 MVP is accepted:")
    for key in failed:
        print(" - " + key)
    sys.exit(1)
print("PASS: FamilyTree product version remains 0.1.0 until MVP.")
