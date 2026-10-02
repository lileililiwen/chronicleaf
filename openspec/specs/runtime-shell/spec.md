# runtime-shell Specification

## Purpose
TBD - created by archiving change community-foundation. Update Purpose after archive.
## Requirements
### Requirement: SHELL-001 API liveness contract
The application SHALL return a stable credential-free liveness JSON response, and unknown API routes SHALL never return SPA HTML.

#### Scenario: Health succeeds
- **WHEN** GET /api/health is requested
- **THEN** status is 200 with application/json and status=ok, service=chronicleaf

#### Scenario: Unknown API fails
- **WHEN** GET /api/unknown is requested
- **THEN** status is 404 with JSON code=not_found and no HTML

### Requirement: SHELL-002 Actual React shell
The application SHALL serve a React shell identifying Chronicleaf without simulated milestones and SHALL distinguish missing frontend builds from missing assets.

#### Scenario: Built shell loads
- **WHEN** a browser opens / with built frontend assets present
- **THEN** Chronicleaf and its tagline are visible, with a setup-coming-later state

#### Scenario: Missing build
- **WHEN** / is requested without the frontend build
- **THEN** status is 503 with JSON code=web_assets_unavailable while /api/health remains 200

#### Scenario: Missing static asset
- **WHEN** an unknown static asset is requested
- **THEN** status is 404 rather than the SPA fallback

### Requirement: SHELL-003 Portable native build
The project SHALL build using .NET 10 and locked frontend dependencies from its own checkout without Forge or sibling sources, and failed native verification SHALL block completion.

#### Scenario: Isolated checkout
- **WHEN** native verification runs in a checkout without workspace parents or sibling libraries
- **THEN** API tests and frontend build pass and the container serves the health and shell contracts

#### Scenario: Native verification fails
- **WHEN** a required build or test exits nonzero
- **THEN** verification exits nonzero and completion evidence records the failing command

