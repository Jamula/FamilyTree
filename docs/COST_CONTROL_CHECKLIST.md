# FamilyTree $0 approval gate

**No service can be enabled with a possible new charge without prior, explicit owner authorization.** The default permitted incremental spending is $0 monthly and $0 one-time. Use only synthetic data in public CI.

## Before enabling or changing a service

| Question | Mandatory proof |
|---|---|
| What is the proposed change? | Service, scope, credentials/access, and purpose |
| Free option? | Document a free/local option and limits |
| Could it incur charges? | Pricing link, usage caps, overage controls, billing thresholds |
| Worst case? | State whether a hard cap is possible; estimate the monthly and one-off exposure |
| Authorization | Obtain explicit owner confirmation for a specific limit and period before activation |
| Rollback | Disable plan, resource deletion, restore data without dependence on service |

## Source gate and CI

Run `python3 scripts/verify-zero-cost.py` to validate GitHub workflow source. The script rejects mutable action refs, unapproved non-standard runners, artifact/Code Quality uploads, and certain cloud deployment commands. It checks **source only**, not active cloud resources or GitHub account billing.

Public-repository standard GitHub Actions can be free, but no claim is made about charges from GitHub Code Quality, Copilot, third-party apps or existing Azure/Cloudflare accounts. The owner states Copilot reviews are free for their setup, which is not an approval for any new paid plan or additional AI service.

## Deployment and observability

- Aspire local orchestration does not provision remote cloud resources.
- OpenTelemetry exporters default **off**. Development output may target a local Aspire dashboard only. Do not send private data to vendor backends.
- Remote hosted resources and production data are not authorized until security, privacy, and hard cost controls are verified.
- Never regard cost alerts as guaranteed spending caps.

## Owner escalation template
Provide: provider/resource; goal; quoted pricing; verified free tier; likely and worst-case monthly charges; hard cap availability; $0 alternative; stop/rollback steps. Ask: **"Do you approve enabling [specific resource] with a spending limit of [amount] for [period]?"** Do not proceed unless explicitly approved.
