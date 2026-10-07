# project-area-ia Specification

## Purpose
TBD - created by archiving change project-area-ia. Update Purpose after archive.
## Requirements
### Requirement: PROJECT-AREA-IA-001 Routed project sub-views with sub-navigation
The Web client SHALL split the project page into six routed sub-views —
Overview, Evidence, Content, Publish, Evolution, Automations — behind one
project sub-navigation, moving all 20 existing panels exactly once with
none deleted and none duplicated, per the design move map (Overview 2,
Evidence 7, Content 6, Publish 2, Evolution 3, Automations 0 moved).

#### Scenario: PROJECT-AREA-IA-001 Success
- **WHEN** the signed-in owner navigates to `/projects/{id}`,
  `/projects/{id}/evidence`, `/content`, `/publish`, `/evolution`, or
  `/automations`
- **THEN** the matching area renders with the project header, the
  six-entry sub-navigation with `aria-current="page"` on the active
  area, and exactly that area's panels; every area is one sub-nav step
  from every other area.

#### Scenario: PROJECT-AREA-IA-001 Failure
- **WHEN** the owner navigates to `/projects/{id}/no-such-area`
- **THEN** the client renders the delivered "Nothing here" view with
  the project sub-navigation intact, changes nothing, and offers a
  one-click return to a real area.

#### Scenario: PROJECT-AREA-IA-001 Boundary
- **WHEN** a migrated pre-existing suite asserts a moved selector
- **THEN** it navigates to that selector's area first (sub-nav click or
  direct sub-path goto) and asserts the same expectation as before —
  the migration is named in the suite, no assertion is weakened, and
  no suite is deleted to make the gate pass.

### Requirement: PROJECT-AREA-IA-002 Per-project Automations view
The Web client SHALL render a NEW Automations view per project showing
GitHub connection status, candidate sync (last run plus an explicit
Sync-now trigger), scheduled sync cadence (view plus create/edit),
webhook status, recent jobs with statuses for the project, and publish
history — READ and TRIGGER only, through already delivered routes.

#### Scenario: PROJECT-AREA-IA-002 Success
- **WHEN** the signed-in owner opens the Automations area
- **THEN** the view reads the delivered connection, candidates-sync,
  sync-schedule, webhook-subscription, and operations routes and shows
  what the server answers (statuses, revisions, instants, redacted
  codes), the Sync-now trigger answers 202 with a watched operation,
  and the schedule form creates/edits through the delivered PUT.

#### Scenario: PROJECT-AREA-IA-002 Failure
- **WHEN** the server refuses (no connection, no schedule, not
  listening, validation, stale revision, quota)
- **THEN** the view shows the server's own code and message verbatim;
  nothing is auto-retried, nothing is optimistically invented, and no
  trigger fires on render or navigation.

#### Scenario: PROJECT-AREA-IA-002 Boundary
- **WHEN** the owner reads publish history for the project
- **THEN** the view states that the server offers NO per-project
  publish-history route, shows the scope's recent
  `publication.publish` operations captioned as scope-wide, and never
  presents them as the project's own history. A needed read with no
  delivered route is reported, never invented.

### Requirement: PROJECT-AREA-IA-003 Publish and Automations within two steps
The Web client SHALL keep the five-entry top nav unchanged and make
Publish and Automations reachable within two steps from anywhere: the
dashboard and the projects list carry per-project Publish and
Automations deep-links landing directly on those areas.

#### Scenario: PROJECT-AREA-IA-003 Success
- **WHEN** the signed-in owner clicks the Projects nav entry from any
  page and then a project's Automations (or Publish) deep-link
- **THEN** the browser lands directly on
  `/projects/{id}/automations` (or `/publish`) with the sub-nav
  active on that area.

#### Scenario: PROJECT-AREA-IA-003 Failure
- **WHEN** the project behind a deep-link is missing or foreign
- **THEN** the client renders the delivered project-missing view and
  changes nothing.

#### Scenario: PROJECT-AREA-IA-003 Boundary
- **WHEN** the owner is already on any project area
- **THEN** every other area (including Publish and Automations) is one
  sub-nav step away — no back-link, no return to the dashboard needed.

### Requirement: PROJECT-AREA-IA-004 Empty states advertise the automation payoff
The Web client SHALL word the no-connection empty states (the
Automations view and the candidate panel) so they name candidate sync
plus scheduled sync as what connecting unlocks, not just the
connection form.

#### Scenario: PROJECT-AREA-IA-004 Success
- **WHEN** the signed-in owner opens Automations (or the Evidence
  candidates panel) for a project with no repository connected
- **THEN** the empty state names candidate sync and scheduled sync as
  the payoff of connecting, beside the unchanged connection form.

#### Scenario: PROJECT-AREA-IA-004 Failure
- **WHEN** the server refuses the connection read for a reason other
  than "missing"
- **THEN** the view shows the server's own code verbatim rather than
  the no-connection payoff state.

#### Scenario: PROJECT-AREA-IA-004 Boundary
- **WHEN** a project is archived
- **THEN** the Automations triggers are absent or disabled exactly
  where the server would refuse them, and the read-only statuses stay
  visible as history.

### Requirement: PROJECT-AREA-IA-005 No backend change, kept tokens, DELTA acceptance
The change SHALL ship no backend change of any kind (no migration, no
route, no envelope, no approval binding, no evidence rule), keep the
product-shell-ux tokens and conventions with no rebrand, and prove the
look with DELTA captures per new view against the 2026-10-07 set in
empty/generic states with no secrets.

#### Scenario: PROJECT-AREA-IA-005 Success
- **WHEN** the gate runs `npm ci`, `tsc` + `vite build`, the new
  suite, the full pre-existing browser set, and `bash
  scripts/verify.sh`
- **THEN** every stage passes with `GATE_EXIT` explicit, and one DELTA
  capture per new view exists with differences justified.

#### Scenario: PROJECT-AREA-IA-005 Failure
- **WHEN** any backend contract drifts (a route, envelope, or rule
  this UI consumes changes shape)
- **THEN** the package fails rather than adapting the server: the
  drift is reported as a separate future package.

#### Scenario: PROJECT-AREA-IA-005 Boundary
- **WHEN** a capture would show a credential, token, PAT, Actor UUID,
  or signed-in session value
- **THEN** the capture is rejected and retaken in an empty/generic
  state; no secret-bearing capture is committed.

