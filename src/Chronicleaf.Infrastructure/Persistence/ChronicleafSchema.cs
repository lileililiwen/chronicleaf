namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Identity of the schema this build requires.
/// </summary>
/// <remarks>
/// The forward migration ids are pinned here because readiness compares them against the
/// <c>__EFMigrationsHistory</c> of the reachable database. A database whose history stops at an
/// earlier migration is a real, older schema, not a ready one - and because the access package
/// adds its own migration, readiness now requires <em>every</em> id in
/// <see cref="RequiredMigrationIds"/>, not only the newest one. A database that has the newest
/// migration but is missing an earlier one would be in a state no supported upgrade path produces,
/// and it is refused rather than half-trusted.
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

    /// <summary>Every migration a database must carry before this build calls itself ready.</summary>
    public static readonly IReadOnlyList<string> RequiredMigrationIds =
    [
        InitialMigrationId,
        OwnerAccessMigrationId,
    ];

    /// <summary>
    /// The newest required forward migration, kept for the single-id checks that predate the
    /// second migration and for the readiness message. It is the last entry of
    /// <see cref="RequiredMigrationIds"/>.
    /// </summary>
    public const string ExpectedMigrationId = OwnerAccessMigrationId;

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
