using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.App;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// GITHUB-APP-CONNECTION-001 and GITHUB-APP-CONNECTION-002 against a real disposable PostgreSQL
/// and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. Every row comes from a real PostgreSQL server
/// started from a pinned image, the setup, connect and revocation go through the real
/// <see cref="IGitHubAppService"/> over the real stores, the token moves through the real
/// <see cref="IGitHubInstallationTokenProvider"/> with its process-wide cache, the collection
/// job runs on the real Hangfire server from the real queue, and the append lands through the
/// real evidence path with the database's own triggers and constraints deciding whether it is
/// honest. There is no EF in-memory provider and no stubbed store in this file.
/// </para>
/// <para>
/// <b>About the transport.</b> <see cref="FakeGitHubHandler"/> is a real
/// <c>HttpMessageHandler</c> and is the only thing in this repository that stands in for
/// anything. It fakes GitHub and nothing else: the API host, the queue, the workers, the vault,
/// the stores, the JWT signing, the HMAC verification, the evidence append and the routes are
/// all the production ones. <b>So nothing in this file is evidence that GitHub works</b>, that
/// an installation is real, or that a token mint is accepted. What these scenarios establish is
/// Chronicleaf's own behaviour: which bindings are verified before which rows, which tokens are
/// never stored, how each refusal becomes a status, and that a removed installation ends reads
/// without publishing anything.
/// </para>
/// <para>
/// Each scenario gets its own database. The App options travel as the holder the host reads, and
/// the clock is injected and advanced by hand, so every expiry is deterministic.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class GithubAppConnectionTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("3f6a9c41-2b7e-4d15-9a03-8c4e5f6a7b81");

    /// <summary>A second scope, used only to prove another tenant's installation cannot be attached.</summary>
    private static readonly Guid ForeignScopeId = new("4a7b0d52-3c8f-4e26-0b14-9d5f607b8c92");

    /// <summary>The server App's identity, as the operator configures it.</summary>
    private const long AppId = 12345;

    /// <summary>GitHub's installation id, as the callback reports it.</summary>
    private const long InstallationNumber = 9001;

    /// <summary>GitHub's repository id, as the owner selects it.</summary>
    private const long RepositoryNumber = 7001;

    private const string AccountLogin = "octo";
    private const string RepositoryOwner = "octo";
    private const string RepositoryName = "fixture";

    /// <summary>A PAT sentinel sealed by the real vault for the Community-path scenario. Never leaves this file.</summary>
    private const string StoredPat = "ghp-chronicleaf-app-boundary-sentinel-9c2f4a6b1d8e";

    // ===========================================================================================
    // GITHUB-APP-CONNECTION-001 Success: a valid callback for an allowed repository creates the
    // scoped installation connection.
    // ===========================================================================================

    [Fact]
    public async Task App_001_Success_a_valid_callback_for_an_allowed_repository_creates_the_scoped_connection()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = AppScript();
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_001_success", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        await ConfigureAppAsync(host, options, rsa);

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 连接成功");
        var setup = await BeginSetupAsync(owner, fixture.ProjectId);

        var connected = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);

        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        Assert.Equal(InstallationNumber, connected.Body.GetProperty("installationId").GetInt64());
        Assert.Equal(RepositoryNumber, connected.Body.GetProperty("repositoryId").GetInt64());
        Assert.Equal(AccountLogin, connected.Body.GetProperty("accountLogin").GetString());
        Assert.Equal(RepositoryOwner, connected.Body.GetProperty("owner").GetString());
        Assert.Equal(RepositoryName, connected.Body.GetProperty("repository").GetString());
        Assert.DoesNotContain("token", connected.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", connected.Raw, StringComparison.OrdinalIgnoreCase);

        // The binding is a live row for this scope, and the connection names the installation
        // rather than any credential.
        var installation = await ReadInstallationAsync(database, InstallationId, InstallationNumber);
        Assert.NotNull(installation);
        Assert.Equal(AccountLogin, installation!.AccountLogin);
        Assert.Equal("selected", installation.RepositorySelection);
        Assert.Equal("active", installation.Status);

        var connection = await ReadConnectionAsync(database, InstallationId, fixture.ProjectId);
        Assert.NotNull(connection);
        Assert.Equal("installation", connection!.AuthMode);
        Assert.Equal(InstallationNumber, connection.InstallationId);
        Assert.Equal(RepositoryNumber, connection.RepositoryId);
        Assert.Null(connection.CredentialRef);

        // The mint narrowed to exactly the selected repository and named no permission: there is
        // no write scope here to grant, and none is asked for.
        var mint = Assert.Single(github.Requests, request => request.Method == "POST");
        Assert.EndsWith($"/app/installations/{InstallationNumber}/access_tokens", mint.Path, StringComparison.Ordinal);
        Assert.Contains($"\"repository_ids\":[{RepositoryNumber}]", mint.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions", mint.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write", mint.Body, StringComparison.OrdinalIgnoreCase);

        // No minted token reaches any row.
        Assert.Equal(0, await CountSubstringAsync(database, "github_connections", "owner", "ghs-test-token"));
        Assert.Equal(0, await CountSubstringAsync(database, "github_app_installations", "account_login", "ghs-test-token"));

        // The binding reads back over the wire, and detaching it revokes the local connection
        // while the installation row stays for the App's own lifetime.
        using var read = await owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/github-app");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var detached = await owner.SendAsync(await owner.DeleteAsync($"/api/projects/{fixture.ProjectId:D}/github-app"));
        Assert.Equal(HttpStatusCode.NoContent, detached.StatusCode);

        Assert.Null(await ReadConnectionAsync(database, InstallationId, fixture.ProjectId));
        Assert.NotNull(await ReadInstallationAsync(database, InstallationId, InstallationNumber));

        using var gone = await owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/github-app");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task App_001_Failure_another_tenants_installation_id_is_refused()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = AppScript();
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_001_failure_mismatch", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        await ConfigureAppAsync(host, options, rsa);

        // The installation is bound to another scope first: the first binding wins.
        using (var scope = host.Services.CreateScope())
        {
            var stores = scope.ServiceProvider.GetRequiredService<IGitHubAppInstallationStore>();
            var units = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await units.ExecuteAsync<object?>(
                async token =>
                {
                    await stores.UpsertAsync(
                        new GitHubAppInstallation(
                            Guid.NewGuid(),
                            ForeignScopeId,
                            InstallationNumber,
                            AccountLogin,
                            "selected",
                            GitHubAppInstallationStatuses.Active,
                            Clock,
                            Clock),
                        token).ConfigureAwait(false);
                    return null;
                }).ConfigureAwait(false);
        }

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 跨租户拒绝");
        var setup = await BeginSetupAsync(owner, fixture.ProjectId);

        var refused = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(GitHubAppError.InstallationMismatch, refused.Body.GetProperty("code").GetString());
        Assert.Null(await ReadConnectionAsync(database, InstallationId, fixture.ProjectId));
    }

    [Fact]
    public async Task App_001_Boundary_replayed_and_expired_states_create_nothing()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = AppScript();
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_001_boundary_state", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        await ConfigureAppAsync(host, options, rsa);

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 状态边界");
        var setup = await BeginSetupAsync(owner, fixture.ProjectId);

        var first = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A replayed state is refused exactly like a forged one, and creates nothing new.
        var replayed = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(GitHubAppError.StateInvalid, replayed.Body.GetProperty("code").GetString());

        var forged = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, "forged-state");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(GitHubAppError.StateInvalid, forged.Body.GetProperty("code").GetString());

        // An expired state lapses: eleven minutes past issue it is refused.
        var later = await BeginSetupAsync(owner, fixture.ProjectId);
        clock.Current = clock.Current.AddMinutes(11);
        var expired = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, later.State);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(GitHubAppError.StateInvalid, expired.Body.GetProperty("code").GetString());

        Assert.Equal(1, await CountConnectionsAsync(database));
    }

    // ===========================================================================================
    // GITHUB-APP-CONNECTION-002 Success: an expiring token is renewed, and a concurrent refresh
    // mints once.
    // ===========================================================================================

    [Fact]
    public async Task App_002_Success_an_expiring_token_is_renewed_and_a_concurrent_refresh_mints_once()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = AppScript(token: "ghs-test-token-1", expiresAt: Clock.AddHours(1));
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_002_success_renew", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        await ConfigureAppAsync(host, options, rsa);

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 令牌续期");
        var setup = await BeginSetupAsync(owner, fixture.ProjectId);
        var connected = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);
        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        github.ClearRequests();

        // Ten callers arriving together mint once: the gate holds nine and hands them the winner.
        string first;
        using (var scope = host.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IGitHubInstallationTokenProvider>();
            var calls = Enumerable.Range(0, 10)
                .Select(_ => tokens.GetAsync(InstallationId, InstallationNumber, RepositoryNumber))
                .ToArray();
            var answers = await Task.WhenAll(calls).ConfigureAwait(false);
            first = answers[0];
            Assert.All(answers, answer => Assert.Equal("ghs-test-token-1", answer));
        }

        Assert.Equal("ghs-test-token-1", first);
        Assert.Equal(1, github.Requests.Count(request => request.Method == "POST"));

        // Fifty-nine minutes on the token is still served from memory: the skew is sixty seconds,
        // so the entry is live until the last minute.
        clock.Current = Clock.AddMinutes(58);
        using (var scope = host.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IGitHubInstallationTokenProvider>();
            Assert.Equal("ghs-test-token-1", await tokens.GetAsync(InstallationId, InstallationNumber, RepositoryNumber).ConfigureAwait(false));
        }

        Assert.Equal(1, github.Requests.Count(request => request.Method == "POST"));

        // Past the skew the provider renews for the selected repository, and the renewal narrows
        // exactly as the first mint did.
        github.OnPostOnce(
            $"/app/installations/{InstallationNumber}/access_tokens",
            HttpStatusCode.OK,
            TokenPayload("ghs-test-token-2", Clock.AddHours(2)));

        clock.Current = Clock.AddMinutes(59).AddSeconds(1);
        using (var scope = host.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IGitHubInstallationTokenProvider>();
            Assert.Equal("ghs-test-token-2", await tokens.GetAsync(InstallationId, InstallationNumber, RepositoryNumber).ConfigureAwait(false));
        }

        var mints = github.Requests.Where(request => request.Method == "POST").ToArray();
        Assert.Equal(2, mints.Length);
        Assert.All(mints, mint =>
        {
            Assert.Contains($"\"repository_ids\":[{RepositoryNumber}]", mint.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("permissions", mint.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("write", mint.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ===========================================================================================
    // GITHUB-APP-CONNECTION-002 Failure: a removed installation ends new and queued reads without
    // publishing or leaking.
    // ===========================================================================================

    [Fact]
    public async Task App_002_Failure_a_removed_installation_fails_new_and_queued_reads_without_publishing()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = AppScript();
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_002_failure_removed", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        await ConfigureAppAsync(host, options, rsa, webhookSecret: "whsec-test-secret-1");

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 卸载撤销");
        var setup = await BeginSetupAsync(owner, fixture.ProjectId);
        var connected = await ConnectAsync(owner, fixture.ProjectId, InstallationNumber, RepositoryNumber, setup.State);
        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", key: "app-revoked");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // GitHub reports the uninstall over the signed webhook: the binding is marked removed and
        // every local connection bound to it is revoked immediately.
        var payload = $"{{\"action\":\"deleted\",\"installation\":{{\"id\":{InstallationNumber}}}}}";
        using var delivery = await PostWebhookAsync(host, "installation", payload, secret: "whsec-test-secret-1");
        Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);

        var installation = await ReadInstallationAsync(database, InstallationId, InstallationNumber);
        Assert.NotNull(installation);
        Assert.Equal("removed", installation!.Status);
        Assert.Null(await ReadConnectionAsync(database, InstallationId, fixture.ProjectId));

        // The queued job finds no connection and fails without appending: the bundle keeps the one
        // manual revision it had, and the failure names the missing connection rather than any
        // token or repository detail.
        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "failed");
        Assert.Equal("github_job_connection_missing", operation.ErrorCode);

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(1, head.Revision);

        // A new read is refused the same way, before any job exists.
        var refused = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", key: "app-after-revoke");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("github_connection_missing", refused.Body.GetProperty("code").GetString());

        // Nothing minted ever reached a row: not the connections, not the installations, not the
        // run log and not the operation's own reference.
        Assert.Equal(0, await CountSubstringAsync(database, "github_connections", "owner", "ghs-test-token"));
        Assert.Equal(0, await CountSubstringAsync(database, "github_app_installations", "account_login", "ghs-test-token"));
        Assert.Equal(0, await CountSubstringAsync(database, "sync_sources", "endpoints", "ghs-test-token"));
        Assert.Equal(0, await CountSubstringAsync(database, "operations", "result_ref", "ghs-test-token"));

        // And an unsigned delivery never reaches the handler at all.
        var forged = await PostWebhookAsync(host, "installation", payload, secret: "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(GitHubAppError.SignatureInvalid, JsonDocument.Parse(await forged.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    // ===========================================================================================
    // GITHUB-APP-CONNECTION-002 Boundary: the Community token path works exactly as before.
    // ===========================================================================================

    [Fact]
    public async Task App_002_Boundary_a_community_token_connection_collects_unchanged_beside_the_app()
    {
        using var keyFile = OperatorKeyFile.Create();
        using var rsa = RSA.Create(2048);

        var github = PatScript();
        var clock = new AdvancingClock(Clock);
        var options = new TestGitHubAppOptions();
        var scenario = await ScenarioAsync("gh_app_002_boundary_pat", github, clock, options, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        await ConfigureAppAsync(host, options, rsa);

        using var owner = await OwnerClient.CreateAsync(host);
        var fixture = await CreateMilestoneAsync(owner, "App 下令牌不变");
        var credentialId = await SavePatAsync(owner);
        var project = await ReadProjectAsync(owner, fixture.ProjectId);

        using var connect = await owner.SendAsync(await owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/github-connection",
            new { owner = RepositoryOwner, repository = "pat-fixture", credentialRef = credentialId.ToString("D") },
            project.GetProperty("revision").GetInt64()));
        Assert.Equal(HttpStatusCode.Created, connect.StatusCode);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0", key: "pat-still-works");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");
        Assert.Null(operation.ErrorCode);

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, head.Revision);

        // The rollout wrote no installation mode onto this row: it is a token connection, with its
        // credential reference and no installation behind it.
        var connection = await ReadConnectionAsync(database, InstallationId, fixture.ProjectId);
        Assert.NotNull(connection);
        Assert.Equal("pat", connection!.AuthMode);
        Assert.Null(connection.InstallationId);
        Assert.Equal(credentialId, connection.CredentialRef);

        // Every request the transport made carried the owner's token and no installation token.
        Assert.NotEmpty(github.Requests);
        Assert.All(github.Requests, request => Assert.Contains(StoredPat, request.Authorization ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(github.Requests, request => (request.Authorization ?? string.Empty).Contains("ghs-", StringComparison.Ordinal));
    }

    // ===========================================================================================
    // Structure: the App transport is a leaf, and its JWT verifies against the stored PEM shape.
    // ===========================================================================================

    [Fact]
    public void App_002_The_app_transport_project_depends_on_nothing_in_this_repository()
    {
        var referenced = typeof(GitHubAppClient).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("Chronicleaf", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(referenced);
    }

    [Fact]
    public void App_002_A_minted_jwt_verifies_against_the_key_that_signed_it()
    {
        using var rsa = RSA.Create(2048);
        var client = new GitHubAppClient(new HttpClient());
        var jwt = client.CreateAppJwt(AppId, rsa.ExportPkcs8PrivateKeyPem());

        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url(parts[1]))).RootElement;
        Assert.Equal(AppId.ToString(CultureInfo.InvariantCulture), payload.GetProperty("iss").GetString());

        var unsigned = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        Assert.True(rsa.VerifyData(
            unsigned,
            Base64Url(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void App_002_The_migration_is_required_and_current()
    {
        Assert.Contains(ChronicleafSchema.GitHubAppConnectionMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));
    }

    // ===========================================================================================
    // Fixtures.
    // ===========================================================================================

    private static byte[] Base64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    /// <summary>The App script: one installation, one mint and one selected repository.</summary>
    private static FakeGitHubHandler AppScript(string token = "ghs-test-token-1", DateTimeOffset? expiresAt = null) =>
        new FakeGitHubHandler()
            .OnGet(
                $"/app/installations/{InstallationNumber}",
                """{"id":9001,"account":{"login":"octo"},"repository_selection":"selected"}""")
            .OnPost(
                $"/app/installations/{InstallationNumber}/access_tokens",
                TokenPayload(token, expiresAt ?? Clock.AddHours(1)))
            .OnGet(
                $"/repositories/{RepositoryNumber}",
                """{"id":7001,"full_name":"octo/fixture","private":false,"default_branch":"main"}""");

    private static string TokenPayload(string token, DateTimeOffset expiresAt) =>
        "{\"token\":\"" + token
        + "\",\"expires_at\":\"" + expiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        + "\",\"permissions\":{\"contents\":\"read\",\"metadata\":\"read\",\"issues\":\"read\",\"pull_requests\":\"read\"}}";

    /// <summary>The token-path script: the describe, two revisions and one compared file.</summary>
    private static FakeGitHubHandler PatScript() =>
        new FakeGitHubHandler()
            .OnGet("/repos/octo/pat-fixture", """
                {"id":7002,"private":false,"default_branch":"main","full_name":"octo/pat-fixture"}
                """)
            .OnGet("/commits/v0.9.0", """
                {"sha":"1111111111111111111111111111111111111111","commit":{"message":"Release v0.9.0","author":{"name":"A","date":"2026-08-01T09:00:00Z"}}}
                """)
            .OnGet("/commits/main", """
                {"sha":"2222222222222222222222222222222222222222","commit":{"message":"Ship the reproducible pipeline","author":{"name":"B","date":"2026-09-01T09:00:00Z"}}}
                """)
            .OnGet("/compare/1111111111111111111111111111111111111111...2222222222222222222222222222222222222222", """
                {
                  "status":"ahead","total_commits":1,
                  "commits":[{"sha":"2222222222222222222222222222222222222222","commit":{"message":"Ship the reproducible pipeline","author":{"name":"B","date":"2026-09-01T09:00:00Z"}}}],
                  "files":[{"filename":"src/app.ts","status":"modified","additions":1,"deletions":0,"patch":"@@ -1 +1,2 @@\n+console.log('reproducible');"}]
                }
                """);

    private sealed record Scenario(string Database, GithubAppConnectionApiHost Host);

    private async Task<Scenario> ScenarioAsync(
        string purpose,
        FakeGitHubHandler github,
        AdvancingClock clock,
        TestGitHubAppOptions options,
        string? keyFile = null)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var host = GithubAppConnectionApiHost.Start(database, clock, github, options, keyFile);

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

        return new Scenario(database, host);
    }

    /// <summary>
    /// Stores the App PEM and webhook secret through the host's own vault and points the options
    /// at them. The references are the vault's own ids, decided after the host starts - which is
    /// why the options travel as a holder rather than configuration.
    /// </summary>
    private static async Task ConfigureAppAsync(
        GithubAppConnectionApiHost host,
        TestGitHubAppOptions options,
        RSA rsa,
        string? webhookSecret = null)
    {
        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        var pem = await vault.PutAsync(
            InstallationId,
            GitHubAppCredentialProviders.AppPrivateKey,
            rsa.ExportPkcs8PrivateKeyPem()).ConfigureAwait(false);

        options.AppId = AppId;
        options.PrivateKeyCredentialRef = pem.Value.ToString("D");

        if (webhookSecret is not null)
        {
            var secret = await vault.PutAsync(
                InstallationId,
                GitHubAppCredentialProviders.AppWebhookSecret,
                webhookSecret).ConfigureAwait(false);

            options.WebhookSecretCredentialRef = secret.Value.ToString("D");
        }
    }

    private sealed record MilestoneFixture(Guid ProjectId, Guid MilestoneId);

    private static async Task<MilestoneFixture> CreateMilestoneAsync(OwnerClient owner, string title)
    {
        using var projectResponse = await owner.SendAsync(await owner.Post(
            "/api/projects",
            new { name = $"{title} 项目", description = $"{title} 说明。" }));
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);

        var projectId = Guid.Parse(JsonDocument
            .Parse(await projectResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var milestoneResponse = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = $"{title} 的记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));
        Assert.Equal(HttpStatusCode.Created, milestoneResponse.StatusCode);

        var milestoneId = Guid.Parse(JsonDocument
            .Parse(await milestoneResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The owner recorded this by hand.", expectedRevision = 0 },
            0));
        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        return new MilestoneFixture(projectId, milestoneId);
    }

    private sealed record SetupState(string State);

    private static async Task<SetupState> BeginSetupAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}/github-app/setup");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);

        return new SetupState(JsonDocument.Parse(raw).RootElement.GetProperty("state").GetString()!);
    }

    private sealed record ConnectResult(HttpStatusCode StatusCode, JsonElement Body, string Raw);

    private static async Task<ConnectResult> ConnectAsync(
        OwnerClient owner,
        Guid projectId,
        long installationId,
        long repositoryId,
        string? state)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/github-app/connect",
            new { installationId, repositoryId, state }));
        var raw = await response.Content.ReadAsStringAsync();

        return new ConnectResult(
            response.StatusCode,
            JsonDocument.Parse(raw).RootElement.Clone(),
            raw);
    }

    private sealed record SyncAccepted(HttpStatusCode StatusCode, Guid OperationId, JsonElement Body);

    private static async Task<SyncAccepted> RequestSyncAsync(
        OwnerClient owner,
        Guid milestoneId,
        string headRef,
        string? baseRef = null,
        string key = "sync")
    {
        var head = await ReadEvidenceAsync(owner, milestoneId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/github-sync",
            new
            {
                headRef,
                baseRef,
                expectedBundleRevision = head.Revision,
                idempotencyKey = key,
            },
            head.Revision));

        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;

        return new SyncAccepted(
            response.StatusCode,
            response.StatusCode == HttpStatusCode.Accepted
                ? Guid.Parse(body.GetProperty("operationId").GetString()!)
                : Guid.Empty,
            body.Clone());
    }

    private static async Task<HttpResponseMessage> PostWebhookAsync(
        GithubAppConnectionApiHost host,
        string eventName,
        string payload,
        string secret)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        Span<byte> mac = stackalloc byte[32];
        Assert.Equal(32, HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, mac));

        var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github-app")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-Hub-Signature-256", $"sha256={Convert.ToHexString(mac).ToLowerInvariant()}");

        return await client.SendAsync(request);
    }

    private static async Task<Guid> SavePatAsync(OwnerClient owner)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{GitHubEvidenceService.CredentialProvider}",
            new { value = StoredPat },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadProjectAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.Clone();
    }

    private sealed record EvidenceHead(long Revision);

    private static async Task<EvidenceHead> ReadEvidenceAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);

        var bundle = JsonDocument.Parse(raw).RootElement.GetProperty("items")[0];

        return new EvidenceHead(bundle.GetProperty("revision").GetInt64());
    }

    private sealed record StoredInstallation(string AccountLogin, string RepositorySelection, string Status);

    private static async Task<StoredInstallation?> ReadInstallationAsync(string database, Guid scopeId, long installationId)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_login, repository_selection, status
            FROM github_app_installations
            WHERE scope_id = @scope AND installation_id = @installation
            """;
        command.Parameters.AddWithValue("scope", scopeId);
        command.Parameters.AddWithValue("installation", installationId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredInstallation(reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private sealed record StoredConnection(string AuthMode, long? InstallationId, long? RepositoryId, Guid? CredentialRef);

    private static async Task<StoredConnection?> ReadConnectionAsync(string database, Guid scopeId, Guid projectId)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT auth_mode, installation_id, repository_id, credential_ref
            FROM github_connections
            WHERE scope_id = @scope AND project_id = @project
            """;
        command.Parameters.AddWithValue("scope", scopeId);
        command.Parameters.AddWithValue("project", projectId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredConnection(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static async Task<long> CountConnectionsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM github_connections";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Counts rows whose column contains a substring, without LIKE wildcards in play.</summary>
    private static async Task<long> CountSubstringAsync(string database, string table, string column, string needle)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table} WHERE POSITION(@needle IN {column}::text) > 0";
        command.Parameters.AddWithValue("needle", needle);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A clock the scenario moves by hand, so expiries are deterministic.</summary>
    private sealed class AdvancingClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => Current;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
