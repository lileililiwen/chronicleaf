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

    public DbSet<Client> Clients => Set<Client>();

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

    public DbSet<GitHubAppInstallation> GitHubAppInstallations => Set<GitHubAppInstallation>();

    public DbSet<SyncSource> SyncSources => Set<SyncSource>();

    public DbSet<EnrichmentRun> EnrichmentRuns => Set<EnrichmentRun>();

    public DbSet<LocalRepository> LocalRepositories => Set<LocalRepository>();

    public DbSet<LocalSyncSource> LocalSyncSources => Set<LocalSyncSource>();

    public DbSet<ScreenshotCandidate> ScreenshotCandidates => Set<ScreenshotCandidate>();

    public DbSet<CaptureRequestRow> CaptureRequests => Set<CaptureRequestRow>();

    public DbSet<ContentAsset> ContentAssets => Set<ContentAsset>();

    public DbSet<ContentRevision> ContentRevisions => Set<ContentRevision>();

    public DbSet<ContentDraftRequestRow> ContentDraftRequests => Set<ContentDraftRequestRow>();

    public DbSet<ContentApproval> ContentApprovals => Set<ContentApproval>();

    public DbSet<PublicationAttempt> PublicationAttempts => Set<PublicationAttempt>();

    public DbSet<GitHubWebhookSubscription> GitHubWebhookSubscriptions => Set<GitHubWebhookSubscription>();

    public DbSet<GitHubWebhookDelivery> GitHubWebhookDeliveries => Set<GitHubWebhookDelivery>();

    public DbSet<GitHubCandidate> GitHubCandidates => Set<GitHubCandidate>();

    public DbSet<GitHubCandidateRun> GitHubCandidateRuns => Set<GitHubCandidateRun>();

    public DbSet<NarrativeEntry> NarrativeEntries => Set<NarrativeEntry>();

    public DbSet<ProjectSummary> ProjectSummaries => Set<ProjectSummary>();

    public DbSet<NarrativeRefreshRequestRow> NarrativeRefreshRequests => Set<NarrativeRefreshRequestRow>();

    public DbSet<EvolutionImage> EvolutionImages => Set<EvolutionImage>();

    public DbSet<StaticBlogExportRequestRow> StaticBlogExportRequests => Set<StaticBlogExportRequestRow>();

    public DbSet<ProjectJourneyRow> ProjectJourneys => Set<ProjectJourneyRow>();

    public DbSet<HostedUser> HostedUsers => Set<HostedUser>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

    public DbSet<TenantScope> TenantScopes => Set<TenantScope>();

    public DbSet<HostedSession> HostedSessions => Set<HostedSession>();

    public DbSet<HostedLoginRequest> HostedLoginRequests => Set<HostedLoginRequest>();

    public DbSet<HostedImport> HostedImports => Set<HostedImport>();

    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();

    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();

    public DbSet<MediaMigrationCopy> MediaMigrationCopies => Set<MediaMigrationCopy>();

    public DbSet<ProjectSyncSchedule> ProjectSyncSchedules => Set<ProjectSyncSchedule>();

    public DbSet<ArchiveTombstone> ArchiveTombstones => Set<ArchiveTombstone>();

    public DbSet<ArchiveBackupManifest> ArchiveBackupManifests => Set<ArchiveBackupManifest>();

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
            entity.Property(project => project.ClientId).HasColumnName("client_id");
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

            // AGENCY-CLIENT-PROJECTS-001: the one optional client a project is grouped under. The
            // foreign key is Restrict, so a client with projects cannot be erased by a key action -
            // and it cannot be erased any other way either, because clients are archived, never
            // deleted. The scope of the association is enforced by the service, not by the key: a
            // foreign-scoped client is refused before this column is ever written.
            entity.HasOne<Client>()
                .WithMany()
                .HasForeignKey(project => project.ClientId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_projects_client_id");
        });

        // AGENCY-CLIENT-PROJECTS-001: a client belongs to exactly one owner scope, carries the
        // optimistic revision every edit presents, the membership generation every assignment
        // advances, and is archived rather than deleted.
        modelBuilder.Entity<Client>(entity =>
        {
            entity.ToTable("clients");
            entity.HasKey(client => client.Id);
            entity.Property(client => client.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(client => client.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(client => client.Name)
                .HasColumnName("name")
                .HasMaxLength(Client.NameMaxLength)
                .IsRequired();
            entity.Property(client => client.Notes)
                .HasColumnName("notes")
                .HasMaxLength(Client.NotesMaxLength)
                .IsRequired();
            entity.Property(client => client.Revision).HasColumnName("revision").IsRequired();
            entity.Property(client => client.ContextRevision).HasColumnName("context_revision").IsRequired();
            entity.Property(client => client.ArchivedAt)
                .HasColumnName("archived_at")
                .HasColumnType("timestamp with time zone");

            // A stale write is a conflict, never an overwrite, for the same reason the projects and
            // milestones revisions carry a token: the parameterized compare-and-set in
            // PostgresClientStore matches on it.
            entity.Property(client => client.Revision).IsConcurrencyToken();

            // Every read is scoped, and a list is ordered by name, so the index serves both.
            entity.HasIndex(client => new { client.ScopeId, client.Name })
                .HasDatabaseName("ix_clients_scope_id_name");
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

            // CLOUD-OBJECT-STORAGE-001/002: the backend the verified bytes live on. Added by this
            // package's forward migration with a 'local' default that backfills every existing row,
            // so a database that has never seen S3 reads exactly what it always read. New rows name
            // the backend their bytes were verified on.
            entity.Property(media => media.ProviderId)
                .HasColumnName("provider_id")
                .HasMaxLength(MediaRecord.ProviderIdMaxLength)
                .HasDefaultValue(StorageModes.Local)
                .IsRequired();

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

        // CLOUD-OBJECT-STORAGE-002: one migration ledger entry per media object. The media id is the
        // primary key, so two runs recording the same object converge rather than diverging, and the
        // state check is what keeps a retry from re-copying a verified object without re-verifying
        // it first. Deliberately not a foreign key to media: the ledger is this package's working
        // state, and a media row that outlives its ledger entry - or the reverse - must not cascade
        // into the other.
        modelBuilder.Entity<MediaMigrationCopy>(entity =>
        {
            entity.ToTable("media_migration_copies");
            entity.HasKey(copy => copy.MediaId);
            entity.Property(copy => copy.MediaId).HasColumnName("media_id").ValueGeneratedNever();
            entity.Property(copy => copy.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(copy => copy.SourceProvider)
                .HasColumnName("source_provider")
                .HasMaxLength(MediaRecord.ProviderIdMaxLength)
                .IsRequired();
            entity.Property(copy => copy.TargetProvider)
                .HasColumnName("target_provider")
                .HasMaxLength(MediaRecord.ProviderIdMaxLength)
                .IsRequired();
            entity.Property(copy => copy.SourceKey)
                .HasColumnName("source_key")
                .HasMaxLength(MediaLimits.MaximumKeyLength)
                .IsRequired();
            entity.Property(copy => copy.TargetKey)
                .HasColumnName("target_key")
                .HasMaxLength(MediaLimits.MaximumKeyLength)
                .IsRequired();
            entity.Property(copy => copy.SourceSha256)
                .HasColumnName("source_sha256")
                .HasMaxLength(MediaRecord.Sha256HexLength)
                .IsRequired();
            entity.Property(copy => copy.CopiedSha256)
                .HasColumnName("copied_sha256")
                .HasMaxLength(MediaRecord.Sha256HexLength);
            entity.Property(copy => copy.State)
                .HasColumnName("state")
                .HasMaxLength(MediaMigrationCopy.StateMaxLength)
                .IsRequired();
            entity.Property(copy => copy.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.ToTable(table => table.HasCheckConstraint(
                "ck_media_migration_copies_state",
                "state IN ('started', 'verified', 'failed')"));

            // A run's only query shape: this scope's copies, with the unverified ones resumable.
            entity.HasIndex(copy => new { copy.ScopeId, copy.State })
                .HasDatabaseName("ix_media_migration_copies_scope_id_state");
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
            entity.Property(connection => connection.AuthMode)
                .HasColumnName("auth_mode")
                .HasMaxLength(16)
                .HasDefaultValue(GitHubAuthModes.Pat)
                .IsRequired();
            entity.Property(connection => connection.InstallationId).HasColumnName("installation_id");
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

            entity.ToTable(table => table.HasCheckConstraint(
                "ck_github_connections_auth_mode",
                "auth_mode IN ('pat','installation')"));

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(connection => connection.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_connections_project_id");
        });

        // GITHUB-APP-CONNECTION-001: one row per App installation bound to one scope. The row
        // names the installation and the account it was installed on, and holds no token and no
        // secret of any kind. The unique index on the installation id is what refuses a second
        // scope's attach of the same installation: the first binding wins and the second
        // conflicts rather than shares.
        modelBuilder.Entity<GitHubAppInstallation>(entity =>
        {
            entity.ToTable("github_app_installations");
            entity.HasKey(installation => installation.Id);
            entity.Property(installation => installation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(installation => installation.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(installation => installation.InstallationId).HasColumnName("installation_id").IsRequired();
            entity.Property(installation => installation.AccountLogin)
                .HasColumnName("account_login")
                .HasMaxLength(GitHubOwnerMaxLength)
                .IsRequired();
            entity.Property(installation => installation.RepositorySelection)
                .HasColumnName("repository_selection")
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(installation => installation.Status)
                .HasColumnName("status")
                .HasMaxLength(16)
                .IsRequired();
            entity.Property(installation => installation.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(installation => installation.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(installation => installation.InstallationId)
                .IsUnique()
                .HasDatabaseName("ux_github_app_installations_installation_id");

            entity.HasIndex(installation => installation.ScopeId)
                .HasDatabaseName("ix_github_app_installations_scope_id");

            entity.ToTable(table => table.HasCheckConstraint(
                "ck_github_app_installations_status",
                "status IN ('active','suspended','removed')"));

            entity.ToTable(table => table.HasCheckConstraint(
                "ck_github_app_installations_repository_selection",
                "repository_selection IN ('all','selected')"));
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
        // github-graphql-enrichment: one row per enrichment run, written when the owner asks and
        // never changed afterwards. The evidence revision is here because a job payload carries an
        // operation id and nothing else, so the revision the owner was looking at has to be durable
        // somewhere the worker can read it from. What the run observed lives on the bundle it
        // appended, not here: this row says what was asked, the bundle says what was read. There is
        // no credential column, because this package only ever holds a vault reference for the
        // length of one read.
        // -----------------------------------------------------------------------------------------
        modelBuilder.Entity<EnrichmentRun>(entity =>
        {
            entity.ToTable("github_enrichment_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(run => run.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(run => run.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(run => run.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(run => run.ExpectedBundleRevision)
                .HasColumnName("expected_bundle_revision")
                .IsRequired();
            entity.Property(run => run.Owner)
                .HasColumnName("owner")
                .HasMaxLength(GitHubOwnerMaxLength)
                .IsRequired();
            entity.Property(run => run.Repo)
                .HasColumnName("repo")
                .HasMaxLength(GitHubRepoMaxLength)
                .IsRequired();
            entity.Property(run => run.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One run per operation, and the worker's lookup: it is handed an operation id and
            // nothing else, so this index is the only way it can find the request it is enriching.
            entity.HasIndex(run => new { run.ScopeId, run.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_github_enrichment_runs_scope_id_operation_id");

            // A milestone's runs are read newest first, so the index leads with the milestone and
            // the time the run was asked for.
            entity.HasIndex(run => new { run.ScopeId, run.MilestoneId, run.CreatedAt })
                .HasDatabaseName("ix_github_enrichment_runs_scope_id_milestone_id_created_at");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(run => run.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_enrichment_runs_milestone_id");
        });

        // -----------------------------------------------------------------------------------------
        // local-git-evidence. The same two shapes as the GitHub tables above, and the two deliberate
        // differences are the point of this package.
        //
        // `root_alias` and `relative_path` are the only address columns, and there is no third. The
        // registered directory lives in the operator's configuration and is never written here, so this
        // table cannot become a map of a filesystem and a response built from it cannot publish a path.
        // There is also no credential column: a local repository is read off the operator's own disk with
        // the operator's own Git, and there is no remote for a token to authenticate to.
        // -----------------------------------------------------------------------------------------

        // LOCAL-GIT-EVIDENCE-002: the local repository a project collects from. One registration per
        // project: the unique index is what makes a second POST for the same project replace the first
        // instead of accumulating directories.
        modelBuilder.Entity<LocalRepository>(entity =>
        {
            entity.ToTable("local_repositories");
            entity.HasKey(repository => repository.Id);
            entity.Property(repository => repository.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(repository => repository.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(repository => repository.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(repository => repository.RootAlias)
                .HasColumnName("root_alias")
                .HasMaxLength(LocalRootAliasMaxLength)
                .IsRequired();
            entity.Property(repository => repository.RelativePath)
                .HasColumnName("relative_path")
                .HasMaxLength(LocalRelativePathMaxLength)
                .IsRequired();
            entity.Property(repository => repository.DefaultBranch)
                .HasColumnName("default_branch")
                .HasMaxLength(LocalRefMaxLength);
            entity.Property(repository => repository.HeadSha)
                .HasColumnName("head_sha")
                .HasMaxLength(LocalShaMaxLength);
            entity.Property(repository => repository.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(repository => repository.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(repository => new { repository.ScopeId, repository.ProjectId })
                .IsUnique()
                .HasDatabaseName("ux_local_repositories_scope_id_project_id");

            // A relative path that begins a segment with a slash, or names a dot segment, is a path that
            // could resolve outside its root. The application policy refuses both before a row exists; this
            // constraint is what makes the refusal a property of the table rather than of one writer.
            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_local_repositories_relative_path_relative",
                        "relative_path NOT LIKE '/%' AND relative_path NOT LIKE '%/../%' "
                        + "AND relative_path NOT LIKE '%/./%' AND relative_path NOT LIKE '../%' "
                        + "AND relative_path NOT LIKE './%' AND relative_path <> '..' "
                        + "AND relative_path NOT LIKE '%\\\\%' AND relative_path <> ''");
                    table.HasCheckConstraint(
                        "ck_local_repositories_root_alias_relative",
                        "root_alias <> '' AND root_alias NOT LIKE '/%' AND root_alias NOT LIKE '%/%'");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(repository => repository.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_local_repositories_project_id");
        });

        // LOCAL-GIT-EVIDENCE-001/002: one row per local collection run, written when the owner asks and
        // completed by the job. `command_log` holds the Git verbs that ran and never their operands, so a
        // reader can see what kind of reads happened without this table being able to name a path.
        modelBuilder.Entity<LocalSyncSource>(entity =>
        {
            entity.ToTable("local_sync_sources");
            entity.HasKey(source => source.Id);
            entity.Property(source => source.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(source => source.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(source => source.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(source => source.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(source => source.ExpectedBundleRevision)
                .HasColumnName("expected_bundle_revision")
                .IsRequired();
            entity.Property(source => source.RootAlias)
                .HasColumnName("root_alias")
                .HasMaxLength(LocalRootAliasMaxLength)
                .IsRequired();
            entity.Property(source => source.RelativePath)
                .HasColumnName("relative_path")
                .HasMaxLength(LocalRelativePathMaxLength)
                .IsRequired();
            entity.Property(source => source.HeadRef)
                .HasColumnName("head_ref")
                .HasMaxLength(LocalRefMaxLength)
                .IsRequired();
            entity.Property(source => source.BaseRef)
                .HasColumnName("base_ref")
                .HasMaxLength(LocalRefMaxLength);
            entity.Property(source => source.BaseSha)
                .HasColumnName("base_sha")
                .HasMaxLength(LocalShaMaxLength);
            entity.Property(source => source.HeadSha)
                .HasColumnName("head_sha")
                .HasMaxLength(LocalShaMaxLength);
            entity.Property(source => source.CommandLog)
                .HasColumnName("command_log")
                .HasMaxLength(LocalCommandLogMaxLength);
            entity.Property(source => source.FetchedAt)
                .HasColumnName("fetched_at")
                .HasColumnType("timestamp with time zone");
            // The five count columns default to zero in the database, not only in the domain record, for the
            // delivered reason: a run is inserted the moment an owner asks for it, so the defaults are what
            // make "asked for, not yet collected" a state the database can hold without this store inventing
            // numbers it has not read.
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
            entity.Property(source => source.UncommittedCount)
                .HasColumnName("uncommitted_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(source => source.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(source => source.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One run per operation, and the worker's lookup: it is handed an operation id and nothing else,
            // so this index is the only way it can find the request it is collecting.
            entity.HasIndex(source => new { source.ScopeId, source.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_local_sync_sources_scope_id_operation_id");

            // A milestone's runs are read newest first, so the index leads with the milestone and the time the
            // run was asked for.
            entity.HasIndex(source => new { source.ScopeId, source.MilestoneId, source.CreatedAt })
                .HasDatabaseName("ix_local_sync_sources_scope_id_milestone_id_created_at");

            // A run that has completed has a head revision, and a run that has not has none. The check is on
            // the pair rather than on either column alone so "collected" and "did not finish" cannot both be
            // true of one row, which is the state the delivered GitHub run log would also refuse.
            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_local_sync_sources_completion",
                        "(completed_at IS NULL AND head_sha IS NULL AND base_sha IS NULL AND command_log IS NULL) "
                        + "OR (completed_at IS NOT NULL AND head_sha IS NOT NULL AND command_log IS NOT NULL)");
                });

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(source => source.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_local_sync_sources_milestone_id");
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
            entity.Property(candidate => candidate.DisplayOrder)
                .HasColumnName("display_order")
                .HasDefaultValue(0)
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

            // One primary picture per milestone, as a database constraint rather than a convention:
            // the partial index holds only the rows that claim to be primary, so clearing the old
            // primary and setting the new one in one transaction can never leave two behind, and a
            // writer that somehow did is refused rather than read back as two first pictures.
            entity.HasIndex(candidate => candidate.MilestoneId)
                .IsUnique()
                .HasFilter("is_primary")
                .HasDatabaseName("ux_screenshot_candidates_milestone_primary");

            // The curation vocabulary, as a database constraint rather than a convention: a status
            // no reader recognises would otherwise read as whatever the panel guessed it meant.
            entity.HasCheckConstraint(
                "ck_screenshot_candidates_status",
                "status IN ('candidate', 'selected', 'rejected')");

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

        // -----------------------------------------------------------------------------------------
        // grounded-markdown-drafts. Three tables: the request a job reads its work back from, the
        // versioned asset, and the immutable revisions under it.
        // -----------------------------------------------------------------------------------------

        modelBuilder.Entity<ContentDraftRequestRow>(entity =>
        {
            entity.ToTable("content_draft_requests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(request => request.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(request => request.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(request => request.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(request => request.PreviewId).HasColumnName("preview_id").IsRequired();
            entity.Property(request => request.ConsentHash)
                .HasColumnName("consent_hash")
                .HasMaxLength(ContentDraftRequestRow.ConsentHashLength)
                .IsRequired();
            entity.Property(request => request.EvidenceRevision)
                .HasColumnName("evidence_revision")
                .IsRequired();
            entity.Property(request => request.VoiceRevision).HasColumnName("voice_revision").IsRequired();
            entity.Property(request => request.ProviderId)
                .HasColumnName("provider_id")
                .HasMaxLength(ContentLabelMaxLength)
                .IsRequired();
            entity.Property(request => request.Model)
                .HasColumnName("model")
                .HasMaxLength(ContentLabelMaxLength)
                .IsRequired();
            entity.Property(request => request.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One request per job. The worker is handed an operation id and nothing else, so this index
            // is the only way it can find what the owner authorized - and a second request row for one
            // job would mean a re-run could draft from a consent nobody presented twice.
            entity.HasIndex(request => new { request.ScopeId, request.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_content_draft_requests_scope_id_operation_id");

            // A milestone's draft history is read newest first, so the index leads with the milestone
            // and the time the request was accepted.
            entity.HasIndex(request => new { request.ScopeId, request.MilestoneId, request.CreatedAt })
                .HasDatabaseName("ix_content_draft_requests_scope_id_milestone_id_created_at");

            // The consent row this request is bound to. Restrictive, because a preview that is removed
            // while a job naming it is still queued would leave a job that cannot be run honestly.
            entity.HasOne<GenerationPreview>()
                .WithMany()
                .HasForeignKey(request => request.PreviewId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_draft_requests_preview_id");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(request => request.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_draft_requests_milestone_id");
        });

        // GROUNDED-MARKDOWN-DRAFTS-002: the stable identity of one milestone's content, and the head
        // revision it points at. The status is stored with a default of `draft` and this package never
        // writes anything else, because approval is the review package's table bound to a revision - not
        // a column this one learns to move. The unique index on (scope, milestone, type) is what makes
        // "one draft story per milestone" a database decision rather than a read-then-write.
        modelBuilder.Entity<ContentAsset>(entity =>
        {
            entity.ToTable("content_assets");
            entity.HasKey(asset => asset.Id);
            entity.Property(asset => asset.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(asset => asset.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(asset => asset.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(asset => asset.MilestoneId).HasColumnName("milestone_id").IsRequired();
            entity.Property(asset => asset.Type)
                .HasColumnName("type")
                .HasMaxLength(ContentAssetTypeMaxLength)
                .IsRequired();
            entity.Property(asset => asset.Status)
                .HasColumnName("status")
                .HasMaxLength(ContentAssetStatusMaxLength)
                .HasDefaultValue(ContentAssetStatus.Draft)
                .IsRequired();

            // The head revision is a concurrency token for the same reason the projects, milestones,
            // credentials and candidates revisions carry one: a stale write is a conflict, never an
            // overwrite. In practice the store's compare-and-set decides, and this keeps an accidental
            // SaveChanges from turning a lost race into a silently merged one.
            entity.Property(asset => asset.CurrentRevision)
                .HasColumnName("current_revision")
                .IsConcurrencyToken()
                .HasDefaultValue(0L)
                .IsRequired();

            entity.Property(asset => asset.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(asset => asset.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One asset per milestone per kind per scope. This is what turns two jobs that both decided
            // there was no asset into one row and one conflict rather than two assets, and it is the
            // discovery read the panel's own route uses.
            entity.HasIndex(asset => new { asset.ScopeId, asset.MilestoneId, asset.Type })
                .IsUnique()
                .HasDatabaseName("ux_content_assets_scope_id_milestone_id_type");

            entity.HasOne<Milestone>()
                .WithMany()
                .HasForeignKey(asset => asset.MilestoneId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_assets_milestone_id");
        });

        // GROUNDED-MARKDOWN-DRAFTS-001/002: one immutable version of one asset. No update statement
        // exists against this table anywhere in the repository, because a provenance record that can be
        // edited is not provenance - and the review package binds an approval to a revision and a body
        // hash, which would be a signature over nothing if the text could then change. The composite key
        // is (asset, revision) so the head is a version and not a surrogate nobody reads.
        modelBuilder.Entity<ContentRevision>(entity =>
        {
            entity.ToTable("content_revisions");
            entity.HasKey(revision => new { revision.AssetId, revision.Revision });
            entity.Property(revision => revision.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(revision => revision.AssetId).HasColumnName("asset_id").IsRequired();
            entity.Property(revision => revision.Revision).HasColumnName("revision").IsRequired();
            entity.Property(revision => revision.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(revision => revision.EvidenceBundleId)
                .HasColumnName("evidence_bundle_id")
                .IsRequired();
            entity.Property(revision => revision.EvidenceRevision)
                .HasColumnName("evidence_revision")
                .IsRequired();
            entity.Property(revision => revision.VoiceRevision).HasColumnName("voice_revision").IsRequired();
            entity.Property(revision => revision.VoiceSnapshotJson)
                .HasColumnName("voice_snapshot")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(revision => revision.SummaryJson)
                .HasColumnName("summary")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(revision => revision.BodyMarkdown)
                .HasColumnName("body_markdown")
                .HasColumnType("text")
                .IsRequired();
            entity.Property(revision => revision.BodyHash)
                .HasColumnName("body_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(revision => revision.ProviderId)
                .HasColumnName("provider_id")
                .HasMaxLength(ContentLabelMaxLength)
                .IsRequired();
            entity.Property(revision => revision.Model)
                .HasColumnName("model")
                .HasMaxLength(ContentLabelMaxLength)
                .IsRequired();
            entity.Property(revision => revision.PromptVersion)
                .HasColumnName("prompt_version")
                .HasMaxLength(ContentVersionMaxLength)
                .IsRequired();
            entity.Property(revision => revision.SchemaVersion)
                .HasColumnName("schema_version")
                .HasMaxLength(ContentVersionMaxLength)
                .IsRequired();
            entity.Property(revision => revision.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The read path's own lookup: a revision by its own identity, for a panel that was handed
            // one.
            entity.HasIndex(revision => revision.Id)
                .IsUnique()
                .HasDatabaseName("ux_content_revisions_id");

            // A milestone's revisions are read newest first. The index leads with the asset because a
            // revision belongs to one, and an asset belongs to one milestone.
            entity.HasIndex(revision => new { revision.AssetId, revision.Revision, revision.Id })
                .HasDatabaseName("ix_content_revisions_asset_id_revision_id");

            // The frozen evidence snapshot. A restrictive delete, because a revision whose bundle has
            // been removed can no longer have its claims checked against anything.
            entity.HasOne<EvidenceBundle>()
                .WithMany()
                .HasForeignKey(revision => revision.EvidenceBundleId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_revisions_evidence_bundle_id");

            entity.HasOne<ContentAsset>()
                .WithMany()
                .HasForeignKey(revision => revision.AssetId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_revisions_asset_id");
        });

        // MARKDOWN-REVIEW-EXPORT-001/002: one human decision about one revision. The row is never
        // deleted: withdrawing an approval marks it invalidated with a reason, so the history of
        // what was once exportable survives. The unique index on (scope, asset, revision) is what
        // makes two approvals of one revision a single row and a refusal rather than two
        // decisions, and the export re-checks the body, the evidence head and the pictures rather
        // than trusting this row - which is why no trigger here needs to refuse anything.
        modelBuilder.Entity<ContentApproval>(entity =>
        {
            entity.ToTable("content_approvals");
            entity.HasKey(approval => approval.Id);
            entity.Property(approval => approval.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(approval => approval.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(approval => approval.AssetId).HasColumnName("asset_id").IsRequired();
            entity.Property(approval => approval.ContentRevision)
                .HasColumnName("content_revision")
                .IsRequired();
            entity.Property(approval => approval.ActorId).HasColumnName("actor_id");
            entity.Property(approval => approval.PayloadKind)
                .HasColumnName("payload_kind")
                .HasMaxLength(ContentPayloadKindMaxLength)
                .IsRequired();
            entity.Property(approval => approval.BodyHash)
                .HasColumnName("body_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(approval => approval.EvidenceRevision)
                .HasColumnName("evidence_revision")
                .IsRequired();
            entity.Property(approval => approval.VoiceSnapshotHash)
                .HasColumnName("voice_snapshot_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(approval => approval.MediaSelectionHash)
                .HasColumnName("media_selection_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(approval => approval.ArtifactHash)
                .HasColumnName("artifact_hash")
                .HasMaxLength(Sha256HexMaxLength);
            entity.Property(approval => approval.ApprovedAt)
                .HasColumnName("approved_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(approval => approval.InvalidatedAt)
                .HasColumnName("invalidated_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(approval => approval.Reason)
                .HasColumnName("reason")
                .HasColumnType("text");

            // One approval per revision per scope. A second decision about the same revision is
            // the same decision answered again, and the store's insert reports it as such.
            entity.HasIndex(approval => new
                {
                    approval.ScopeId,
                    approval.AssetId,
                    approval.ContentRevision,
                })
                .IsUnique()
                .HasDatabaseName("ux_content_approvals_scope_id_asset_id_revision");

            entity.HasIndex(approval => approval.AssetId)
                .HasDatabaseName("IX_content_approvals_asset_id");

            entity.HasOne<ContentAsset>()
                .WithMany()
                .HasForeignKey(approval => approval.AssetId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_content_approvals_asset_id");
        });

        // PUBLISHER-CONTRACT-001/002: one reviewed publication decision and its outcome. The row is
        // never deleted and never rewritten: the state moves forward by compare-and-set from
        // prepared through sending to one terminal state, and the unique index on (scope,
        // idempotency key) is what makes a repeated key return the stored receipt rather than a
        // second send. The second unique index on the operation id is what makes one operation
        // run exactly one attempt, and the check constraints close the state, mode and digest
        // vocabularies at the database rather than in application code.
        modelBuilder.Entity<PublicationAttempt>(entity =>
        {
            entity.ToTable("publication_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(attempt => attempt.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(attempt => attempt.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(attempt => attempt.TargetId)
                .HasColumnName("target_id")
                .HasMaxLength(PublisherTargetMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.AssetId).HasColumnName("asset_id").IsRequired();
            entity.Property(attempt => attempt.ContentRevision)
                .HasColumnName("content_revision")
                .IsRequired();
            entity.Property(attempt => attempt.BodyHash)
                .HasColumnName("body_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.EvidenceRevision)
                .HasColumnName("evidence_revision")
                .IsRequired();
            entity.Property(attempt => attempt.PreviewHash)
                .HasColumnName("preview_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.Mode)
                .HasColumnName("mode")
                .HasMaxLength(PublishModeMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.ApprovalId).HasColumnName("approval_id").IsRequired();
            entity.Property(attempt => attempt.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(PublisherIdempotencyKeyMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.State)
                .HasColumnName("state")
                .HasMaxLength(PublishStateMaxLength)
                .IsRequired();
            entity.Property(attempt => attempt.RemoteId)
                .HasColumnName("remote_id")
                .HasMaxLength(PublisherRemoteIdMaxLength);
            entity.Property(attempt => attempt.RemoteUrl)
                .HasColumnName("remote_url")
                .HasMaxLength(PublisherRemoteUrlMaxLength);
            entity.Property(attempt => attempt.ErrorCode)
                .HasColumnName("error_code")
                .HasMaxLength(PublisherErrorCodeMaxLength);
            entity.Property(attempt => attempt.DispatchedAt)
                .HasColumnName("dispatched_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(attempt => attempt.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(attempt => attempt.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(attempt => attempt.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.ToTable(
                table =>
                {
                    // The state, mode and revision vocabularies as database constraints rather than
                    // conventions. A state no reader recognises would otherwise be reported as
                    // whatever the panel guessed it meant, and a revision that is not positive is
                    // not a revision anyone approved.
                    table.HasCheckConstraint(
                        "ck_publication_attempts_revision_positive",
                        "content_revision >= 1");
                    table.HasCheckConstraint(
                        "ck_publication_attempts_evidence_revision_positive",
                        "evidence_revision >= 1");
                    table.HasCheckConstraint(
                        "ck_publication_attempts_state_known",
                        "state IN ('prepared', 'sending', 'published', 'remote-draft', 'unknown', 'failed')");
                    table.HasCheckConstraint(
                        "ck_publication_attempts_mode_known",
                        "mode IN ('draft', 'publish')");
                });

            // One attempt per idempotency key per scope: a repeated key returns the stored
            // receipt rather than a second send.
            entity.HasIndex(attempt => new
                {
                    attempt.ScopeId,
                    attempt.IdempotencyKey,
                })
                .IsUnique()
                .HasDatabaseName("ux_publication_attempts_scope_id_idempotency_key");

            // One attempt per operation: the queue may deliver the same operation twice, but
            // only one receipt is ever committed for it.
            entity.HasIndex(attempt => attempt.OperationId)
                .IsUnique()
                .HasDatabaseName("ux_publication_attempts_operation_id");

            entity.HasIndex(attempt => new
                {
                    attempt.ScopeId,
                    attempt.AssetId,
                })
                .HasDatabaseName("ix_publication_attempts_scope_id_asset_id");

            entity.HasOne<ContentAsset>()
                .WithMany()
                .HasForeignKey(attempt => attempt.AssetId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_publication_attempts_asset_id");
        });

        // -----------------------------------------------------------------------------------------
        // github-release-candidates. Four tables, and the two unique indexes are the package.
        //
        // A candidate is a proposal, so the table has no milestone column, no bundle column and no
        // revision: confirming one is a human decision made elsewhere, and a row that could name a
        // milestone would be a claim this package cannot make. `source_key` is the provider's own
        // identity for the thing proposed, and the unique index on (scope_id, source_key) is what
        // makes "exactly one candidate per source key" a fact about the database rather than a hope
        // about a loop - a tag and a release naming one commit are two candidates on purpose, because
        // their keys differ.
        // -----------------------------------------------------------------------------------------

        // GITHUB-RELEASE-CANDIDATES-001: which connection accepts a signed delivery, and where the
        // secret that authenticates it is held. `credential_ref` is the vault's own id, so this table
        // cannot hold a secret and a read of it learns only that one is configured. Archiving is an
        // update rather than a delete, because the receipts already stored name this connection and
        // are the only record that a delivery ever arrived.
        modelBuilder.Entity<GitHubWebhookSubscription>(entity =>
        {
            entity.ToTable("github_webhook_subscriptions");
            entity.HasKey(subscription => subscription.Id);
            entity.Property(subscription => subscription.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(subscription => subscription.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(subscription => subscription.ConnectionId)
                .HasColumnName("connection_id")
                .IsRequired();
            entity.Property(subscription => subscription.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(subscription => subscription.CredentialRef)
                .HasColumnName("credential_ref")
                .IsRequired();
            entity.Property(subscription => subscription.ArchivedAt)
                .HasColumnName("archived_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(subscription => subscription.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(subscription => subscription.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One subscription per connection, and this is the index the delivery route's only lookup
            // uses. A connection with two rows would make "is this connection subscribed" a question
            // with two answers.
            entity.HasIndex(subscription => subscription.ConnectionId)
                .IsUnique()
                .HasDatabaseName("ux_github_webhook_subscriptions_connection_id");

            // A project's own read, and the archive statement's filter.
            entity.HasIndex(subscription => new { subscription.ScopeId, subscription.ProjectId })
                .HasDatabaseName("ix_github_webhook_subscriptions_scope_id_project_id");

            // The connection this subscription is for, and the project it belongs to. Both are
            // restrictive: a connection that is replaced or a project that is archived stops a delivery
            // being routed, and a subscription row that could be cascaded away would take the receipts
            // that name it with it.
            entity.HasOne<GitHubConnection>()
                .WithMany()
                .HasForeignKey(subscription => subscription.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_webhook_subscriptions_connection_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(subscription => subscription.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_webhook_subscriptions_project_id");
        });

        // GITHUB-RELEASE-CANDIDATES-001/002: one signed delivery and what became of it. There is no body
        // column, no signature column and no secret column: a delivery's own text may name a private
        // issue title or an author's login, and a table of receipts is a table a support export would
        // carry. The `proposals` column is this package's own already-routed reading of the body, and it
        // exists because the work happens after the route has answered 202.
        modelBuilder.Entity<GitHubWebhookDelivery>(entity =>
        {
            entity.ToTable("github_webhook_deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(delivery => delivery.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(delivery => delivery.ConnectionId)
                .HasColumnName("connection_id")
                .IsRequired();
            entity.Property(delivery => delivery.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(delivery => delivery.DeliveryId)
                .HasColumnName("delivery_id")
                .HasMaxLength(GitHubDeliveryIdMaxLength)
                .IsRequired();
            entity.Property(delivery => delivery.Event)
                .HasColumnName("event")
                .HasMaxLength(GitHubCandidateEventNameMaxLength)
                .IsRequired();
            entity.Property(delivery => delivery.Action)
                .HasColumnName("action")
                .HasMaxLength(GitHubCandidateEventNameMaxLength);
            entity.Property(delivery => delivery.Status)
                .HasColumnName("status")
                .HasMaxLength(GitHubCandidateStatusMaxLength)
                .IsRequired();
            entity.Property(delivery => delivery.Reason)
                .HasColumnName("reason")
                .HasMaxLength(GitHubCandidateReasonMaxLength);
            entity.Property(delivery => delivery.CandidateCount)
                .HasColumnName("candidate_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(delivery => delivery.ReceivedAt)
                .HasColumnName("received_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(delivery => delivery.ProposalsJson)
                .HasColumnName("proposals")
                .HasColumnType("jsonb")
                .IsRequired();

            // The retry guard, decided by the database. GitHub redelivers without announcing it, and
            // this is what makes a redelivery the same delivery rather than a second one.
            entity.HasIndex(delivery => new { delivery.ConnectionId, delivery.DeliveryId })
                .IsUnique()
                .HasDatabaseName("ux_github_webhook_deliveries_connection_id_delivery_id");

            // The outbox consumer's only lookup: the aggregate it carries is this row's own id, so this
            // index is the read that turns a queued event back into the connection and delivery it
            // names.
            entity.HasIndex(delivery => new { delivery.ScopeId, delivery.Id })
                .HasDatabaseName("ix_github_webhook_deliveries_scope_id_id");

            // A connection's receipts are read newest first, so the index carries that order.
            entity.HasIndex(delivery => new { delivery.ScopeId, delivery.ConnectionId, delivery.ReceivedAt })
                .HasDatabaseName("ix_github_webhook_deliveries_scope_id_connection_received_at");

            entity.ToTable(
                table =>
                {
                    // The receipt and ignored-reason vocabularies as database constraints rather than
                    // conventions. A status no reader recognises would otherwise be reported as whatever
                    // the panel guessed it meant, and a reason a reader cannot interpret is not an
                    // explanation.
                    table.HasCheckConstraint(
                        "ck_github_webhook_deliveries_status",
                        "status IN ('pending', 'recorded', 'ignored')");

                    table.HasCheckConstraint(
                        "ck_github_webhook_deliveries_reason",
                        "reason IS NULL OR reason IN ('unsupported-event', 'unsupported-action', "
                        + "'below-star-threshold', 'connection-inactive', 'nothing-named')");

                    // A receipt that was decided says so consistently: a counted receipt is recorded, and
                    // a receipt with nothing counted is ignored and names why. Half of that is not a
                    // state a reader could be told about honestly.
                    table.HasCheckConstraint(
                        "ck_github_webhook_deliveries_outcome",
                        "(status = 'pending' AND candidate_count = 0) "
                        + "OR (status = 'recorded' AND candidate_count > 0 AND reason IS NULL) "
                        + "OR (status = 'ignored' AND candidate_count = 0 AND reason IS NOT NULL)");
                });

            entity.HasOne<GitHubConnection>()
                .WithMany()
                .HasForeignKey(delivery => delivery.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_webhook_deliveries_connection_id");
        });

        // GITHUB-RELEASE-CANDIDATES-001/002: one proposal for a Milestone, attributed to the thing that
        // produced it. The kind and source vocabularies are check constraints rather than conventions,
        // so a stored value no reader recognises cannot be published as one it does.
        modelBuilder.Entity<GitHubCandidate>(entity =>
        {
            entity.ToTable("github_candidates");
            entity.HasKey(candidate => candidate.Id);
            entity.Property(candidate => candidate.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(candidate => candidate.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(candidate => candidate.ConnectionId)
                .HasColumnName("connection_id")
                .IsRequired();
            entity.Property(candidate => candidate.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(candidate => candidate.SourceKey)
                .HasColumnName("source_key")
                .HasMaxLength(GitHubCandidateSourceKeyMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.Kind)
                .HasColumnName("kind")
                .HasMaxLength(GitHubCandidateKindMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.Title)
                .HasColumnName("title")
                .HasMaxLength(GitHubCandidateTitleMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.Revision)
                .HasColumnName("revision")
                .HasMaxLength(GitHubCandidateRevisionMaxLength);
            entity.Property(candidate => candidate.Source)
                .HasColumnName("source")
                .HasMaxLength(GitHubCandidateSourceMaxLength)
                .IsRequired();
            entity.Property(candidate => candidate.DeliveryId)
                .HasColumnName("delivery_id")
                .HasMaxLength(GitHubDeliveryIdMaxLength);
            entity.Property(candidate => candidate.ReceivedAt)
                .HasColumnName("received_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // The whole package in one index: exactly one candidate per provider source key per scope.
            // Two deliveries of one release arriving at the same instant therefore produce one candidate
            // and one refusal rather than two, and the caller counts the refusal as "already proposed"
            // rather than raising.
            entity.HasIndex(candidate => new { candidate.ScopeId, candidate.SourceKey })
                .IsUnique()
                .HasDatabaseName("ux_github_candidates_scope_id_source_key");

            // The owner's review queue, read newest first.
            entity.HasIndex(candidate => new { candidate.ScopeId, candidate.ProjectId, candidate.ReceivedAt })
                .HasDatabaseName("ix_github_candidates_scope_id_project_id_received_at");

            entity.ToTable(
                table =>
                {
                    // The three disjoint key prefixes, as a database constraint. Without it a writer
                    // that produced a key of its own shape would make "where did this key come from"
                    // unanswerable.
                    table.HasCheckConstraint(
                        "ck_github_candidates_source_key",
                        "source_key LIKE 'release:%' OR source_key LIKE 'tag:%' OR source_key LIKE 'event:%'");

                    table.HasCheckConstraint(
                        "ck_github_candidates_kind",
                        "kind IN ('release', 'tag', 'pull-request', 'issue', 'star')");

                    table.HasCheckConstraint(
                        "ck_github_candidates_source",
                        "source IN ('webhook', 'poll')");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(candidate => candidate.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_candidates_project_id");

            entity.HasOne<GitHubConnection>()
                .WithMany()
                .HasForeignKey(candidate => candidate.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_candidates_connection_id");
        });

        // GITHUB-RELEASE-CANDIDATES-001: one row per poll, written when the owner asks and completed by
        // the job. The four count columns default to zero in the database as well as in the record, so
        // "asked for and not yet polled" is a state the table can hold without this store inventing
        // numbers it has not read. The check constraint is on the completion pair rather than on any one
        // column, so half a completion cannot be stored at all.
        modelBuilder.Entity<GitHubCandidateRun>(entity =>
        {
            entity.ToTable("github_candidate_runs");
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(run => run.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(run => run.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(run => run.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(run => run.ExpectedProjectRevision)
                .HasColumnName("expected_project_revision")
                .IsRequired();
            entity.Property(run => run.Owner)
                .HasColumnName("owner")
                .HasMaxLength(GitHubOwnerMaxLength)
                .IsRequired();
            entity.Property(run => run.Repo)
                .HasColumnName("repo")
                .HasMaxLength(GitHubRepoMaxLength)
                .IsRequired();
            entity.Property(run => run.Since)
                .HasColumnName("since")
                .HasColumnType("timestamp with time zone");
            entity.Property(run => run.HeadSha)
                .HasColumnName("head_sha")
                .HasMaxLength(GitHubShaMaxLength);
            entity.Property(run => run.Endpoints)
                .HasColumnName("endpoints")
                .HasMaxLength(GitHubCandidateEndpointsMaxLength);
            entity.Property(run => run.FetchedAt)
                .HasColumnName("fetched_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(run => run.ReleaseCount)
                .HasColumnName("release_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(run => run.TagCount)
                .HasColumnName("tag_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(run => run.CandidateCount)
                .HasColumnName("candidate_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(run => run.RecordedCount)
                .HasColumnName("recorded_count")
                .HasDefaultValue(0)
                .IsRequired();
            entity.Property(run => run.CompletedAt)
                .HasColumnName("completed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(run => run.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One run per operation, and the worker's lookup: a job is handed an operation id and
            // nothing else, so this index is the only way it can find the window the owner typed.
            entity.HasIndex(run => new { run.ScopeId, run.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_github_candidate_runs_scope_id_operation_id");

            // A project's polls are read newest first, so the index carries that order.
            entity.HasIndex(run => new { run.ScopeId, run.ProjectId, run.CreatedAt })
                .HasDatabaseName("ix_github_candidate_runs_scope_id_project_id_created_at");

            // A run that has completed named the endpoints it read; one that has not has not. Checked on
            // the pair so "polled" and "did not finish" cannot both be true of one row.
            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_github_candidate_runs_completion",
                        "(completed_at IS NULL AND fetched_at IS NULL AND endpoints IS NULL) "
                        + "OR (completed_at IS NOT NULL AND fetched_at IS NOT NULL AND endpoints IS NOT NULL)");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(run => run.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_github_candidate_runs_project_id");
        });

        // SCHEDULED-PROJECT-SYNC-001: one project's opt-in recurring poll. Off until an owner turns
        // it on: a project without a row has no schedule, and the tick only reads rows that say
        // enabled. The revision is the compare-and-set token a stale writer - an owner editing while
        // a tick fires, or two ticks at once - loses with.
        modelBuilder.Entity<ProjectSyncSchedule>(entity =>
        {
            entity.ToTable("project_sync_schedules");
            entity.HasKey(schedule => schedule.Id);
            entity.Property(schedule => schedule.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(schedule => schedule.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(schedule => schedule.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(schedule => schedule.Enabled).HasColumnName("enabled").IsRequired();
            entity.Property(schedule => schedule.IntervalMinutes)
                .HasColumnName("interval_minutes")
                .IsRequired();
            entity.Property(schedule => schedule.NextRunAt)
                .HasColumnName("next_run_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(schedule => schedule.LastOperationId).HasColumnName("last_operation_id");
            entity.Property(schedule => schedule.Revision).HasColumnName("revision").IsRequired();
            entity.Property(schedule => schedule.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(schedule => schedule.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(schedule => schedule.Revision).IsConcurrencyToken();

            // One schedule per project: the insert takes this index in the same statement, so two
            // concurrent PUTs cannot both create.
            entity.HasIndex(schedule => new { schedule.ScopeId, schedule.ProjectId })
                .IsUnique()
                .HasDatabaseName("ux_project_sync_schedules_scope_id_project_id");

            // The tick's read: due enabled rows, oldest due first.
            entity.HasIndex(schedule => new { schedule.Enabled, schedule.NextRunAt })
                .HasDatabaseName("ix_project_sync_schedules_enabled_next_run_at");

            // A cadence is quarter-hourly at most and weekly at most, and a revision is one or
            // greater: checked on the pair of bounds so a row outside the cadence cannot be stored
            // at all.
            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_project_sync_schedules_interval",
                        $"interval_minutes >= {ProjectSyncSchedule.MinIntervalMinutes} "
                        + $"AND interval_minutes <= {ProjectSyncSchedule.MaxIntervalMinutes}");
                    table.HasCheckConstraint(
                        "ck_project_sync_schedules_revision",
                        "revision >= 1");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(schedule => schedule.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_project_sync_schedules_project_id");
        });

        // HOSTED-ARCHIVE-OPERATIONS-002: one project's recoverable archive-deletion marker. A
        // delete suspends the projection and the poll and opens a thirty-day window; nothing is
        // erased by this row. The revision is the compare-and-set token a stale restore loses
        // with, and the partial unique index is what makes a second live tombstone for one
        // project a conflict in the database rather than a merged window.
        modelBuilder.Entity<ArchiveTombstone>(entity =>
        {
            entity.ToTable("archive_tombstones");
            entity.HasKey(tombstone => tombstone.Id);
            entity.Property(tombstone => tombstone.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(tombstone => tombstone.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(tombstone => tombstone.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(tombstone => tombstone.ProjectName)
                .HasColumnName("project_name")
                .IsRequired();
            entity.Property(tombstone => tombstone.DeletedAt)
                .HasColumnName("deleted_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(tombstone => tombstone.RecoveryUntil)
                .HasColumnName("recovery_until")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(tombstone => tombstone.EraseRequested)
                .HasColumnName("erase_requested")
                .IsRequired();
            entity.Property(tombstone => tombstone.HoldReason).HasColumnName("hold_reason");
            entity.Property(tombstone => tombstone.RestoredAt)
                .HasColumnName("restored_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(tombstone => tombstone.PurgedAt)
                .HasColumnName("purged_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(tombstone => tombstone.Revision).HasColumnName("revision").IsRequired();
            entity.Property(tombstone => tombstone.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(tombstone => tombstone.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(tombstone => tombstone.Revision).IsConcurrencyToken();

            // One live tombstone per project: restored and purged rows stay as history and never
            // collide with the live one.
            entity.HasIndex(tombstone => new { tombstone.ScopeId, tombstone.ProjectId })
                .IsUnique()
                .HasFilter("restored_at IS NULL AND purged_at IS NULL")
                .HasDatabaseName("ux_archive_tombstones_scope_id_project_id_live");

            // The operator purge pass reads candidates oldest-window first.
            entity.HasIndex(tombstone => tombstone.RecoveryUntil)
                .HasDatabaseName("ix_archive_tombstones_recovery_until");

            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_archive_tombstones_window",
                        "recovery_until > deleted_at");
                    table.HasCheckConstraint(
                        "ck_archive_tombstones_revision",
                        "revision >= 1");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(tombstone => tombstone.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_archive_tombstones_project_id");
        });

        // HOSTED-ARCHIVE-OPERATIONS-001: one operator-recorded backup manifest over a
        // tenant-scoped store. Recording proves nothing: the row reads unverified until a
        // rehearsal stamps it, and the only later write is that stamp under compare-and-set. No
        // route serves these rows, so the backup reference and the key version never reach an
        // owner-facing response.
        modelBuilder.Entity<ArchiveBackupManifest>(entity =>
        {
            entity.ToTable("archive_backup_manifests");
            entity.HasKey(manifest => manifest.Id);
            entity.Property(manifest => manifest.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(manifest => manifest.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(manifest => manifest.SchemaVersion)
                .HasColumnName("schema_version")
                .IsRequired();
            entity.Property(manifest => manifest.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(manifest => manifest.DbBackupRef)
                .HasColumnName("db_backup_ref")
                .HasMaxLength(ArchiveBackupManifest.BackupRefMaxLength)
                .IsRequired();
            entity.Property(manifest => manifest.MediaInventoryHash)
                .HasColumnName("media_inventory_hash")
                .HasMaxLength(ArchiveBackupManifest.HashHexLength)
                .IsFixedLength()
                .IsRequired();
            entity.Property(manifest => manifest.ContentDigest)
                .HasColumnName("content_digest")
                .HasMaxLength(ArchiveBackupManifest.HashHexLength)
                .IsFixedLength()
                .IsRequired();
            entity.Property(manifest => manifest.EncryptionKeyVersion)
                .HasColumnName("encryption_key_version")
                .IsRequired();
            entity.Property(manifest => manifest.RestoreVerifiedAt)
                .HasColumnName("restore_verified_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(manifest => manifest.Revision).HasColumnName("revision").IsRequired();
            entity.Property(manifest => manifest.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(manifest => manifest.Revision).IsConcurrencyToken();

            entity.HasIndex(manifest => new { manifest.ScopeId, manifest.CreatedAt })
                .HasDatabaseName("ix_archive_backup_manifests_scope_id_created_at");

            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_archive_backup_manifests_schema",
                        "schema_version >= 1");
                    table.HasCheckConstraint(
                        "ck_archive_backup_manifests_key",
                        "encryption_key_version >= 1");
                    table.HasCheckConstraint(
                        "ck_archive_backup_manifests_revision",
                        "revision >= 1");
                });
        });

        // NARRATIVE-MEMORY-001: one explicit, owner-written line of project history, attributed to
        // the evidence items it is drawn from. The scope is the owning project's scope, checked
        // through the project on every read and write; the revision is the compare-and-set token a
        // stale writer loses with.
        modelBuilder.Entity<NarrativeEntry>(entity =>
        {
            entity.ToTable("narrative_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(entry => entry.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(entry => entry.Kind)
                .HasColumnName("kind")
                .HasMaxLength(NarrativeKindMaxLength)
                .IsRequired();
            entity.Property(entry => entry.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(entry => entry.Text)
                .HasColumnName("text")
                .HasMaxLength(NarrativeEntry.TextMaxLength)
                .IsRequired();
            entity.Property(entry => entry.SourceEvidenceIds)
                .HasColumnName("source_evidence_ids")
                .HasColumnType("uuid[]")
                .IsRequired();
            entity.Property(entry => entry.Revision).HasColumnName("revision").IsRequired();
            entity.Property(entry => entry.Visibility)
                .HasColumnName("visibility")
                .HasMaxLength(NarrativeVisibilityMaxLength)
                .IsRequired();

            entity.Property(entry => entry.Revision).IsConcurrencyToken();

            // One project's entries read oldest first, so the index leads with the project and the
            // order every read uses.
            entity.HasIndex(entry => new { entry.ProjectId, entry.OccurredAt, entry.Id })
                .HasDatabaseName("ix_narrative_entries_project_id_occurred_at_id");

            // The kind is one of the six this build knows. A value outside the vocabulary is not a
            // kind to be guessed at: it means the row was written by a build with different words.
            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_narrative_entries_kind",
                        "kind IN ('origin', 'audience', 'technical-route', 'decision', 'problem', 'feedback')");
                    table.HasCheckConstraint(
                        "ck_narrative_entries_visibility",
                        "visibility IN ('private', 'exportable')");
                    table.HasCheckConstraint(
                        "ck_narrative_entries_revision_at_least_one",
                        "revision >= 1");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(entry => entry.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_narrative_entries_project_id");
        });

        // NARRATIVE-MEMORY-002: the generated project summary, one row per project. A refresh
        // recomputes the input hash from the sources and either keeps this row or regenerates it;
        // the stale mark says a source moved after the body was built.
        modelBuilder.Entity<ProjectSummary>(entity =>
        {
            entity.ToTable("project_summaries");
            entity.HasKey(summary => summary.Id);
            entity.Property(summary => summary.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(summary => summary.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(summary => summary.ThroughMilestoneId).HasColumnName("through_milestone_id");
            entity.Property(summary => summary.InputHash)
                .HasColumnName("input_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(summary => summary.BodyJson)
                .HasColumnName("body_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(summary => summary.Revision).HasColumnName("revision").IsRequired();
            entity.Property(summary => summary.Stale).HasColumnName("stale").IsRequired();
            entity.Property(summary => summary.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(summary => summary.Revision).IsConcurrencyToken();

            // One summary per project: two refreshes that race still leave one row, and the loser
            // re-reads rather than writing a second.
            entity.HasIndex(summary => summary.ProjectId)
                .IsUnique()
                .HasDatabaseName("ux_project_summaries_project_id");

            entity.ToTable(
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_project_summaries_revision_at_least_one",
                        "revision >= 1");
                    table.HasCheckConstraint(
                        "ck_project_summaries_input_hash_sha256_hex",
                        "input_hash ~ '^[0-9a-f]{64}$'");
                });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(summary => summary.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_project_summaries_project_id");
        });

        // NARRATIVE-MEMORY-002: the values an owner stated when they asked for a refresh, kept
        // where the job that runs can read them. A queued job carries an operation id and nothing
        // else, so this row is the only way the worker learns the cutoff and the consented
        // private-entry selection.
        modelBuilder.Entity<NarrativeRefreshRequestRow>(entity =>
        {
            entity.ToTable("narrative_refresh_requests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(request => request.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(request => request.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(request => request.Cutoff)
                .HasColumnName("cutoff")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(request => request.SelectedEntryIds)
                .HasColumnName("selected_entry_ids")
                .HasColumnType("uuid[]")
                .IsRequired();
            entity.Property(request => request.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            // One request per job: a re-run reads the same authorized values rather than a second
            // row that could disagree with the first.
            entity.HasIndex(request => new { request.ScopeId, request.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_narrative_refresh_requests_scope_id_operation_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(request => request.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_narrative_refresh_requests_project_id");
        });

        // SCREENSHOT-EVOLUTION-IMAGES-001/002: one rendered comparison per row. The manifest is
        // the provenance - every depicted cell beside the media and caption revisions it was
        // built from - so the artifact is re-derivable rather than a second store of truth.
        // Selection stays the curation package's and approval stays a decision about this row's
        // two hashes, which is why no trigger here refuses anything: staleness is recomputed on
        // read rather than enforced on write.
        modelBuilder.Entity<EvolutionImage>(entity =>
        {
            entity.ToTable(
                "evolution_images",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_evolution_images_layout",
                        "layout IN ('before-after', 'horizontal-timeline', 'vertical-long', 'gallery')");
                    table.HasCheckConstraint("ck_evolution_images_status", "status IN ('draft', 'approved')");
                    table.HasCheckConstraint(
                        "ck_evolution_images_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint(
                        "ck_evolution_images_artifact_sha256_hex",
                        "artifact_sha256 ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint("ck_evolution_images_dimensions_positive", "width > 0 AND height > 0");
                });
            entity.HasKey(image => image.Id);
            entity.Property(image => image.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(image => image.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(image => image.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(image => image.Layout)
                .HasColumnName("layout")
                .HasMaxLength(EvolutionLayoutMaxLength)
                .IsRequired();
            entity.Property(image => image.ManifestJson)
                .HasColumnName("manifest_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(image => image.SelectionHash)
                .HasColumnName("selection_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(image => image.ArtifactMediaId).HasColumnName("artifact_media_id").IsRequired();
            entity.Property(image => image.ArtifactSha256)
                .HasColumnName("artifact_sha256")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(image => image.Width).HasColumnName("width").IsRequired();
            entity.Property(image => image.Height).HasColumnName("height").IsRequired();
            entity.Property(image => image.Status)
                .HasColumnName("status")
                .HasMaxLength(EvolutionStatusMaxLength)
                .IsRequired();
            entity.Property(image => image.ApprovedBy).HasColumnName("approved_by");
            entity.Property(image => image.ApprovedAt)
                .HasColumnName("approved_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(image => image.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(OperationIdempotencyKeyMaxLength);
            entity.Property(image => image.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(image => image.ProjectId)
                .HasDatabaseName("IX_evolution_images_project_id");

            // One selection per key: the same key with the same selection replays the row, and
            // with another selection it is a conflict rather than a second row. Null keys are
            // distinct by definition, so keyless renders never collide with each other.
            entity.HasIndex(image => new { image.ScopeId, image.ProjectId, image.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("ux_evolution_images_scope_id_project_id_idempotency_key")
                .HasFilter("idempotency_key IS NOT NULL");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(image => image.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evolution_images_project_id");

            entity.HasOne<MediaRecord>()
                .WithMany()
                .HasForeignKey(image => image.ArtifactMediaId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evolution_images_artifact_media_id");
        });

        // EVOLUTION-GIF-EXPORT-001/002: one rendered animation per row. The manifest is the
        // provenance - every depicted frame beside the media and caption revisions it was built
        // from, in canvas order, with the playback the review authorized - so the GIF is
        // re-derivable rather than a second store of truth. The artifact bytes live in this row
        // because the delivered media store is PNG-only by its own contract, and widening that
        // vocabulary is that package's boundary rather than this one's: bytes and manifest are
        // written in one INSERT, so a row can never name bytes that were not stored. Selection
        // stays the curation package's and approval stays a decision about this row's two
        // hashes, which is why no trigger here refuses anything: staleness is recomputed on
        // read rather than enforced on write.
        modelBuilder.Entity<EvolutionGif>(entity =>
        {
            entity.ToTable(
                "evolution_gifs",
                table =>
                {
                    table.HasCheckConstraint("ck_evolution_gifs_status", "status IN ('draft', 'approved')");
                    table.HasCheckConstraint(
                        "ck_evolution_gifs_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint(
                        "ck_evolution_gifs_artifact_sha256_hex",
                        "artifact_sha256 ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint("ck_evolution_gifs_dimensions_positive", "width > 0 AND height > 0");
                    table.HasCheckConstraint("ck_evolution_gifs_frame_count", "frame_count BETWEEN 2 AND 12");
                    table.HasCheckConstraint("ck_evolution_gifs_total_duration_ms", "total_duration_ms > 0 AND total_duration_ms <= 60000");
                });
            entity.HasKey(gif => gif.Id);
            entity.Property(gif => gif.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(gif => gif.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(gif => gif.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(gif => gif.ManifestJson)
                .HasColumnName("manifest_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(gif => gif.SelectionHash)
                .HasColumnName("selection_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(gif => gif.ArtifactSha256)
                .HasColumnName("artifact_sha256")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(gif => gif.ArtifactBytes)
                .HasColumnName("artifact_bytes")
                .HasColumnType("bytea")
                .IsRequired();
            entity.Property(gif => gif.Width).HasColumnName("width").IsRequired();
            entity.Property(gif => gif.Height).HasColumnName("height").IsRequired();
            entity.Property(gif => gif.FrameCount).HasColumnName("frame_count").IsRequired();
            entity.Property(gif => gif.TotalDurationMs).HasColumnName("total_duration_ms").IsRequired();
            entity.Property(gif => gif.Loop).HasColumnName("loop").IsRequired();
            entity.Property(gif => gif.Status)
                .HasColumnName("status")
                .HasMaxLength(EvolutionStatusMaxLength)
                .IsRequired();
            entity.Property(gif => gif.ApprovedBy).HasColumnName("approved_by");
            entity.Property(gif => gif.ApprovedAt)
                .HasColumnName("approved_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(gif => gif.IdempotencyKey)
                .HasColumnName("idempotency_key")
                .HasMaxLength(OperationIdempotencyKeyMaxLength);
            entity.Property(gif => gif.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(gif => gif.ProjectId)
                .HasDatabaseName("IX_evolution_gifs_project_id");

            // One selection per key: the same key with the same selection replays the row, and
            // with another selection it is a conflict rather than a second row. Null keys are
            // distinct by definition, so keyless renders never collide with each other.
            entity.HasIndex(gif => new { gif.ScopeId, gif.ProjectId, gif.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("ux_evolution_gifs_scope_id_project_id_idempotency_key")
                .HasFilter("idempotency_key IS NOT NULL");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(gif => gif.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_evolution_gifs_project_id");
        });

        // STATIC-BLOG-EXPORT-001/002: one export selection per row. The canonical asset list and
        // the frozen approval bindings are references the run re-resolves against the asset,
        // evidence and media stores - never approved text, which a queue row is the wrong place
        // for. The unique index on (scope, operation) is what lets a re-delivered enqueue find
        // the row its job was accepted with rather than growing a second one.
        modelBuilder.Entity<StaticBlogExportRequestRow>(entity =>
        {
            entity.ToTable(
                "static_blog_export_requests",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_static_blog_export_requests_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                });
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(request => request.OperationId).HasColumnName("operation_id").IsRequired();
            entity.Property(request => request.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(request => request.BasePath)
                .HasColumnName("base_path")
                .HasMaxLength(StaticBlogExportBasePathMaxLength)
                .IsRequired();
            entity.Property(request => request.AssetIdsJson)
                .HasColumnName("asset_ids_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(request => request.SelectionHash)
                .HasColumnName("selection_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(request => request.FrozenJson)
                .HasColumnName("frozen_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(request => request.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(request => new { request.ScopeId, request.OperationId })
                .IsUnique()
                .HasDatabaseName("ux_static_blog_export_requests_scope_id_operation_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(request => request.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_static_blog_export_requests_project_id");
        });

        // PUBLIC-PROJECT-JOURNEY-001/002: one explicit publication decision per project. The slug
        // is the global public address, so its unique index is global too; the (scope, project)
        // index is what makes one journey per project a database decision. The snapshot JSON is
        // a frozen projection of approved text and picture references the public routes serve -
        // never private notes, never candidates, never unselected pictures - and the two hashes
        // are what the publication and the public read compare against.
        modelBuilder.Entity<ProjectJourneyRow>(entity =>
        {
            entity.ToTable(
                "project_journeys",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_project_journeys_slug_kebab_case",
                        "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.HasCheckConstraint(
                        "ck_project_journeys_selection_hash_sha256_hex",
                        "selection_hash ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint(
                        "ck_project_journeys_snapshot_ref_sha256_hex",
                        "snapshot_ref ~ '^[0-9a-f]{64}$'");
                    table.HasCheckConstraint(
                        "ck_project_journeys_revision_nonnegative",
                        "revision >= 0");
                });
            entity.HasKey(journey => journey.Id);
            entity.Property(journey => journey.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(journey => journey.ScopeId).HasColumnName("scope_id").IsRequired();
            entity.Property(journey => journey.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(journey => journey.Slug)
                .HasColumnName("slug")
                .HasMaxLength(JourneySlugMaxLength)
                .IsRequired();
            entity.Property(journey => journey.Enabled).HasColumnName("enabled").IsRequired();
            entity.Property(journey => journey.Revision).HasColumnName("revision").IsRequired();
            entity.Property(journey => journey.SelectionHash)
                .HasColumnName("selection_hash")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(journey => journey.SnapshotRef)
                .HasColumnName("snapshot_ref")
                .HasMaxLength(Sha256HexMaxLength)
                .IsRequired();
            entity.Property(journey => journey.SnapshotJson)
                .HasColumnName("snapshot_json")
                .HasColumnType("jsonb")
                .IsRequired();
            entity.Property(journey => journey.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(journey => journey.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(journey => journey.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(journey => new { journey.ScopeId, journey.ProjectId })
                .IsUnique()
                .HasDatabaseName("ux_project_journeys_scope_id_project_id");

            entity.HasIndex(journey => journey.Slug)
                .IsUnique()
                .HasDatabaseName("ux_project_journeys_slug");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(journey => journey.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_project_journeys_project_id");
        });

        // CLOUD-TENANT-BOUNDARY-001/002: the Hosted identity and tenant isolation tables. A user
        // is one OIDC issuer plus one subject, a tenant owns one scope row with a fresh id, and a
        // membership is the only thing that authorizes a session under a tenant. The Community
        // installation scope is never referenced here, which is what keeps a tenant from
        // addressing Community rows. Row-level security on the tenant tables (see the forward
        // migration) additionally filters by the transaction's app.tenant_id, set per transaction
        // by the tenancy service and dead with it on a pooled connection.
        modelBuilder.Entity<HostedUser>(entity =>
        {
            entity.ToTable("hosted_users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(user => user.Issuer)
                .HasColumnName("issuer")
                .HasMaxLength(HostedIssuerMaxLength)
                .IsRequired();
            entity.Property(user => user.Subject)
                .HasColumnName("subject")
                .HasMaxLength(HostedSubjectMaxLength)
                .IsRequired();
            entity.Property(user => user.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(user => new { user.Issuer, user.Subject })
                .IsUnique()
                .HasDatabaseName("ux_hosted_users_issuer_subject");
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable(
                "tenants",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_tenants_revision_nonnegative",
                        "revision >= 0");
                });
            entity.HasKey(tenant => tenant.Id);
            entity.Property(tenant => tenant.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(tenant => tenant.Name)
                .HasColumnName("name")
                .HasMaxLength(Tenant.NameMaxLength)
                .IsRequired();
            entity.Property(tenant => tenant.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(tenant => tenant.Revision).HasColumnName("revision").IsRequired();
        });

        modelBuilder.Entity<TenantMembership>(entity =>
        {
            entity.ToTable(
                "tenant_memberships",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_tenant_memberships_role_known",
                        "role IN ('owner', 'editor', 'viewer')");
                    table.HasCheckConstraint(
                        "ck_tenant_memberships_revision_nonnegative",
                        "revision >= 0");
                });
            entity.HasKey(membership => new { membership.TenantId, membership.UserId });
            entity.Property(membership => membership.TenantId).HasColumnName("tenant_id").IsRequired();
            entity.Property(membership => membership.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(membership => membership.Role)
                .HasColumnName("role")
                .HasMaxLength(TenantRoleMaxLength)
                .IsRequired();
            entity.Property(membership => membership.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(membership => membership.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(membership => membership.Revision).HasColumnName("revision").IsRequired();

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(membership => membership.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_tenant_memberships_tenant_id");

            entity.HasOne<HostedUser>()
                .WithMany()
                .HasForeignKey(membership => membership.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_tenant_memberships_user_id");
        });

        modelBuilder.Entity<TenantScope>(entity =>
        {
            entity.ToTable("tenant_scopes");
            entity.HasKey(scope => scope.Id);
            entity.Property(scope => scope.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(scope => scope.TenantId).HasColumnName("tenant_id").IsRequired();
            entity.Property(scope => scope.InstallationId).HasColumnName("installation_id").IsRequired();
            entity.Property(scope => scope.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(scope => scope.TenantId)
                .IsUnique()
                .HasDatabaseName("ux_tenant_scopes_tenant_id");

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(scope => scope.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_tenant_scopes_tenant_id");
        });

        modelBuilder.Entity<HostedSession>(entity =>
        {
            entity.ToTable("hosted_sessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(session => session.IdHash)
                .HasColumnName("id_hash")
                .HasMaxLength(HostedSession.IdHashLength)
                .IsRequired();
            entity.Property(session => session.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(session => session.ActiveTenantId).HasColumnName("active_tenant_id");
            entity.Property(session => session.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(session => session.LastSeenAt)
                .HasColumnName("last_seen_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(session => session.IdleExpiresAt)
                .HasColumnName("idle_expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(session => session.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(session => session.AntiforgeryTokenHash)
                .HasColumnName("antiforgery_token_hash")
                .HasMaxLength(HostedSession.IdHashLength)
                .IsRequired();
            entity.Property(session => session.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");

            entity.HasIndex(session => session.IdHash)
                .IsUnique()
                .HasDatabaseName("ux_hosted_sessions_id_hash");
            entity.HasIndex(session => session.UserId).HasDatabaseName("ix_hosted_sessions_user_id");

            entity.HasOne<HostedUser>()
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_hosted_sessions_user_id");
        });

        modelBuilder.Entity<HostedLoginRequest>(entity =>
        {
            entity.ToTable("hosted_login_requests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(request => request.StateHash)
                .HasColumnName("state_hash")
                .HasMaxLength(HostedSession.IdHashLength)
                .IsRequired();
            entity.Property(request => request.NonceHash)
                .HasColumnName("nonce_hash")
                .HasMaxLength(HostedSession.IdHashLength)
                .IsRequired();
            entity.Property(request => request.CodeVerifier).HasColumnName("code_verifier");
            entity.Property(request => request.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(request => request.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(request => request.ConsumedAt)
                .HasColumnName("consumed_at")
                .HasColumnType("timestamp with time zone");

            entity.HasIndex(request => request.StateHash)
                .IsUnique()
                .HasDatabaseName("ux_hosted_login_requests_state_hash");
        });

        modelBuilder.Entity<HostedImport>(entity =>
        {
            entity.ToTable("hosted_imports");
            entity.HasKey(import => import.Id);
            entity.Property(import => import.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(import => import.InstallationId).HasColumnName("installation_id").IsRequired();
            entity.Property(import => import.TenantId).HasColumnName("tenant_id").IsRequired();
            entity.Property(import => import.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(import => import.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(import => new { import.InstallationId, import.TenantId })
                .IsUnique()
                .HasDatabaseName("ux_hosted_imports_installation_id_tenant_id");

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(import => import.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_hosted_imports_tenant_id");
        });

        // TEAM-PROJECT-PERMISSIONS-001/002: explicit bearer invitations and per-project grants.
        // An invitation is one live token per email per tenant; a grant is one explicit permission
        // per user per project. The role and permission vocabularies are closed by the database
        // itself, so no writer can invent a grant the policy never defined.
        modelBuilder.Entity<TenantInvitation>(entity =>
        {
            entity.ToTable(
                "tenant_invitations",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_tenant_invitations_role_known",
                        "role IN ('owner', 'editor', 'viewer')");
                });
            entity.HasKey(invitation => invitation.Id);
            entity.Property(invitation => invitation.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(invitation => invitation.TenantId).HasColumnName("tenant_id").IsRequired();
            entity.Property(invitation => invitation.Email)
                .HasColumnName("email")
                .HasMaxLength(TenantInvitation.EmailMaxLength)
                .IsRequired();
            entity.Property(invitation => invitation.Role)
                .HasColumnName("role")
                .HasMaxLength(TenantRoleMaxLength)
                .IsRequired();
            entity.Property(invitation => invitation.TokenHash)
                .HasColumnName("token_hash")
                .HasMaxLength(HostedSession.IdHashLength)
                .IsRequired();
            entity.Property(invitation => invitation.CreatedBy).HasColumnName("created_by").IsRequired();
            entity.Property(invitation => invitation.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(invitation => invitation.ExpiresAt)
                .HasColumnName("expires_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
            entity.Property(invitation => invitation.AcceptedAt)
                .HasColumnName("accepted_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.RevokedAt)
                .HasColumnName("revoked_at")
                .HasColumnType("timestamp with time zone");

            entity.HasIndex(invitation => invitation.TokenHash)
                .IsUnique()
                .HasDatabaseName("ux_tenant_invitations_token_hash");
            entity.HasIndex(invitation => new { invitation.TenantId, invitation.Email })
                .IsUnique()
                .HasDatabaseName("ux_tenant_invitations_tenant_id_email")
                .HasFilter("accepted_at IS NULL AND revoked_at IS NULL");

            entity.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(invitation => invitation.TenantId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_tenant_invitations_tenant_id");

            entity.HasOne<HostedUser>()
                .WithMany()
                .HasForeignKey(invitation => invitation.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_tenant_invitations_created_by");
        });

        modelBuilder.Entity<ProjectMembership>(entity =>
        {
            entity.ToTable(
                "project_memberships",
                table =>
                {
                    table.HasCheckConstraint(
                        "ck_project_memberships_permission_known",
                        "permission IN ('reader', 'editor', 'approver', 'publisher')");
                    table.HasCheckConstraint(
                        "ck_project_memberships_revision_nonnegative",
                        "revision >= 0");
                });
            entity.HasKey(grant => new { grant.ProjectId, grant.UserId, grant.Permission });
            entity.Property(grant => grant.ProjectId).HasColumnName("project_id").IsRequired();
            entity.Property(grant => grant.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(grant => grant.Permission)
                .HasColumnName("permission")
                .HasMaxLength(ProjectPermissionMaxLength)
                .IsRequired();
            entity.Property(grant => grant.Revision).HasColumnName("revision").IsRequired();
            entity.Property(grant => grant.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(grant => grant.ProjectId).HasDatabaseName("ix_project_memberships_project_id");
            entity.HasIndex(grant => grant.UserId).HasDatabaseName("ix_project_memberships_user_id");

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(grant => grant.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_project_memberships_project_id");

            entity.HasOne<HostedUser>()
                .WithMany()
                .HasForeignKey(grant => grant.UserId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_project_memberships_user_id");
        });
    }

    /// <summary>At most 40 characters for a content kind, matching the Core record's own bound.</summary>
    private const int ContentAssetTypeMaxLength = ContentAssetTypes.MaxLength;

    /// <summary>At most 32 characters for a content status, matching the Core record's own bound.</summary>
    private const int ContentAssetStatusMaxLength = ContentAssetStatus.MaxLength;

    /// <summary>At most 200 characters for a provider id, a model name or a stored label.</summary>
    private const int ContentLabelMaxLength = ContentAssetLimits.LabelMaxLength;

    /// <summary>At most 64 characters for a prompt or schema version label.</summary>
    private const int ContentVersionMaxLength = 64;

    /// <summary>At most 32 characters for an approval payload kind, matching the Core record's own bound.</summary>
    private const int ContentPayloadKindMaxLength = ContentPayloadKinds.MaxLength;

    /// <summary>At most 64 characters for a publisher target id, matching the Core record's own bound.</summary>
    private const int PublisherTargetMaxLength = PublisherTargetIds.MaxLength;

    /// <summary>At most 16 characters for a publish mode, matching the Core record's own bound.</summary>
    private const int PublishModeMaxLength = PublishModes.MaxLength;

    /// <summary>At most 16 characters for a publication attempt state, matching the Core record's own bound.</summary>
    private const int PublishStateMaxLength = PublishStates.MaxLength;

    /// <summary>At most 200 characters for a caller idempotency key, matching the Core record's own bound.</summary>
    private const int PublisherIdempotencyKeyMaxLength = PublicationAttempt.Limits.MaxIdempotencyKeyLength;

    /// <summary>At most 200 characters for a remote side's identifier, matching the Core record's own bound.</summary>
    private const int PublisherRemoteIdMaxLength = PublicationAttempt.Limits.MaxRemoteIdLength;

    /// <summary>At most 2000 characters for a remote side's address, matching the Core record's own bound.</summary>
    private const int PublisherRemoteUrlMaxLength = PublicationAttempt.Limits.MaxRemoteUrlLength;

    /// <summary>At most 64 characters for a redacted publication failure code, matching the Core record's own bound.</summary>
    private const int PublisherErrorCodeMaxLength = PublicationAttempt.Limits.MaxErrorCodeLength;

    /// <summary>At most 512 characters for a wait selector, matching the application-side bound.</summary>
    private const int CaptureSelectorMaxLength = 512;

    /// <summary>At most 2048 characters for a stored URL, matching the Core record's own bound.</summary>
    private const int CaptureUrlMaxLength = ScreenshotCandidateLimits.MaximumUrlLength;

    /// <summary>At most 32 characters for a candidate status.</summary>
    private const int CaptureStatusMaxLength = 32;

    /// <summary>At most 1000 characters for a caption, matching the Core record's own bound.</summary>
    private const int CaptureCaptionMaxLength = ScreenshotCurationLimits.CaptionMaxLength;

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

    /// <summary>
    /// At most 39 characters; the longest operator-chosen root alias, and the same bound the repository
    /// reference uses for an owner.
    /// </summary>
    private const int LocalRootAliasMaxLength = 39;

    /// <summary>
    /// At most 255 characters; the longest relative path inside a registered root, and the same bound the
    /// reference contract enforces before a row exists.
    /// </summary>
    private const int LocalRelativePathMaxLength = 255;

    /// <summary>At most 255 characters; a local ref is a branch, tag or revision like any other.</summary>
    private const int LocalRefMaxLength = 255;

    /// <summary>At most 64 characters; a SHA-1 or SHA-256 revision is hex.</summary>
    private const int LocalShaMaxLength = 64;

    /// <summary>
    /// At most 500 characters for the consulted subcommand list. A local run issues a bounded number of
    /// commands and this column holds only their verbs, so the bound is generous rather than tight.
    /// </summary>
    private const int LocalCommandLogMaxLength = 500;

    // ---------------------------------------------------------------------------------------------
    // github-release-candidates. Every bound below is the application policy's own number rather than
    // a fresh one, so the column and the rule that writes it cannot disagree: a value the policy
    // already cut is always within the column, and a row that somehow was not is refused by the column
    // instead of being silently shortened.
    // ---------------------------------------------------------------------------------------------

    /// <summary>At most 64 characters; GitHub's delivery ids are UUIDs and this leaves room.</summary>
    private const int GitHubDeliveryIdMaxLength = 64;

    /// <summary>At most 40 characters; the longest routed event name is <c>pull_request</c>.</summary>
    private const int GitHubCandidateEventNameMaxLength = 40;

    /// <summary>At most 12 characters; the longest receipt status is <c>recorded</c>.</summary>
    private const int GitHubCandidateStatusMaxLength = 12;

    /// <summary>At most 24 characters; the longest reason is <c>below-star-threshold</c>.</summary>
    private const int GitHubCandidateReasonMaxLength = 24;

    /// <summary>At most 16 characters; the longest kind is <c>pull-request</c>.</summary>
    private const int GitHubCandidateKindMaxLength = 16;

    /// <summary>At most 200 characters; a provider's release name, cut on the way in.</summary>
    private const int GitHubCandidateTitleMaxLength = 200;

    /// <summary>At most 320 characters; the widest source key the builder can produce.</summary>
    private const int GitHubCandidateSourceKeyMaxLength = 320;

    /// <summary>At most 64 characters; a commit sha or a stable revision, both hex or ISO 8601.</summary>
    private const int GitHubCandidateRevisionMaxLength = 64;

    /// <summary>At most 8 characters; the longest source is <c>webhook</c>.</summary>
    private const int GitHubCandidateSourceMaxLength = 8;

    /// <summary>
    /// At most 200 characters for the consulted path list. A candidate poll consults exactly two
    /// endpoints, so this is generous rather than tight, and a run that somehow exceeded it is refused
    /// by the column instead of being silently shortened.
    /// </summary>
    private const int GitHubCandidateEndpointsMaxLength = 200;

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

    /// <summary>At most 15 characters; the longest narrative kind is <c>technical-route</c>.</summary>
    private const int NarrativeKindMaxLength = 15;

    /// <summary>At most 10 characters; the longest narrative visibility is <c>exportable</c>.</summary>
    private const int NarrativeVisibilityMaxLength = 10;

    /// <summary>At most 32 characters; the longest evolution layout is <c>horizontal-timeline</c>.</summary>
    private const int EvolutionLayoutMaxLength = EvolutionLayouts.MaxLength;

    /// <summary>At most 32 characters; the longest evolution status is <c>approved</c>.</summary>
    private const int EvolutionStatusMaxLength = 32;

    /// <summary>At most 128 characters for a deployment base path, matching the builder's own bound.</summary>
    private const int StaticBlogExportBasePathMaxLength = 128;

    /// <summary>At most 80 characters for a journey slug, matching the service's own bound.</summary>
    private const int JourneySlugMaxLength = 80;

    /// <summary>At most 512 characters for a stored OIDC issuer URL.</summary>
    private const int HostedIssuerMaxLength = 512;

    /// <summary>At most 512 characters for a stored OIDC subject identifier.</summary>
    private const int HostedSubjectMaxLength = 512;

    /// <summary>At most 16 characters for a stored tenant role.</summary>
    private const int TenantRoleMaxLength = 16;

    /// <summary>At most 16 characters for a stored project permission; the longest is 'publisher'.</summary>
    private const int ProjectPermissionMaxLength = 16;

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
