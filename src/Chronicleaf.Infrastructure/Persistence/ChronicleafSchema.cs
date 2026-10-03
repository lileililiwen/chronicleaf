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

    /// <summary>Every migration a database must carry before this build calls itself ready.</summary>
    public static readonly IReadOnlyList<string> RequiredMigrationIds =
    [
        InitialMigrationId,
        OwnerAccessMigrationId,
        ProjectsAndMilestonesMigrationId,
        BackgroundOperationsMigrationId,
        LocalMediaMigrationId,
        MilestoneEvidenceMigrationId,
    ];

    /// <summary>
    /// The newest required forward migration, kept for the single-id checks that predate the
    /// later migrations and for the readiness message. It is the last entry of
    /// <see cref="RequiredMigrationIds"/>.
    /// </summary>
    public const string ExpectedMigrationId = MilestoneEvidenceMigrationId;

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
