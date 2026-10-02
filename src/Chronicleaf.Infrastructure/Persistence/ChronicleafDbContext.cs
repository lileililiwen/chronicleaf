using Chronicleaf.Core;
using Chronicleaf.Core.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// The PostgreSQL context behind the Community data foundation: the singleton
/// <c>installation</c> row, the append-only <c>audit_events</c> log, the durable
/// <c>outbox</c>, the access package's <c>session</c>, <c>login_attempt</c> and
/// <c>access_state</c> tables, and the <c>projects</c> and <c>milestones</c> tables that hold
/// the owner's own records.
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
    }

    /// <summary>At most 32 characters; the longest fixed milestone type is <c>first-customer</c>.</summary>
    private const int MilestoneTypeMaxLength = 32;

    /// <summary>At most 16 characters; the longest status is <c>dismissed</c>.</summary>
    private const int MilestoneStatusMaxLength = 16;
}
