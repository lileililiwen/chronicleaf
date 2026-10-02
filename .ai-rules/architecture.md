# Architecture

Milestone and versioned Evidence Bundle are authoritative; AI produces candidate content only. Draft approval binds content/evidence revision and edits revoke it. Platform adapters never own core states. Voice Profile overrides presets. Private evidence, model requests and publicly exportable content have distinct boundaries.

Target .NET 10 ordinary layering: Core domain; Application use cases/contracts; Infrastructure EF Core/PostgreSQL, Hangfire, storage; GitHub integration; AI providers; Capture Playwright; Publishers adapters; Api composition; Web React/TypeScript/Vite. Do not instantiate all layers merely to fill a diagram. Community remains one application + database.

Shared ownership checkpoint: sibling dotnet-platform-libs README exposes Platform.Storage and Local/S3 adapters; evaluate package compatibility before implementing IObjectStorage, never link production builds to a personal checkout. Consume published/versioned packages only when verified. Forge owns assembly, workspace-governance metadata policy, Driftwatchdog shared gates; do not clone those runtimes here. Milestone/narrative/voice/content contracts remain Chronicleaf-owned.
