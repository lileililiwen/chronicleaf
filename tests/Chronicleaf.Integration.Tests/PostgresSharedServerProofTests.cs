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
}
