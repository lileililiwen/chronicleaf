## ADDED Requirements

### Requirement: CLOUD-OBJECT-STORAGE-001 Tenant-scoped S3 media
Hosted object storage SHALL preserve scoped owned media and bounded authenticated access.

#### Scenario: CLOUD-OBJECT-STORAGE-001 Success
- **WHEN** a valid configured provider receives an upload
- **THEN** the stored hash and scoped key match the media record

#### Scenario: CLOUD-OBJECT-STORAGE-001 Failure
- **WHEN** a tenant requests another tenant object
- **THEN** access is denied before presign or byte transfer

#### Scenario: CLOUD-OBJECT-STORAGE-001 Boundary
- **WHEN** a presigned private URL expires
- **THEN** it no longer authorizes the object operation

### Requirement: CLOUD-OBJECT-STORAGE-002 Verified resumable storage migration
Local-to-S3 migration SHALL preserve source availability until each target object is verified.

#### Scenario: CLOUD-OBJECT-STORAGE-002 Success
- **WHEN** a copied object has matching checksum
- **THEN** its media reference can switch to the target provider

#### Scenario: CLOUD-OBJECT-STORAGE-002 Failure
- **WHEN** copy is interrupted or checksum differs
- **THEN** the source reference remains valid and retry resumes safely

#### Scenario: CLOUD-OBJECT-STORAGE-002 Boundary
- **WHEN** Community continues using Local mode
- **THEN** it requires no S3 credentials or remote storage

