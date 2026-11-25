# PWA, mobile and sponsorship readiness (#17)

This is preparatory work for the M3 expansion issue, not a release or activation
decision. The web app currently has no static-asset pipeline or browser install
flow. The manifest and icon in `src/FamilyTree.Web/wwwroot` are an isolated
starting contract; they are not linked to the landing page or served by the
current app. The manifest contract assertion does not prove browser
installability. Keep issue #17 open until the integration and acceptance gates
below pass.

## Offline and privacy boundary

- Do not add a service worker, offline data store, or background sync as part
  of this preparation. No family, person, relationship, media, membership,
  search, export, or authentication data may enter Cache Storage, IndexedDB,
  local storage, or a service-worker queue.
- Until an explicit privacy review and tests approve otherwise, private
  responses must remain network-only and fail closed when offline. Do not
  present stale family information as current or available after sign-out,
  membership revocation, or family switching.
- If an offline shell is later approved, limit it to versioned, public,
  non-personal application assets. Bypass all private routes and APIs, and
  test cache deletion on sign-out, revocation, and app update.
- Never place private media in `wwwroot`; use protected, authorized storage
  as required by the existing media boundary.

## Mobile and accessibility audit plan

The current app is a landing page rather than a complete family-tree UI, so
this is an audit plan, not a claim that the app passed an accessibility audit.
Before calling mobile readiness complete, test at 320 CSS pixels and common
phone/tablet viewports; verify portrait and landscape layouts, 200% zoom,
visible focus, keyboard-only operation, screen-reader names and status
announcements, contrast, reduced motion, and no unintended horizontal
scrolling. Keep controls comfortably touchable (target 44 by 44 CSS pixels
where practical) and preserve an accessible list or table alternative for
any pan/zoom tree visualization. Do not rely on color, dragging, or gestures
alone to perform an action.

Browser acceptance tests should cover keyboard, pointer, and touch flows
alongside the existing synthetic-only privacy cases. In particular, test
mobile tree navigation, focus retention, family switching, denied access, and
offline behavior without loading real genealogy data. Target WCAG 2.2 AA.

## Installation test gates

The added assertion checks the manifest identity, standalone display, root
launch URL, and bundled icon. It intentionally does not claim installation
works. Once the app owners integrate static assets and link the manifest,
add browser tests on supported mobile and desktop browsers to verify:

1. The manifest and icon load from the intended origin and the browser offers
   installation in a secure context.
2. Installing and launching opens the expected landing experience; uninstall
   and reinstall do not reveal cached private data.
3. Offline navigation exposes no private content or queued mutations; online
   retry and denied requests remain safe.
4. Keyboard, touch, viewport, and screen-reader checks pass on the installed
   experience.

## Native packaging feasibility

Prefer evaluating the PWA path before native packaging because this repository
has a minimal .NET server-rendered web app and no native client. A future
feasibility spike may compare a standards-based PWA with a .NET MAUI shell or
other thin wrapper only after the authenticated web UI and privacy boundaries
exist. Evaluate platform support, accessible controls, secure sign-in,
deep-link handling, update/revocation behavior, store review, maintenance, and
incremental cost. This work adds no native project, package, store account, or
paid service; any implementation decision needs separate owner approval.

## Sponsorship and donation boundaries

The repository already has GitHub Sponsors metadata in `.github/FUNDING.yml`;
this preparation does not change it, add direct-donation links, or integrate a
payment service. The presence of repository funding metadata is not approval
for in-product payment collection or access changes.

Any future sponsorship or voluntary direct donation must be independent of
family membership and authorization. Payment must never buy access to
restricted family information, membership, priority review, or a way around
human approval. Do not request or include family information in a donation
form, receipt, public acknowledgment, or supporter list.

Before offering direct donations, obtain explicit owner approval for the
provider, fees, jurisdictional/legal and tax review, refund handling, receipt
responsibility, and a $0 incremental hosting boundary. Prefer a hosted
provider's payment and receipt handling; the FamilyTree app and repository
must not collect card or bank credentials or add billing/customer records.
Disclose who receives the donation, what it supports, whether it is
recurring, applicable fees/refund terms, and that no restricted-data access or
tax-deductible status is promised. Platform-issued receipts remain the
provider/account holder's responsibility; do not generate tax receipts or
make tax claims without qualified review. No billing or donation service is
activated by this change.
