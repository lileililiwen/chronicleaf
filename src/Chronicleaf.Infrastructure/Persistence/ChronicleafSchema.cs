namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Identity of the schema this build requires.
/// </summary>
/// <remarks>
/// The forward migration ids are pinned here because readiness compares them against the
/// <c>__EFMigrationsHistory</c> of the reachable database. A database whose history stops at an
/// earlier migration is a real, older schema, not a ready one - and because every later package adds
/// its own migration, readiness requires <em>every</em> id in <see cref="RequiredMigrationIds"/>, not
/// only the newest one. A database that has the newest migration but is missing an earlier one would
/// be in a state no supported upgrade path produces, and it is refused rather than half-trusted.
/// </remarks>
public static class ChronicleafSchema
{
    /// <summary>The first forward migration, owned by the data-foundation package.</summary>
    public const string InitialMigrationId = "20261002080845_InitialCommunityPersistenceSchema";

    /// <summary>
    /// The forward migration owned by the access package. Must stay in step with the generated
    /// migration class in <c>Persistence/Migrations</c>.
    /// </summary>
    public const string OwnerAccessMigrationId = "20261002091842_AddCommunityOwnerAccessBoundary";

    /// <summary>
    /// The forward migration that adds the <c>projects</c> and <c>milestones</c> tables
    /// (PROJECTS-AND-MILESTONES-001/002). A database that has never applied it is not a Community
    /// schema this build can serve: its owner has no project to work on and its routes would fail
    /// against missing tables, so readiness says so by name rather than answering 200 and failing on
    /// the first request.
    /// </summary>
    public const string ProjectsAndMilestonesMigrationId = "20261002101309_AddProjectsAndMilestones";

    /// <summary>
    /// The forward migration that adds the <c>operations</c> table and the queue's own
    /// <c>hangfire</c> schema (BACKGROUND-JOBS-001/002). A database that has never applied it has
    /// nowhere to record what background work was asked for or what became of it, so readiness says
    /// so by name rather than answering 200 and failing on the first operation.
    /// </summary>
    public const string BackgroundOperationsMigrationId = "20261002112221_AddBackgroundOperations";

    /// <summary>
    /// The forward migration that adds the <c>media</c> table (LOCAL-MEDIA-STORAGE-001/002). A database
    /// that has never applied it has nowhere to record a stored image, so its media routes would fail
    /// against a missing table; readiness says so by name rather than answering 200 and failing on
    /// the first upload.
    /// </summary>
    public const string LocalMediaMigrationId = "20261003111500_AddLocalMediaStorage";

    /// <summary>
    /// The forward migration that adds the <c>evidence_bundles</c> and <c>evidence_items</c> tables and
    /// the triggers that make them append-only (MILESTONE-EVIDENCE-001/002). A database that has never
    /// applied it has nowhere to record what was actually collected for a milestone, so its evidence
    /// routes would fail against missing tables; readiness says so by name rather than answering 200
    /// and failing on the first read.
    /// </summary>
    public const string MilestoneEvidenceMigrationId = "20261003121500_AddMilestoneEvidence";

    /// <summary>
    /// The forward migration that adds the <c>credentials</c> table (CREDENTIAL-VAULT-001/002). A
    /// database that has never applied it has nowhere to record a sealed credential, so its settings
    /// routes would fail against a missing table; readiness says so by name rather than answering 200
    /// and failing on the first save.
    /// </summary>
    public const string CredentialVaultMigrationId = "20261003124600_AddCredentialVaultCredentials";

    /// <summary>
    /// The forward migration that adds the <c>voice_profiles</c> and <c>voice_drafts</c> tables
    /// (VOICE-PROFILES-001/002). A database that has never applied it has nowhere to record the
    /// owner's writing voice, so its voice routes would fail against missing tables; readiness says
    /// so by name rather than answering 200 and failing on the first read.
    /// </summary>
    public const string VoiceProfilesMigrationId = "20261003131200_AddVoiceProfiles";

    /// <summary>
    /// The forward migration that adds the <c>generation_previews</c> table
    /// (LLM-PROVIDER-CONTRACT-002). A database that has never applied it has nowhere to record an
    /// owner-authorized redacted input, so its generation routes would fail against a missing table;
    /// readiness says so by name rather than answering 200 and failing on the first preview.
    /// </summary>
    public const string GenerationPreviewsMigrationId = "20261003141800_AddGenerationPreviews";

    /// <summary>
    /// The forward migration that adds the <c>github_connections</c> and <c>sync_sources</c> tables
    /// (GITHUB-EVIDENCE-001/002). A database that has never applied it has nowhere to record which
    /// repository a project collects from, nor what a collection actually read, so its GitHub routes
    /// would fail against a missing table; readiness says so by name rather than answering 200 and
    /// failing on the first connection.
    /// </summary>
    public const string GitHubEvidenceMigrationId = "20261003152000_AddGitHubEvidence";

    /// <summary>
    /// The forward migration that adds the <c>capture_requests</c> and
    /// <c>screenshot_candidates</c> tables (SCREENSHOT-CAPTURE-001/002). A database that has never
    /// applied it has nowhere to record what a capture job was asked to photograph, nor which
    /// candidate belongs to which milestone, so its capture routes would fail against a missing
    /// table; readiness says so by name rather than answering 200 and failing on the first request.
    /// </summary>
    public const string ScreenshotCaptureMigrationId = "20261004080450_AddScreenshotCapture";

    /// <summary>
    /// The forward migration that adds the <c>content_assets</c>, <c>content_revisions</c> and
    /// <c>content_draft_requests</c> tables (GROUNDED-MARKDOWN-DRAFTS-001/002). A database that has never
    /// applied it has nowhere to record a draft, so its draft routes would fail against missing tables;
    /// readiness says so by name rather than answering 200 and failing on the first request.
    /// </summary>
    public const string GroundedMarkdownDraftsMigrationId = "20261005081500_AddGroundedMarkdownDrafts";

    /// <summary>
    /// The forward migration that curates the <c>screenshot_candidates</c> table
    /// (SCREENSHOT-CURATION-001/002): a wider caption for the owner's words, the explicit display
    /// order of the selected pictures, the one-primary-per-milestone index and the status
    /// vocabulary. It also drops the milestone-only index the capture migration created but the
    /// model never described, so the database and the model agree again. A database that has never
    /// applied it has nowhere to record what an owner chose, so its curation routes would fail
    /// against missing columns; readiness says so by name rather than answering 200 and failing on
    /// the first selection.
    /// </summary>
    public const string ScreenshotCurationMigrationId = "20261006090000_AddScreenshotCuration";

    /// <summary>
    /// The forward migration that adds the <c>content_approvals</c> table
    /// (MARKDOWN-REVIEW-EXPORT-001/002): one human decision per content revision, bound to the
    /// revision, the body hash, the evidence revision, the voice snapshot hash and the media
    /// selection hash, with revision and digest check constraints. A database that has never
    /// applied it has nowhere to record an approval, so its review routes would fail against a
    /// missing table; readiness says so by name rather than answering 200 and failing on the
    /// first approval.
    /// </summary>
    public const string MarkdownReviewExportMigrationId = "20261007090000_AddMarkdownReviewExport";

    /// <summary>
    /// The forward migration that adds the <c>local_repositories</c> and <c>local_sync_sources</c> tables
    /// and the check constraints that keep a relative path inside its registered root
    /// (LOCAL-GIT-EVIDENCE-001/002). A database that has never applied it has nowhere to record which
    /// registered root a project collects from, nor what a run actually read, so its local-git routes would
    /// fail against a missing table; readiness says so by name rather than answering 200 and failing on the
    /// first registration.
    /// </summary>
    public const string LocalGitEvidenceMigrationId = "20261008090000_AddLocalGitEvidence";

    /// <summary>
    /// The forward migration that adds the <c>github_webhook_subscriptions</c>,
    /// <c>github_webhook_deliveries</c>, <c>github_candidates</c> and <c>github_candidate_runs</c> tables
    /// with the unique indexes that make one candidate per provider source key and one receipt per
    /// delivery database decisions (GITHUB-RELEASE-CANDIDATES-001/002). A database that has never
    /// applied it has nowhere to record a delivery, a receipt or a candidate, so its candidate routes
    /// would fail against missing tables; readiness says so by name rather than answering 200 and
    /// failing on the first delivery.
    /// </summary>
    public const string GitHubReleaseCandidatesMigrationId = "20261009090000_AddGitHubReleaseCandidates";

    /// <summary>
    /// The forward migration that adds the <c>github_enrichment_runs</c> table
    /// (GITHUB-GRAPHQL-ENRICHMENT-001/002). A database that has never applied it has nowhere to
    /// record which evidence revision an enrichment was asked against, so its enrichment routes
    /// would fail against a missing table; readiness says so by name rather than answering 200 and
    /// failing on the first request.
    /// </summary>
    public const string GitHubGraphqlEnrichmentMigrationId = "20261010090000_AddGitHubGraphqlEnrichment";

    /// <summary>
    /// The forward migration that adds the <c>narrative_entries</c>, <c>project_summaries</c> and
    /// <c>narrative_refresh_requests</c> tables (NARRATIVE-MEMORY-001/002): one owner-written,
    /// attributed line of history per entry with its revision compare-and-set, one generated
    /// summary per project with its input hash and stale mark, and one durable refresh request per
    /// job. A database that has never applied it has nowhere to record narrative history, so its
    /// narrative routes would fail against missing tables; readiness says so by name rather than
    /// answering 200 and failing on the first request.
    /// </summary>
    public const string NarrativeMemoryMigrationId = "20261011090000_AddNarrativeMemory";

    /// <summary>
    /// The forward migration that adds the <c>evolution_images</c> table
    /// (SCREENSHOT-EVOLUTION-IMAGES-001/002). A database that has never applied it has nowhere
    /// to record a rendered comparison, so its evolution routes would fail against a missing
    /// table; readiness says so by name rather than answering 200 and failing on the first
    /// render.
    /// </summary>
    public const string ScreenshotEvolutionImagesMigrationId = "20261012090000_AddScreenshotEvolutionImages";

    /// <summary>
    /// The forward migration that adds the <c>static_blog_export_requests</c> table
    /// (STATIC-BLOG-EXPORT-001/002). A database that has never applied it has nowhere to record
    /// an export selection, so its static blog routes would fail against a missing table;
    /// readiness says so by name rather than answering 200 and failing on the first request.
    /// </summary>
    public const string StaticBlogExportMigrationId = "20261013090000_AddStaticBlogExport";

    /// <summary>
    /// The forward migration that adds the <c>project_journeys</c> table
    /// (PUBLIC-PROJECT-JOURNEY-001/002). A database that has never applied it has nowhere to
    /// record an explicit publication decision, so its journey routes would fail against a
    /// missing table; readiness says so by name rather than answering 200 and failing on the
    /// first request.
    /// </summary>
    public const string PublicProjectJourneyMigrationId = "20261014090000_AddPublicProjectJourney";

    /// <summary>
    /// The forward migration that adds the <c>clients</c> table and the nullable
    /// <c>projects.client_id</c> association (AGENCY-CLIENT-PROJECTS-001/002). A database that has
    /// never applied it has nowhere to record an agency client, so its client routes would fail
    /// against a missing table; readiness says so by name rather than answering 200 and failing
    /// on the first request.
    /// </summary>
    public const string AgencyClientProjectsMigrationId = "20261015090000_AddAgencyClientProjects";

    /// <summary>
    /// The forward migration that adds the <c>hosted_users</c>, <c>tenants</c>,
    /// <c>tenant_memberships</c>, <c>tenant_scopes</c>, <c>hosted_sessions</c>,
    /// <c>hosted_login_requests</c> and <c>hosted_imports</c> tables with the row-level
    /// policies that filter the tenant tables by the transaction's tenant context
    /// (CLOUD-TENANT-BOUNDARY-001/002). A database that has never applied it has nowhere to
    /// record a Hosted identity or a tenant scope, so its tenancy routes would fail against
    /// missing tables; readiness says so by name rather than answering 200 and failing on the
    /// first login.
    /// </summary>
    public const string CloudTenantBoundaryMigrationId = "20261006013139_AddCloudTenantBoundary";

    /// <summary>
    /// The forward migration that adds the <c>tenant_invitations</c> and
    /// <c>project_memberships</c> tables and the <c>tenant_memberships.revision</c> column
    /// (TEAM-PROJECT-PERMISSIONS-001/002). A database that has never applied it has nowhere to
    /// record an invitation or a project grant, so its team routes would fail against missing
    /// tables; readiness says so by name rather than answering 200 and failing on the first
    /// request.
    /// </summary>
    public const string TeamProjectPermissionsMigrationId = "20261006024003_AddTeamProjectPermissions";

    /// <summary>
    /// The forward migration that adds the <c>media.provider_id</c> column and the
    /// <c>media_migration_copies</c> ledger table (CLOUD-OBJECT-STORAGE-001/002). A database that
    /// has never applied it has nowhere to record which backend a media object's verified bytes
    /// live on or what a migration run did, so its S3 routes would fail against a missing column
    /// and table; readiness says so by name rather than answering 200 and failing on the first
    /// request. Existing rows read <c>local</c> through the column default, so a database that
    /// stops here still serves everything it served before.
    /// </summary>
    public const string CloudObjectStorageMigrationId = "20261006032737_AddCloudObjectStorage";

    /// <summary>
    /// The forward migration that adds the <c>project_sync_schedules</c> table
    /// (SCHEDULED-PROJECT-SYNC-001/002). A database that has never applied it has nowhere to
    /// record an opt-in sync schedule, so its schedule routes would fail against a missing table;
    /// readiness says so by name rather than answering 200 and failing on the first request.
    /// </summary>
    public const string ScheduledProjectSyncMigrationId = "20261016090000_AddScheduledProjectSync";

    /// <summary>
    /// The forward migration that adds the <c>archive_tombstones</c> and
    /// <c>archive_backup_manifests</c> tables (HOSTED-ARCHIVE-OPERATIONS-001/002). A database
    /// that has never applied it has nowhere to record a recoverable deletion or an operator
    /// backup manifest, so its archive routes would fail against missing tables; readiness says
    /// so by name rather than answering 200 and failing on the first request.
    /// </summary>
    public const string HostedArchiveOperationsMigrationId = "20261006051619_AddHostedArchiveOperations";
    public static readonly IReadOnlyList<string> RequiredMigrationIds =
    [
        InitialMigrationId,
        OwnerAccessMigrationId,
        ProjectsAndMilestonesMigrationId,
        BackgroundOperationsMigrationId,
        LocalMediaMigrationId,
        MilestoneEvidenceMigrationId,
        CredentialVaultMigrationId,
        VoiceProfilesMigrationId,
        GenerationPreviewsMigrationId,
        GitHubEvidenceMigrationId,
        ScreenshotCaptureMigrationId,
        GroundedMarkdownDraftsMigrationId,
        ScreenshotCurationMigrationId,
        MarkdownReviewExportMigrationId,
        LocalGitEvidenceMigrationId,
        GitHubReleaseCandidatesMigrationId,
        GitHubGraphqlEnrichmentMigrationId,
        NarrativeMemoryMigrationId,
        ScreenshotEvolutionImagesMigrationId,
        StaticBlogExportMigrationId,
        PublicProjectJourneyMigrationId,
        AgencyClientProjectsMigrationId,
        CloudTenantBoundaryMigrationId,
        TeamProjectPermissionsMigrationId,
        CloudObjectStorageMigrationId,
        ScheduledProjectSyncMigrationId,
        HostedArchiveOperationsMigrationId,
    ];

    /// <summary>
    /// The newest required forward migration, kept for the single-id checks that predate the
    /// later migrations and for the readiness message. It is the last entry of
    /// <see cref="RequiredMigrationIds"/>.
    /// </summary>
    public const string ExpectedMigrationId = HostedArchiveOperationsMigrationId;

    /// <summary>True when the applied migrations include every id this build requires.</summary>
    public static bool IsCurrent(IReadOnlyCollection<string> appliedMigrationIds) =>
        RequiredMigrationIds.All(required => appliedMigrationIds.Contains(required, StringComparer.Ordinal));

    /// <summary>
    /// The required ids an applied set is still missing, in requirement order. Readiness reports
    /// this so a mismatch names what has to be applied without exposing anything secret.
    /// </summary>
    public static IReadOnlyList<string> MissingMigrationIds(IReadOnlyCollection<string> appliedMigrationIds) =>
        [.. RequiredMigrationIds.Where(required => !appliedMigrationIds.Contains(required, StringComparer.Ordinal))];
}
