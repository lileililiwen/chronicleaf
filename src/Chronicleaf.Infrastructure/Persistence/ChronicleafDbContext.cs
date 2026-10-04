using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Chronicleaf.Core.Access;

// The Core record is addressed through an alias because a sibling namespace is also called Media.
using MediaRecord = Chronicleaf.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// The PostgreSQL context behind the Community data foundation: the singleton
/// <c>installation</c> row, the append-only <c>audit_events</c> log, the durable
/// <c>outbox</c>, the access package's <c>session</c>, <c>login_attempt</c> and
/// <c>access_state</c> tables, and the <c>projects</c>, <c>milestones</c>, <c>media</c>,
/// <c>evidence_bundles</c> and <c>evidence_items</c> tables that hold the owner's own records, the
/// <c>credentials</c> table that holds sealed provider credentials and no plaintext, and the
/// <c>voice_profiles</c> and <c>voice_drafts</c> tables that hold the owner's writing voice and the
/// text written under it (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// The three data-foundation tables are owned by their package and are not changed here; the
/// access tables are owned by the access package and arrived in their own forward migration. Every
/// later package adds its own tables in its own migration: this context never creates or edits
/// them, and nothing here exposes a raw-SQL or audit-editing path. All timestamps are
/// <c>timestamptz</c> and all identifiers are UUIDs, as the repository contracts require.
/// <para>
/// The <c>projects</c> and <c>milestones</c> records are immutable in the domain, so they are
/// written with explicit parameterized statements that carry the revision compare-and-set; the
/// change tracker is what reads them. Both revisions are declared concurrency tokens as well, so the
/// model itself records that a stale write is a conflict rather than an overwrite.
/// </para>
/// </remarks>
public sealed class ChronicleafDbContext(DbContextOptions<ChronicleafDbContext> options) : DbContext(options)
{
    /// <summary>The schema version this build writes and requires.</summary>
    public const int RequiredSchemaVersion = 1;

    public DbSet<Installation> Installations => Set<Installation>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    public DbSet<OwnerSession> Sessions => Set<OwnerSession>();

    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    public DbSet<AccessTokenState> AccessTokenStates => Set<AccessTokenState>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Milestone> Milestones => Set<Milestone>();

    public DbSet<Operation> Operations => Set<Operation>();

    public DbSet<MediaRecord> Media => Set<MediaRecord>();

    public DbSet<EvidenceBundle> EvidenceBundles => Set<EvidenceBundle>();

    public DbSet<EvidenceItem> EvidenceItems => Set<EvidenceItem>();

    public DbSet<Credential> Credentials => Set<Credential>();

    public DbSet<VoiceProfile> VoiceProfiles => Set<VoiceProfile>();

    public DbSet<VoiceDraft> VoiceDrafts => Set<VoiceDraft>();

    public DbSet<GenerationPreview> GenerationPreviews => Set<GenerationPreview>();

    public DbSet<GitHubConnection> GitHubConnections => Set<GitHubConnection>();

    public DbSet<SyncSource> SyncSources => Set<SyncSource>();

    public DbSet<ScreenshotCandidate> ScreenshotCandidates => Set<ScreenshotCandidate>();

    public DbSet<CaptureRequestRow> CaptureRequests => Set<CaptureRequestRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Installation>(entity =>
        {
            entity.ToTable("installation");
            entity.HasKey(installation => installation.Id);
            entity.Property(installation => installation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(installation => installation.SchemaVersion).HasColumnName("schema_version");
            entity.Property(installation => installation.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");

            // Added by the access package's forward migration and backfilled there for an
            // installation that already existed. It stays nullable in the database because the
            // data-foundation store still provisions the singleton row without it; the access
            // layer is what guarantees a non-null owner before any session exists.
            entity.Property(installation => installation.OwnerActorId).HasColumnName("owner_actor_id");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(auditEvent => auditEvent.Id);
            entity.Property(auditEvent => auditEvent.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(auditEvent => auditEvent.ActorId).HasColumnName("actor_id");
            entity.Property(auditEvent => auditEvent.Action)
                .HasColumnName("action")
                .HasMaxLength(80)
                .IsRequired();
            entity.Property(auditEvent => auditEvent.TargetId).HasColumnName("target_id").IsRequired();
            entity.Property(auditEvent => auditEvent.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(auditEvent => auditEvent.SafeMetadataJson)
                .HasColumnName("safe_metadata")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.HasIndex(auditEvent => auditEvent.TargetId).HasDatabaseName("ix_audit_events_target_id");
        });

        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(outboxEvent => outboxEvent.Id);
            entity.Property(outboxEvent => outboxEvent.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(outboxEvent => outboxEvent.Kind)
                .HasColumnName("kind")
                .HasMaxLength(80)
                .IsRequired();
            entity.Property(outboxEvent => outboxEvent.AggregateId).HasColumnName("aggregate_id").IsRequired();
            entity.Property(outboxEvent => outboxEvent.Revision).HasColumnName("revision").IsRequired();
            entity.Property(outboxEvent => outboxEvent.PayloadJson)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(outboxEvent => outboxEvent.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(outboxEvent => outboxEvent.DispatchedAt)
                .HasColumnName("dispatched_at")
                .HasColumnType("timestamp with time zone");

            // The uniqueness that makes redelivery of the same revision impossible.
            entity.HasIndex(outboxEvent => new { outboxEvent.Kind, outboxEvent.AggregateId, outboxEvent.Revision })
                .IsUnique()
                .HasDatabaseName("ux_outbox_kind_aggregate_revision");

            entity.HasIndex(outboxEvent => outboxEvent.DispatchedAt).HasDatabaseName("ix_outbox_dispatched_at");
        });

        modelBuilder.Entity<OwnerSession>(entity =>
        {
            entity.ToTable("session");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasColumnName("id").ValueGeneratedNever();

            // The opaque session token is never stored: only its SHA-256 hex, which is the lookup
            // key and therefore also the only thing a database disclosure can hand out.
            entity.Property(session => session.IdHash)
                .HasColumnName("id_hash")
                .HasMaxLength(OwnerSession.IdHashLength)
                .IsRequired();
            entity.Property(session => session.ActorId).HasColumnName("actor_id").IsRequired();
            entity.Property(session => session.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(session => session.LastSeenAt)
                .HasColumnName("last_seen_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(session => session.IdleExpiresAt)
                .HasColumnName("idle_expires_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(session => session.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(session => session.AntiforgeryTokenHash)
                .HasColumnName("antiforgery_token_hash")
                .HasMaxLength(OwnerSession.IdHashLength)
                .IsRequired();
            entity.Property(session => session.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");

            // Uniqueness is what makes a presented session token identify exactly one session.
            entity.HasIndex(session => session.IdHash)
                .IsUnique()
                .HasDatabaseName("ux_session_id_hash");
            entity.HasIndex(session => session.ActorId).HasDatabaseName("ix_session_actor_id");
        });

        modelBuilder.Entity<LoginAttempt>(entity =>
        {
            entity.ToTable("login_attempt");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(attempt => attempt.ClientKeyHash)
                .HasColumnName("client_key_hash")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(attempt => attempt.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone");

            // The throttle window is always "this client, since some instant", which is exactly
            // what the index answers.
            entity.HasIndex(attempt => new { attempt.ClientKeyHash, attempt.OccurredAt })
                .HasDatabaseName("ix_login_attempt_client_occurred_at");
        });

        modelBuilder.Entity<AccessTokenState>(entity =>
        {
            entity.ToTable("access_state");
            entity.HasKey(state => state.Id);
            entity.Property(state => state.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();
            entity.Property(state => state.TokenHashFingerprint)
                .HasColumnName("token_hash_fingerprint")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(state => state.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone");
        });

        // PROJECTS-AND-MILESTONES-002: a project belongs to exactly one owner scope, carries the
        // optimistic revision every edit presents, and is archived rather than deleted.
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(project => project.Id);
            entity.Property(project => project.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(project => project.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(project => project.Name)
                .HasColumnName("name")
                .HasMaxLength(Project.NameMaxLength)
                .IsRequired();
            entity.Property(project => project.Description)
                .HasColumnName("description")
                .HasMaxLength(Project.DescriptionMaxLength)
                .IsRequired();
            entity.Property(project => project.RepoConnectionId).HasColumnName("repo_connection_id");
            entity.Property(project => project.ProjectUrl)
                .HasColumnName("project_url")
                .HasMaxLength(Project.ProjectUrlMaxLength);
            entity.Property(project => project.Revision).HasColumnName("revision").IsRequired();
            entity.Property(project => project.ArchivedAt)
                .HasColumnName("archived_at")
                .HasColumnType("timestamp with time zone");

            // A stale write is a conflict, never an overwrite. The token is what the parameterized
            // compare-and-set in PostgresProjectStore matches on.
            entity.Property(project => project.Revision).IsConcurrencyToken();

            // Every read is scoped, and a list is ordered by name, so the index serves both.
            entity.HasIndex(project => new { project.ScopeId, project.Name })
                .HasDatabaseName("ix_projects_scope_id_name");
        });

        // PROJECTS-AND-MILESTONES-001: a milestone is owned by its project and carries the source it
        // came from, the status only the owner may change, and its own revision.
        modelBuilder.Entity<Milestone>(entity =>
        {
            entity.ToTable("milestones");
            entity.HasKey(milestone => milestone.Id);
            entity.Property(milestone => milestone.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(milestone => milestone.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(milestone => milestone.Title)
                .HasColumnName("title")
                .HasMaxLength(Milestone.TitleMaxLength)
                .IsRequired();
            entity.Property(milestone => milestone.Version)
                .HasColumnName("version")
                .HasMaxLength(Milestone.VersionMaxLength);
            entity.Property(milestone => milestone.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(milestone => milestone.Type)
                .HasColumnName("type")
                .HasMaxLength(MilestoneTypeMaxLength)
                .IsRequired();
            entity.Property(milestone => milestone.Source)
                .HasColumnName("source")
                .HasMaxLength(MilestoneSource.MaxLength)
                .IsRequired();
            entity.Property(milestone => milestone.SourceKey)
                .HasColumnName("source_key")
                .HasMaxLength(MilestoneSource.SourceKeyMaxLength);
            entity.Property(milestone => milestone.Status)
                .HasColumnName("status")
                .HasMaxLength(MilestoneStatusMaxLength)
                .IsRequired();
            entity.Property(milestone => milestone.Revision).HasColumnName("revision").IsRequired();
            entity.Property(milestone => milestone.Notes)
                .HasColumnName("notes")
                .HasMaxLength(Milestone.NotesMaxLength)
                .IsRequired();
            entity.Property(milestone => milestone.KnownLimitations)
                .HasColumnName("known_limitations")
                .HasColumnType("text[]")
                .IsRequired();
            entity.Property(milestone => milestone.NextSteps)
                .HasColumnName("next_steps")
                .HasColumnType("text[]")
                .IsRequired();

            entity.Property(milestone => milestone.Revision).IsConcurrencyToken();

            // The list order every read uses: occurredAt desc, id desc.
            entity.HasIndex(milestone => new { milestone.ProjectId, milestone.OccurredAt, milestone.Id })
                .HasDatabaseName("ix_milestones_project_id_occurred_at_id");

            // The idempotence of an import, enforced by the database rather than by a read-then-write
            // in application code. The filter is what makes it partial: a record the owner wrote has
            // no source key, so the uniqueness never constrains manual history and several manual
            // records can share a project, a source and an empty key.
            entity.HasIndex(milestone => new { milestone.ProjectId, milestone.Source, milestone.SourceKey })
                .IsUnique()
                .HasFilter("source_key IS NOT NULL")
                .HasDatabaseName("ux_milestones_project_id_source_source_key");
        });

        // The durable operation and the whole of its queue and result state
        // (BACKGROUND-JOBS-001/002). The revision column carries the EF concurrency token for the same
        // reason the projects and milestones tables carry one: a stale write is a conflict, never an
        // overwrite. In practice the worker never relies on it - every one of its writes is already a
        // conditional statement - but the token keeps an accidental context.SaveChanges from turning a
        // lost claim into a silent overwrite.
        modelBuilder.Entity<Operation>(entity =>
        {
            entity.ToTable("operations");
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(operation => operation.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(operation => operation.Kind)
                .HasColumnName("kind")
                .HasMaxLength(OperationKindMaxLength)
                .IsRequired();
            entity.Property(operation => operation.TargetId).HasColumnName("target_id").IsRequired();
            entity.Property(operation => operation.InputRevision).HasColumnName("input_revision").IsRequired();
            entity.Property(operation => operation.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(OperationIdempotencyKeyMaxLength)
                .IsRequired();
            entity.Property(operation => operation.Fingerprint)
                .HasColumnName("fingerprint")
                .HasMaxLength(OperationFingerprintMaxLength)
                .IsRequired();
            entity.Property(operation => operation.Status)
                .HasColumnName("status")
                .HasConversion(
                    status => OperationStatuses.ToWire(status),
                    wire => ParseStatus(wire))
                .HasMaxLength(OperationStatusMaxLength)
                .IsRequired();
            entity.Property(operation => operation.Attempt).HasColumnName("attempt").IsRequired();
            entity.Property(operation => operation.HeartbeatAt)
                .HasColumnName("heartbeat_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.ErrorCode)
                .HasColumnName("error_code")
                .HasMaxLength(OperationErrorCodeMaxLength);
            entity.Property(operation => operation.ResultRef)
                .HasColumnName("result_ref")
                .HasMaxLength(OperationResultRefMaxLength);
            entity.Property(operation => operation.LeaseExpiresAt)
                .HasColumnName("lease_expires_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.LeaseToken).HasColumnName("lease_token");
            entity.Property(operation => operation.NextAttemptAt)
                .HasColumnName("next_attempt_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.CancelRequestedAt)
                .HasColumnName("cancel_requested_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.LeaseRecoveries).HasColumnName("lease_recoveries").IsRequired();
            entity.Property(operation => operation.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(operation => operation.Revision)
                .HasColumnName("revision")
                .IsConcurrencyToken();

            // The idempotence of a request, decided by the database. A repeated (scope, kind, key)
            // cannot create a second operation, and the stored fingerprint is what tells a replay of
            // the same work apart from a conflict on different work.
            entity.HasIndex(operation => new { operation.ScopeId, operation.Kind, operation.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("ux_operations_scope_id_kind_idempotency_key");

            // Every list and the lease sweep read a scope newest-first.
            entity.HasIndex(operation => new { operation.ScopeId, operation.CreatedAt, operation.Id })
                .HasDatabaseName("ix_operations_scope_id_created_at_id");

            // The claim that is allowed to recover an expired lease. Without this index every claim
            // from `queued` would still scan the running rows of the scope.
            entity.HasIndex(operation => new { operation.Status, operation.LeaseExpiresAt })
                .HasDatabaseName("ix_operations_status_lease_expires_at");
        });

        // LOCAL-MEDIA-STORAGE-001/002: one private image, addressed by a server-generated scoped key.
        // The row is the only description of the object: it names the key, the decoded type, the
        // verified length, the digest of exactly those bytes and the status that decides whether the
        // object may be served at all.
        modelBuilder.Entity<MediaRecord>(entity =>
        {
            entity.ToTable("media");
            entity.HasKey(media => media.Id);
            entity.Property(media => media.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(media => media.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(media => media.ProjectId).HasColumnName("project_id").IsRequired();

            // The key is generated here and is the only thing that reaches the storage provider. There
            // is no absolute path column, so a disclosure of this table cannot disclose where the
            // operator's bytes live.
            entity.Property(media => media.StorageKey)
                .HasColumnName("storage_key")
                .HasMaxLength(MediaLimits.MaximumKeyLength)
                .IsRequired();
            entity.Property(media => media.MediaType)
                .HasColumnName("media_type")
                .HasMaxLength(MediaRecord.MediaTypeMaxLength)
                .IsRequired();
            entity.Property(media => media.ByteLength).HasColumnName("byte_length").IsRequired();
            entity.Property(media => media.Sha256)
                .HasColumnName("sha256")
                .HasMaxLength(MediaRecord.Sha256HexLength)
                .IsRequired();
            entity.Property(media => media.Status)
                .HasColumnName("status")
                .HasMaxLength(MediaRecord.StatusMaxLength)
                .IsRequired();
            entity.Property(media => media.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The operation that produced the image, or null for one the owner uploaded. Deliberately
            // not a foreign key: operations are the jobs package's table and this package must not
            // decide what happens to an operation that outlives the image it produced.
            entity.Property(media => media.OwnerOperationId).HasColumnName("owner_operation_id");
            entity.Property(media => media.Revision).HasColumnName("revision").IsRequired();

            // A stale write is a conflict, never an overwrite, for the same reason the projects and
            // milestones tables carry a token.
            entity.Property(media => media.Revision).IsConcurrencyToken();

            // Every read is scoped, and a key is globally unique because the media id in it is.
            entity.HasIndex(media => new { media.ScopeId, media.CreatedAt, media.Id })
                .HasDatabaseName("ix_media_scope_id_created_at_id");

            // One object per key, enforced by the database. The key is generated from the id, so this
            // is what makes a second row able to claim bytes that already exist impossible.
            entity.HasIndex(media => media.StorageKey)
                .IsUnique()
                .HasDatabaseName("ux_media_storage_key");

            // The purge's only query: staged rows older than a cutoff, oldest first.
            entity.HasIndex(media => new { media.Status, media.CreatedAt })
                .HasDatabaseName("ix_media_status_created_at");

            // The project is the owner of the image, enforced by the database rather than only by the
            // service's lookup. No cascade: a project is archived rather than deleted, and a row of
            // evidence that has been stored must not be removed by a change to the project that
            // happened to own it.
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(media => media.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_media_project_id");
        });

        // MILESTONE-EVIDENCE-001/002: the immutable provenance snapshots. A bundle row is a revision
        // and an item row is one collected fact inside it; both are written once and never written
        // again, and the forward migration installs the triggers that refuse an update or a delete on
        // either table. The mapping below is what makes that schema, and the constraints are part of
        // the evidence argument rather than bookkeeping: the unique index is what decides a race
        // between two appends, and the availability check is what makes an unavailable source unable
        // to claim a payload or media.
        modelBuilder.Entity<EvidenceBundle>(entity =>
        {
            entity.ToTable("evidence_bundles");
            entity.HasKey(bundle => bundle.Id);
            entity.Property(bundle => bundle.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(bundle => bundle.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(bundle => bundle.Revision).HasColumnName("revision").IsRequired();
            entity.Property(bundle => bundle.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The item set is not a navigation. A bundle is a snapshot assembled by the store's own
            // SQL, read back in one statement, and the change tracker is never asked to walk from a
            // bundle to its items - which is the only way this model can honestly say that evidence
            // is never rewritten.
            entity.Ignore(bundle => bundle.Items);

            // One bundle per milestone per revision. This is the whole concurrency story: an append
            // inserts at exactly expectedRevision + 1 and lets this index say whether it won.
            entity.HasIndex(bundle => new { bundle.MilestoneId, bundle.Revision })
                .IsUnique()
                .HasDatabaseName("ux_evidence_bundles_milestone_id_revision");

            // The list's own order - revision descending, id descending - is served by this index
            // rather than by a sort over the milestone's whole history.
            entity.HasIndex(bundle => new { bundle.MilestoneId, bundle.Revision, bundle.Id })
                .HasDatabaseName("ix_evidence_bundles_milestone_revision_id");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(bundle => bundle.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evidence_bundles_milestone_id");
        });

        modelBuilder.Entity<EvidenceItem>(entity =>
        {
            entity.ToTable("evidence_items");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(item => item.BundleId).HasColumnName("bundle_id").IsRequired();
            entity.Property(item => item.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(item => item.Kind).HasColumnName("kind").HasMaxLength(EvidenceKindMaxLength).IsRequired();
            entity.Property(item => item.SourceRef)
                .HasColumnName("source_ref")
                .HasMaxLength(EvidenceLimits.SourceRefMaxLength)
                .IsRequired();
            entity.Property(item => item.SourceRevision)
                .HasColumnName("source_revision")
                .HasMaxLength(EvidenceLimits.SourceRevisionMaxLength)
                .IsRequired();
            entity.Property(item => item.CapturedAt)
                .HasColumnName("captured_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(item => item.PayloadSha256)
                .HasColumnName("payload_sha256")
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(item => item.Text).HasColumnName("text").IsRequired();
            entity.Property(item => item.MediaId).HasColumnName("media_id");
            entity.Property(item => item.Redactions)
                .HasColumnName("redactions")
                .HasColumnType("text[]")
                .IsRequired();
            entity.Property(item => item.Availability)
                .HasColumnName("availability")
                .HasMaxLength(EvidenceAvailabilityMaxLength)
                .IsRequired();
            entity.Property(item => item.SupersedesId).HasColumnName("supersedes_id");

            // The media reference checker's only query: "does anything still point at this media?".
            // A partial index is not required - media ids are sparse - but the lookup has to be a
            // single indexed probe rather than a scan of the evidence history.
            entity.HasIndex(item => item.MediaId).HasDatabaseName("ix_evidence_items_media_id");

            // The store reads a bundle's items back in id order, so this index is that read.
            entity.HasIndex(item => new { item.BundleId, item.Id })
                .HasDatabaseName("ix_evidence_items_bundle_id_id");

            // The scope is carried on the item as well as on the bundle so a scope-bound lookup
            // never has to trust a join. No cascade anywhere: evidence that has been collected is
            // retained even if the milestone or the media row that owned it is later archived.
            entity.HasOne<EvidenceBundle>()
                .WithMany()
                .HasForeignKey(item => item.BundleId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evidence_items_bundle_id");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(item => item.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evidence_items_milestone_id");

            entity.HasOne<MediaRecord>()
                .WithMany()
                .HasForeignKey(item => item.MediaId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evidence_items_media_id");

            // A correction names the item it corrects, and that item is kept. The self-reference is
            // restricted for the same reason the others are: nothing here is ever cascaded away.
            entity.HasOne<EvidenceItem>()
                .WithMany()
                .HasForeignKey(item => item.SupersedesId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evidence_items_supersedes_id");
        });

        // CREDENTIAL-VAULT-001: a provider credential, and the only place a secret is stored. There is
        // no plaintext column because there is no plaintext value to write: what is here is the
        // sealed body with its tag, the nonce it was sealed with, and the key version the ciphertext
        // is bound to. The unique (scope_id, provider) index is what makes "this scope's credential
        // for this provider" a single row, and what turns two concurrent first saves into one row and
        // one conflict instead of two rows.
        modelBuilder.Entity<Credential>(entity =>
        {
            entity.ToTable("credentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(credential => credential.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(credential => credential.Provider)
                .HasColumnName("provider")
                .HasMaxLength(CredentialProviders.MaxLength)
                .IsRequired();
            entity.Property(credential => credential.Ciphertext)
                .HasColumnName("ciphertext")
                .HasColumnType("bytea")
                .IsRequired();
            entity.Property(credential => credential.Nonce)
                .HasColumnName("nonce")
                .HasColumnType("bytea")
                .IsRequired();
            entity.Property(credential => credential.KeyVersion)
                .HasColumnName("key_version")
                .IsRequired();
            entity.Property(credential => credential.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(credential => credential.Revision)
                .HasColumnName("revision")
                .IsRequired();

            // The same reason the projects and milestones revisions carry a token: a stale write is a
            // conflict, never an overwrite. In practice the store's compare-and-set decides, and this
            // keeps an accidental SaveChanges from turning a lost race into a silent one.
            entity.Property(credential => credential.Revision).IsConcurrencyToken();

            entity.HasIndex(credential => new { credential.ScopeId, credential.Provider })
                .IsUnique()
                .HasDatabaseName("ux_credentials_scope_id_provider");

            // The order a scope-bound list reads, so the page position is an index walk.
            entity.HasIndex(credential => new { credential.ScopeId, credential.Provider, credential.Id })
                .HasDatabaseName("ix_credentials_scope_id_provider_id");
        });

        // VOICE-PROFILES-001: one voice profile per project, holding a preset and the owner's
        // explicit deviations from it. There is no resolved column and there never will be one: the
        // effective voice is derived at read time, so a stored copy could only ever disagree with
        // its own inputs. The overrides ride in a jsonb column through a value converter, because
        // the difference between "not overridden" and "overridden to false" is exactly a JSON null
        // against a JSON false, and a relational column per field could not express it.
        modelBuilder.Entity<VoiceProfile>(entity =>
        {
            entity.ToTable("voice_profiles");
            entity.HasKey(profile => profile.Id);
            entity.Property(profile => profile.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(profile => profile.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(profile => profile.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(profile => profile.PresetId)
                .HasColumnName("preset_id")
                .HasMaxLength(VoiceLimits.PresetIdMaxLength)
                .IsRequired();
            entity.Property(profile => profile.Overrides)
                .HasColumnName("overrides")
                .HasColumnType("jsonb")
                .HasConversion(
                    value => VoiceOverridesCodec.Serialize(value),
                    json => VoiceOverridesCodec.Deserialize(json) ?? VoiceOverrides.Empty)
                .IsRequired();
            entity.Property(profile => profile.Revision).HasColumnName("revision").IsRequired();
            entity.Property(profile => profile.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(profile => profile.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(profile => profile.Revision).IsConcurrencyToken();

            // One row per project per scope. This index is what turns two concurrent first saves
            // into one row and one conflict rather than two rows that both claim to be the voice.
            entity.HasIndex(profile => new { profile.ScopeId, profile.ProjectId })
                .IsUnique()
                .HasDatabaseName("ux_voice_profiles_scope_id_project_id");

            // The profile is only ever reached through its project, and a project is never deleted,
            // so a profile is retained with the project it states the voice for.
            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(profile => profile.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_voice_profiles_project_id");
        });

        // VOICE-PROFILES-001/002: one owner-written draft, with the voice it was created under
        // copied into it. The `voice_snapshot` column is the whole point of the requirement's
        // boundary scenario: it holds a self-contained value, so there is no id in it for a later
        // profile edit to follow, and a draft written under an older voice keeps that voice.
        modelBuilder.Entity<VoiceDraft>(entity =>
        {
            entity.ToTable("voice_drafts");
            entity.HasKey(draft => draft.Id);
            entity.Property(draft => draft.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(draft => draft.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(draft => draft.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(draft => draft.Title).HasColumnName("title").IsRequired();
            entity.Property(draft => draft.Body).HasColumnName("body").IsRequired();
            entity.Property(draft => draft.Status)
                .HasColumnName("status")
                .HasMaxLength(VoiceDraftStatusMaxLength)
                .IsRequired();
            entity.Property(draft => draft.Revision).HasColumnName("revision").IsRequired();
            entity.Property(draft => draft.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(draft => draft.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(draft => draft.VoiceSnapshot)
                .HasColumnName("voice_snapshot")
                .HasColumnType("jsonb")
                .HasConversion(
                    value => VoiceSnapshotCodec.Serialize(value),
                    json => VoiceSnapshotCodec.Deserialize(json)!)
                .IsRequired();

            entity.Property(draft => draft.Revision).IsConcurrencyToken();

            // The order the draft list reads, so the cursor is an index walk rather than a sort.
            entity.HasIndex(draft => new { draft.ScopeId, draft.ProjectId, draft.CreatedAt, draft.Id })
                .HasDatabaseName("ix_voice_drafts_scope_id_project_id_created_at_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(draft => draft.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_voice_drafts_project_id");
        });

        // LLM-PROVIDER-CONTRACT-002: one owner-authorized redacted input snapshot, written once.
        // There is no revision column, no updated_at and no concurrency token on this table, and
        // that is the requirement rather than an omission: a consent that can be edited in place is
        // a consent the owner never gave, so the only way a row changes is that it expires and stops
        // being usable. `sha256` covers the snapshot bytes so a stored row can be shown to be the
        // bytes the owner saw, and `milestone_id` is nullable because the generic route is
        // expressible ahead of the extensions that own the other purposes.
        modelBuilder.Entity<GenerationPreview>(entity =>
        {
            entity.ToTable("generation_previews");
            entity.HasKey(preview => preview.Id);
            entity.Property(preview => preview.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(preview => preview.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(preview => preview.MilestoneId).HasColumnName("milestone_id");
            entity.Property(preview => preview.Purpose)
                .HasColumnName("purpose")
                .HasMaxLength(GenerationPreviewPurposeMaxLength)
                .IsRequired();
            entity.Property(preview => preview.AggregateRef)
                .HasColumnName("aggregate_ref")
                .HasMaxLength(GenerationPreviewPurposeMaxLength)
                .IsRequired();
            entity.Property(preview => preview.SnapshotJson)
                .HasColumnName("snapshot_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(preview => preview.Sha256)
                .HasColumnName("sha256")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(preview => preview.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // Expiry is the only lifecycle a row has, so the column the sweep and the stale check
            // share is an index rather than a filter over the whole table.
            entity.HasIndex(preview => new { preview.ScopeId, preview.ExpiresAt })
                .HasDatabaseName("ix_generation_previews_scope_id_expires_at");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(preview => preview.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_generation_previews_milestone_id");
        });

        // GITHUB-EVIDENCE-001: the repository a project collects evidence from. Two name columns
        // and a scope, and no URL column at all - a host is a constant of this product, never a
        // property of a stored row. `credential_ref` is the vault's own id, so this table cannot
        // hold a token. One connection per project: the unique index is what makes a second
        // POST for the same project replace the first instead of accumulating repositories.
        modelBuilder.Entity<GitHubConnection>(entity =>
        {
            entity.ToTable("github_connections");
            entity.HasKey(connection => connection.Id);
            entity.Property(connection => connection.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(connection => connection.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(connection => connection.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(connection => connection.Owner)
                .HasColumnName("owner")
                .HasMaxLength(GitHubOwnerMaxLength)
                .IsRequired();
            entity.Property(connection => connection.Repo)
                .HasColumnName("repo")
                .HasMaxLength(GitHubRepoMaxLength)
                .IsRequired();
            entity.Property(connection => connection.CredentialRef).HasColumnName("credential_ref");
            entity.Property(connection => connection.DefaultBranch)
                .HasColumnName("default_branch")
                .HasMaxLength(GitHubRefMaxLength);
            entity.Property(connection => connection.RepositoryId).HasColumnName("repository_id");
            entity.Property(connection => connection.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(connection => connection.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(connection => new { connection.ScopeId, connection.ProjectId })
                .IsUnique()
                .HasDatabaseName("ux_github_connections_scope_id_project_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(connection => connection.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_connections_project_id");
        });

        // GITHUB-EVIDENCE-002: one row per collection run, written when the owner asks and completed
        // by the job. The requested refs are here because a job payload carries an operation id and
        // nothing else, so the words the owner typed have to be durable somewhere the worker can read
        // them from. The resolved revisions, the endpoint list and the two limitation markers are
        // filled in when the run finishes and stay null until then, so a row that was asked for and
        // never collected says so instead of looking like a run that read nothing.
        modelBuilder.Entity<SyncSource>(entity =>
        {
            entity.ToTable("sync_sources");
            entity.HasKey(source => source.Id);
            entity.Property(source => source.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(source => source.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(source => source.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(source => source.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(source => source.ExpectedBundleRevision)
                .HasColumnName("expected_bundle_revision")
                .IsRequired();
            entity.Property(source => source.Owner)
                .HasColumnName("owner")
                .HasMaxLength(GitHubOwnerMaxLength)
                .IsRequired();
            entity.Property(source => source.Repo)
                .HasColumnName("repo")
                .HasMaxLength(GitHubRepoMaxLength)
                .IsRequired();
            entity.Property(source => source.HeadRef)
                .HasColumnName("head_ref")
                .HasMaxLength(GitHubRefMaxLength)
                .IsRequired();
            entity.Property(source => source.BaseRef)
                .HasColumnName("base_ref")
                .HasMaxLength(GitHubRefMaxLength);
            entity.Property(source => source.ReleaseTag)
                .HasColumnName("release_tag")
                .HasMaxLength(GitHubTagMaxLength);
            entity.Property(source => source.IssueNumber).HasColumnName("issue_number");
            entity.Property(source => source.PullRequestNumber).HasColumnName("pull_request_number");
            entity.Property(source => source.BaseSha)
                .HasColumnName("base_sha")
                .HasMaxLength(GitHubShaMaxLength);
            entity.Property(source => source.HeadSha)
                .HasColumnName("head_sha")
                .HasMaxLength(GitHubShaMaxLength);
            entity.Property(source => source.Endpoints)
                .HasColumnName("endpoints")
                .HasMaxLength(GitHubEndpointsMaxLength);
            entity.Property(source => source.FetchedAt)
                .HasColumnName("fetched_at")
                .HasColumnType("timestamp with time zone");
            // The four count columns default to zero in the database, not only in the domain record. A
            // run is inserted the moment an owner asks for it and filled in when it finishes, so the
            // defaults are what make "asked for, not yet collected" a state the database can hold
            // without this store having to invent numbers it has not read.
            entity.Property(source => source.CommitCount)
                .HasColumnName("commit_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(source => source.ChangedFileCount)
                .HasColumnName("changed_file_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(source => source.Truncated)
                .HasColumnName("truncated")
                .HasDefaultValue(false)
                .IsRequired();
            entity.Property(source => source.ExcludedFileCount)
                .HasColumnName("excluded_file_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(source => source.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(source => source.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One run per operation, and the worker's lookup: it is handed an operation id and
            // nothing else, so this index is the only way it can find the request it is collecting.
            entity.HasIndex(source => new { source.ScopeId, source.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_sync_sources_scope_id_operation_id");

            // A milestone's runs are read newest first, so the index leads with the milestone and
            // the time the run was asked for.
            entity.HasIndex(source => new { source.ScopeId, source.MilestoneId, source.CreatedAt })
                .HasDatabaseName("ix_sync_sources_scope_id_milestone_id_created_at");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(source => source.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_sync_sources_milestone_id");
        });

        // -----------------------------------------------------------------------------------------
        // screenshot-capture. Two tables and one uniqueness tuple between them: the request a job reads
        // its work back from, and the candidates that job produces.
        // -----------------------------------------------------------------------------------------

        modelBuilder.Entity<CaptureRequestRow>(entity =>
        {
            entity.ToTable("capture_requests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(request => request.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(request => request.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(request => request.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(request => request.Urls)
                .HasColumnName("urls")
                .HasColumnType("jsonb")
                .HasConversion(
                    urls => CaptureColumnCodec.SerializeUrls(urls),
                    json => CaptureColumnCodec.DeserializeUrls(json))
                .IsRequired();
            entity.Property(request => request.Viewports)
                .HasColumnName("viewports")
                .HasColumnType("jsonb")
                .HasConversion(
                    viewports => CaptureColumnCodec.SerializeViewports(viewports),
                    json => CaptureColumnCodec.DeserializeViewports(json))
                .IsRequired();

            // The comparers, set after the properties rather than in the fluent chain. Without one the
            // mapper compares the two lists by reference, so an unchanged request looks changed to the
            // change tracker and is written back on every read. Ordered and ordinal, because the order
            // is the order the job walks and two lists holding the same strings in a different order
            // are not the same capture.
            entity.Metadata
                .FindProperty(nameof(CaptureRequestRow.Urls))!
                .SetValueComparer(StringListComparer.Instance);
            entity.Metadata
                .FindProperty(nameof(CaptureRequestRow.Viewports))!
                .SetValueComparer(StringListComparer.Instance);
            entity.Property(request => request.WaitSelector)
                .HasColumnName("wait_selector")
                .HasMaxLength(CaptureSelectorMaxLength);
            entity.Property(request => request.WaitMilliseconds)
                .HasColumnName("wait_milliseconds")
                .IsRequired();
            entity.Property(request => request.FullPage)
                .HasColumnName("full_page")
                .HasDefaultValue(false)
                .IsRequired();
            entity.Property(request => request.ExpectedBundleRevision)
                .HasColumnName("expected_bundle_revision")
                .IsRequired();
            entity.Property(request => request.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One request per job. The worker is handed an operation id and nothing else, so this index
            // is the only way it can find the words the owner typed - and a second request row for one
            // job would mean a re-run could photograph something nobody asked for.
            entity.HasIndex(request => new { request.ScopeId, request.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_capture_requests_scope_id_operation_id");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(request => request.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_capture_requests_milestone_id");
        });

        modelBuilder.Entity<ScreenshotCandidate>(entity =>
        {
            entity.ToTable("screenshot_candidates");
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(candidate => candidate.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(candidate => candidate.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(candidate => candidate.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(candidate => candidate.MediaId).HasColumnName("media_id").IsRequired();
            entity.Property(candidate => candidate.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(candidate => candidate.SourceUrl)
                .HasColumnName("source_url")
                .HasMaxLength(CaptureUrlMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.FinalUrl)
                .HasColumnName("final_url")
                .HasMaxLength(CaptureUrlMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.Viewport)
                .HasColumnName("viewport")
                .HasColumnType("jsonb")
                .HasConversion(
                    viewport => CaptureColumnCodec.SerializeViewport(viewport),
                    // A stored object that names a viewport this package does not offer, or that carries
                    // numbers other than that viewport's, reads as null. A candidate is still read; its
                    // viewport is simply not one this build can claim was used.
                    json => CaptureColumnCodec.DeserializeViewport(json) ?? CaptureViewport.Desktop)
                .IsRequired();
            entity.Property(candidate => candidate.CapturedAt)
                .HasColumnName("captured_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The three curation fields. They are stored rather than derived, with the values this
            // package writes, because screenshot-curation is the package that will move them: a column
            // that does not exist cannot be constrained later without a migration, and a flag that was
            // only ever computed has no history to curate.
            entity.Property(candidate => candidate.Status)
                .HasColumnName("status")
                .HasMaxLength(CaptureStatusMaxLength)
                .HasDefaultValue(ScreenshotCandidateStatus.Candidate)
                .IsRequired();
            entity.Property(candidate => candidate.Caption)
                .HasColumnName("caption")
                .HasMaxLength(CaptureCaptionMaxLength)
                .HasDefaultValue(string.Empty)
                .IsRequired();
            entity.Property(candidate => candidate.IsPrimary)
                .HasColumnName("is_primary")
                .HasDefaultValue(false)
                .IsRequired();
            entity.Property(candidate => candidate.Revision)
                .HasColumnName("revision")
                .HasDefaultValue(1L)
                .IsRequired();
            entity.Property(candidate => candidate.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The design's tuple, as a database constraint rather than a convention: one candidate per
            // (operation, source URL, viewport). A job may be retried, resumed or delivered twice, and
            // without this a re-run would write a second copy of the same picture. jsonb has a btree
            // equality operator, so the viewport is part of the key rather than filtered out of it -
            // which is what lets one job capture the same URL at both viewports and get two candidates.
            entity.HasIndex(candidate => new { candidate.OperationId, candidate.SourceUrl, candidate.Viewport })
                .IsUnique()
                .HasDatabaseName("ux_screenshot_candidates_operation_id_source_url_viewport");

            // A milestone's candidates are read newest first, so the index leads with the milestone and
            // the time the row was inserted.
            entity.HasIndex(candidate => new { candidate.ScopeId, candidate.MilestoneId, candidate.CreatedAt })
                .HasDatabaseName("ix_screenshot_candidates_scope_id_milestone_id_created_at");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(candidate => candidate.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_screenshot_candidates_milestone_id");

            // The image is owned by the delivered media store, not by this table. The foreign key is
            // what makes "a candidate without bytes is not a candidate" enforced rather than merely
            // intended, and a restrictive delete means a media record cannot be removed out from under a
            // row that points at it.
            entity.HasOne<MediaRecord>()
                .WithMany()
                .HasForeignKey(candidate => candidate.MediaId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_screenshot_candidates_media_id");
        });
    }

    /// <summary>At most 512 characters for a wait selector, matching the application-side bound.</summary>
    private const int CaptureSelectorMaxLength = 512;

    /// <summary>At most 2048 characters for a stored URL, matching the Core record's own bound.</summary>
    private const int CaptureUrlMaxLength = ScreenshotCandidateLimits.MaximumUrlLength;

    /// <summary>At most 32 characters for a candidate status.</summary>
    private const int CaptureStatusMaxLength = 32;

    /// <summary>At most 500 characters for a caption. This package writes an empty one.</summary>
    private const int CaptureCaptionMaxLength = 500;

    /// <summary>At most 39 characters; the longest owner segment GitHub accepts.</summary>
    private const int GitHubOwnerMaxLength = 39;

    /// <summary>At most 100 characters; the longest repository name GitHub accepts.</summary>
    private const int GitHubRepoMaxLength = 100;

    /// <summary>At most 255 characters; a ref is a branch or tag name, not a path.</summary>
    private const int GitHubRefMaxLength = 255;

    /// <summary>At most 100 characters; the longest release tag a request may name.</summary>
    private const int GitHubTagMaxLength = 100;

    /// <summary>At most 64 characters; a SHA-1 or SHA-256 revision is hex.</summary>
    private const int GitHubShaMaxLength = 64;

    /// <summary>
    /// At most 2000 characters for the consulted path list. A run consults at most nine endpoints,
    /// so this is generous rather than tight, and a run that somehow exceeds it is refused by the
    /// column instead of being silently shortened.
    /// </summary>
    private const int GitHubEndpointsMaxLength = 2000;

    /// <summary>At most 12 characters; the longest draft status is <c>approved</c>.</summary>
    private const int VoiceDraftStatusMaxLength = 12;

    /// <summary>
    /// At most 64 characters; it holds a purpose name and a GUID, and the longest purpose this
    /// build enables is <c>milestone-story</c>.
    /// </summary>
    private const int GenerationPreviewPurposeMaxLength = GenerationPreviewPurposes.MaxLength;

    /// <summary>A lower-case hex SHA-256 is 64 characters, with no room to spare and none to grow.</summary>
    private const int Sha256HexMaxLength = 64;

    /// <summary>At most 16 characters; the longest evidence kind is <c>project-metadata</c>.</summary>
    private const int EvidenceKindMaxLength = 16;

    /// <summary>At most 12 characters; the longest availability is <c>unavailable</c>.</summary>
    private const int EvidenceAvailabilityMaxLength = 12;

    /// <summary>At most 80 characters, matching the delivered outbox kind column.</summary>
    private const int OperationKindMaxLength = 80;

    /// <summary>At most 200 characters; a caller's key, stored as given and never interpreted.</summary>
    private const int OperationIdempotencyKeyMaxLength = 200;

    /// <summary>
    /// At most 128 characters. The fingerprint is a fixed-shape derived value, not a payload, so this
    /// bound cannot be reached by a caller and exists only so the column is not unbounded.
    /// </summary>
    private const int OperationFingerprintMaxLength = 128;

    /// <summary>At most 16 characters; the longest status is <c>superseded</c>.</summary>
    private const int OperationStatusMaxLength = 16;

    /// <summary>At most 64 characters; the longest code is <c>operation_provider_unavailable</c>.</summary>
    private const int OperationErrorCodeMaxLength = 64;

    /// <summary>At most 200 characters; an opaque reference, never a payload.</summary>
    private const int OperationResultRefMaxLength = 200;

    /// <summary>At most 32 characters; the longest milestone type is <c>first-customer</c>.</summary>
    private const int MilestoneTypeMaxLength = 32;

    /// <summary>At most 16 characters; the longest status is <c>dismissed</c>.</summary>
    private const int MilestoneStatusMaxLength = 16;

    /// <summary>
    /// Refuses a stored status this build does not know. A value that is not one of the six is not a
    /// status to be guessed at: it means the row was written by a build with a different vocabulary,
    /// and reading it as some other status would report a lie to the owner.
    /// </summary>
    private static OperationStatus ParseStatus(string wire) =>
        OperationStatuses.TryParse(wire, out var status)
            ? status
            : throw new InvalidOperationException($"'{wire}' is not a known operation status.");
}
