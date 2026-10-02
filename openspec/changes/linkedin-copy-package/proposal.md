# Proposal: Reviewed copy-ready LinkedIn post

## Why
requirement §4 LinkedIn/social posts; §6 presets requires this independently verifiable outcome: Reviewed copy-ready LinkedIn post. Preserve the full brief and DEMAND-GATED scope boundary.

## What Changes
Introduce the Publishers/LinkedIn contract, persisted state or adapter and its user-facing evidence as defined in design.md. Status: Specified handoff; implementation not started.

## Package Boundary and Split Assessment
Own only Reviewed copy-ready LinkedIn post; runtime C# .NET10, React/TypeScript where UI is described, repository Chronicleaf. Dependencies: content-asset-variants. Different persistence/approval/publishing/Cloud lifecycles are separate packages in ../../../ROADMAP.md, not hidden sub-features. This unit is accepted by its scoped tests and outcome, not by completion of the full product. Demand/CLOUD priority does not authorize implementation. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Chronicleaf domain/workflow lifecycle and user review are product-specific; no matching recurring implementation is evidenced in sibling platform contracts. Reuse dependency-owned access/jobs/storage contracts, never duplicate those concerns. Evidence inspected: dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage families but no Milestone/narrative/voice/content/publisher workflow. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: authenticated project owner, or explicitly granted Hosted role; anonymous only for documented webhook/public projection routes.
- Boundary/callers: src/Chronicleaf.Publishers/LinkedIn/, Api endpoint composition and src/Chronicleaf.Web/src/features/linkedin-copy-package/ where described. Dependency contracts remain authoritative.
- Data/integration: schema, versions, outbound IO, idempotency and failures in design; append-only evidence and approval bindings remain preserved.
- Verification: LinkedinCopyPackageTests uses the exact positive/negative/boundary fixtures below; no mock success becomes provider/runtime proof.
- Privacy/compatibility: credentials by vault refs, scoped lookups, redacted errors, explicit model consent and reviewed public outputs. No sibling mutations.

## Capabilities
### New Capabilities
- `linkedin-copy-package`: Reviewed copy-ready LinkedIn post.

## Non-goals
Other roadmap capabilities; replacing shared infrastructure; automatic growth/Star/comment/DM activity; unreviewed publishing; no-runtime completion claims. No implementation, archive, commit, push or deployment in this authoring work.
