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
/// <c>evidence_bundles</c> and <c>evidence_items</c> tables that hold the owner's own records.
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
    }

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
