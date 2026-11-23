# Identity and Membership Contract (Issue #9)

**Design only. Not implemented.** The genealogy Person record is independent of the application User identity and family membership. A relative can remain documented without ever registering.

## Membership lifecycle and permitted transitions

Invitations and membership records are distinct objects. Invitation expiry or decline does not affect the underlying Person genealogy record.

| From | To | Trigger and required authorization |
|---|---|---|
| No membership | Invited | Family administrator creates a scoped, expiring invitation |
| Invited | PendingApproval | Invitee opts in, authenticates with an allowed OIDC identity, and accepts the membership terms |
| Invited | No membership | Invitee declines or invitation expires; retain only necessary invite audit data |
| PendingApproval | Active | Independent authorized family administrator approves requested scope |
| PendingApproval | No membership | Authorized administrator rejects, or applicant withdraws; preserve a safe audit record |
| Active | Paused | Member requests temporary pause, or administrator suspends access |
| Active | Left | Member voluntarily leaves, or an authorized administrator revokes membership |
| Paused | Active | Member requests resumption; allowed only when prior grants remain valid and no admin suspension is active, otherwise require new approval |
| Paused | Left | Member chooses to leave or administrator revokes membership |
| Left | PendingApproval | Former member presents a **fresh, valid, administrator-issued invitation** and submits a new application; no direct reapplication is permitted, and previous grants are not automatically restored |

**All access-losing transitions must invalidate effective permissions and cached authorization decisions immediately.** No token can authorize a revoked grant merely because its signature and expiration remain valid. An identity linked to multiple families or branches loses only the grants for the affected membership.

Rejoining requires a fresh administrator-issued invitation followed by independent administrator approval; nobody can self-approve access or profile claims. The membership-request endpoint rejects requests that cannot prove a valid, unused invitation. A member's historical genealogy identity is preserved subject to data-rights processes.


## Authorization
OIDC identity + PKCE where applicable; API validates issuer, audience, expiry, signature and group membership. An authenticated login alone never grants record access. Use explicit tenant/family/branch grants; deny by default.

## Profile claim
A member can propose claiming an existing Person; claim remains pending until an independent authorized administrator verifies evidence. Self approval is forbidden. Audits record claim state transitions without storing uploaded evidence publicly.

## Proposed endpoints
- POST /api/v1/invitations/accept: accepts authenticated invite and requests membership.
- POST /api/v1/membership-requests: creates pending opt-in request.
- GET /api/v1/me/memberships: lists only caller's memberships.
- POST /api/v1/profile-claims: proposes a claim.
- POST /api/v1/admin/profile-claims/{id}/decisions: approved/rejected by authorized nonclaimant.
- POST /api/v1/me/memberships/{id}/leave: revoke access and invalidate sessions.
No endpoint above should be enabled before authentication and authorization are implemented.

## Key tests
U01–U06, U20, U33; expired invitation, duplicate claim, self approval, unauthenticated request, revoked membership, unauthorized cross-tenant claim, safe audit.
