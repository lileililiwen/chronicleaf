# feature-trigger-panels Specification

## Purpose
TBD - created by archiving change feature-trigger-panels. Update Purpose after archive.
## Requirements
### Requirement: FEATURE-TRIGGER-PANELS-001 Team invitations and members UI
The Web client SHALL render team invitation and member triggers calling only
the delivered team-project-permissions routes: invite, invitation list,
invitation accept, member list, role patch, member remove, and project grant.

#### Scenario: FEATURE-TRIGGER-PANELS-001 Success
- **WHEN** the signed-in owner invites, accepts, re-roles, removes, or grants
- **THEN** the panel performs the matching route call and shows the
  server-observed result (listed invitation/member, updated role, removed row,
  granted access) with the server's own codes on failure.

#### Scenario: FEATURE-TRIGGER-PANELS-001 Failure
- **WHEN** the server refuses (validation, permission, stale revision)
- **THEN** the panel shows the server code and message verbatim; no action is
  pretended and no row is optimistically invented.

#### Scenario: FEATURE-TRIGGER-PANELS-001 Boundary
- **WHEN** the installation has no invitations or members beyond the owner
- **THEN** the panel renders the empty state; invitation accept without a
  pending invitation answers the route's own refusal.

### Requirement: FEATURE-TRIGGER-PANELS-002 Hosted tenancy UI
The Web client SHALL render hosted tenancy triggers calling only the
delivered cloud-tenant-boundary routes: tenants list and create, tenant
select, hosted login start (navigating to the returned authorization
address), and hosted import.

#### Scenario: FEATURE-TRIGGER-PANELS-002 Success
- **WHEN** the signed-in owner lists, creates, or selects a tenant, starts
  hosted login, or runs hosted import
- **THEN** the view performs the matching route call and shows the
  server-observed result with the server's own codes on failure.

#### Scenario: FEATURE-TRIGGER-PANELS-002 Failure
- **WHEN** the server refuses or tenancy is not enabled
- **THEN** the view shows the server's own answer verbatim; no client-side
  tenancy is emulated and no tenant is invented.

#### Scenario: FEATURE-TRIGGER-PANELS-002 Boundary
- **WHEN** a Community single-tenant installation renders these views
- **THEN** they show the server's own Community answers; Community behavior
  is byte-identical with or without these views present.

### Requirement: FEATURE-TRIGGER-PANELS-003 Evolution image triggers
The Web client SHALL render evolution image triggers calling only the
delivered screenshot-evolution-images routes: render, list, single read,
preview, download, and approve.

#### Scenario: FEATURE-TRIGGER-PANELS-003 Success
- **WHEN** the signed-in owner renders, lists, previews, downloads, or
  approves an evolution image
- **THEN** the panel performs the matching route call and shows the
  server-observed result with the server's own codes on failure.

#### Scenario: FEATURE-TRIGGER-PANELS-003 Failure
- **WHEN** the server refuses (unselected media, stale selection, missing
  bytes)
- **THEN** the panel shows the server code and message verbatim; candidates
  are never depicted and nothing is promoted by this view.

#### Scenario: FEATURE-TRIGGER-PANELS-003 Boundary
- **WHEN** no evolution image exists for the project
- **THEN** the panel renders the empty state; preview/download links are
  served bytes from the route, never synthesised pictures.

### Requirement: FEATURE-TRIGGER-PANELS-004 Evolution GIF triggers
The Web client SHALL render evolution GIF triggers calling only the
delivered evolution-gif-export routes: render, list, single read, preview,
download, and approve.

#### Scenario: FEATURE-TRIGGER-PANELS-004 Success
- **WHEN** the signed-in owner renders, lists, previews, downloads, or
  approves an evolution GIF
- **THEN** the panel performs the matching route call and shows the
  server-observed result with the server's own codes on failure.

#### Scenario: FEATURE-TRIGGER-PANELS-004 Failure
- **WHEN** the server refuses (frame caps, stale selection, missing bytes)
- **THEN** the panel shows the server code and message verbatim; no animation
  is synthesised client-side.

#### Scenario: FEATURE-TRIGGER-PANELS-004 Boundary
- **WHEN** no GIF exists for the project
- **THEN** the panel renders the empty state; the download is route-served
  bytes with a deterministic rebuild behind them.

### Requirement: FEATURE-TRIGGER-PANELS-005 Publication status and reconcile UI
The Web client SHALL render publication status and reconcile triggers calling
only the delivered publisher-contract routes: publication read, reconcile,
publish preview, and the publish entry from the asset view.

#### Scenario: FEATURE-TRIGGER-PANELS-005 Success
- **WHEN** the signed-in owner reads a publication attempt, reconciles it, or
  previews/publishes from an asset
- **THEN** the panel performs the matching route call and shows the
  server-observed result with the server's own codes on failure.

#### Scenario: FEATURE-TRIGGER-PANELS-005 Failure
- **WHEN** the server refuses (unknown attempt, export-only target, stale
  approval)
- **THEN** the panel shows the server code and message verbatim; nothing is
  published by rendering and no transport is contacted except through the
  route the owner explicitly triggered.

#### Scenario: FEATURE-TRIGGER-PANELS-005 Boundary
- **WHEN** no publication attempt exists
- **THEN** the panel renders the empty state; reconcile on an unknown attempt
  answers the route's own refusal.

### Requirement: FEATURE-TRIGGER-PANELS-006 Journey publish and public view UI
The Web client SHALL render journey publish triggers calling only the
delivered public-project-journey routes (preview, put, read) plus a public
`/journey/:slug` view reading only the public projection routes.

#### Scenario: FEATURE-TRIGGER-PANELS-006 Success
- **WHEN** the signed-in owner previews or publishes a journey, or anyone
  visits a published slug
- **THEN** the calls performed match the routes above, the public page shows
  only what the projection route returns, and failures show the server's own
  codes.

#### Scenario: FEATURE-TRIGGER-PANELS-006 Failure
- **WHEN** the server refuses (unapproved content, slug conflict, stale
  selection)
- **THEN** the views show the server code and message verbatim; unpublished
  or unapproved material never renders on the public page.

#### Scenario: FEATURE-TRIGGER-PANELS-006 Boundary
- **WHEN** an unknown slug is visited
- **THEN** the route's own 404 page renders; there is no redirect and no
  content guess.

