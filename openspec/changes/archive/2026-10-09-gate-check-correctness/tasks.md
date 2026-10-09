## 1. BFS — Baseline and impact coverage

- [x] 1.1 Also the three named files to the impact map: `.ai-gate/gate.yaml`
  (timeouts map only), `scripts/verify-platform-feed.sh` (trailing restore
  only), `scripts/gate/dotnet-tests.sh` (verbosity flags only). Baseline
  recorded: `platform-feed` had no timeout entry and inherited
  `DEFAULT_CHECK_TIMEOUT_MS = 60000`; `MAX_DIAGNOSTIC_BYTES = 2048`; a hand
  run of `dotnet test` passed 858/858 in 40m47s while the gate's `dotnet`
  check reported FAIL.

## 2. DFS — Requirement-by-requirement implementation

- [x] 2.1 GATE-FIX-001: add `platform-feed: 1800000` to the `timeouts:` map in
  `.ai-gate/gate.yaml` with a comment naming the cold-restore proof.
- [x] 2.2 GATE-FIX-002: add the trailing normal `dotnet restore Chronicleaf.sln
  --nologo --verbosity quiet` to `scripts/verify-platform-feed.sh`.
- [x] 2.3 GATE-FIX-003: add `--verbosity quiet` to the `dotnet build` and
  `dotnet test` lines in `scripts/gate/dotnet-tests.sh`.

## 3. BFS — Cross-surface regression and completeness

- [x] 3.1 No other check's command or timeout changes; no product or test
  file changes; the platform-feed proof still restores into an empty folder
  and still asserts each platform package resolved from the committed feed.
- [x] 3.2 `git diff --stat` names only the three files plus the archived
  change.

## 4. Verification

- [x] 4.1 `bash scripts/gate/platform-feed.sh` exits 0 ("platform feed
  verification passed") and `src/Chronicleaf.Core/obj/project.assets.json`
  names `/home/paul/.nuget/packages/`, which exists, after the run.
- [x] 4.2 A deliberate temporary failing test
  (`TempGateDiagnosticProbeTests`, added then removed) shows the failed-test
  line `[xUnit.net ...] ... [FAIL]` and `失败: 1` inside the first 2048 bytes
  of combined output; measured build output 78 bytes, test output 471 bytes.
- [x] 4.3 `node scripts/check-openspec-change-names.mjs` exit 0; `openspec
  validate --all --strict --no-interactive` green; `openspec archive
  gate-check-correctness --yes` (never `--skip-specs`); exactly two commits
  (no push).
