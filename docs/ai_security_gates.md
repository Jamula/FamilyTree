# AI copilot security gates (#15)

## Current boundary

The AI contracts are preparatory only. The default provider is disabled, the
test provider accepts synthetic fixtures only, and the proposal gate creates
an unapproved in-memory draft. There is no live model, retrieval pipeline,
provider configuration, persistence, approval, or publication path. Do not
send real family data, member content, secrets, or production records to a
provider. Keep this issue open until the gates below and the original
acceptance criteria are complete.

## Threat model

| Threat | Required control | Current status |
|---|---|---|
| Prompt injection in user text, profiles, citations, or retrieved documents | Treat all source text and model output as untrusted data; do not execute tool requests or treat instructions in retrieved content as policy | Synthetic contract and draft-only assertions; production retrieval/injection defenses remain unimplemented |
| Hidden-person or cross-branch disclosure | Retrieve only through server-side membership, branch, person, relationship, and field authorization; return generic denials | Draft gate checks cited people and relationships; no AI retrieval pipeline exists |
| Model output directly changing records | Keep output as a pending proposal; require the existing human-reviewed approval path and prohibit provider-side writes | Current gate only stages an unapproved draft; no AI publication integration |
| Sensitive prompt or context retained, logged, or used for training by a provider | No provider until retention, training, deletion, region, and subprocessors are reviewed and explicitly approved; minimize/redact inputs and prohibit prompt/content telemetry | No provider is configured; provider-retention review is outstanding |
| Hosted provider exfiltration or compromised provider | Make hosted and local adapters opt-in and replaceable; send only authorized minimum context; isolate provider credentials and never expose them to prompts or clients | Provider-neutral interface only; no hosted/local adapter or credentials |
| Unbounded or malformed inputs and outputs | Apply strict size/type limits, validate outputs as drafts, and fail closed on errors | Synthetic input bounds and proposal text bounds are tested; production response validation remains outstanding |
| False provenance or fabricated citations | Attach citations from authorized source records, preserve provenance, and independently validate each cited subject and relation before display or review | Cited person/relation authorization exists in the draft gate; source retrieval, provenance attachment, and citation rendering are not implemented |

## Enablement conditions

AI must remain disabled for real data until M02 membership/privacy acceptance,
server-side permission-filtered retrieval with provenance, prompt-injection
and data-exfiltration tests, provider retention approval, and explicit owner
approval are complete. No remote inference, paid service, persistent AI draft,
or automatic publication is authorized by this preparatory work. The
`permissionChecked` flag on the synthetic test gate is not an authorization
proof and must not be used as a production security boundary.
