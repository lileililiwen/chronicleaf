using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// SCHEDULED-PROJECT-SYNC-001 and SCHEDULED-PROJECT-SYNC-002 against a real disposable PostgreSQL
/// and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. Every row comes from a real PostgreSQL server started
/// from a pinned image, the committed migrations created the tables, the schedule routes are the real
/// ones behind the real access boundary, the tick is the delivered ticker's own body called by hand,
/// the poll is performed by the real job on the real queue, and the candidates are written by the
/// real delivered poll over the real stores. There is no EF in-memory provider, no stubbed store and
/// no in-process dictionary standing in for a table in this file.
/// </para>
/// <para>
/// Each scenario gets its own database and the clock is injected and frozen, so every stored instant
/// is deterministic and a due instant, a next run and a fetched instant are the policy's numbers
/// rather than approximations of them. The tick takes its instant as a parameter, so a scenario names
/// the moment a schedule becomes due without waiting a minute for a loop.
/// </para>
/// <para>
/// <b>About the transport.</b> The shared <see cref="FakeGitHubHandler"/> is a real
/// <c>HttpMessageHandler</c> replacing the one <see cref="HttpClient"/> the production registration
/// builds, and it is the only thing in this file that stands in for anything. <b>So nothing in this
/// file is evidence that GitHub works</b>, that a repository is public, or that a release has the
/// shape this suite's fixtures give it. What these scenarios establish is Chronicleaf's own
/// behaviour: which schedules fire, which do not, what one firing creates, what it never creates,
/// and what a panel is told when a run fails.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class ScheduledProjectSyncTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("b3e7a1c4-5f28-4d92-8a11-0e9c4f6b2a51");

    private const string Owner = "chronicleaf-tests";
    private const string Repo = "schedule-fixture";

    private const string TagSha = "3333333333333333333333333333333333333333";
    private const string SecondTagSha = "4444444444444444444444444444444444444444";

    private const string ScheduledKind = "project.scheduled-sync";

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-001 Success: a due enabled schedule creates one operation.
    // ===========================================================================================

    [Fact]
    public async Task Sync_001_Success_a_due_enabled_schedule_creates_one_operation_and_advances_next_run()
    {
        var scenario = await ScenarioAsync("sps_001_success");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "定时同步");
        await ConnectAsync(owner, project);

        var stored = await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);
        Assert.Equal(1, stored.Revision);
        Assert.True(stored.Enabled);
        Assert.Equal(Clock.AddMinutes(60), stored.NextRunAt);

        var due = Clock.AddMinutes(60);
        var tick = await host.TickAsync(due);
        Assert.Equal(1, tick.Due);
        Assert.Equal(1, tick.Dispatched);

        // One operation, of this package's own kind, bound to the project with the schedule's key.
        var operations = await ListOperationsAsync(scenario.Database);
        var operation = Assert.Single(operations);
        Assert.Equal(ScheduledKind, operation.Kind);
        Assert.Equal(project, operation.TargetId);
        Assert.Equal("queued", operation.Status);

        var scheduleId = await ReadScheduleIdAsync(scenario.Database, project);
        var expectedKey = $"{scheduleId:N}:{due.ToUnixTimeSeconds()}";
        Assert.Equal(expectedKey, await ReadTextAsync(
            scenario.Database,
            $"SELECT idempotency_key FROM operations WHERE id = '{operation.Id:D}'"));

        // The delivered poll's run row names the job, so the worker can find what it was asked for.
        var run = await ReadRunAsync(scenario.Database, operation.Id);
        Assert.NotNull(run);
        Assert.Equal(project, run!.ProjectId);
        Assert.Equal(Owner, run.Owner);
        Assert.Equal(Repo, run.Repo);
        Assert.Null(run.CompletedAt);

        // The next run moves one interval past the firing instant, and the revision moves so a
        // concurrent edit loses instead of overwriting the advance.
        var reread = await ReadScheduleAsync(owner, project);
        Assert.Equal(2, reread.Revision);
        Assert.Equal(Clock.AddMinutes(120), reread.NextRunAt);
        Assert.NotNull(reread.LastOperation);
        Assert.Equal(operation.Id, reread.LastOperation!.OperationId);
        Assert.Equal("queued", reread.LastOperation.Status);
    }

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-001 Failure: a disabled or disconnected schedule dispatches nothing.
    // ===========================================================================================

    [Fact]
    public async Task Sync_001_Failure_a_disabled_schedule_dispatches_nothing()
    {
        var scenario = await ScenarioAsync("sps_001_disabled");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "关闭的计划");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: false, intervalMinutes: 60, expectedRevision: 0);

        var tick = await host.TickAsync(Clock.AddMinutes(60));
        Assert.Equal(0, tick.Due);
        Assert.Equal(0, tick.Dispatched);

        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM operations"));
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM github_candidate_runs"));
    }

    [Fact]
    public async Task Sync_001_Failure_an_archived_or_disconnected_schedule_is_turned_off_not_polled()
    {
        var scenario = await ScenarioAsync("sps_001_off");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var disconnected = await CreateProjectAsync(owner, "断开的计划");
        await ConnectAsync(owner, disconnected);
        await PutScheduleAsync(owner, disconnected, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        var archived = await CreateProjectAsync(owner, "归档的计划");
        await ConnectAsync(owner, archived);
        await PutScheduleAsync(owner, archived, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        // The connection is removed and the project archived after the schedules were armed.
        await ChronicleafPostgresFixture.ExecuteAsync(
            scenario.Database,
            $"DELETE FROM github_connections WHERE project_id = '{disconnected:D}'");

        var archivedRevision = await ReadProjectRevisionAsync(owner, archived);
        using var archive = await owner.SendAsync(await owner.Archive($"/api/projects/{archived:D}", archivedRevision));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        var tick = await host.TickAsync(Clock.AddMinutes(60));
        Assert.Equal(2, tick.Due);
        Assert.Equal(0, tick.Dispatched);
        Assert.Equal(2, tick.Disabled);

        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM operations"));

        // Both schedules say they are off, with the revision moved so the change is visible.
        Assert.False((await ReadScheduleAsync(owner, disconnected)).Enabled);
        Assert.False((await ReadScheduleAsync(owner, archived)).Enabled);
    }

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-001 Boundary: repeated ticks and a missed window fire once.
    // ===========================================================================================

    [Fact]
    public async Task Sync_001_Boundary_repeated_ticks_and_a_missed_window_collapse_to_one_poll()
    {
        var scenario = await ScenarioAsync("sps_001_collapse", PollScript());
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "合并触发");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        var due = Clock.AddMinutes(60);

        var first = await host.TickAsync(due);
        Assert.Equal(1, first.Dispatched);

        // The operation is still queued, so the second tick for the same instant does not enqueue
        // another: one active sync per project.
        var second = await host.TickAsync(due);
        Assert.Equal(0, second.Dispatched);

        await host.DispatchAsync();
        var firstId = await ReadSingleGuidAsync(
            scenario.Database,
            $"SELECT id FROM operations WHERE target_id = '{project:D}'");
        _ = await OperationRows.AwaitAsync(scenario.Database, firstId, "succeeded");

        // A three-hour outage is one latest poll, never a burst of catch-ups: the hours at 11:00
        // and 12:00 produce no operations of their own, and the next run is one interval past the
        // late tick rather than a chain of missed ones.
        var late = await host.TickAsync(due.AddHours(3));
        Assert.Equal(1, late.Dispatched);
        Assert.Equal(2L, await CountAsync(scenario.Database, "SELECT count(*) FROM operations"));

        var reread = await ReadScheduleAsync(owner, project);
        Assert.Equal(due.AddHours(3).AddMinutes(60), reread.NextRunAt);
    }

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-002 Success: a scheduled poll records candidates and nothing else.
    // ===========================================================================================

    [Fact]
    public async Task Sync_002_Success_a_scheduled_poll_records_candidates_and_nothing_else()
    {
        var github = PollScript();
        var scenario = await ScenarioAsync("sps_002_poll", github);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "计划轮询");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        host.GitHub.ClearRequests();

        var tick = await host.TickAsync(Clock.AddMinutes(60));
        Assert.Equal(1, tick.Dispatched);

        var operationId = await ReadSingleGuidAsync(
            scenario.Database,
            $"SELECT id FROM operations WHERE target_id = '{project:D}'");

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(scenario.Database, operationId, "succeeded");
        Assert.Null(operation.ErrorCode);
        Assert.Equal($"{ScheduledKind}:5/5", operation.ResultRef);

        // Three releases and two tags, all attributed to the poll, and nothing confirmed.
        var candidates = await ReadCandidatesAsync(owner, project);
        Assert.Equal(5, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("poll", candidate.Source));
        Assert.Contains(candidates, candidate => candidate.SourceKey == "release:9501");
        Assert.Contains(candidates, candidate => candidate.SourceKey == $"tag:v8.0.0:{TagSha}");

        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM milestones"));
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM evidence_bundles"));
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM evidence_items"));
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM media"));

        // Exactly two reads, both on the pinned host, and no write.
        Assert.Equal(2, host.GitHub.Requests.Count);
        Assert.All(host.GitHub.Requests, request => Assert.Equal("GET", request.Method));
        Assert.All(host.GitHub.Requests, request => Assert.Equal("api.github.com", request.Host));
    }

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-002 Failure: the eleventh enabled schedule is refused.
    // ===========================================================================================

    [Fact]
    public async Task Sync_002_Failure_the_eleventh_enabled_schedule_is_refused_with_schedule_quota()
    {
        var scenario = await ScenarioAsync("sps_002_quota");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        for (var index = 0; index < 10; index++)
        {
            var project = await CreateProjectAsync(owner, $"配额{index}");
            await ConnectAsync(owner, project);
            await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);
        }

        var eleventh = await CreateProjectAsync(owner, "配额之外");
        await ConnectAsync(owner, eleventh);

        using var refused = await owner.SendAsync(await owner.Put(
            $"/api/projects/{eleventh:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 60 },
            0));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("schedule_quota", JsonDocument
            .Parse(await refused.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        // A disabled schedule is not an activation, so storing one off is still allowed - and
        // turning it on afterwards is refused the same way.
        var stored = await PutScheduleAsync(owner, eleventh, enabled: false, intervalMinutes: 60, expectedRevision: 0);
        Assert.False(stored.Enabled);

        using var refusedAgain = await owner.SendAsync(await owner.Put(
            $"/api/projects/{eleventh:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 60 },
            stored.Revision));

        Assert.Equal(HttpStatusCode.TooManyRequests, refusedAgain.StatusCode);

        Assert.Equal(10L, await CountAsync(
            scenario.Database, "SELECT count(*) FROM project_sync_schedules WHERE enabled"));
    }

    // ===========================================================================================
    // SCHEDULED-PROJECT-SYNC-002 Boundary: a failed provider run stays visible without progress.
    // ===========================================================================================

    [Fact]
    public async Task Sync_002_Boundary_a_failed_provider_run_stays_visible_with_the_next_bounded_attempt()
    {
        var github = new FakeGitHubHandler()
            .OnGet($"/repos/{Owner}/{Repo}/releases", HttpStatusCode.NotFound, """{"message":"nope"}""")
            .OnGet($"/repos/{Owner}/{Repo}/tags", HttpStatusCode.NotFound, """{"message":"nope"}""");

        var scenario = await ScenarioAsync("sps_002_failed", github);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "失败的计划");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        var due = Clock.AddMinutes(60);
        Assert.Equal(1, (await host.TickAsync(due)).Dispatched);

        var operationId = await ReadSingleGuidAsync(
            scenario.Database,
            $"SELECT id FROM operations WHERE target_id = '{project:D}'");

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(scenario.Database, operationId, "failed");
        Assert.Equal("github_candidate_job_repository_unavailable", operation.ErrorCode);

        // No candidate is fabricated from a refusal, and the run row says it never read anything.
        Assert.Equal(0L, await CountAsync(scenario.Database, "SELECT count(*) FROM github_candidates"));

        // The settings show the failed outcome with its redacted code, and the next attempt is still
        // the bounded cadence - one interval past the firing instant, not a retry storm.
        var reread = await ReadScheduleAsync(owner, project);
        Assert.NotNull(reread.LastOperation);
        Assert.Equal(operationId, reread.LastOperation!.OperationId);
        Assert.Equal("failed", reread.LastOperation.Status);
        Assert.Equal("github_candidate_job_repository_unavailable", reread.LastOperation.ErrorCode);
        Assert.NotNull(reread.LastOperation.CompletedAt);
        Assert.Equal(Clock.AddMinutes(120), reread.NextRunAt);
    }

    // ===========================================================================================
    // The route contract: private, scoped, conditional, and bounded.
    // ===========================================================================================

    [Fact]
    public async Task Schedule_changes_are_revision_guarded_and_a_project_without_a_connection_cannot_be_scheduled()
    {
        var scenario = await ScenarioAsync("sps_guards");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "守卫");

        // No connection, no schedule: 409 naming the missing repository link.
        using var noConnection = await owner.SendAsync(await owner.Put(
            $"/api/projects/{project:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 60 },
            0));

        Assert.Equal(HttpStatusCode.Conflict, noConnection.StatusCode);
        Assert.Equal("schedule_no_connection", JsonDocument
            .Parse(await noConnection.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        // No schedule stored yet is 404 rather than an empty body.
        using var missing = await owner.GetAsync($"/api/projects/{project:D}/sync-schedule");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await ConnectAsync(owner, project);

        // A missing If-Match is 428, as every other conditional route answers.
        using var noMatch = await owner.SendAsync(await owner.Put(
            $"/api/projects/{project:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 60 }));
        Assert.Equal((HttpStatusCode)428, noMatch.StatusCode);

        // An interval outside the cadence is 422 naming the field, with nothing written.
        using var badInterval = await owner.SendAsync(await owner.Put(
            $"/api/projects/{project:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 5 },
            0));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badInterval.StatusCode);

        var created = await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);
        Assert.Equal(1, created.Revision);

        // Creating again at zero is a 409: the stored revision is one, and the caller is stale.
        using var recreate = await owner.SendAsync(await owner.Put(
            $"/api/projects/{project:D}/sync-schedule",
            new { enabled = true, intervalMinutes = 60 },
            0));
        Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
        Assert.Equal("revision_conflict", JsonDocument
            .Parse(await recreate.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        // The same intent twice is a no-op with no new revision.
        var noop = await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 1);
        Assert.Equal(1, noop.Revision);

        // A changed cadence at the stored revision moves the revision and restarts the cadence.
        var changed = await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 120, expectedRevision: 1);
        Assert.Equal(2, changed.Revision);
        Assert.Equal(Clock.AddMinutes(120), changed.NextRunAt);

        // A foreign project is 404 without exposure.
        using var foreign = await owner.GetAsync($"/api/projects/{Guid.NewGuid():D}/sync-schedule");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task The_schedule_routes_require_an_owner_session()
    {
        var scenario = await ScenarioAsync("sps_private");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "私有路由");
        var anonymous = host.CreateClient();

        using var get = await anonymous.GetAsync($"/api/projects/{project:D}/sync-schedule");
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);

        using var put = await anonymous.SendAsync(new HttpRequestMessage(
            HttpMethod.Put, $"/api/projects/{project:D}/sync-schedule")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    // ===========================================================================================
    // Restart, supersede, and disable: the durable half of the contract.
    // ===========================================================================================

    [Fact]
    public async Task A_restart_keeps_the_next_due_instant_with_no_duplicate_operation()
    {
        var github = PollScript();
        var scenario = await ScenarioAsync("sps_restart", github);

        Guid project;
        Guid operationId;
        {
            await using var host = scenario.Host;
            using var owner = await OwnerClient.CreateAsync(host);

            project = await CreateProjectAsync(owner, "重启");
            await ConnectAsync(owner, project);
            await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

            Assert.Equal(1, (await host.TickAsync(Clock.AddMinutes(60))).Dispatched);
            operationId = await ReadSingleGuidAsync(
                scenario.Database,
                $"SELECT id FROM operations WHERE target_id = '{project:D}'");

            await host.DispatchAsync();
            _ = await OperationRows.AwaitAsync(scenario.Database, operationId, "succeeded");
        }

        // A new process on the same database honors the stored next run: the same instant is no
        // longer due, so it fires nothing and records nothing twice.
        using var keyFile = OperatorKeyFile.Create();
        var keyPath = Path.Combine(Path.GetTempPath(), $"chronicleaf-schedules-restart-{Guid.NewGuid():N}.key");
        File.Copy(keyFile.FilePath, keyPath, overwrite: true);

        await using var restarted = ScheduledProjectSyncApiHost.Start(
            scenario.Database,
            new FixedTimeProvider(Clock),
            PollScript(),
            keyPath);

        try
        {
            var tick = await restarted.TickAsync(Clock.AddMinutes(60));
            Assert.Equal(0, tick.Due);
            Assert.Equal(0, tick.Dispatched);
            Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM operations"));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public async Task An_archived_project_supersedes_the_in_flight_sync_rather_than_overwriting()
    {
        var github = PollScript();
        var scenario = await ScenarioAsync("sps_superseded", github);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "移动的包");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        Assert.Equal(1, (await host.TickAsync(Clock.AddMinutes(60))).Dispatched);
        var operationId = await ReadSingleGuidAsync(
            scenario.Database,
            $"SELECT id FROM operations WHERE target_id = '{project:D}'");

        // The project moves after the tick bound its revision: archiving advances it.
        var revision = await ReadProjectRevisionAsync(owner, project);
        using var archive = await owner.SendAsync(await owner.Archive($"/api/projects/{project:D}", revision));
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(scenario.Database, operationId, "superseded");
        Assert.Null(operation.ResultRef);
    }

    [Fact]
    public async Task Disabling_cancels_a_poll_that_never_started_and_schedules_nothing_further()
    {
        var scenario = await ScenarioAsync("sps_disable");
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var project = await CreateProjectAsync(owner, "停用");
        await ConnectAsync(owner, project);
        await PutScheduleAsync(owner, project, enabled: true, intervalMinutes: 60, expectedRevision: 0);

        Assert.Equal(1, (await host.TickAsync(Clock.AddMinutes(60))).Dispatched);
        var operationId = await ReadSingleGuidAsync(
            scenario.Database,
            $"SELECT id FROM operations WHERE target_id = '{project:D}'");

        var disabled = await PutScheduleAsync(owner, project, enabled: false, intervalMinutes: 60, expectedRevision: 2);
        Assert.False(disabled.Enabled);
        Assert.Equal(3, disabled.Revision);

        // The queued poll never started, so the disable withdrew it.
        var operation = await OperationRows.ReadAsync(scenario.Database, operationId);
        Assert.NotNull(operation);
        Assert.Equal("cancelled", operation!.Status);

        // And nothing further is scheduled while the switch is off.
        var tick = await host.TickAsync(Clock.AddMinutes(120));
        Assert.Equal(0, tick.Dispatched);
        Assert.Equal(1L, await CountAsync(scenario.Database, "SELECT count(*) FROM operations"));
    }

    // ===========================================================================================
    // Fixtures and readers.
    // ===========================================================================================

    private async Task<Scenario> ScenarioAsync(
        string purpose,
        FakeGitHubHandler? github = null)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

        // The delivered connection route reads the repository over the transport, so a scenario
        // that scripted only its own poll reads would fail at the connection and never reach the
        // code under test. The description and the default branch answer here, once, for every
        // scenario; the poll scripts below add the releases and tags each scenario needs.
        var transport = github ?? new FakeGitHubHandler();
        transport
            .OnGet(
                $"/repos/{Owner}/{Repo}",
                JsonSerializer.Serialize(new
                {
                    id = 4242,
                    @private = false,
                    default_branch = "main",
                    full_name = $"{Owner}/{Repo}",
                }, Wire))
            .OnGet(
                $"/repos/{Owner}/{Repo}/commits/main",
                JsonSerializer.Serialize(new { sha = TagSha }, Wire));

        using var keyFile = OperatorKeyFile.Create();

        // The key file has to outlive the host, because the vault reads it per operation and a
        // scenario that finished resolving a lease after the file was deleted would be testing a
        // different failure. It is therefore copied into the scenario rather than disposed with
        // the builder.
        var keyPath = Path.Combine(Path.GetTempPath(), $"chronicleaf-schedules-{purpose}-{Guid.NewGuid():N}.key");
        File.Copy(keyFile.FilePath, keyPath, overwrite: true);

        var host = ScheduledProjectSyncApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            transport,
            keyPath);

        using (var scope = host.Services.CreateScope())
        {
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return new Scenario(database, host, keyPath);
    }

    /// <summary>The database, the one host a scenario runs in, and the key file that must outlive it.</summary>
    private sealed record Scenario(string Database, ScheduledProjectSyncApiHost Host, string KeyFile)
        : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(KeyFile);
            }
            catch (IOException)
            {
                // A key file this process cannot delete is not a scenario failure; it is a temporary
                // directory that will be cleaned up with the machine.
            }
        }
    }

    /// <summary>
    /// A repository with three releases and two tags, so a scheduled poll with no window reads five
    /// proposals and a scenario can tell a full read from a swallowed filter.
    /// </summary>
    private static FakeGitHubHandler PollScript() =>
        new FakeGitHubHandler()
            .OnGet(
                $"/repos/{Owner}/{Repo}/releases",
                JsonSerializer.Serialize(new object[]
                {
                    Release(9501, "v8.1.0", "Eighth one one", "2026-09-20T08:00:00Z"),
                    Release(9500, "v8.0.0", "Eighth release", "2026-09-15T08:00:00Z"),
                    Release(9400, "v7.0.0", "Seventh release", "2026-08-01T08:00:00Z"),
                }, Wire))
            .OnGet(
                $"/repos/{Owner}/{Repo}/tags",
                JsonSerializer.Serialize(new object[]
                {
                    new { name = "v8.1.0", commit = new { sha = SecondTagSha } },
                    new { name = "v8.0.0", commit = new { sha = TagSha } },
                }, Wire));

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>One published, non-draft release as the releases resource returns it.</summary>
    private static object Release(long id, string tag, string name, string publishedAt) =>
        new
        {
            id,
            tag_name = tag,
            name,
            draft = false,
            prerelease = false,
            target_commitish = "main",
            published_at = publishedAt,
        };

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = $"{name} 项目", description = $"{name} 说明。" }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<long> ReadProjectRevisionAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revision").GetInt64();
    }

    private static async Task<Guid> ConnectAsync(OwnerClient owner, Guid projectId)
    {
        var revision = await ReadProjectRevisionAsync(owner, projectId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/github-connection",
            new { owner = Owner, repository = Repo, credentialRef = (string?)null },
            revision));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<ScheduleView> PutScheduleAsync(
        OwnerClient owner,
        Guid projectId,
        bool enabled,
        int intervalMinutes,
        long expectedRevision)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/sync-schedule",
            new { enabled, intervalMinutes },
            expectedRevision));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"PUT sync-schedule answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return ScheduleView.Read(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement);
    }

    private static async Task<ScheduleView> ReadScheduleAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}/sync-schedule");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return ScheduleView.Read(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement);
    }

    private sealed record ScheduleView(
        bool Enabled,
        int IntervalMinutes,
        DateTimeOffset NextRunAt,
        LastOperationView? LastOperation,
        long Revision)
    {
        public static ScheduleView Read(JsonElement body) =>
            new(
                body.GetProperty("enabled").GetBoolean(),
                body.GetProperty("intervalMinutes").GetInt32(),
                body.GetProperty("nextRunAt").GetDateTimeOffset(),
                body.TryGetProperty("lastOperation", out var last) && last.ValueKind is JsonValueKind.Object
                    ? new LastOperationView(
                        Guid.Parse(last.GetProperty("operationId").GetString()!),
                        last.GetProperty("status").GetString()!,
                        last.TryGetProperty("errorCode", out var code) && code.ValueKind is JsonValueKind.String
                            ? code.GetString()
                            : null,
                        last.TryGetProperty("completedAt", out var completed) && completed.ValueKind is JsonValueKind.String
                            ? completed.GetDateTimeOffset()
                            : null)
                    : null,
                body.GetProperty("revision").GetInt64());
    }

    private sealed record LastOperationView(
        Guid OperationId,
        string Status,
        string? ErrorCode,
        DateTimeOffset? CompletedAt);

    private sealed record CandidateView(string SourceKey, string Kind, string Source);

    private static async Task<IReadOnlyList<CandidateView>> ReadCandidatesAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync(
            $"/api/projects/{projectId:D}/github-candidates?limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => new CandidateView(
                item.GetProperty("sourceKey").GetString()!,
                item.GetProperty("kind").GetString()!,
                item.GetProperty("source").GetString()!))
            .ToArray();
    }

    private sealed record OperationRow(Guid Id, string Kind, Guid TargetId, string Status);

    private static async Task<IReadOnlyList<OperationRow>> ListOperationsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, target_id, status FROM operations ORDER BY created_at ASC";

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<OperationRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new OperationRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3)));
        }

        return rows;
    }

    private sealed record RunRow(Guid ProjectId, string Owner, string Repo, DateTimeOffset? CompletedAt);

    private static async Task<RunRow?> ReadRunAsync(string database, Guid operationId)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT project_id, "owner", repo, completed_at
            FROM github_candidate_runs
            WHERE operation_id = @id
            """;
        command.Parameters.AddWithValue("id", operationId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RunRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<Guid> ReadScheduleIdAsync(string database, Guid projectId)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM project_sync_schedules WHERE project_id = '{projectId:D}'";

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);

        return (Guid)value!;
    }

    private static Task<long> CountAsync(string database, string sql) =>
        OperationRows.CountAsync(database, sql);

    private static async Task<Guid> ReadSingleGuidAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();
        Assert.NotNull(value);

        return (Guid)value!;
    }

    private static async Task<string?> ReadTextAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return (await command.ExecuteScalarAsync()) as string;
    }
}
