#!/usr/bin/env python3
"""Guard hosting at $0 and require review for new development-tool cost risks.

This is a *source-control* guard, not a billing cap or a substitute for checking
GitHub account invoices and third-party pricing. Does not access credentials.
"""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
WORKFLOWS = ROOT / ".github" / "workflows"
errors: list[str] = []

for file in sorted(WORKFLOWS.glob("*.y*ml")):
    contents = file.read_text(encoding="utf-8")
    # SHA pinning is required by the Jamula organization action policy.
    for line_number, line in enumerate(contents.splitlines(), 1):
        action = re.search(r"^\s*(?:-\s*)?uses:\s*(\S+)", line)
        if action:
            reference = action.group(1)
            if reference.startswith("./"):
                continue  # Local actions are part of this repository.
            if not re.fullmatch(r"[^@\s]+@[0-9a-fA-F]{40}", reference):
                errors.append(f"{file.relative_to(ROOT)}:{line_number}: action must use a verified full 40-character SHA")
            if re.search(r"/(?:upload-artifact|download-artifact|upload-code-coverage)@", reference, re.I):
                errors.append(f"{file.relative_to(ROOT)}:{line_number}: new development artifact/Code Quality upload requires review of shared engineering budget")
        runner = re.search(r"^\s*runs-on:\s*(.*)", line)
        if runner and runner.group(1).strip(" '\"") != "ubuntu-latest":
            errors.append(f"{file.relative_to(ROOT)}:{line_number}: unsupported runner, must verify $0 first")

    # No cloud resources or remote AI inference should be provisioned by a PR
    # workflow. These patterns only cover workflow automation, not all possible spend.
    for pattern in [r"\baz\s+deployment\b", r"\baz\s+containerapp\b",
                    r"\bterraform\s+apply\b", r"\bwrangler\s+deploy\b",
                    r"\bgh\s+codespace\b", r"\bvercel\s+deploy\b"]:
        if re.search(pattern, contents, re.I):
            errors.append(f"{file.relative_to(ROOT)}: deployment operation requires an explicit owner-approved plan")

if errors:
    print("WORKFLOW POLICY FAILED. Unapproved hosting or development-tool cost risk:")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print("PASS workflow source checks: SHA pins, reviewed runners, no configured remote hosting deployments.")
print("NOTE: development tool charges are shared engineering overhead, not FamilyTree hosting. This check does not read invoices or prove actual costs.")
