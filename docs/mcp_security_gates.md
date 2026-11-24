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

## Local capability groundwork
- MCP startup verifies that `FamilyDemoTools` is the only attributed MCP tool type and that it contains exactly the reviewed tool methods before SDK discovery; any additional type or method fails startup. Do not add private-data tools to the synthetic class.
- Capability negotiation intersects agent requests with explicit consent and the local synthetic capability set. Private reads, private proposals and publication are always denied; caller-supplied identity and consent are not authenticated authorization.
- `ScopedDemoDraftStore` provides bounded, process-local partitions for fictional drafts with scope deletion. Its generated scope IDs are not identity credentials, and the store is not wired to MCP client identity or durable storage.
- Prompt-like proposal text remains inert synthetic draft text. The local demo has no model or private-data path; these checks do not establish end-to-end prompt-injection or cross-family protections.

The local capability and draft scope work is partial M1/M2 groundwork only. Identity-bound scopes, verified consent, private read/write authorization, approval/audit, encrypted per-user persistence, production HTTP controls and deployment-time connector review remain blocked on #9, #10, #12, #15 and #90 plus independent security review. Keep #46 open.
