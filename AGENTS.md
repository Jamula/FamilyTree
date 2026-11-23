# FamilyTree instructions for coding agents

## Source of truth: GitHub issues

- **Track every distinct feature, bug, security, documentation or integration workstream as a GitHub issue before implementation.** Link the issue from every PR, and post evidence of implementation, CI, tests and outstanding dependencies back to the issue.
- Reuse an existing issue for related changes. Do **not** create dozens of redundant tracking issues for tiny substeps that belong to one scoped issue.
- Mark issues complete only after all promised acceptance criteria pass; otherwise mark remaining criteria and blockers explicitly. Do not confuse a merged foundation PR with a usable product feature.
- Keep separate M0, M1, M2 and M3 scopes visible in issue titles and PR descriptions. Work can run in parallel using independent branches, with privacy and CI as merge gates.

## Binding product policies
- Keep FamilyTree product version **0.1.0 until the owner accepts the private-family MVP**. The .NET SDK version is independent.
- **$0 incremental production hosting/infrastructure** unless a specific exception is authorized. GitHub/Copilot/Code Quality developer costs are shared engineering overhead, amortized across active projects, not hosting.
- Never commit real family records, photographs, dates, emails, contacts, genealogy exports, chats, passwords or secrets. Only fictional synthetic families may be used in public fixtures.
- Do not enable public family-tree access, real-data MCP tools, paid cloud resources or remote AI providers until membership, permissions, privacy tests, cost controls and explicit owner approval are complete.
- Preserve SHA-pinned Actions, required `build-test`, security scanning, Copilot review and privacy regression tests.

## MCP integration
- Local demo MCP is **stdio-only**, synthetic-only and cannot approve or persist genealogical edits. Never let an agent silently bypass human review. Remote ChatGPT/Copilot integrations need distinct issue tracking, scoped authentication and deployment security review.
