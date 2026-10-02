## 1. BFS — Baseline and impact coverage
- [ ] 1.1 Map every requirement and Success/Failure/Boundary scenario to src/Chronicleaf.GitHub/GraphQL/, owned state/contracts, API/UI callers and GithubGraphqlEnrichmentTests fixtures; confirm proposal/design/spec agree and record baseline.
- [ ] 1.2 Confirm completed dependencies (github-evidence), C# .NET10 / React TypeScript boundary, shared-package adoption and every activation/blocker decision; do not start a blocked handoff or duplicate sibling code.

## 2. DFS — Requirement-by-requirement implementation
- [ ] 2.1 Implement GITHUB-GRAPHQL-ENRICHMENT-001 Attributed relationship enrichment through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.
- [ ] 2.2 Implement GITHUB-GRAPHQL-ENRICHMENT-002 Read-only bounded query execution through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [ ] 3.1 Run GithubGraphqlEnrichmentTests with the exact fixtures in design.md; verify scope, privacy, revision/concurrency, retry/cancel and output invariants across every changed caller, and audit the original impact map for omissions.
- [ ] 3.2 Exercise dependency compatibility and retained preexisting history; test documented rollback/partial failure and verify no current-change placeholders, unauthorized side effects or unrelated sibling changes.

## 4. Verification
- [ ] 4.1 Run native build and scoped real scenario tests from design.md; execute applicable UI/browser, PostgreSQL or provider-contract checks and local Gate. Record exact commands/results and unavailable prerequisites; no mock-only runtime claims.
- [ ] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review untracked/staged content and the mapped impact surface before archive.
- [ ] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.
