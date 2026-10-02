# Proposal: Scoped filesystem media storage

## Why
requirement §3 screenshots; §10 filesystem/IObjectStorage requires this independently verifiable outcome: Scoped filesystem media storage. Preserve the full brief and MVP scope boundary.

## What Changes
Introduce the Infrastructure/Media contract, persisted state or adapter and its user-facing evidence as defined in design.md. Status: Specified handoff; implementation not started.

## Package Boundary and Split Assessment
Own only Scoped filesystem media storage; runtime C# .NET10, React/TypeScript where UI is described, repository Chronicleaf. Dependencies: community-persistence, community-access, projects-and-milestones. Different persistence/approval/publishing/Cloud lifecycles are separate packages in ../../../ROADMAP.md, not hidden sub-features. This unit is accepted by its scoped tests and outcome, not by completion of the full product. Demand/CLOUD priority does not authorize implementation. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Adopt.** Inspected sibling dotnet-platform-libs/src/Platform.Storage/Contracts/StorageRequests.cs IObjectStorage (UploadAsync/DownloadAsync/GetMetadataAsync/DeleteAsync/PresignAsync), StorageOptions.cs and Local adapter csproj net10.0; README also declares Platform.Storage.S3. Reuse Platform.Storage 0.1.0 plus selected Local/S3 published adapters; Chronicleaf owns authorization/media metadata facade. Infrastructure → versioned packages; no personal checkout or implicit source hook. Verify restore/build/provider conformance before implementation; unavailable release artifact blocks adoption rather than triggers a duplicate storage interface or sibling edits.

## BFS Impact Map
- Actor/flow: authenticated project owner, or explicitly granted Hosted role; anonymous only for documented webhook/public projection routes.
- Boundary/callers: src/Chronicleaf.Infrastructure/Media/, Api endpoint composition and src/Chronicleaf.Web/src/features/local-media-storage/ where described. Dependency contracts remain authoritative.
- Data/integration: schema, versions, outbound IO, idempotency and failures in design; append-only evidence and approval bindings remain preserved.
- Verification: LocalMediaStorageTests uses the exact positive/negative/boundary fixtures below; no mock success becomes provider/runtime proof.
- Privacy/compatibility: credentials by vault refs, scoped lookups, redacted errors, explicit model consent and reviewed public outputs. No sibling mutations.

## Capabilities
### New Capabilities
- `local-media-storage`: Scoped filesystem media storage.

## Non-goals
Other roadmap capabilities; replacing shared infrastructure; automatic growth/Star/comment/DM activity; unreviewed publishing; no-runtime completion claims. No implementation, archive, commit, push or deployment in this authoring work.
