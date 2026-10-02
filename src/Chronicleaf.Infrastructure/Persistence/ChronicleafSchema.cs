namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Identity of the schema this build requires.
/// </summary>
/// <remarks>
/// The forward migration id is pinned here because readiness compares it against the
/// <c>__EFMigrationsHistory</c> of the reachable database. A database whose history stops at an
/// earlier migration is a real, older schema, not a ready one.
/// </remarks>
public static class ChronicleafSchema
{
    /// <summary>
    /// The forward migration that brings a database to the schema this build requires. Must stay
    /// in step with the generated migration class in <c>Persisted/Migrations</c>.
    /// </summary>
    public const string ExpectedMigrationId = "20261002080845_InitialCommunityPersistenceSchema";
}
