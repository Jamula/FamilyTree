#!/usr/bin/env python3
"""Small self-contained parser regression tests (zero extra test packages)."""
import os
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("report-coverage.py")


def check(xml, expected, *extra):
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "coverage.xml"
        path.write_text(xml, encoding="utf-8")
        run = subprocess.run([sys.executable, str(SCRIPT), str(path), *extra],
                             capture_output=True, text=True, check=False,
                             env={**os.environ, "GITHUB_STEP_SUMMARY": str(Path(temp) / "summary.md")})
        assert run.returncode == expected, (run.stdout, run.stderr, expected)
        return run.stdout


GOOD = """<coverage line-rate="0.5" branch-rate="0.25"><packages>
<package name="FamilyTree.Domain"><classes><class filename="src/FamilyTree.Domain/Example.cs"><lines>
<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/>
<line number="2" hits="0"/>
</lines></class></classes></package>
</packages></coverage>"""


if __name__ == "__main__":
    text = check(GOOD, 0)
    assert "50.0%" in text and "25.0%" in text
    assert "Domain source line observations: 2" in text
    check(GOOD, 1, "--min-domain", "90")
    check(GOOD, 1, "--min-overall", "90")
    check(GOOD, 0, "--min-overall-branch", "20")
    check(GOOD, 1, "--min-overall-branch", "30")
    check("<notcoverage/>", 2)
    check("<coverage line-rate='x' branch-rate='0'/>", 2)
    check("<coverage line-rate='2' branch-rate='0'/>", 2)
    check("<coverage", 2)
    check(GOOD.replace("50% (1/2)", "150% (3/2)"), 2)
    check(GOOD.replace("50% (1/2)", "0% (0/0)"), 2)
    print("PASS coverage-report parser, thresholds and malformed-report scenarios")
