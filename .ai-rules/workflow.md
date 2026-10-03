# Workflow

BFS → DFS → BFS: map impacted APIs, data, users, integrations and docs; resolve contracts and ownership before implementing one boundary; regress the full original impact surface afterwards. OpenSpec proposal/design/tasks/specs are the only durable change plans. Inspect sibling common/manager packages before duplicating recurring capabilities; do not edit siblings without scope.

On main, run name preflight then openspec list. Set exactly one current_spec in HANDOFF.md before working. Implement one eligible change, maintain tasks, run meaningful local tests and any configured Gate, then strict OpenSpec validation. FAIL or unresolved REVIEW_REQUIRED blocks completion. Archive without --skip-specs.

Each completed OpenSpec spec/change requires exactly two commits: first the implementation/tests/archive commit, then a HANDOFF.md-only pointer/evidence commit.

Commit 1 includes related implementation, tests, archive and canonical specs; commit 2 only updates HANDOFF.md evidence and advances current_spec, removing it if none remain. Do not ask for conversational authorization for these commits when implementation is authorized. Stop after commit 2; do not push. Planning-only bootstrap does not archive or claim delivery.

Check command: node scripts/check-openspec-change-names.mjs. Validate: openspec validate --all --strict --no-interactive. Build: dotnet build. Meaningful product tests must be added with implementation; dotnet test on this skeleton proves no behavior. CI is a second layer, never the first local check.

Use global shot-scraper/playwright-cli for documentation captures after UI exists, record real source/revision/privacy evidence, and never pass off a mockup as runtime capture.

Work directly on `main` by default; do not create a branch or worktree unless the user asks for one or designates one.
