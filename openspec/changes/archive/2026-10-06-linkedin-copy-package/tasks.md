## 1. BFS — Baseline and impact coverage
- [x] 1.1 Map every requirement and Success/Failure/Boundary scenario to src/Chronicleaf.Publishers/LinkedIn/, owned state/contracts, API/UI callers and LinkedinCopyPackageTests fixtures; confirm proposal/design/spec agree and record baseline.
- [x] 1.2 Confirm completed dependencies (content-asset-variants), C# .NET10 / React TypeScript boundary, shared-package adoption and every activation/blocker decision; do not start a blocked handoff or duplicate sibling code.

## 2. DFS — Requirement-by-requirement implementation
- [x] 2.1 Implement LINKEDIN-COPY-PACKAGE-001 Evidence-based LinkedIn copy through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.
- [x] 2.2 Implement LINKEDIN-COPY-PACKAGE-002 Human LinkedIn publication through its declared contracts, state/IO and applicable API/UI; add tests asserting its specified success, failure and boundary results before marking complete.

## 3. BFS — Cross-surface regression and completeness
- [x] 3.1 Run LinkedinCopyPackageTests with the exact fixtures in design.md; verify scope, privacy, revision/concurrency, retry/cancel and output invariants across every changed caller, and audit the original impact map for omissions.
- [x] 3.2 Exercise dependency compatibility and retained preexisting history; test documented rollback/partial failure and verify no current-change placeholders, unauthorized side effects or unrelated sibling changes.

## 4. Verification
- [x] 4.1 Run native build and scoped real scenario tests from design.md; execute applicable UI/browser, PostgreSQL or provider-contract checks and local Gate. Record exact commands/results and unavailable prerequisites; no mock-only runtime claims.
- [x] 4.2 Run `node scripts/check-openspec-change-names.mjs`, `openspec validate --all --strict --no-interactive`, `git diff --check`, and review untracked/staged content and the mapped impact surface before archive.
- [x] 4.3 Only after implementation authorization and all scoped acceptance: archive without --skip-specs, create implementation/tests/archive commit, then HANDOFF.md-only pointer/evidence commit; exactly two commits, no conversational confirmation, stop and do not push.

Evidence (all run by the implementer, 2026-10-06):
- 1.1/1.2: impact map recorded in-session. Boundary implemented as `src/Chronicleaf.Application/LinkedIn/`
  + `src/Chronicleaf.Api/Content/LinkedInCopyEndpoints.cs` (the `src/Chronicleaf.Publishers/LinkedIn/`
  sketch in design.md resolved to the repo's Application/Api/Infrastructure layering; no new
  top-level project). Dependency `content-asset-variants` archived (canonical spec
  `openspec/specs/content-asset-variants/spec.md`); variant/review/voice/media/evidence contracts
  reused, none duplicated. No Web UI shipped: design names it optional, scenarios are
  API-observable, no new browser suite (precedent of adjacent packages).
- 2.1: `LinkedInCopyRenderer` (pure, `linkedin-copy/1`, internal 10000-char ceiling with warning
  at 9000, no platform claim) + `LinkedInCopyService.AdaptAsync` (approved markdown-story source,
  live-approval bindings re-checked, evidence/voice currency, deterministic CTA/hashtag/selection
  rules, grounding re-validation, draft revision + `linkedin_copy_media` rows + audit in one unit
  of work). New asset kind `linkedin-copy` (Core, additive, no migration needed for the value).
- 2.2: derivative starts with no approval row (delivered edit/approve/review routes reused
  unchanged); `ExportAsync` re-checks every review binding and serves ZIP
  (`copy.txt` + `images/*.png` + `manifest.json`, fixed entry timestamps); native publish route
  always answers 422 `publisher_unsupported` with no credential lease, no job, no audit, no
  socket. New codes `publisher_unsupported`/`linkedin_copy_too_long` each carry an explicit
  middleware type-arm plus an `ApiErrorStatus` arm (bare-`ApiException` would 500).
- 3.1: `LinkedinCopyPackageTests` 10/10 against real Testcontainers PostgreSQL (real generation
  job behind the scripted-provider seam; real capture+select behind the scripted-browser seam;
  two-host discipline shared with the HTML export suite). Covers: grounded copy + snapshot
  immutability + selection rows; voice-restriction-wins + derivative approval; no-CTA
  informational + empty manifest; 13 refusals storing nothing; ZIP byte-exactness + manifest
  digests; approval_required/invalidated; publish refusal with unchanged operations/non-session
  audits/linkedin audits; warning + ceiling; migration columns/PK. Two real defects found by
  tests: migration lacked the `[Migration]` discovery attribute (Designer added, model
  byte-identical since the table is raw-SQL-only), and the length fixtures exceeded the summary
  schema's 8000-char item bound (resized to 4500/6000). One test-side finding recorded, not
  hidden: the access boundary audits its own per-mutation antiforgery rotation, so the
  no-side-effect assertion counts non-session audits.
- 3.2: adjacent suites green: ContentAssetVariants + MarkdownReviewExport + HtmlContentExport +
  VoiceProfiles 53/53. New migration `20261018090000_AddLinkedInCopyPackage` (+ hand-copied
  Designer); `RequiredMigrationIds` appended, `ExpectedMigrationId` advanced (all consumers assert
  by position). No sibling edits, no placeholders, no unrelated reformatting.
- 4.1: `dotnet build Chronicleaf.sln --nologo --no-incremental` 0 errors, no new warnings (6
  pre-existing in untouched files); `bash scripts/verify.sh` exit 0 (`GATE_EXIT=0`, `set -o
  pipefail`, foreground) incl. the new `linkedin copy package tests` stage, `npm ci`, `tsc` +
  `vite build`. No E2E browser run (no UI touched); no LinkedIn network exists to test against
  (structural: no HttpClient in the package).
- 4.2: name preflight exit 0; `openspec validate --all --strict --no-interactive` 54/54 before
  archive; `git diff --check` clean; staged/untracked reviewed (implementation + tests + archive
  only; HANDOFF.md excluded from commit 1).
