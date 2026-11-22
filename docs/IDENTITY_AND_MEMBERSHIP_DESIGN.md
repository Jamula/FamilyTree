# Identity and Membership Contract (Issue #9)

**Design only. Not implemented.** The genealogy Person record is independent of the application User identity and family membership. A relative can remain documented without ever registering.

## States
Invited -> PendingApproval -> Active -> Paused -> Left. Invitations expire. Rejoining requires authorization. Declining an invitation does not delete genealogy history.

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
