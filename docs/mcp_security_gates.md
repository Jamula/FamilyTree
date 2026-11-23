# Agent and MCP security gates (#46)

## Threat boundary
The local stdio MCP process is a **fictional playground**, not a family database.
It has no tenant identity binding, user authentication, approval persistence or
production connector. Never expose it as an unauthenticated HTTP service.

## Pre-MVP threat model
| Attack | Required behavior | Verification |
|---|---|---|
| Malicious text/prompt-injection in a proposal | Treat as untrusted text; never execute, promote or publish | Bounds and explicit draft state only; full prompt-injection review pending |
| Cross-family ID enumeration | No existence hint, edges, media or count disclosures | M1 #9/#10 API authorization tests pending |
| Forged moderator identity | Review identity server-bound; no self approval | M1 #12 draft state machine tests |
| Lost member access | Invalidate effective grants on pause/leave | M1 #9 lifecycle tests pending |
| Sensitive field or image | Deny by default, consent + field permission | M1 #10 and M2 #14 gates |
| Rogue remote MCP client | Exact HTTPS origin/host allowlist, auth, scope, rate limits, per-agent capability | **Not implemented; prohibited remotely** |
| Draft overflow / control chars | Bounded queue and summaries, no content logging | MCP negative tests |

## Deployment gate
- No remote transport, real genealogy reads, persistent drafts, paid hosting or AI calls until identity, per-family/person/field authorization and a reviewed approval workflow are complete.
- Before remote access: test OIDC/JWT identity binding, denial equivalence, CSRF/Origin, TLS and host validation, per-agent scopes, data exfiltration, bounded responses, audit redaction and rate limiting.
- Keep provider tokens, all private datasets and raw prompts out of public source and telemetry.
- Reassess connector capabilities at deployment time. Do not assume an arbitrary localhost MCP server is accessible in a ChatGPT browser session.
