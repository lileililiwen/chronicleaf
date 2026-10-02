## 1. BFS — Baseline and impact coverage
- [x] 1.1 Verify package boundary, root assets/receipts, toolchains and sibling ownership decisions.
- [x] 1.2 Record current greeting behavior and impacted API/static/build callers.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Move API scaffold to specified project/solution; implement SHELL-001 health and API 404 contract.
- [x] 2.2 Implement SHELL-002 React shell and static routing/missing-build behavior without simulated product data.
- [x] 2.3 Implement SHELL-003 locked dependencies, scoped native verification and Docker stages; preserve standard ownership.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Test health JSON/no secrets and unknown API non-HTML 404.
- [x] 3.2 Test actual browser shell, non-API fallback, missing assets 503 and unknown static files 404.
- [x] 3.3 Verify isolated build without sibling checkout and failed-build handling; inspect complete mapped impact surface.

## 4. Verification
- [x] 4.1 Run dotnet build/test with meaningful WebApplicationFactory assertions.
- [x] 4.2 Run npm ci/build and browser smoke, capture real UI with revision/privacy record.
- [x] 4.3 Build/run container and assert health and shell; run local verification, name preflight, strict validation and diff checks. Record any unavailable check.
- [ ] 4.4 Archive with canonical specs then implementation/tests/archive commit; final HANDOFF-only evidence/pointer commit; stop.
