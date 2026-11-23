#!/usr/bin/env python3
"""Adversarial tests of component coverage categorization and fail-closed gates."""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).with_name("report-component-coverage.py")

SAMPLE = """<coverage line-rate="1" branch-rate="1"><packages>
<package name="FamilyTree.Domain"><classes>
<class filename="src/FamilyTree.Domain/A.cs"><lines>
<line number="1" hits="1" branch="true" condition-coverage="50% (1/2)"/>
<line number="2" hits="0"/>
</lines></class>
<class filename="src/FamilyTree.Domain/A.cs"><lines>
<line number="1" hits="1"/>
</lines></class>
</classes></package>
<package name="FamilyTree.Api"><classes>
<class filename="src/FamilyTree.Api/Program.cs"><lines>
<line number="1" hits="1"/>
</lines></class></classes></package>
<package name="FamilyTree.Tests"><classes>
<class filename="tests/FamilyTree.Tests/Example.cs"><lines><line number="1" hits="1"/>
</lines></class></classes></package>
</packages></coverage>"""


def check(xml, expected, *params):
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "cov.xml"
        path.write_text(xml, encoding="utf-8")
        env = dict(os.environ, GITHUB_STEP_SUMMARY=str(Path(temp) / "summary.md"))
        result = subprocess.run([sys.executable, str(SCRIPT), str(path), *params],
                                capture_output=True, text=True, check=False, env=env)
        assert result.returncode == expected, (result.stdout, result.stderr, expected)
        return result.stdout + result.stderr


if __name__ == "__main__":
    output = check(SAMPLE, 0)
    assert "FamilyTree.Domain: lines=50.0% (2 lines)" in output, output
    assert "FamilyTree.Api: lines=100.0% (1 lines)" in output, output
    assert "FamilyTree.Web: lines=NOT MEASURED" in output, output
    assert "branches=50.0% (2 conditions)" in output, output
    check(SAMPLE, 0, "--min-component", "Api=90")
    check(SAMPLE, 1, "--min-component", "Domain=90")
    check(SAMPLE, 1, "--min-component", "Web=90")
    check(SAMPLE, 2, "--min-component", "Tests=90")
    check(SAMPLE, 2, "--min-component", "Domain=101")
    check(SAMPLE, 2, "--min-component", "Domain=nan")
    check(SAMPLE, 2, "--min-component", "Api=90", "--min-component", "Api=95")
    check(SAMPLE.replace('hits="0"', 'hits="-1"'), 2)
    check(SAMPLE.replace('number="2"', 'number="x"'), 2)
    check(SAMPLE.replace("50% (1/2)", "150% (3/2)"), 2)
    # Independent C# lambda/classes can share a file and source line while
    # contributing distinct conditional branches. Never overstate branch %.
    same_line = SAMPLE.replace('<class filename="src/FamilyTree.Domain/A.cs">',
        '<class name="Parent" filename="src/FamilyTree.Domain/A.cs">', 1)
    same_line = same_line.replace(
        '</classes></package>\n<package name="FamilyTree.Api">',
        '<class name="NestedLambda" filename="src/FamilyTree.Domain/A.cs">'
        '<lines><line number="1" hits="1" branch="true" '
        'condition-coverage="0% (0/2)"/></lines></class>'
        '</classes></package>\n<package name="FamilyTree.Api">', 1)
    branch_output = check(same_line, 0)
    assert "branches=25.0% (4 conditions)" in branch_output, branch_output
    check("<notcoverage/>", 2)
    check("<coverage", 2)
    print("PASS component coverage separation, duplicate source, missing components and malformed data")
