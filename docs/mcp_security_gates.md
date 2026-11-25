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
- Capability negotiation intersects agent requests with explicit consent and the local synthetic capability set. Private reads, private proposals, draft review, draft approval and publication are always denied. This policy helper is not connected to tool dispatch and is not an authorization boundary; caller-supplied identity and consent are not authenticated authorization.
- Draft review and approval capabilities are explicitly denied and no review, approval or publication tools are exposed. The current proposal queue is process-global and in-memory; proposals remain pending and unapproved, and capability consent cannot promote or publish a draft.
- `ScopedDemoDraftStore` provides bounded, process-local partitions for fictional drafts with scope deletion, but current MCP proposal tools do not use it. Its generated scope IDs are not identity credentials, and the store is not wired to MCP client identity or durable storage.
- Prompt-like proposal text remains inert synthetic draft text. The local demo has no model or private-data path; these checks do not establish end-to-end prompt-injection or cross-family protections.

## Authenticated/durable integration dependencies
| Dependency | Existing partial contract | MCP work still blocked |
|---|---|---|
| #107 | Default-off loopback private API composition for verified OIDC sessions and persisted membership; live provider, Windows ACL and enabled-private-mode integration remain unverified | Bind an MCP session to the server-verified principal, pass family/branch-scoped authorization into each private operation, and verify identical unknown/forbidden responses. Do not treat MCP client metadata or `AgentId` as identity. |
| #117 | Partial durable person-mutation service with branch-scoped authorization, independent review and audit; private routes remain gated and end-to-end #107/session integration and broader mutation coverage remain outstanding | Only after #107 integration, adapt reviewed mutation contracts to MCP with a different authenticated approver, durable audit and restart/revocation tests. Draft proposals must not call this service directly or imply approval. |

This MCP lane remains partial M1 groundwork contributing to #46, not completion of these dependencies. Private tools, review/approval, publication, persistence and remote transport stay disabled until #9/#10/#12/#90 plus #107/#117 integrated acceptance and independent security review. No external service or paid resource is authorized; keep #46 open.
