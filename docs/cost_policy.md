# Cost policy: $0 hosting, shared engineering costs

## Binding limits

**Hosting and production infrastructure: $0 incremental recurring and $0 incremental one-time spend.** FamilyTree must remain runnable in a local or self-hosted mode with no mandatory paid managed services. Do not provision paid Azure/Cloudflare compute, databases, object storage, monitoring, CDN, hosted AI inference, managed email, or similar infrastructure without explicit advance owner approval of the particular service and amount.

**Development and engineering tooling: separate, shared budget.** GitHub Actions, GitHub Code Quality, Copilot, security scanning, coding agents, developer subscriptions, and other engineering tools are development expenses, **not** FamilyTree hosting fees. Such expenses may be incurred within the owner's existing tools and entitlements and allocated across the projects that benefit. The user has authorized considering these costs amortized across projects, **not** an unlimited or unspecified new purchase. Before activating a new paid plan, enabling overage billing, or increasing a paid license/credit limit, present pricing and seek explicit owner approval.

## Allocation and accounting

1. Charge usage directly to the attributable repository/project where reliable per-repository usage or invoices exist.
2. Amortize shared organization-wide licenses/subscriptions across the *active projects using the tool* for the relevant month. Default to an equal allocation across those projects in the absence of better, verifiable usage data; document the project count, denominator and accounting period.
3. Where usage data is available (CI runner minutes, AI credits, contributor licenses), prefer a documented usage-weighted allocation over arbitrary equal splits.
4. **Never count a shared GitHub license as if it were exclusively a FamilyTree hosting charge.** Also do not count the same charge twice across projects.
5. Report actual billed amounts separately from forecasts, free credits, and estimates. Do not fabricate project costs when invoices or utilization are unavailable.

## Operational guardrails

- Public repository standard GitHub Actions runners may be used under applicable free usage. Additional developer tooling can be considered under the shared engineering budget, but paid changes require specific prior approval.
- Maintain SHA-pinned Actions, required CI/security tests, a reproducible build, and independent code-quality checks.
- Local .NET Aspire and opt-in loopback OpenTelemetry development tools must not provision cloud resources. Remote OTLP remains disabled by default.
- Do not assume any free cloud tier is always costless: reject auto-overage and usage-billed runtime resources unless an enforceable $0 configuration is verified.
- Billing alerts alone are **not** hard spending caps.
- Before any hosting exception: state the service, purpose, alternative, expected monthly/one-time charges, worst-case overage exposure, enforceable cap, rollback and obtain explicit owner authorization.
- Reassess development-tool allocation if GitHub repository visibility or organization entitlements change.

## CI policy

`scripts/verify-zero-cost.py` is a conservatively named legacy guard for repository workflow sources. It checks SHA-pinned actions, permitted runners and common *remote deployments*. It may still flag unreviewed artifact/Code Quality uploads; this is an engineering-budget approval checkpoint, **not** a claim that all development costs must be $0 or that Code Quality is hosting. No source-level check proves an organization's actual invoice or that any deployed hosting resource is free.

This is project governance. It neither establishes nor authorizes actual existing charges outside FamilyTree.
