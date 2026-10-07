# Design: Feature trigger panels (feature-trigger-panels)

## Context and implementation boundary
Six delivered backends are unreachable from the Web client (audit 2026-10-07:
147 routes mapped, misses verified by direct source grep, false positives for
webhook receivers and `/api/health` excluded). This package adds only the
trigger UI. The called routes, envelopes, bindings, and evidence rules are
owned by the archived dependency packages and are consumed verbatim. If a
route the panels need does not exist, the panel is out of scope: no backend
route is added here; the gap is reported instead.

## Language, ownership and release
C# .NET 10 API untouched; React/TypeScript Web only, repository Chronicleaf.
POST-MVP. No migration (new tables: none; schema byte-identical — assert it).
No new test project; browser suites live beside the existing ones in
`src/Chronicleaf.Web/tests/`. Rollback is removal of the panels and their
routes; stored evidence/assets/receipts are untouched because this package
writes nothing except through the delivered routes it triggers.

## Contract and compatibility decisions
Consume the common contracts owned by dependency packages: UUID ids, UTC
ISO8601 timestamps, camelCase JSON, owner/scope lookup and optimistic bigint
revision. Private routes use community-access owner sessions; Hosted views
call the cloud-tenant-boundary/team-project-permissions routes and render
whatever the server answers — including the honest not-enabled states — never
a client-side tenancy emulation. The public journey view reads only
`GET /api/journey/{slug}` and its media route. Unknown or foreign-scoped
resource is 404; unauthenticated private request 401, insufficient permission
403; malformed 400, semantic validation 422, missing If-Match 428, stale
revision 409, oversized 413, transient dependency 503, timeout 504. Error
envelope is `{code,message,traceId,fieldErrors?}`; panel text repeats the
server's code and message, never paraphrase. Every panel keeps the existing
conventions: one `api.ts` + `types.ts` + `useX` hook + `Panel.tsx` per
feature, stable `data-testid` selectors, no new styling primitives (ad-hoc
panel styles as-is; `product-shell-ux` systematises later). List endpoints
default 25/max 100 with scope-bound opaque cursor when applicable.

Panels and the exact routes each triggers (all delivered, all authenticated
except the public journey read):
- team panels: `POST /api/hosted/tenants/{id}/invitations`,
  `GET .../invitations`, `POST /api/hosted/invitations/accept`,
  `GET .../team-members`, `PATCH`/`DELETE .../team-members/{userId}`,
  `POST /api/projects/{id}/members`, `GET .../team-access`.
- tenancy views: `GET`/`POST /api/hosted/tenants`,
  `POST .../select`, `POST /api/hosted/login/start` (navigates the browser to
  the returned authorization address; the callback route is server-side),
  `POST /api/hosted/import`.
- evolution images: `POST /api/projects/{id}/evolution/images`,
  `GET .../evolution/images`, `GET /api/evolution/images/{id}`,
  `.../preview`, `.../download`, `POST .../approve`.
- evolution GIFs: the same six shapes under `.../evolution/gifs`.
- publications: `GET /api/publications/{attemptId}`,
  `POST .../reconcile`, plus `POST /api/assets/{id}/publish-preview` and
  `POST .../publish` entry from the asset view.
- journey: `POST /api/projects/{id}/journey/preview`,
  `PUT .../journey`, `GET .../journey`, and a public `/journey/:slug` view
  reading `GET /api/journey/{slug}` and `.../media/{mediaId}`.

## Behavioral model and invariants
- Every trigger is an explicit owner act (button per action, confirm where the
  route is destructive); nothing auto-fires on render or on navigation.
- Panels never invent state: empty results render the empty state; errors
  render the server code; loading renders a pending marker, never fake rows.
- The public journey view shows only what the projection route returns; an
  unknown slug is the route's own 404 page, not a redirect.
- Hosted views in a Community (single-tenant) installation render the
  server's own answers; no client-side feature flag pretends tenancy on.

## Failure, boundary and rollback policy
A called route that 404s in a way the design did not anticipate fails the
package: report the exact route, do not invent a fallback UI and do not add
the route. Partial delivery fails rather than ships half the panels: all six
areas trigger or none is claimed. Rollback removes panels, routes, and
suites; no migration to roll back, no stored state to reconcile.

## Verification oracle
New Playwright suites (one per panel, empty/generic states only, real API
host, real database, no secret-bearing captures): each suite drives its
panel's triggers and asserts the server-observed outcome (row listed, status
shown, approval recorded, download served, public page rendered). Full
`bash scripts/verify.sh` green with `GATE_EXIT` explicit; `npm ci`,
`tsc` + `vite build` green; all pre-existing suites pass unmodified;
`openspec validate --all --strict --no-interactive` 54/54-class green before
and after archive (count grows by exactly this package); `git diff --check`
clean.

## Decision ledger and activation gates
Phase: POST-MVP. DEMAND satisfied by explicit owner order after the 2026-10-07
trigger audit. No owner-blocked decision remains in this package: panel layout
follows existing conventions (no taste calls), and visual systematisation is
explicitly deferred to `product-shell-ux`. If a needed route is missing, that
is a package-external gap, recorded not worked around.

## Requirement traceability
| Requirement | Proposal outcome | Design decision | Implementation boundary | Scenarios | Tasks | Exact oracle |
|---|---|---|---|---|---|---|
| FEATURE-TRIGGER-PANELS-001 | Team triggers | team panels + routes above | Web team feature + tests | 001 Success/Failure/Boundary | 1.1, 2.1, 3.1, 4.1 | new team browser suite: invite/accept/list/patch/remove/grant observed over the wire |
| FEATURE-TRIGGER-PANELS-002 | Tenancy triggers | tenancy views + routes above | Web tenancy feature + tests | 002 Success/Failure/Boundary | 1.1, 2.2, 3.1, 4.1 | new tenancy browser suite: tenants create/select, login start navigates, import observed |
| FEATURE-TRIGGER-PANELS-003 | Evolution image triggers | evolution image panel + routes above | Web evolution feature + tests | 003 Success/Failure/Boundary | 1.1, 2.3, 3.1, 4.1 | new evolution browser suite: render/list/approve/download observed |
| FEATURE-TRIGGER-PANELS-004 | Evolution GIF triggers | evolution GIF panel + routes above | Web evolution-gif feature + tests | 004 Success/Failure/Boundary | 1.1, 2.4, 3.1, 4.1 | new gif browser suite: render/list/approve/download observed |
| FEATURE-TRIGGER-PANELS-005 | Publication triggers | publications panel + routes above | Web publishing feature + tests | 005 Success/Failure/Boundary | 1.1, 2.5, 3.1, 4.1 | new publications browser suite: status shown, reconcile + publish entry observed |
| FEATURE-TRIGGER-PANELS-006 | Journey triggers | journey publish + public view + routes above | Web journey feature + tests | 006 Success/Failure/Boundary | 1.1, 2.6, 3.1, 4.1 | new journey browser suite: preview/put observed, public slug page renders, unknown slug 404s |
