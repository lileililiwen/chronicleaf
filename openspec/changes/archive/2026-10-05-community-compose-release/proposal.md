# Proposal: Reproducible two-service Community MVP packaging

## Why
requirement §8 Community; §9 MVP; §10 Compose; §11 launch requires this independently verifiable outcome: Reproducible two-service Community MVP packaging. Preserve the full brief and MVP-RELEASE scope boundary.

## What Changes
Introduce the Infrastructure/Deployment contract, persisted state or adapter and its user-facing evidence as defined in design.md. Status: Specified handoff; implementation not started.

## Package Boundary and Split Assessment
Own only Reproducible two-service Community MVP packaging; runtime C# .NET10, React/TypeScript where UI is described, repository Chronicleaf. Dependencies: markdown-review-export, llm-openai-adapter, llm-gemini-adapter, llm-anthropic-adapter, llm-openrouter-adapter, llm-ollama-adapter, background-jobs, local-media-storage. Different persistence/approval/publishing/Cloud lifecycles are separate packages in ../../../ROADMAP.md, not hidden sub-features. This unit is accepted by its scoped tests and outcome, not by completion of the full product. Demand/CLOUD priority does not authorize implementation. Excluded surfaces follow design and non-goals.

## Sibling and Shared Architecture Reconnaissance
**Keep local.** Chronicleaf domain/workflow lifecycle and user review are product-specific; no matching recurring implementation is evidenced in sibling platform contracts. Reuse dependency-owned access/jobs/storage contracts, never duplicate those concerns. Evidence inspected: dotnet-platform-libs/README.md lists Platform.Jobs and Platform.Storage families but no Milestone/narrative/voice/content/publisher workflow. No shared `/lib` added and no sibling edit authorized.

## BFS Impact Map
- Actor/flow: authenticated project owner, or explicitly granted Hosted role; anonymous only for documented webhook/public projection routes.
- Boundary/callers: src/Chronicleaf.Infrastructure/Deployment/, Api endpoint composition and src/Chronicleaf.Web/src/features/community-compose-release/ where described. Dependency contracts remain authoritative.
- Data/integration: schema, versions, outbound IO, idempotency and failures in design; append-only evidence and approval bindings remain preserved.
- Verification: CommunityComposeReleaseTests uses the exact positive/negative/boundary fixtures below; no mock success becomes provider/runtime proof.
- Privacy/compatibility: credentials by vault refs, scoped lookups, redacted errors, explicit model consent and reviewed public outputs. No sibling mutations.

## Capabilities
### New Capabilities
- `community-compose-release`: Reproducible two-service Community MVP packaging.

## Non-goals
Other roadmap capabilities; replacing shared infrastructure; automatic growth/Star/comment/DM activity; unreviewed publishing; no-runtime completion claims. No implementation, archive, commit, push or deployment in this authoring work.
