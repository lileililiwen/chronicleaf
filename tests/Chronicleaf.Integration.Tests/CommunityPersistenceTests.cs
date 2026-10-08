using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// COMMUNITY-PERSISTENCE-001 and COMMUNITY-PERSISTENCE-002 against a real disposable PostgreSQL
/// started from a pinned Docker image. Nothing here is simulated: transactions, constraints,
/// rollback, idempotence and connection failure are produced by a real PostgreSQL server.
/// </summary>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class CommunityPersistenceTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid InstallationId = new("0f9d2f4a-6d3b-4a4a-9c1e-1b2c3d4e5f60");
    private static readonly Guid BaselineInstallationId = new("1a0e3d5c-77e2-4c2f-8f0a-2b3c4d5e6f71");
    private static readonly Guid DoomedInstallationId = new("2b1f4e6d-88f3-4d30-901b-3c4d5e6f7082");

    private const string InstallationProvisioned = "installation.provisioned";
    private const string InstallationReprovisioned = "installation.reprovisioned";

    // ---------------------------------------------------------------------------------------
    // COMMUNITY-PERSISTENCE-001 Success: migration + commit + restart keeps the same ids.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Committed_installation_audit_and_outbox_survive_a_host_restart()
    {
        Assert.True(postgres.IsRealPostgres, "the persistence scenarios require a real PostgreSQL container");

        var database = await postgres.CreateDatabaseAsync("success");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        // First host: write the installation, its audit row and its outbox row in one transaction.
        await using (var first = ChronicleafApiHost.Start(database, Clock))
        {
            await ProvisionAsync(first.Services, InstallationId, InstallationProvisioned, revision: 1);
        }

        // The first host is gone. A second host is a different process reading the same database.
        await using var second = ChronicleafApiHost.Start(database, Clock);
        using var scope = second.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var installation = await provider.GetRequiredService<IInstallationStore>()
            .FindAsync(InstallationId);

        Assert.NotNull(installation);
        Assert.Equal(InstallationId, installation!.Id);
        Assert.Equal(ChronicleafDbContext.RequiredSchemaVersion, installation.SchemaVersion);
        Assert.Equal(Clock, installation.CreatedAt);

        var pending = await provider.GetRequiredService<IOutboxStore>().ListPendingAsync(25);
        var outboxEvent = Assert.Single(pending);
        Assert.Equal(InstallationId, outboxEvent.AggregateId);
        Assert.Equal(1, outboxEvent.Revision);
        Assert.Equal(InstallationProvisioned, outboxEvent.Kind);

        // The stored state itself, read over a raw connection, not through the same context.
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));

        // The audit row names the same target and the same revision.
        var audit = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT action || '|' || target_id::text FROM audit_events WHERE target_id = '{InstallationId}'");
        Assert.Equal($"{InstallationProvisioned}|{InstallationId}", audit);

        // The outbox payload is the versioned, camelCase envelope the design names.
        using var payload = JsonDocument.Parse(outboxEvent.PayloadJson);
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(InstallationProvisioned, payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(InstallationId, payload.RootElement.GetProperty("scopeId").GetGuid());
        Assert.Equal(InstallationId, payload.RootElement.GetProperty("aggregateId").GetGuid());
        Assert.Equal(1, payload.RootElement.GetProperty("revision").GetInt64());
        Assert.DoesNotContain("schema_version", outboxEvent.PayloadJson, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // COMMUNITY-PERSISTENCE-001 Failure: an exception before commit rolls everything back.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Exception_before_commit_leaves_no_partial_row_in_any_table()
    {
        var database = await postgres.CreateDatabaseAsync("rollback_fresh");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);

        var failure = await Assert.ThrowsAsync<InjectedFailureException>(() =>
            FailingProvisionAsync(host.Services, DoomedInstallationId, InstallationProvisioned));

        Assert.Equal("simulated failure before commit", failure.Message);

        // A real rollback: not one of the three tables kept a row.
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));

        using var scope = host.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IInstallationStore>()
            .FindAsync(DoomedInstallationId));
    }

    [Fact]
    public async Task Exception_before_commit_leaves_committed_state_untouched()
    {
        var database = await postgres.CreateDatabaseAsync("rollback_preserves");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);

        // A committed baseline, so "unchanged" means unchanged rather than merely empty.
        await ProvisionAsync(host.Services, BaselineInstallationId, InstallationProvisioned, revision: 1);
        var baseline = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT schema_version::text || '|' || created_at::text FROM installation WHERE id = '{BaselineInstallationId}'");

        await Assert.ThrowsAsync<InjectedFailureException>(() =>
            FailingProvisionAsync(host.Services, DoomedInstallationId, InstallationReprovisioned));

        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));

        Assert.Equal(
            baseline,
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT schema_version::text || '|' || created_at::text FROM installation WHERE id = '{BaselineInstallationId}'"));

        Assert.Equal(
            "0",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                $"SELECT count(*)::text FROM outbox WHERE aggregate_id = '{DoomedInstallationId}'"));
    }

    // ---------------------------------------------------------------------------------------
    // COMMUNITY-PERSISTENCE-001 Boundary: a second migration run preserves existing rows.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Running_the_migration_again_leaves_existing_rows_unchanged()
    {
        var database = await postgres.CreateDatabaseAsync("idempotent");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        await ProvisionAsync(host.Services, InstallationId, InstallationProvisioned, revision: 1);

        var before = await ReadInstallationAsync(database);

        // How many migrations this build has applied. The count is captured rather than written
        // down, because the access package has since added a second forward migration: the
        // property under test is that a second run adds no migration and changes no row, and that
        // holds for any number of applied migrations.
        var appliedMigrations = await ChronicleafPostgresFixture.CountRowsAsync(database, "__EFMigrationsHistory");
        Assert.True(
            appliedMigrations >= 1,
            "the first migration run must have applied at least this build's initial migration");

        // Applying the committed migrations a second time must be a no-op.
        await ChronicleafPostgresFixture.MigrateAsync(database);

        Assert.Equal(before, await ReadInstallationAsync(database));
        Assert.Equal(
            appliedMigrations,
            await ChronicleafPostgresFixture.CountRowsAsync(database, "__EFMigrationsHistory"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));

        // The row written before the second run is still readable through a fresh host.
        await using var restarted = ChronicleafApiHost.Start(database, Clock);
        using var scope = restarted.Services.CreateScope();
        var installation = await scope.ServiceProvider.GetRequiredService<IInstallationStore>()
            .FindAsync(InstallationId);
        Assert.Equal(InstallationId, installation!.Id);
    }

    // ---------------------------------------------------------------------------------------
    // COMMUNITY-PERSISTENCE-002 Success / Failure / Boundary.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Ready_endpoint_reports_ready_when_the_expected_schema_is_reachable()
    {
        var database = await postgres.CreateDatabaseAsync("ready_success");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"ready\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stopped_postgres_makes_ready_unavailable_while_health_stays_ok()
    {
        Assert.True(postgres.IsRealPostgres);

        // A dedicated server, so stopping it cannot disturb the shared fixture.
        await using var database = new PostgreSqlBuilder(ChronicleafPostgresFixture.Image)
            .WithDatabase("chronicleaf")
            .WithUsername("chronicleaf")
            .WithPassword("chronicleaf-test")
            .WithCleanUp(true)
            .Build();

        await database.StartAsync();
        var connectionString = database.GetConnectionString();
        await ChronicleafPostgresFixture.MigrateAsync(connectionString);

        await using var host = ChronicleafApiHost.Start(connectionString, Clock);
        using var client = host.CreateClient();

        using (var ready = await client.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        // The database is really stopped, not pointed somewhere else.
        await database.StopAsync();

        using var unavailable = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("database_unavailable", (await ReadCodeAsync(unavailable)));

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"service\":\"chronicleaf\"}", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_schema_makes_ready_a_mismatch_while_health_stays_ok()
    {
        // A reachable, empty database: PostgreSQL answers, but nothing has been migrated.
        var database = await postgres.CreateDatabaseAsync("schema_missing");

        await using var host = ChronicleafApiHost.Start(database, Clock);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("schema_mismatch", await ReadCodeAsync(response));

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\",\"service\":\"chronicleaf\"}", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Older_schema_makes_ready_a_mismatch_while_health_stays_ok()
    {
        // A database migrated by an earlier build: reachable, and its history stops at an older
        // migration id. It is a real older schema, not an empty one.
        var database = await postgres.CreateDatabaseAsync("schema_older");
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260101000000_LegacyCommunitySchema', '10.0.0');
            CREATE TABLE installation (
                id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL,
                CONSTRAINT "PK_installation" PRIMARY KEY (id));
            """);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("schema_mismatch", await ReadCodeAsync(response));

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ---------------------------------------------------------------------------------------
    // Outbox uniqueness and versioned consumption.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_outbox_revision_is_rejected_by_the_database()
    {
        var database = await postgres.CreateDatabaseAsync("outbox_unique");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        await ProvisionAsync(host.Services, InstallationId, InstallationProvisioned, revision: 1);

        var duplicate = new OutboxEvent(
            Guid.NewGuid(),
            InstallationProvisioned,
            InstallationId,
            1,
            new OutboxPayload
            {
                Kind = InstallationProvisioned,
                ScopeId = InstallationId,
                AggregateId = InstallationId,
                Revision = 1,
            }.ToJson(),
            Clock);

        await using (var context = ChronicleafPostgresFixture.CreateContext(database))
        {
            context.Outbox.Add(duplicate);

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var provider = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, provider.SqlState);
        }

        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));
    }

    [Fact]
    public async Task Unknown_payload_version_is_unsupported_and_never_executed()
    {
        var database = await postgres.CreateDatabaseAsync("outbox_version");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        // One event this build understands and one it does not, both with a registered consumer.
        var futureKind = InstallationReprovisioned;
        var futureId = Guid.Parse("3c2a5f7e-99a4-4e41-b12c-4d5e6f708192");
        var knownId = Guid.Parse("4d3b6080-aab5-4f52-c23d-5e6f70819203");
        var knownJson = Payload(InstallationProvisioned, InstallationId, 1).ToJson();
        var futureJson = new OutboxPayload
        {
            SchemaVersion = 2,
            Kind = futureKind,
            ScopeId = InstallationId,
            AggregateId = InstallationId,
            Revision = 2,
        }.ToJson();

        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            "INSERT INTO outbox (id, kind, aggregate_id, revision, payload, created_at) VALUES " +
            $"('{knownId}', '{InstallationProvisioned}', '{InstallationId}', 1, '{knownJson}', '2026-10-02T12:00:00+00:00'), " +
            $"('{futureId}', '{futureKind}', '{InstallationId}', 2, '{futureJson}', '2026-10-02T12:01:00+00:00');");

        var known = new RecordingOutboxHandler(InstallationProvisioned);
        var unknown = new RecordingOutboxHandler(futureKind);

        await using var context = ChronicleafPostgresFixture.CreateContext(database);
        var dispatcher = new OutboxDispatcher(new OutboxStore(context), [known, unknown]);

        var results = await dispatcher.DispatchPendingAsync(25);

        var handled = Assert.Single(results, result => result.Kind == InstallationProvisioned);
        Assert.Equal(OutboxDispatchOutcome.Handled, handled.Outcome);
        Assert.Equal(1, handled.SchemaVersion);

        var unsupported = Assert.Single(results, result => result.Kind == futureKind);
        Assert.Equal(OutboxDispatchOutcome.UnsupportedEvent, unsupported.Outcome);
        Assert.Equal(2, unsupported.SchemaVersion);

        // The understood event reached its consumer; the unknown version never did.
        Assert.Equal(1, known.Invocations);
        Assert.Equal(0, unknown.Invocations);
        Assert.Equal(InstallationId, known.LastPayload!.AggregateId);

        // The unexecuted event stays pending rather than being marked dispatched.
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT dispatched_at::text FROM outbox WHERE kind = '{futureKind}'"));
    }

    // ---------------------------------------------------------------------------------------
    // Cancel and redelivery invariants across the changed callers.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_before_commit_rolls_the_transaction_back()
    {
        var database = await postgres.CreateDatabaseAsync("cancelled");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using var host = ChronicleafApiHost.Start(database, Clock);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CancelledProvisionAsync(host.Services, DoomedInstallationId, cancellation));

        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "installation"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "audit_events"));
        Assert.Equal(0, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));
    }

    [Fact]
    public async Task Failed_consumer_leaves_the_event_pending_so_it_can_be_redelivered()
    {
        var database = await postgres.CreateDatabaseAsync("outbox_retry");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await ProvisionDirectlyAsync(database, InstallationId, InstallationProvisioned, revision: 1);

        await using var first = ChronicleafPostgresFixture.CreateContext(database);
        var failing = new FailingOutboxHandler(InstallationProvisioned);
        var firstPass = await new OutboxDispatcher(new OutboxStore(first), [failing]).DispatchPendingAsync(25);

        var failed = Assert.Single(firstPass);
        Assert.Equal(OutboxDispatchOutcome.Failed, failed.Outcome);
        Assert.Equal(1, failing.Invocations);

        // A failed consumer is never recorded as success and never consumes the event.
        Assert.Null(await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT dispatched_at::text FROM outbox WHERE kind = '{InstallationProvisioned}'"));

        await using var second = ChronicleafPostgresFixture.CreateContext(database);
        var succeeding = new RecordingOutboxHandler(InstallationProvisioned);
        var secondPass = await new OutboxDispatcher(new OutboxStore(second), [succeeding]).DispatchPendingAsync(25);

        var handled = Assert.Single(secondPass);
        Assert.Equal(OutboxDispatchOutcome.Handled, handled.Outcome);
        Assert.Equal(1, succeeding.Invocations);
        Assert.Equal(1, await ChronicleafPostgresFixture.CountRowsAsync(database, "outbox"));
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    /// <summary>Commits an installation with its audit and outbox rows, without an HTTP host.</summary>
    private static async Task ProvisionDirectlyAsync(
        string database,
        Guid installationId,
        string kind,
        long revision)
    {
        using var context = ChronicleafPostgresFixture.CreateContext(database);
        var unitOfWork = new EfUnitOfWork(context);
        var store = new InstallationStore(context, new FixedTimeProvider(Clock));

        await unitOfWork.ExecuteAsync(
            token => store.ProvisionAsync(
                installationId,
                ChronicleafDbContext.RequiredSchemaVersion,
                InstallationStore.ProvisionedAction,
                actorId: null,
                Payload(kind, installationId, revision),
                token));
    }

    private static async Task ProvisionAsync(
        IServiceProvider services,
        Guid installationId,
        string kind,
        long revision)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
        var store = provider.GetRequiredService<IInstallationStore>();

        await unitOfWork.ExecuteAsync(
            token => store.ProvisionAsync(
                installationId,
                ChronicleafDbContext.RequiredSchemaVersion,
                InstallationStore.ProvisionedAction,
                actorId: null,
                Payload(kind, installationId, revision),
                token));
    }

    /// <summary>
    /// Stages an installation, audit row and outbox row and then fails before the commit, which
    /// is the failure the unit of work has to roll back.
    /// </summary>
    private static async Task FailingProvisionAsync(IServiceProvider services, Guid installationId, string kind)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
        var store = provider.GetRequiredService<IInstallationStore>();

        await unitOfWork.ExecuteAsync<Installation>(
            async token =>
            {
                await store.ProvisionAsync(
                    installationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    Payload(kind, installationId, revision: 1),
                    token);

                throw new InjectedFailureException("simulated failure before commit");
            });
    }

    /// <summary>Stages the same three writes and is cancelled before the commit point.</summary>
    private static async Task CancelledProvisionAsync(
        IServiceProvider services,
        Guid installationId,
        CancellationTokenSource cancellation)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
        var store = provider.GetRequiredService<IInstallationStore>();

        await unitOfWork.ExecuteAsync<Installation>(
            async token =>
            {
                var installation = await store.ProvisionAsync(
                    installationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    Payload(InstallationProvisioned, installationId, revision: 1),
                    token);

                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return installation;
            },
            cancellation.Token);
    }

    private static OutboxPayload Payload(string kind, Guid installationId, long revision) =>
        new()
        {
            Kind = kind,
            ScopeId = installationId,
            AggregateId = installationId,
            Revision = revision,
        };

    private static async Task<string> ReadInstallationAsync(string database) =>
        (await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            $"SELECT id::text || '|' || schema_version::text || '|' || created_at::text FROM installation"))
        ?? string.Empty;

    /// <summary>
    /// Reads a failure body and pins the envelope: exactly code, message and traceId, with the
    /// optional fieldErrors omitted rather than serialized as null.
    /// </summary>
    private static async Task<string> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["code", "message", "traceId"], fields);
        Assert.False(document.RootElement.GetProperty("message").GetString()!.Contains(
            "chronicleaf-fixture",
            StringComparison.Ordinal));

        return document.RootElement.GetProperty("code").GetString()!;
    }
}

/// <summary>A failure the test injects on purpose, before the transaction commits.</summary>
public sealed class InjectedFailureException(string message) : Exception(message);

/// <summary>A consumer that always fails, to prove a failed dispatch is never recorded as success.</summary>
public sealed class FailingOutboxHandler(string kind) : IOutboxEventHandler
{
    public string Kind { get; } = kind;

    public int Invocations { get; private set; }

    public Task HandleAsync(OutboxPayload payload, CancellationToken cancellationToken = default)
    {
        Invocations++;
        throw new InjectedFailureException("the consumer could not reach its own dependency");
    }
}

/// <summary>A consumer that records what it was asked to execute.</summary>
public sealed class RecordingOutboxHandler(string kind) : IOutboxEventHandler
{
    public string Kind { get; } = kind;

    public int Invocations { get; private set; }

    public OutboxPayload? LastPayload { get; private set; }

    public Task HandleAsync(OutboxPayload payload, CancellationToken cancellationToken = default)
    {
        Invocations++;
        LastPayload = payload;
        return Task.CompletedTask;
    }
}
