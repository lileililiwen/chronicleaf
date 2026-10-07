# Proposal: Feature trigger panels (feature-trigger-panels)

## Why
Fifty-two changes are implemented, verified and archived — and a route-by-route
audit (2026-10-07, 147 API routes mapped against every `fetch`/link in
`src/Chronicleaf.Web/src`) proves whole delivered backends have no frontend
trigger at all: team invitations and members, hosted tenancy and login,
evolution image approve/preview/download, evolution GIF approve/preview/
download, publication status and reconcile, and journey publish plus the public
journey view. The features exist behind authenticated routes; the owner cannot
reach them from the product. A feature with no place to trigger is a feature
that does not exist for its user. This package wires the missing triggers.
Status: Specified handoff; implementation not started. Stage: POST-MVP (new
package; DEMAND satisfied by explicit owner order after the trigger audit).

## What Changes
Introduce one Web feature panel per unreachable area, each calling only already
delivered routes through the existing `api.ts` per-feature convention with
stable `data-testid` selectors: team invitations/members (invite, accept, list,
role patch, remove, project grant), hosted tenancy (tenants list/create/select,
hosted login start/callback wiring, import), evolution image approve plus
preview/download links, evolution GIF approve plus preview/download links,
publication status plus reconcile, and journey publish (preview/put) plus a
public journey view route. No backend change of any kind: no migration, no
route, no envelope, no approval binding, no evidence rule. Panels follow the
current ad-hoc panel conventions deliberately — visual systematisation belongs
to `product-shell-ux`, which restyles these panels once the owner answers its
taste questions. Server-to-server surfaces (webhook receivers, `/api/health`)
are explicitly excluded: they need no trigger by design.

## Package Boundary and Split Assessment
Own only the missing trigger UI for six named backend areas; runtime React/
TypeScript in `src/Chronicleaf.Web/src/features/`, repository Chronicleaf.
Dependencies (all archived): team-project-permissions, cloud-tenant-boundary,
screenshot-evolution-images, evolution-gif-export, publisher-contract,
public-project-journey, static-blog-export. Different visual restyle, shell, and
navigation lifecycles are `product-shell-ux`, not hidden sub-features. This
unit is accepted by its scoped browser suites and outcome, not by completion
of the visual overhaul. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Chronicleaf product panels are product-specific; no matching
recurring implementation is evidenced in sibling platform contracts. Reuse
dependency-owned access/jobs/storage contracts, never duplicate those concerns.
No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: authenticated project owner, or explicitly granted Hosted role;
  the public journey view is anonymous read-only over the delivered public
  projection route.
- Boundary/callers: new `src/Chronicleaf.Web/src/features/` panels plus route
  entries beside the existing route map; every called route already exists and
  is named in design. Dependency contracts remain authoritative.
- Data/integration: no schema, no new state; failures surface the server's own
  `{code,message,traceId,fieldErrors?}` envelope text, never invented words.
- Verification: new Playwright suites per panel use empty/generic states only
  against the real API host with the real database; no secret-bearing captures.
- Privacy/compatibility: credentials by vault refs, scoped lookups, redacted
  errors; existing suites keep passing (selectors they assert are preserved).

## Capabilities
### New Capabilities
- `feature-trigger-panels`: reachable UI triggers for six delivered backends.

## Non-goals
Backend changes of any kind; visual restyle, tokens, shell, navigation
(`product-shell-ux`); webhook/health UI (server-to-server by design);
auto-posting or auto-publishing anywhere; TipTap; video synthesis. No
implementation, archive, commit, push or deployment in this authoring work.
