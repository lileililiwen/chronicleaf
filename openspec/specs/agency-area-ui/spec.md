# agency-area-ui Specification

## Purpose
TBD - created by archiving change agency-area-ui. Update Purpose after archive.
## Requirements
### Requirement: AGENCY-AREA-UI-001 Scoped client organization UI
The Web client SHALL let the signed-in owner list, create, edit and archive agency clients and
assign or unassign owned projects to them, over the delivered client routes with revision
guards and verbatim server refusals.

#### Scenario: AGENCY-AREA-UI-001 Success
- **WHEN** the owner creates two clients, assigns two projects to one of them and archives the other
- **THEN** the list shows both with the archived badge, the detail shows the two member projects,
  and the archived client stays readable with new assignment disabled

#### Scenario: AGENCY-AREA-UI-001 Failure
- **WHEN** an assignment names an archived client, an edit presents a stale revision, or a
  foreign client id is opened
- **THEN** the UI reports `client_archived` 409, `revision_conflict` 409, or `not_found` 404 in
  the server's own words with nothing renamed and nothing probed

#### Scenario: AGENCY-AREA-UI-001 Boundary
- **WHEN** no client exists, or a client has no projects
- **THEN** the area states the empty case honestly and names the owner's next step (create a
  client; assign a project), with no portal, invoicing or email control anywhere

### Requirement: AGENCY-AREA-UI-002 Weekly report flow UI
The Web client SHALL generate a weekly report per client from an explicit week range and an
explicit in-client project set, preview its per-project sources, approve it at its shown
revision and body/attribution bindings, and download the Markdown and HTML exports, over the
delivered weekly-report routes.

#### Scenario: AGENCY-AREA-UI-002 Success
- **WHEN** the owner picks a week, selects the client's projects, generates, approves and exports
- **THEN** the preview shows the deterministic body with per-project source refs, the approval
  binds the shown revision, and both downloads are byte-equal to the delivered export route

#### Scenario: AGENCY-AREA-UI-002 Failure
- **WHEN** a project outside the client is submitted, an export precedes approval, or evidence
  drifts after approval
- **THEN** the UI reports `weekly_report_project_outside_client` 422, `approval_required` 409,
  or `approval_stale` 409 in the server's own words, and drift names regeneration as the remedy

#### Scenario: AGENCY-AREA-UI-002 Boundary
- **WHEN** the week crosses a daylight-saving transition, or the week has no recorded completion
- **THEN** the window bounds still represent exactly seven local calendar dates as the backend
  defines them, regeneration appends a new revision starting unapproved, and the empty week
  reports no recorded completion without inventing progress

### Requirement: AGENCY-AREA-UI-003 Reachable agency area under shell conventions
The agency area SHALL be reachable from the top nav or dashboard as a clearly linked area that
follows the product-shell-ux token and convention rules with no rebrand.

#### Scenario: AGENCY-AREA-UI-003 Success
- **WHEN** the signed-in owner uses the top nav
- **THEN** the Clients entry is one nav step from every other area with `aria-current` on the
  active entry, and each agency view renders its own h1

#### Scenario: AGENCY-AREA-UI-003 Failure
- **WHEN** a pre-existing shell or nav suite runs
- **THEN** it passes unmodified: existing entries, order and selectors are unchanged

#### Scenario: AGENCY-AREA-UI-003 Boundary
- **WHEN** the area renders with no session-adjacent secret or foreign data
- **THEN** no token, credential, or foreign client/project name appears on screen or in a
  capture; empty states carry no spec-authoring text

