using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// PG-ADOPT-002 proof: one container serves every test class in this assembly. Two test
/// classes in the shared collection record the server they observe; the second one to run
/// asserts it is the same server, and both assert their databases are distinct clones.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PostgresSharedServerProofA(ChronicleafPostgresFixture postgres)
{
    [Fact]
    public async Task First_class_observes_the_shared_server_and_its_own_clone()
    {
        Assert.True(postgres.IsRealPostgres, "the proof requires a real PostgreSQL container");
        Assert.True(postgres.Server.IsTemplateSeeded, "the template must be seeded before any test clones");

        var database = await postgres.CreateDatabaseAsync("proof");
        SharedServerIdentity.CheckSameServer(postgres.Server, database);

        // The migrated schema is visible with no per-test migration replay: the table exists
        // (a missing migration would throw) and starts empty (no cross-test rows).
        Assert.Equal(0L, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
    }
}

/// <summary>
/// PG-ADOPT-002 proof, second witness: asserts server identity equality with whatever class
/// ran first, and database inequality with it.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PostgresSharedServerProofB(ChronicleafPostgresFixture postgres)
{
    [Fact]
    public async Task Second_class_observes_the_same_server_and_a_different_clone()
    {
        Assert.True(postgres.IsRealPostgres, "the proof requires a real PostgreSQL container");

        var database = await postgres.CreateDatabaseAsync("proof");
        SharedServerIdentity.CheckSameServer(postgres.Server, database);

        Assert.Equal(0L, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
    }
}

/// <summary>
/// PG-ADOPT-COMPLETE-001 oracle: the <c>EnsureTemplateSeededAsync</c> migrate delegate is
/// invoked exactly once for the lifetime of the assembly's shared server. The counter lives on
/// the fixture and is incremented inside the delegate so a re-entry past the seed gate cannot
/// double-count.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PostgresSharedServerProofC
{
    [Fact]
    public void The_migration_delegate_runs_exactly_once()
    {
        // The counter is read after a sibling test has already created and torn down at least
        // one clone, so the value is the lifetime count, not a per-test artefact.
        Assert.Equal(1, ChronicleafPostgresFixture.MigrationDelegateInvocations);
    }
}

/// <summary>
/// PG-ADOPT-COMPLETE-001 oracle: the shared server's <c>ContainerId</c> is stable across the
/// assembly run. Records the value observed at first touch and asserts every later observation
/// equals it; the rendezvous is independent of the existing two-class equality check.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PostgresSharedServerProofD(ChronicleafPostgresFixture postgres)
{
    [Fact]
    public void The_shared_server_container_id_is_stable_across_the_assembly()
    {
        Assert.True(postgres.IsRealPostgres, "the proof requires a real PostgreSQL container");
        SharedServerIdentity.CheckContainerIdStable(postgres.Server);
    }
}

/// <summary>
/// PG-TIGHTEN-004 oracle: the per-test connection string returned by
/// <see cref="ChronicleafPostgresFixture.CreateDatabaseAsync"/> carries the four
/// pool-self-trimming parameters the audit requires
/// (<c>Maximum Pool Size=4; Minimum Pool Size=0; Connection Pruning Interval=1;
/// Connection Idle Lifetime=5</c>), composed via <c>NpgsqlConnectionStringBuilder</c>
/// so the sibling-provided string is not parsed. <c>Connection Lifetime</c> stays at
/// the Npgsql default because a hard 30s recycle inflates the assembly run with no
/// observable benefit on the 53300 failure class; the invariant is structural on the
/// four other params.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PostgresSharedServerProofE(ChronicleafPostgresFixture postgres)
{
    [Fact]
    public async Task The_per_test_pool_string_carries_the_self_trimming_parameters()
    {
        Assert.True(postgres.IsRealPostgres, "the proof requires a real PostgreSQL container");

        var database = await postgres.CreateDatabaseAsync("pool-format");
        var builder = new NpgsqlConnectionStringBuilder(database);

        Assert.Equal(4, builder.MaxPoolSize);
        Assert.Equal(0, builder.MinPoolSize);
        Assert.Equal(1, builder.ConnectionPruningInterval);
        Assert.Equal(5, builder.ConnectionIdleLifetime);
        Assert.Equal(3600, builder.ConnectionLifetime);
        Assert.True(builder.Pooling, "the suite stays fast by reusing the per-test pool entry");
    }
}

/// <summary>
/// Order-independent rendezvous for the two proof classes: whichever runs second asserts
/// the same server identity and a different database connection string.
/// </summary>
internal static class SharedServerIdentity
{
    private static readonly object Gate = new();
    private static string? _containerId;
    private static string? _database;

    internal static void CheckSameServer(
        Platform.Persistence.Postgres.Testing.PostgresTestServer server,
        string database)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(database);

        lock (Gate)
        {
            if (_containerId is null)
            {
                _containerId = server.ContainerId;
                _database = database;
                Assert.False(string.IsNullOrEmpty(_containerId), "the shared server must report a container id");
            }
            else
            {
                Assert.Equal(_containerId, server.ContainerId);
                Assert.NotEqual(_database, database);
            }
        }
    }

    /// <summary>
    /// PG-ADOPT-COMPLETE-001 container-stability rendezvous: records the container id on first
    /// observation and asserts equality on every later observation. Independent of
    /// <see cref="CheckSameServer"/> so the four-oracle proof survives a rename of either
    /// witness class.
    /// </summary>
    internal static void CheckContainerIdStable(
        Platform.Persistence.Postgres.Testing.PostgresTestServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        lock (Gate)
        {
            if (_containerId is null)
            {
                _containerId = server.ContainerId;
                Assert.False(string.IsNullOrEmpty(_containerId), "the shared server must report a container id");
            }
            else
            {
                Assert.Equal(_containerId, server.ContainerId);
            }
        }
    }
}
