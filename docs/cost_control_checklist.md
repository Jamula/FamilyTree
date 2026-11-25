# FamilyTree cost approval and allocation checklist

**Hard target: $0 incremental hosting and production infrastructure.** Development spending is a distinct, shared engineering category amortized among projects.

| Category | Example | Policy |
|---|---|---|
| Hosting/production | Cloud compute, hosted database, runtime storage, production monitoring, paid inference | $0 until explicitly approved otherwise; no overages |
| Development tooling | GitHub Actions, Code Quality, Copilot, developer subscriptions, test services | Shared engineering expense; existing entitlements allowed, new paid changes require specific approval |
| Local/self-hosted | .NET, Aspire, local SQLite, loopback OpenTelemetry | No managed hosting cost; user-supplied hardware/electricity are not represented as a cloud invoice |
| Domain/other existing shared items | Registrar, organization's existing utilities | Account separately as applicable; do not invent or reclassify charges as FamilyTree usage |

## Amortize engineering tools accurately

1. Collect the actual GitHub organization invoice, billable license counts, usage, credits, and projects sharing each service.
2. Attribute metered usage to the exact repository if documented. Allocate a shared subscription evenly across the active using projects for that billing period if better usage evidence is unavailable. Revisit the allocation when usage becomes measurable.
3. Record a single source charge once across all projects, and distinguish actual, estimate and fully credited usage.
4. Document monthly **development overhead** separately from FamilyTree's **hosting target of $0**.
5. GitHub Code Quality may be a useful paid development tool, but showing successful scan runs does not verify whether it's billed. The user's desired adoption is not authorization for a specific additional subscription, new overage, or uncapped AI usage.

## Before enabling a paid *development* change

Provide product, projected license/AI credit usage, actual incremental fee, shared-project allocation method, hard-stop availability, rollback, and ask for approval of the specific change. Existing services may continue under existing entitlements.

## Before any *hosting* change

Confirm $0 incremental cost and a hard stop on overages. If not enforceably $0, do not deploy without explicit exception approval stating the maximum one-time and recurring amount, service and time period. An alert alone is not a spending cap.

## CI check

`python3 scripts/verify-zero-cost.py` checks workflow-source hygiene and rejects remote deployment commands, mutable GitHub Action versions and some unreviewed cost-incurring workflows. It does not read account invoices or determine hosted infrastructure costs.

## Release gate

A foundation-only GitHub release **does not** create a hosted deployment. No production family data is allowed before M02 authentication, server-side authorization, security and consent controls. Publishing `v0.1.0` requires a separate explicit release decision.
