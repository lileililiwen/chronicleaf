# Proposal: Hosted team invitations and project roles

## Why
requirement §8 Hosted collaboration requires this independently verifiable outcome: Hosted team invitations and project roles. Preserve the full brief and CLOUD scope boundary.

## What Changes
Introduce the Application/Teams contract, persisted state or adapter and its user-facing evidence as defined in design.md. Status: Specified handoff; implementation not started.

## Package Boundary and Split Assessment
Own only Hosted team invitations and project roles; runtime C# .NET10, React/TypeScript where UI is described, repository Chronicleaf. Dependencies: cloud-tenant-boundary. Different persistence/approval/publishing/Cloud lifecycles are separate packages in ../../../ROADMAP.md, not hidden sub-features. This unit is accepted by its scoped tests and outcome, not by completion of the full product. Demand/CLOUD priority does not authorize implementation. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Chronicleaf domain/workflow lifecycle and user review are product-specific; no matching recurring implementation is evidenced in sibling platform contracts. Reuse dependency-owned access/jobs/storage contracts, never duplicate those concerns. Evidence inspected: dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage families but no Milestone/narrative/voice/content/publisher workflow. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: authenticated project owner, or explicitly granted Hosted role; anonymous only for documented webhook/public projection routes.
- Boundary/callers: src/Chronicleaf.Application/Teams/, Api endpoint composition and src/Chronicleaf.Web/src/features/team-project-permissions/ where described. Dependency contracts remain authoritative.
- Data/integration: schema, versions, outbound IO, idempotency and failures in design; append-only evidence and approval bindings remain preserved.
- Verification: TeamProjectPermissionsTests uses the exact positive/negative/boundary fixtures below; no mock success becomes provider/runtime proof.
- Privacy/compatibility: credentials by vault refs, scoped lookups, redacted errors, explicit model consent and reviewed public outputs. No sibling mutations.

## Capabilities
### New Capabilities
- `team-project-permissions`: Hosted team invitations and project roles.

## Non-goals
Other roadmap capabilities; replacing shared infrastructure; automatic growth/Star/comment/DM activity; unreviewed publishing; no-runtime completion claims. No implementation, archive, commit, push or deployment in this authoring work.
