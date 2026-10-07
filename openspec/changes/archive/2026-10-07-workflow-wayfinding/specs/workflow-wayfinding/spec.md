# workflow-wayfinding Specification

## Purpose
Close the wayfinding gaps over delivered routes: explicit next-step links
in MVP-loop order with Evidence ⇄ Evolution cross-links, a real UI trigger
for the delivered milestone import route, multi-URL capture in one
submission through an additive wire field, and the recorded Team/Tenancy
owner decision — each proven by a regression suite against the server.

## ADDED Requirements
### Requirement: WORKFLOW-WAYFINDING-001 Workflow-order next-step links
The Web client SHALL render explicit next-step links in MVP-loop order
(Overview → Evidence → Evolution → Content → Publish → Overview, plus
Automations from the Evidence candidate panel that already names it) and
per-row Evidence ⇄ Evolution cross-links, each landing on the named area's
real route, with no invented address and no new publish control.

#### Scenario: WORKFLOW-WAYFINDING-001 Success
- **WHEN** the owner presses any next-step or cross-link control
- **THEN** the named area renders (its area testid present) with all prior
  panels unchanged, and no request fires from the navigation itself

#### Scenario: WORKFLOW-WAYFINDING-001 Failure
- **WHEN** the owner navigates to an unknown area segment
- **THEN** the delivered "Nothing here" view renders with the sub-nav
  intact (no new 404 surface introduced by this change)

#### Scenario: WORKFLOW-WAYFINDING-001 Boundary
- **WHEN** the project is archived
- **THEN** footers and cross-links still navigate (navigation is not a
  mutation) while acting controls stay hidden under existing gating

### Requirement: WORKFLOW-WAYFINDING-002 Record a candidate as a milestone
Each GitHub candidate row SHALL offer "Record as milestone", calling the
delivered import route with source `github` and the documented
kind→type/version/occurredAt/notes mapping; the stored record stays
candidate status and confirmation stays in Overview.

#### Scenario: WORKFLOW-WAYFINDING-002 Success
- **WHEN** the owner presses "Record as milestone" on a candidate row
- **THEN** the panel POSTs the delivered import route once, names the
  created (or already-stored, on replay) milestone, and points at Overview
  for confirmation; the new row reads in the milestone list as `candidate`

#### Scenario: WORKFLOW-WAYFINDING-002 Failure
- **WHEN** the import route refuses (validation, stale project, archived)
- **THEN** the panel shows the server's `code: message` verbatim with field
  errors named, stores nothing client-side, and fires nothing on render

#### Scenario: WORKFLOW-WAYFINDING-002 Boundary
- **WHEN** the same candidate is recorded twice, or the project is archived
- **THEN** the replay names the stored row with no duplicate (delivered
  idempotency), and an archived project offers no import control

### Requirement: WORKFLOW-WAYFINDING-003 Schedule create/edit regression
The delivered Automations schedule form SHALL create and edit the cadence
through the delivered PUT, and the regression suite SHALL prove the server
answers both the save and its refusal.

#### Scenario: WORKFLOW-WAYFINDING-003 Success
- **WHEN** the owner saves a cadence in the Automations schedule form
- **THEN** the stored enabled flag and interval read back from the server

#### Scenario: WORKFLOW-WAYFINDING-003 Failure
- **WHEN** the server refuses the save (below-minimum cadence, stale
  revision, quota, no connection)
- **THEN** the form shows the server's `code: message` verbatim

#### Scenario: WORKFLOW-WAYFINDING-003 Boundary
- **WHEN** no schedule is armed
- **THEN** the honest empty state renders with the arm form (no invented
  cadence)

### Requirement: WORKFLOW-WAYFINDING-004 Multi-URL capture in one submission
The API SHALL accept an additive `urls` (1..3) field on the capture request
beside the legacy `url`, and the capture form SHALL offer three URL inputs
sending one request for all non-blank pages.

#### Scenario: WORKFLOW-WAYFINDING-004 Success
- **WHEN** the owner submits two or three pages in one capture form
- **THEN** the server answers one 202 whose job photographs every page, and
  the legacy single-`url` path behaves byte-identically when `urls` is
  absent

#### Scenario: WORKFLOW-WAYFINDING-004 Failure
- **WHEN** the request names both `url` and `urls`, or a malformed `urls`
  element
- **THEN** the server answers 422 naming `urls`; more than three pages
  answers 413 with no job (delivered bound)

#### Scenario: WORKFLOW-WAYFINDING-004 Boundary
- **WHEN** the form holds no non-blank URL
- **THEN** submit stays disabled and nothing fires; an archived project
  offers no capture form (existing gating)

### Requirement: WORKFLOW-WAYFINDING-005 Team/Tenancy decision pinned
The Settings surface SHALL continue to list session internals plus the
credentials link only (owner decision 2026-10-07), Team/Tenancy SHALL remain
reachable through the dashboard crumbs, and the regression suite SHALL pin
both halves.

#### Scenario: WORKFLOW-WAYFINDING-005 Success
- **WHEN** the owner opens Settings and the dashboard
- **THEN** Settings names no team/tenancy surface and the dashboard crumbs
  navigate to the real team and tenancy routes

#### Scenario: WORKFLOW-WAYFINDING-005 Boundary
- **WHEN** a later change lists Team/Tenancy on Settings or removes the
  dashboard crumbs
- **THEN** this suite fails (the decision changes loudly, never silently)

### Requirement: WORKFLOW-WAYFINDING-006 Wayfinding regression suite
A database-isolated browser suite SHALL prove every new link lands on a
real route and every new control answers the server, with armed counters
proving nothing fires before its explicit control.

#### Scenario: WORKFLOW-WAYFINDING-006 Success
- **WHEN** the suite runs against the published app on its own database
- **THEN** each link asserts its target area testid, each control asserts
  the server's answer (import 201/replay 200, schedule save/refusal,
  capture 202 then the host's `capture_sandbox_unavailable` refusal), and
  no request leaves loopback

#### Scenario: WORKFLOW-WAYFINDING-006 Failure
- **WHEN** any pre-existing suite regresses under the full run
- **THEN** the failure is recorded with its suite and case (no silent
  re-run until green)
