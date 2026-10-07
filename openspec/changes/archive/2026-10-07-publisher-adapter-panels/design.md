# Design: publisher-adapter-panels

## 1. Implementation boundary
- `src/Chronicleaf.Publishers/{DevTo,Hashnode,Ghost,WordPress,CustomBlog}/*Options.cs`:
  add one public parameterless constructor each delegating to the primary
  positional constructor with nulls (all five records' parameters are
  nullable `string?`). No validation change; `Create`/`Read`/publishers
  untouched. `GitHubPagesOptions.cs` untouched (already repaired).
- `tests/Chronicleaf.Integration.Tests/Publisher{Devto,Hashnode,Ghost,Wordpress,CustomBlog}AdapterTests.cs`:
  add `_configured` field, `StartConfigured` static, `UseSetting` loop, and
  one `Configured_section_registers_target_and_serves_preview` fact each,
  mirroring `PublisherGithubPagesApiHost` (`StartConfigured` at ~line 1549,
  fact at ~line 515). No existing test edited.
- `src/Chronicleaf.Web/src/features/publications/`:
  - new `AdapterTargetPanel.tsx`: pure presentational block; props are the
    picked `PublisherTarget` descriptor plus the current preview (nullable).
    Per-target copy table in the component (8 entries: 6 transports + medium
    + linkedin). Testids under the form's `namespace`:
    `{ns}-adapter-panel`, `{ns}-adapter-modes`, `{ns}-adapter-config`.
  - `PublishForm.tsx`: render the panel under the target picker; derive the
    mode options from the picked descriptor (`supportsDraft`/`supportsPublish`;
    default to the first supported; reset on target change only when the
    current mode becomes unsupported); remove `disabled` from export-only
    options (selection is what lets the server refusal surface).
  - `api.ts`/`types.ts`: untouched (all shapes already serve the need).
- `src/Chronicleaf.Web/tests/publisher-adapter-panels.spec.ts`: new suite.
- `scripts/e2e.sh`: new suite-24 block (port `5104`), six transport sections
  plus `Chronicleaf__Operations__Enabled=false`, reusing the
  `seed_publish_variant` seed verbatim (approved story + sealed `publisher`
  credential).
- Must not change: any API route, envelope, migration, credential flow,
  `PublicationPanel`, `VariantPublishSection`, other changes' files.

## 2. Language and runtime
C# .NET 10 (SDK pinned by `global.json`; `Microsoft.Extensions.Options`
binder already referenced — no new package) for the five constructors and the
five xUnit facts; TypeScript React (Vite, `src/Chronicleaf.Web`) for the panel;
bash + Playwright for the browser suite. Commands: `dotnet test
Chronicleaf.sln --filter FullyQualifiedName~Publisher<Name>AdapterTests`,
`npm ci`, `tsc --noEmit` + `vite build` (web dir), new Playwright suite, full
`bash scripts/e2e.sh`, `git diff --check`,
`openspec validate --all --strict --no-interactive`.

## 3. Ownership and shared code
Project-local. The consent flow (`PublishForm`) is owned by the publications
feature and reused, not forked: the adapter block is a child component with no
fetch of its own. The Pages repair is the owned precedent, mirrored — no
shared `/lib` extraction (one consumer: the options binder). No sibling,
platform-feed, or other-change file edited.

## 4. User experience and interface
Entry points unchanged (Publish area, variant row). When a target is picked,
`{ns}-adapter-panel` shows: transport name; what the send carries
(`devto/hashnode`: Markdown text only, pictures named-not-carried;
`ghost/wordpress`: sanitized HTML article; `custom-blog`: Markdown to the
fixed origin; `github-pages`: site files to the bound repo/branch/subdir);
draft/publish support and reconcile support in the registry's own words;
for export-only: hand-carry guidance naming the export package flow, with the
preview button left enabled so the server refusal is what the owner reads.
After preview, `{ns}-adapter-config` shows the preview's first warning —
each adapter binds its `TargetLine` there (verified: devto, github-pages, and
by symmetry the same `warnings = [target.TargetLine(), ...]` shape in the
other four publishers) — labeled as the bound configuration the confirm
would authorize. Mode select lists only supported modes; switching target
keeps a still-supported mode, otherwise falls to the first supported one.
All existing states (loading/empty/notice/error/receipt) unchanged. No new
route, no focus move, no auto-fire: preview/publish stay explicit buttons.

## 5. Behavioral model

| Input | Rule |
|---|---|
| Section absent | Demand gate: adapter unregistered, target answers `publisher_unknown_target` (unchanged). |
| Section present, valid | `Configure<T>` builds the record via the new ctor; `CurrentValue` + per-call `Create` revalidates; preview serves (defect fixed). |
| Section present, invalid | Startup fails (demand-gate `Read` throws) — unchanged, asserted by existing tests. |
| Target picked in UI | Adapter block renders from the already-loaded descriptor; mode box narrows; no POST fires. |
| Preview against export-only | Server 422 `publisher_target_export_only`; panel shows code + message verbatim; no dispatch, no receipt. |
| Preview then operator reconfig | Next preview describes the new target; old `previewHash` goes stale → publish refused `publisher_preview_stale` (delivered). |
| Publish | Unchanged shared flow: confirm + approval + hash + key → 202 watched operation, identical resubmit → original receipt. |

## 6. Contract and compatibility
No wire change. Env keys consumed (e2e only; production unchanged):
`Chronicleaf__DevTo__Tags`, `Chronicleaf__Hashnode__PublicationId`,
`Chronicleaf__Ghost__{Origin,ApiVersion,Tags}`,
`Chronicleaf__WordPress__{Origin,Username}`,
`Chronicleaf__CustomBlog__{Origin,Formats}`,
plus the existing `Chronicleaf__GitHubPages__*`. Envelope codes surfaced
verbatim: `publisher_target_export_only`, `publisher_unknown_target`,
`publisher_mode_unsupported`, `publisher_confirmation_required`,
`approval_required`. Backward compatible: positional construction sites and
existing suites unaffected (no suite asserts disabled options or a fixed mode
list — verified by grep before editing).

## 7. Failure and boundary policy
- Binding throws at `CurrentValue` → would-be 500 becomes a bound target
  (the fix); regression tests fail without the ctors.
- Export-only preview/publish → 422 verbatim, recorded no dispatch.
- Unsupported mode (cannot be picked post-fix, but hand-called) → 422
  `publisher_mode_unsupported` (delivered).
- Unconfigured transport in the new suite → would answer unknown; the suite
  asserts all six registered (guards a half-configured installation).
- Silenced dispatch: receipts stay `prepared`; the suite asserts acceptance
  state only and says so (run half is the .NET suites').
- Missing credential → publish refused `publisher_credential_missing`; the
  suite seals one, and one negative preview case needs none (preview is
  CPU-only).

## 8. Verification oracle
- `dotnet test` five adapter stages green, each selecting the new fact
  (prove the fact fails first: temporarily revert one ctor and watch the
  preview 500 — recorded as the reproduction).
- `npm ci`, `tsc --noEmit`, `vite build` green.
- New Playwright suite green (8 scenarios: 6 adapters + 2 export-only
  refusals), asserting no preview/publish POST before the clicks.
- Full `bash scripts/e2e.sh` green (no existing suite regressed, notably
  `publish-from-variant` and `publications`).
- `git diff --check` clean; `openspec validate --all --strict
  --no-interactive` green; archive without `--skip-specs`.

## 9. Decision ledger
- Mirror the Pages ctor repair rather than replacing `Configure<T>` with
  manual binding: the precedent is archived, production registration stays
  uniform, and the per-call `Create` revalidation (reload-safety) is
  preserved. Alternative (indexer-only `IOptions` factory) rejected: two
  binding mechanisms for six slots.
- Adapter block inside `PublishForm` rather than six panels: one consent
  flow, both hosts inherit it, no forked consent to drift.
- Export-only options enabled: a refusal only the server may utter must be
  reachable; the panel never invents it.
- Suite reuses `seed_publish_variant`: approved story + sealed credential is
  exactly the state needed; no new seed shape.
- CRUD holes: attempt listing has no route by design → named future package
  `publication-history` (already named upstream); operator target CRUD stays
  config-file-owned with UI read via targets-list + preview line. Both
  reported, neither silently dropped.

## Per-entity CRUD lifecycle matrix

| Entity | Create | Read | Update | Delete |
|---|---|---|---|---|
| Publisher target configuration | Operator config file/env section (no API; out of scope) | `GET /api/publisher-targets` + picker; **new**: per-adapter block reads the descriptor field-for-field | Operator reconfig; next preview describes it, old hash stales (delivered) | Remove section → unregisters, answers unknown (delivered; no UI) |
| Publication attempt / receipt (per adapter) | `POST …/publish` → 202 (shared form, unchanged) | `GET /api/publications/{id}` + receipt block (shared, unchanged) | `POST …/reconcile` where the target supports it (github-pages only; others refuse per delivered capability) | **No route by design** → named future package `publication-history`; attempt lookup stays hand-typed |
| Adapter options (bound values) | Bound at startup/reload via `Configure<T>` (**fixed** for 5/6) | **New**: preview's target line shown as the bound configuration; no second source of truth | Per-call `Create` revalidation refuses half-read targets (delivered) | N/A (values, not rows; removal = unregister) |
