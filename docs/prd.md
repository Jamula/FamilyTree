# FamilyTree Product Requirements Document
Status: living draft, 2026-11-22

## Value proposition
A private-by-default, family-owned place to preserve ancestry, relationships, memories and gratitude, with standards-based portability and optional AI assistance. It must serve both self-hosted communities and a hosted freemium global-family service without locking in user data.

## Personas
- Unjoined person: may be represented in an authorized genealogy record; cannot browse/edit.
- Invited visitor: accepts invitation and requests access.
- Active member: explores authorized tree, adds proposed people and connections, contributes memories, gives gratitude.
- Branch steward: reviews changes to assigned branches only.
- Administrator: manages invitations, approvals and privacy policies; cannot approve their own claims.
- Self-hosting operator: controls hosting, exports, backups and AI provider.

## P0
1. Invitation-only, optional membership with join, pause, leave and identity-linked profile claim.
2. Person-centered ancestors/descendants, defaults: 4 above, 4 below, spouses on, sibling branches on, social connections off. User can recenter or change each toggle.
3. Unrelated people and multiple independent family/social links between any two people.
4. GEDCOM 7 import/export with legacy GEDCOM compatibility assessment, source provenance, round-trip test corpus.
5. Admin-reviewed suggestions for persons, relationships, events and corrections, with audit and rollback.
6. Server-side per-family/per-person/per-field/media authorization. Unauthorized existence must not leak via searches, trees, AI or exports.
7. Public-source repository; no real genealogy fixtures, accounts, credentials or live data in source.

## P1
Gratitude and private memories; photographs and life events; opt-in AI copilot (permission-filtered, cited, human-approved edits); product feedback and triage; hosted freemium pilot.

## P2
Installable PWA and mobile experience; sponsorship via GitHub Sponsors / direct donations; optional extension marketplace and carefully consented cross-family discovery.

## Platform and business model
One .NET-first codebase, portable APIs, OIDC/OAuth 2.0 with PKCE, tenant isolation, container-friendly self-hosting, hosted freemium with transparent quotas and paid features that do not compromise data ownership. No requirement to send private records to an AI model.

## Non-functional acceptance gates
- Deny-by-default access policy verified for all read, query, traversal, media, export, AI and notification paths.
- Secure deletion/export, audit, purpose limits, retention, consent, account erasure request workflows.
- WCAG 2.2 AA target, mobile responsiveness, observability without private content logging.
- No public release until threat modeling, privacy review, GDPR/US state privacy compliance review, backup/restore drills and abuse handling.
- Source-controlled requirements trace to executable tests before release.

## Success measures
Opt-in activation, family stories preserved, verified contributions, tree navigation success, privacy incidents (target zero), import round-trip fidelity, member feedback resolution time.
