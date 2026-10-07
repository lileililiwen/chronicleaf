## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map AGENCY-AREA-UI-001/002/003 and every Success/Failure/Boundary scenario to the
  delivered routes (`AgencyClientEndpoints`, `WeeklyReportEndpoints`), the new
  `features/agency-area/` modules, the two `ProjectArea` routes plus nav entry, and the new
  Playwright suite fixtures; confirm proposal/design/spec agree and record the CRUD lifecycle
  matrix baseline (client, assignment, report, revision, approval, export × Create/Read/Update/
  Delete across API routes and UI triggers).
- [x] 1.2 Confirm both agency backends archived with no backend gap (every UI call has a delivered
  route + guard), TypeScript/React/Vite boundary, adopted call patterns, and no activation
  blocker; record `agency-client-recovery` and `agency-report-history` as the named out-of-scope
  holes.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement AGENCY-AREA-UI-001 (Clients area: list/create/edit/archive + assign/unassign
  with revision guards, verbatim refusals, archived/foreign handling) through `api.ts`,
  `types.ts`, `ClientsArea.tsx` + detail, nav entry and `/clients*` routes, with tests
  asserting the specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement AGENCY-AREA-UI-002 (weekly-report flow: week picker, in-client multi-select,
  generate, per-project preview, bound approval, Markdown/HTML byte-equal downloads,
  regeneration revisions, drift re-approval, DST week) through `WeeklyReportPanel.tsx`, with
  tests asserting the specified success, failure and boundary results before marking complete.
- [x] 2.3 Implement AGENCY-AREA-UI-003 (top-nav/dashboard reachability under product-shell-ux
  tokens/conventions, no rebrand) and the new Playwright suite plus e2e.sh group 26 with
  `seed_agency_area` (delivered routes only; report rows only from UI generate calls).
  Targeted proof: 3/3 green on a disposable PostgreSQL + published app.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Exercise the end-to-end journey (CRUD → assign → generate → approve → export), refusal
  verbatimness, archived-client behavior, empty states, POST/no-remote counters, and every
  affected caller/route; remove current-change placeholders; confirm no backend file changed and
  no other change's files touched.
- [x] 3.2 Fill in-scope CRUD holes found by the matrix; report out-of-scope holes explicitly as
  the named future packages (never silently dropped); finalize the per-entity CRUD matrix.

## 4. Verification
- [x] 4.1 Run `npm ci`, `tsc --noEmit` + `vite build`, the new suite plus full `bash scripts/e2e.sh`
  (26 suites) green, and `git diff --check`; record exact evidence and any defect found by
  verification.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs` and
  `openspec validate --all --strict --no-interactive` (never `--skip-specs`); review
  untracked/staged content and the mapped impact surface before archive.
- [ ] 4.3 Archive with `openspec archive agency-area-ui --yes`, confirm
  `openspec/specs/agency-area-ui/spec.md` is promoted, then create exactly two commits:
  (1) implementation+tests+archive+canonical spec (exclude HANDOFF.md),
  (2) HANDOFF.md ONLY with evidence + remove `current_spec`; stop, do not push.
