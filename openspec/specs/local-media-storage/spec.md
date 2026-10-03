# local-media-storage Specification

## Purpose
TBD - created by archiving change local-media-storage. Update Purpose after archive.
## Requirements
### Requirement: LOCAL-MEDIA-STORAGE-001 Private verified media
Media SHALL be stored under server-generated scoped keys and served only to authorized owners.

#### Scenario: LOCAL-MEDIA-STORAGE-001 Success
- **WHEN** a valid bounded PNG is saved
- **THEN** its decoded type and hash match the stored private media

#### Scenario: LOCAL-MEDIA-STORAGE-001 Failure
- **WHEN** a media id belongs to another scope
- **THEN** the API returns 404 without bytes

#### Scenario: LOCAL-MEDIA-STORAGE-001 Boundary
- **WHEN** a traversal or symlink path is supplied
- **THEN** no file outside MediaRoot is read or written

### Requirement: LOCAL-MEDIA-STORAGE-002 Consistent storage lifecycle
Interrupted media writes SHALL remain unreferenced and retained evidence SHALL prevent deletion.

#### Scenario: LOCAL-MEDIA-STORAGE-002 Success
- **WHEN** a ready media upload is linked into evidence
- **THEN** it remains readable after restart

#### Scenario: LOCAL-MEDIA-STORAGE-002 Failure
- **WHEN** upload promotion or database registration fails
- **THEN** no ready media reference points to missing bytes

#### Scenario: LOCAL-MEDIA-STORAGE-002 Boundary
- **WHEN** referenced media is deleted
- **THEN** the API returns 409 media_in_use

