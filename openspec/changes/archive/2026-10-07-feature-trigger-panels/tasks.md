## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every requirement and Success/Failure/Boundary scenario to new `src/Chronicleaf.Web/src/features/` panels, the exact delivered routes in design.md, the route map, and the new Playwright suites; confirm proposal/design/spec agree and record the no-caller baseline.
- [x] 1.2 Confirm completed dependencies (team-project-permissions, cloud-tenant-boundary, screenshot-evolution-images, evolution-gif-export, publisher-contract, public-project-journey, static-blog-export), Web-only boundary (no migration, route, envelope, binding, or evidence-rule change), POST-MVP stage with DEMAND satisfied by owner order, and the no-secret-in-suite constraint; do not add backend work and report a missing route instead of inventing one.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement FEATURE-TRIGGER-PANELS-001 team panels through the declared Web boundary; add the team browser suite asserting its specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement FEATURE-TRIGGER-PANELS-002 tenancy views through the declared Web boundary; add the tenancy browser suite asserting its specified success, failure and boundary results before marking complete.
- [x] 2.3 Implement FEATURE-TRIGGER-PANELS-003 evolution image panels through the declared Web boundary; add the evolution browser suite asserting its specified success, failure and boundary results before marking complete.
- [x] 2.4 Implement FEATURE-TRIGGER-PANELS-004 evolution GIF panels through the declared Web boundary; add the gif browser suite asserting its specified success, failure and boundary results before marking complete.
- [x] 2.5 Implement FEATURE-TRIGGER-PANELS-005 publications panels through the declared Web boundary; add the publications browser suite asserting its specified success, failure and boundary results before marking complete.
- [x] 2.6 Implement FEATURE-TRIGGER-PANELS-006 journey publish plus public view through the declared Web boundary; add the journey browser suite asserting its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run all six new suites plus the full pre-existing Playwright set unmodified; verify every trigger reaches a delivered route, empty states render with no data, server codes surface verbatim, Community behavior is unchanged, and audit the original impact map for omissions.

## 4. Verification
- [x] 4.1 Run `npm ci`, `tsc` + `vite build`, the six new suites plus the full browser set, and the applicable local Gate with `GATE_EXIT` explicit under `set -o pipefail`; record exact commands/results and unavailable prerequisites; no mock-only claims and no secret-bearing captures.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review untracked/staged content and the mapped impact surface before archive.
- [ ] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.
