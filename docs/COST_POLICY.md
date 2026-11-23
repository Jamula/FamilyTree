# Mandatory $0 cost guardrail

**Default authorization: $0 per month and $0 one-time cost.** No contributor or automation may initiate a service, resource, subscription, deployment, feature or workflow that can incur charges without the project owner's **explicit prior permission**.

## Enforcement rules
1. Prefer local development and no-cost, open-source libraries and tools.
2. Public GitHub repository standard hosted CI runners are allowed under published free public-repository usage. Do not switch to larger, paid or private runners, or use billed artifact storage, without explicit approval.
3. Avoid creating Azure, Cloudflare, managed database, storage, monitoring, AI inference, or hosting resources unless their free-tier limits, billing settings and zero-cost behavior have been verified. A free tier is not an unconditional $0 guarantee.
4. Do not enable usage-based AI/code review/security add-ons, paid GitHub Code Quality, recurring paid monitoring, services with automatic overage charging, or payment methods for new services without consent.
5. Any proposed exception must be presented **before activation**, with the resource, reason, estimated one-time and monthly costs, worst-case exposure, quota controls, and free alternative. Proceed only after explicit approval.
6. Prefer soft/hard quotas, billing alerts and no-overage configurations where supported. Alerts **alone** do not prevent charges.
7. Cost approvals are specific to a given service, budget, and time period; never infer blanket future approval.
8. Documentation, synthetic tests and local development do not authorize production spend.

## CI specifics
The .NET 11 upgrade removes artifact uploads and Code Quality action dependencies from the required CI path to avoid chargeable or unverified optional features. Standard public-repository runner jobs remain. If repository visibility changes, reassess billing **before** running CI.

This is a project governance rule. It is not proof that existing resources outside the project cannot incur charges.
