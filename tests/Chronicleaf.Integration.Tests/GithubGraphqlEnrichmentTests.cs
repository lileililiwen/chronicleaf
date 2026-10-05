using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.GitHubEnrichment;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;
using Chronicleaf.GitHub.GraphQL;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.GitHubEnrichment;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// GITHUB-GRAPHQL-ENRICHMENT-001 and GITHUB-GRAPHQL-ENRICHMENT-002 against a real disposable
/// PostgreSQL and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. Every row comes from a real PostgreSQL server
/// started from a pinned image, the connection write, the seeding sync and the enrichment go
/// through the real services over the real stores, the job is run by the real Hangfire server
/// from the real queue, and the append lands through the real <c>MilestoneEvidenceService</c>
/// with the database's own triggers and constraints deciding whether it is honest. There is no EF
/// in-memory provider and no stubbed store in this file. Every claim is read back over the wire -
/// a status code, a header, a body - or over a real query against the real tables.
/// </para>
/// <para>
/// <b>About the transport.</b> <see cref="FakeEnrichmentTransport"/> is a real
/// <c>HttpMessageHandler</c> and is the only thing in this repository that stands in for anything.
/// It fakes GitHub and nothing else: the API host, the queue, the workers, the vault, the stores,
/// the evidence append and the routes are all the production ones. <b>So nothing in this file is
/// evidence that GitHub works</b>, that a repository is public, or that a personal access token is
/// accepted. What these scenarios establish is Chronicleaf's own behaviour: which SHAs are queried,
/// how partial errors become unavailable items, how duplicates collapse, which hosts are ever
/// contacted, how each refusal becomes a job status, and that a moved bundle is never overwritten.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one, and each
/// is named after the scenario it covers.
/// </para>
/// </remarks>
public sealed class GithubGraphqlEnrichmentTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("3f6b9c41-1e5d-4a27-9b38-8c7d6e5f4a21");

    private const string Owner = "chronicleaf-tests";
    private const string Repo = "enrichment-fixture";

    private const string BaseSha = "1111111111111111111111111111111111111111";
    private const string HeadSha = "2222222222222222222222222222222222222222";

    /// <summary>A secret sealed by the real vault under the scenario's real key file. Never leaves this file.</summary>
    private const string StoredToken = "ghp-chronicleaf-enrichment-sentinel-9c2e5b7d4f1a";

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-001 Success: readable associated PRs become attributed evidence.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_001_Success_readable_associated_pull_requests_are_appended_with_ids_states_and_urls()
    {
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, CommitPage(BaseSha, PullRequestNode(
                "PR_7", 7, "Ship the reproducible pipeline", "MERGED", true,
                "https://github.com/chronicleaf-tests/enrichment-fixture/pull/7",
                "main", "feature-pipeline",
                IssueNode("ISS_3", 3, "Reproducible pipeline", "CLOSED",
                    "https://github.com/chronicleaf-tests/enrichment-fixture/issues/3"))))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, CommitPage(HeadSha, PullRequestNode(
                "PR_9", 9, "Document the pipeline", "OPEN", false,
                "https://github.com/chronicleaf-tests/enrichment-fixture/pull/9",
                "main", "docs-pipeline")));
        var scenario = await ScenarioAsync("gh_enrich_001_success", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "关联丰富成功");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);

        Assert.True(accepted.Status == HttpStatusCode.Accepted, $"{accepted.Body}\n{host.Logs.Text}");

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        Assert.Null(operation.ErrorCode);
        Assert.Equal(
            $"{GitHubEnrichmentService.EnrichmentOperationKind}:3",
            operation.ResultRef);

        var head = await ReadEvidenceAsync(owner, seeded.MilestoneId);
        Assert.Equal(3, head.Revision);

        // The attributed relationships: ids, observed states and source URLs.
        var pull7 = head.Items.Single(item =>
            item.Kind == EvidenceKinds.Pr && item.Text.Contains("Provider node id: PR_7", StringComparison.Ordinal));
        Assert.Equal($"github:{Owner}/{Repo}", pull7.SourceRef);
        Assert.Equal(BaseSha, pull7.SourceRevision);
        Assert.Equal(EvidenceAvailability.Complete, pull7.Availability);
        Assert.Contains("Pull request #7: Ship the reproducible pipeline", pull7.Text, StringComparison.Ordinal);
        Assert.Contains("State: MERGED", pull7.Text, StringComparison.Ordinal);
        Assert.Contains("Merged: yes", pull7.Text, StringComparison.Ordinal);
        Assert.Contains("https://github.com/chronicleaf-tests/enrichment-fixture/pull/7", pull7.Text, StringComparison.Ordinal);
        Assert.Contains("Base: main", pull7.Text, StringComparison.Ordinal);
        Assert.Contains("Head: feature-pipeline", pull7.Text, StringComparison.Ordinal);

        var issue3 = head.Items.Single(item =>
            item.Kind == EvidenceKinds.Issue && item.Text.Contains("Provider node id: ISS_3", StringComparison.Ordinal));
        Assert.Equal(EvidenceAvailability.Complete, issue3.Availability);
        Assert.Contains("Issue #3: Reproducible pipeline", issue3.Text, StringComparison.Ordinal);
        Assert.Contains("Observed through pull request #7.", issue3.Text, StringComparison.Ordinal);

        var pull9 = head.Items.Single(item =>
            item.Kind == EvidenceKinds.Pr && item.Text.Contains("Provider node id: PR_9", StringComparison.Ordinal));
        Assert.Equal(HeadSha, pull9.SourceRevision);
        Assert.Contains("State: OPEN", pull9.Text, StringComparison.Ordinal);

        var observation = head.Items.Single(item =>
            item.Kind == EvidenceKinds.ProjectMetadata && item.Text.Contains("Relationships observed:", StringComparison.Ordinal));
        Assert.Contains("Relationships observed: 3", observation.Text, StringComparison.Ordinal);

        // The REST evidence this enriches is intact: both commits and both diffs read as before.
        Assert.Equal(2, head.Items.Count(item =>
            item.Kind == EvidenceKinds.Commit && item.Availability == EvidenceAvailability.Complete));
        Assert.Contains(head.Items, item =>
            item.Kind == EvidenceKinds.Commit &&
            item.SourceRevision == BaseSha &&
            item.Text.Contains("Release v0.9.0", StringComparison.Ordinal));
        Assert.Contains(head.Items, item =>
            item.Kind == EvidenceKinds.Diff && item.Text.Contains("src/app.ts", StringComparison.Ordinal));
    }

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-001 Failure: errored fields are unavailable, REST evidence remains.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_001_Failure_errored_fields_are_unavailable_and_rest_evidence_remains()
    {
        const string sentinel = "sentinel-partial-field-failure-8f3a";
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, """
                {"data":{"repository":{"object":{"__typename":"Commit","oid":"__BASE__","associatedPullRequests":null}}},"errors":[{"message":"__SENTINEL__","type":"INTERNAL"}]}
                """.Replace("__BASE__", BaseSha, StringComparison.Ordinal)
                    .Replace("__SENTINEL__", sentinel, StringComparison.Ordinal))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, CommitPage(HeadSha, PullRequestNode(
                "PR_9", 9, "Document the pipeline", "OPEN", false,
                "https://github.com/chronicleaf-tests/enrichment-fixture/pull/9",
                "main", "docs-pipeline")));
        var scenario = await ScenarioAsync("gh_enrich_001_failure", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "部分失败");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();

        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");
        Assert.Null(operation.ErrorCode);

        var head = await ReadEvidenceAsync(owner, seeded.MilestoneId);
        Assert.Equal(3, head.Revision);

        // The good commit's relations arrived; the errored commit is unavailable with its reason.
        Assert.Contains(head.Items, item =>
            item.Kind == EvidenceKinds.Pr &&
            item.Text.Contains("Provider node id: PR_9", StringComparison.Ordinal));
        var missing = head.Items.Single(item =>
            item.Kind == EvidenceKinds.Commit &&
            item.Availability == EvidenceAvailability.Unavailable &&
            item.SourceRevision == BaseSha);
        Assert.Contains("could not be read", missing.Text, StringComparison.Ordinal);

        // The provider's own error sentence is never stored: the reason is this package's
        // vocabulary, not a sentence copied out of a response body.
        Assert.DoesNotContain(sentinel, string.Join('\n', head.Items.Select(item => item.Text)), StringComparison.Ordinal);

        // The REST evidence remains exactly what the sync collected.
        Assert.Equal(2, head.Items.Count(item =>
            item.Kind == EvidenceKinds.Commit && item.Availability == EvidenceAvailability.Complete));
    }

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-001 Boundary: a repeated provider node is appended once.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_001_Boundary_a_repeated_provider_node_is_appended_once()
    {
        var first = PullRequestNode(
            "PR_DUP", 11, "Deduplicated work", "OPEN", false,
            "https://github.com/chronicleaf-tests/enrichment-fixture/pull/11",
            "main", "dedup");
        var firstAgain = PullRequestNode(
            "PR_DUP", 11, "Deduplicated work", "OPEN", false,
            "https://github.com/chronicleaf-tests/enrichment-fixture/pull/11",
            "main", "dedup");
        var second = PullRequestNode(
            "PR_NEXT", 12, "Next work", "OPEN", false,
            "https://github.com/chronicleaf-tests/enrichment-fixture/pull/12",
            "main", "next");
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, CommitPage(BaseSha, true, "c1", first))
            .OnGraphQl(BaseSha, "c1", HttpStatusCode.OK, CommitPage(BaseSha, firstAgain, second))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, EmptyCommitPage(HeadSha));
        var scenario = await ScenarioAsync("gh_enrich_001_boundary", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "去重边界");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();

        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");
        var head = await ReadEvidenceAsync(owner, seeded.MilestoneId);

        Assert.Equal(1, head.Items.Count(item =>
            item.Text.Contains("Provider node id: PR_DUP", StringComparison.Ordinal)));
        Assert.Equal(1, head.Items.Count(item =>
            item.Text.Contains("Provider node id: PR_NEXT", StringComparison.Ordinal)));
        Assert.DoesNotContain(head.Items, item =>
            item.Availability == EvidenceAvailability.Unavailable && item.SourceRevision == BaseSha);
    }

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-002 Success: only the declared repository is queried.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_002_Success_only_the_declared_repository_is_queried()
    {
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, CommitPage(BaseSha, PullRequestNode(
                "PR_7", 7, "Ship the reproducible pipeline", "MERGED", true,
                "https://github.com/chronicleaf-tests/enrichment-fixture/pull/7",
                "main", "feature-pipeline")))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, EmptyCommitPage(HeadSha));
        var scenario = await ScenarioAsync("gh_enrich_002_success", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "只查已声明仓库");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        var posts = transport.Requests
            .Where(request => string.Equals(request.Method, "POST", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(posts);

        foreach (var post in posts)
        {
            // The fixed origin: every request went to the pinned host and the GraphQL path, and no
            // request went anywhere else.
            Assert.Equal("api.github.com", post.Host);
            Assert.Equal("/graphql", post.Path);

            using var body = JsonDocument.Parse(post.Body);
            var root = body.RootElement;
            Assert.Equal(GitHubRelationshipReader.OperationName, root.GetProperty("operationName").GetString());

            // The query is the fixed text, not a composition: byte-identical to the shipped query.
            Assert.Equal(GitHubRelationshipReader.Query, root.GetProperty("query").GetString());

            var variables = root.GetProperty("variables");
            Assert.Equal(Owner, variables.GetProperty("owner").GetString());
            Assert.Equal(Repo, variables.GetProperty("name").GetString());
        }

        Assert.All(
            transport.Requests,
            request => Assert.Equal("api.github.com", request.Host));
    }

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-002 Failure: missing permissions report permission_denied.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_002_Failure_missing_permissions_report_permission_denied_without_facts()
    {
        var transport = SeedScript()
            .OnGraphQl(null, null, HttpStatusCode.Forbidden, """{"message":"Forbidden"}""");
        var scenario = await ScenarioAsync("gh_enrich_002_failure", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "权限不足");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();

        var failed = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            GitHubEnrichmentJobError.PermissionDenied,
            TimeSpan.FromSeconds(60));

        Assert.Equal("failed", failed.Status);

        // No relation fact was fabricated to fill the gap: the bundle is exactly the seeded one.
        var head = await ReadEvidenceAsync(owner, seeded.MilestoneId);
        Assert.Equal(seeded.Revision, head.Revision);
        Assert.DoesNotContain(head.Items, item =>
            item.Kind is EvidenceKinds.Pr or EvidenceKinds.Issue &&
            item.Text.Contains("Provider node id:", StringComparison.Ordinal));
    }

    // ===========================================================================================
    // GITHUB-GRAPHQL-ENRICHMENT-002 Boundary: a bundle that moves mid-query is superseded.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_002_Boundary_a_bundle_that_moves_during_the_query_is_superseded()
    {
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, CommitPage(BaseSha, PullRequestNode(
                "PR_7", 7, "Ship the reproducible pipeline", "MERGED", true,
                "https://github.com/chronicleaf-tests/enrichment-fixture/pull/7",
                "main", "feature-pipeline")))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, EmptyCommitPage(HeadSha));

        // Holds the first GraphQL POST inside the read, so the bundle can be moved while the query
        // is still in flight. The service has already read the head it must extend by then.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.BeforeGraphQlResponse = () => gate.Task;

        var scenario = await ScenarioAsync("gh_enrich_002_boundary", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "中途移动");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();

        // Wait until the job is inside the GraphQL read, move the bundle, then let the read finish.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!transport.Requests.Any(request =>
                   string.Equals(request.Method, "POST", StringComparison.Ordinal)) &&
               DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(
            transport.Requests.Any(request => string.Equals(request.Method, "POST", StringComparison.Ordinal)),
            "The enrichment job never reached the GraphQL read.");

        await AppendManualAsync(owner, seeded.MilestoneId, seeded.Revision, "The owner wrote while the query ran.");
        gate.TrySetResult();

        var superseded = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            GitHubEnrichmentJobError.Superseded,
            TimeSpan.FromSeconds(60));
        Assert.Equal("failed", superseded.Status);

        // Nothing was appended over the moved bundle: the revision is the manual one, with no
        // relation facts on it.
        var head = await ReadEvidenceAsync(owner, seeded.MilestoneId);
        Assert.Equal(seeded.Revision + 1, head.Revision);
        Assert.DoesNotContain(head.Items, item =>
            item.Text.Contains("Provider node id:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enrichment_002_Boundary_a_stale_request_is_superseded_without_calling_github()
    {
        var transport = SeedScript()
            .OnGraphQl(null, null, HttpStatusCode.OK, EmptyCommitPage(BaseSha));
        var scenario = await ScenarioAsync("gh_enrich_002_stale", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "过期请求");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);

        // The bundle moves after the request but before the job runs: the run row's revision no
        // longer describes the head, so the job ends before any GraphQL request exists.
        await AppendManualAsync(owner, seeded.MilestoneId, seeded.Revision, "The owner wrote first.");
        await host.DispatchAsync();

        var superseded = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            GitHubEnrichmentJobError.Superseded,
            TimeSpan.FromSeconds(60));
        Assert.Equal("failed", superseded.Status);

        Assert.DoesNotContain(
            transport.Requests,
            request => string.Equals(request.Method, "POST", StringComparison.Ordinal));
    }

    // ===========================================================================================
    // Durable semantics: a 429 waits for the provider's own reset, then the run succeeds.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_rate_limited_waits_for_the_providers_reset_then_succeeds()
    {
        var transport = SeedScript()
            .OnGraphQl(null, null, HttpStatusCode.OK, EmptyCommitPage(BaseSha));
        var scenario = await ScenarioAsync("gh_enrich_rate_limited", transport);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var seeded = await SeedCommitsAsync(owner, host, database, "限流等待");
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);

        // Only the first POST is throttled, with the provider's own two-second delay: the retry
        // reads the same script and succeeds. Throttling every request would never let the run
        // succeed and would prove nothing about the wait.
        transport.OnGraphQlOnce(
            null,
            null,
            HttpStatusCode.TooManyRequests,
            """{"message":"rate limited"}""",
            new Dictionary<string, string> { ["Retry-After"] = "2" });

        await host.DispatchAsync();

        var retried = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            OperationErrorCodes.RateLimited,
            TimeSpan.FromSeconds(30));
        Assert.Equal("queued", retried.Status);
        Assert.Equal(1, retried.Attempt);
        Assert.Equal(
            OperationPolicy.DelayFor(1, TimeSpan.FromSeconds(2)),
            retried.RecordedBackoff);

        var succeeded = await OperationRows.AwaitAsync(
            database,
            accepted.OperationId,
            "succeeded",
            TimeSpan.FromSeconds(60));
        Assert.Equal(2, succeeded.Attempt);
        Assert.Null(succeeded.ErrorCode);
    }

    // ===========================================================================================
    // Privacy: the credential is a leased header, never a stored or logged value.
    // ===========================================================================================

    [Fact]
    public async Task Enrichment_credential_is_a_leased_header_never_a_stored_value()
    {
        using var keyFile = OperatorKeyFile.Create();
        var transport = SeedScript()
            .OnGraphQl(BaseSha, null, HttpStatusCode.OK, EmptyCommitPage(BaseSha))
            .OnGraphQl(HeadSha, null, HttpStatusCode.OK, EmptyCommitPage(HeadSha));
        var scenario = await ScenarioAsync("gh_enrich_credential", transport, keyFile.FilePath);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "凭据是租借的");
        var credentialRef = await SaveCredentialAsync(owner);
        await ConnectAsync(owner, fixture.ProjectId, credentialRef, host);

        var seeded = await CollectSeedAsync(owner, host, database, fixture.ProjectId, fixture.MilestoneId);
        var accepted = await RequestEnrichmentAsync(owner, seeded.MilestoneId, seeded.Revision);
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        // The leased bytes became one bearer header on the GraphQL POST and nothing else.
        var posts = transport.Requests
            .Where(request => string.Equals(request.Method, "POST", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(posts);
        Assert.All(posts, post => Assert.Equal($"Bearer {StoredToken}", post.Authorization));

        // The run row and the operation row carry the request, not the secret.
        await using var connection = new Npgsql.NpgsqlConnection(database);
        await connection.OpenAsync();

        await using (var run = connection.CreateCommand())
        {
            run.CommandText = "SELECT owner, repo FROM github_enrichment_runs WHERE operation_id = @id";
            run.Parameters.AddWithValue("id", accepted.OperationId);

            await using var reader = await run.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(Owner, reader.GetString(0));
            Assert.Equal(Repo, reader.GetString(1));
            Assert.DoesNotContain(StoredToken, $"{reader.GetString(0)} {reader.GetString(1)}", StringComparison.Ordinal);
        }

        var operation = await OperationRows.ReadAsync(database, accepted.OperationId);
        Assert.NotNull(operation);
        Assert.DoesNotContain(StoredToken, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(StoredToken, host.Logs.Text, StringComparison.Ordinal);
    }

    // ===========================================================================================
    // Registration: the reader shadows nothing and the provider slots are untouched.
    // ===========================================================================================

    [Fact]
    public void Enrichment_registration_reuses_the_pinned_transport_and_shadows_nothing()
    {
        var services = new ServiceCollection()
            .AddGitHubEvidence()
            .AddGitHubGraphqlEnrichment();

        var provider = services.BuildServiceProvider();

        using (provider)
        {
            // One HttpClient, still the pinned one: this package registers no client of its own, so
            // it cannot move a collection onto another host, timeout or redirect policy.
            var client = provider.GetRequiredService<HttpClient>();
            Assert.Equal(new Uri("https://api.github.com"), client.BaseAddress);

            Assert.IsType<GitHubEvidenceClient>(provider.GetRequiredService<IGitEvidenceSource>());
            Assert.IsType<GitHubRelationshipReader>(provider.GetRequiredService<IGitHubRelationshipReader>());
        }

        // The handler is registered for its kind on the delivered handler contract: the registry the
        // operations package builds finds it by kind, and this package adds no second transport the
        // registry could confuse it with.
        var handler = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IOperationHandler) &&
                descriptor.ImplementationType == typeof(GitHubEnrichmentHandler));
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);
    }

    [Fact]
    public void Enrichment_job_error_codes_are_a_closed_vocabulary()
    {
        Assert.Equal(11, GitHubEnrichmentJobError.All.Count);
        Assert.Equal(GitHubEnrichmentJobError.All.Count, GitHubEnrichmentJobError.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(GitHubEnrichmentJobError.PermissionDenied, GitHubEnrichmentJobError.All);
        Assert.Contains(GitHubEnrichmentJobError.Superseded, GitHubEnrichmentJobError.All);
    }

    [Theory]
    [InlineData("1111111111111111111111111111111111111111", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("main", false)]
    [InlineData("111111111111111111111111111111111111111", false)]
    [InlineData("11111111111111111111111111111111111111111", false)]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData("", false)]
    public void Enrichment_only_immutable_revisions_are_queried(string value, bool expected) =>
        Assert.Equal(expected, GitHubRelationshipReader.IsCommitSha(value));

    // ===========================================================================================
    // Fixtures.
    // ===========================================================================================

    /// <summary>
    /// A fresh database, migrated, with the installation provisioned through the scenario's own host.
    /// </summary>
    private async Task<Scenario> ScenarioAsync(
        string purpose,
        FakeEnrichmentTransport transport,
        string? keyFile = null)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var host = GithubGraphqlEnrichmentApiHost.Start(database, new FixedTimeProvider(Clock), transport, keyFile);

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

    /// <summary>The database and the one host a scenario runs in.</summary>
    private sealed record Scenario(string Database, GithubGraphqlEnrichmentApiHost Host);

    /// <summary>
    /// The REST script the seeding sync reads: a public repository, two revisions and a comparison
    /// with two commits and two changed files. Enrichment scenarios never script these again; they
    /// only add GraphQL rules for the SHAs the sync stored.
    /// </summary>
    private static FakeEnrichmentTransport SeedScript() =>
        new FakeEnrichmentTransport()
            .OnGet($"/repos/{Owner}/{Repo}", """
                {"id":4242,"private":false,"default_branch":"main","full_name":"chronicleaf-tests/enrichment-fixture"}
                """)
            .OnGet("/commits/v0.9.0", Commit(BaseSha, "Release v0.9.0", "2026-08-01T09:00:00Z"))
            .OnGet("/commits/main", Commit(HeadSha, "Ship the reproducible pipeline", "2026-09-01T09:00:00Z"))
            .OnGet($"/compare/{BaseSha}...{HeadSha}", ComparePayload());

    private static string ComparePayload() =>
        ("""
        {
          "status":"ahead","total_commits":2,
          "commits":[
            {"sha":"__BASE__","commit":{"message":"Release v0.9.0","author":{"name":"A","date":"2026-08-01T09:00:00Z"}}},
            {"sha":"__HEAD__","commit":{"message":"Ship the reproducible pipeline","author":{"name":"B","date":"2026-09-01T09:00:00Z"}}}
          ],
          "files":[
            {"filename":"src/app.ts","status":"modified","additions":1,"deletions":0,"patch":"@@ -1 +1,2 @@\n+console.log('reproducible');"},
            {"filename":"docs/notes.md","status":"modified","additions":2,"deletions":0,"patch":"@@ -0,0 +1,2 @@\n+notes"}
          ]
        }
        """).Replace("__BASE__", BaseSha, StringComparison.Ordinal)
            .Replace("__HEAD__", HeadSha, StringComparison.Ordinal);

    private static string Commit(string sha, string message, string date) =>
        ("""
        {"sha":"__SHA__","commit":{"message":"__MESSAGE__","author":{"name":"Test Author","date":"__DATE__"}}}
        """).Replace("__SHA__", sha, StringComparison.Ordinal)
            .Replace("__MESSAGE__", message, StringComparison.Ordinal)
            .Replace("__DATE__", date, StringComparison.Ordinal);

    /// <summary>One commit's page of associated pull requests, with page info.</summary>
    private static string CommitPage(string sha, params JsonObject[] pullRequests) =>
        CommitPage(sha, hasNextPage: false, endCursor: null, pullRequests);

    private static string CommitPage(
        string sha,
        bool hasNextPage,
        string? endCursor,
        params JsonObject[] pullRequests)
    {
        var nodes = new JsonArray();
        foreach (var pull in pullRequests)
        {
            nodes.Add(pull);
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["repository"] = new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["__typename"] = "Commit",
                        ["oid"] = sha,
                        ["associatedPullRequests"] = new JsonObject
                        {
                            ["nodes"] = nodes,
                            ["pageInfo"] = new JsonObject
                            {
                                ["hasNextPage"] = hasNextPage,
                                ["endCursor"] = endCursor is null ? null : JsonValue.Create(endCursor),
                            },
                        },
                    },
                },
            },
        }.ToJsonString();
    }

    private static string EmptyCommitPage(string sha) => CommitPage(sha);

    private static JsonObject PullRequestNode(
        string id,
        int number,
        string title,
        string state,
        bool merged,
        string url,
        string baseRef,
        string headRef,
        params JsonObject[] issues)
    {
        var nodes = new JsonArray();
        foreach (var issue in issues)
        {
            nodes.Add(issue);
        }

        return new JsonObject
        {
            ["id"] = id,
            ["number"] = number,
            ["title"] = title,
            ["state"] = state,
            ["merged"] = merged,
            ["url"] = url,
            ["baseRefName"] = baseRef,
            ["headRefName"] = headRef,
            ["closingIssuesReferences"] = new JsonObject { ["nodes"] = nodes },
        };
    }

    private static JsonObject IssueNode(string id, int number, string title, string state, string url) =>
        new()
        {
            ["id"] = id,
            ["number"] = number,
            ["title"] = title,
            ["state"] = state,
            ["url"] = url,
        };

    private sealed record MilestoneFixture(Guid ProjectId, Guid MilestoneId);

    private sealed record SeededMilestone(Guid ProjectId, Guid MilestoneId, long Revision);

    /// <summary>
    /// Seeds two collected commits through the real sync: connect, collect and await the bundle
    /// the enrichment scenarios enrich. The SHAs queried are the ones this stored, never ones a
    /// test typed into a request.
    /// </summary>
    private static async Task<SeededMilestone> SeedCommitsAsync(
        OwnerClient owner,
        GithubGraphqlEnrichmentApiHost host,
        string database,
        string title)
    {
        var fixture = await CreateMilestoneAsync(owner, title);
        await ConnectAsync(owner, fixture.ProjectId, host: host);

        return await CollectSeedAsync(owner, host, database, fixture.ProjectId, fixture.MilestoneId);
    }

    private static async Task<SeededMilestone> CollectSeedAsync(
        OwnerClient owner,
        GithubGraphqlEnrichmentApiHost host,
        string database,
        Guid projectId,
        Guid milestoneId)
    {
        var before = await ReadEvidenceAsync(owner, milestoneId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/github-sync",
            new
            {
                headRef = "main",
                baseRef = "v0.9.0",
                releaseTag = (string?)null,
                issueNumber = (int?)null,
                pullRequestNumber = (int?)null,
                expectedBundleRevision = before.Revision,
                idempotencyKey = $"seed-{milestoneId:D}",
            },
            before.Revision));

        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"{await response.Content.ReadAsStringAsync()}\n{host.Logs.Text}");

        var operationId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(60));

        var head = await ReadEvidenceAsync(owner, milestoneId);
        Assert.Contains(head.Items, item =>
            item.Kind == EvidenceKinds.Commit && item.SourceRevision == BaseSha);

        return new SeededMilestone(projectId, milestoneId, head.Revision);
    }

    private static async Task<MilestoneFixture> CreateMilestoneAsync(OwnerClient owner, string title)
    {
        using var projectResponse = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = $"{title} 项目", description = $"{title} 说明。" }));
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

    private static async Task ConnectAsync(
        OwnerClient owner,
        Guid projectId,
        Guid? credentialRef = null,
        GithubGraphqlEnrichmentApiHost? host = null)
    {
        var project = await ReadProjectAsync(owner, projectId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/github-connection",
            new
            {
                owner = Owner,
                repository = Repo,
                credentialRef = credentialRef?.ToString("D"),
            },
            project.GetProperty("revision").GetInt64()));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{await response.Content.ReadAsStringAsync()}\n{host?.Logs.Text}");
    }

    private static async Task<Guid> SaveCredentialAsync(OwnerClient owner)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{GitHubEvidenceService.CredentialProvider}",
            new { value = StoredToken },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<EnrichmentAccepted> RequestEnrichmentAsync(
        OwnerClient owner,
        Guid milestoneId,
        long revision,
        string key = "enrich")
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/github-enrichment",
            new
            {
                expectedBundleRevision = revision,
                idempotencyKey = key,
            },
            revision));

        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        return new EnrichmentAccepted(
            response.StatusCode,
            body.RootElement.TryGetProperty("operationId", out var operationId)
                ? Guid.Parse(operationId.GetString()!)
                : Guid.Empty,
            body.RootElement.TryGetProperty("created", out var created) && created.GetBoolean(),
            raw);
    }

    private sealed record EnrichmentAccepted(
        HttpStatusCode Status,
        Guid OperationId,
        bool Created,
        string Body);

    private static async Task AppendManualAsync(
        OwnerClient owner,
        Guid milestoneId,
        long revision,
        string notes)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes, expectedRevision = revision },
            revision));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonElement> ReadProjectAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.Clone();
    }

    private static async Task<EvidenceHead> ReadEvidenceAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var bundle = body.RootElement.GetProperty("items")[0];

        return new EvidenceHead(
            bundle.GetProperty("revision").GetInt64(),
            [.. bundle.GetProperty("items").EnumerateArray().Select(item => new EvidenceItem(
                item.GetProperty("kind").GetString()!,
                item.GetProperty("sourceRef").GetString()!,
                item.GetProperty("sourceRevision").GetString()!,
                item.GetProperty("availability").GetString()!,
                item.GetProperty("text").GetString()!))]);
    }

    private sealed record EvidenceHead(long Revision, IReadOnlyList<EvidenceItem> Items);

    private sealed record EvidenceItem(
        string Kind,
        string SourceRef,
        string SourceRevision,
        string Availability,
        string Text);

    /// <summary>
    /// Waits for an operation row to carry <paramref name="errorCode"/>, and returns it.
    /// </summary>
    private static async Task<StoredOperation> AwaitErrorAsync(
        string database,
        Guid operationId,
        string errorCode,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        StoredOperation? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await OperationRows.ReadAsync(database, operationId);

            if (last is not null && last.ErrorCode == errorCode)
            {
                return last;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"operation {operationId:D} did not record {errorCode} within {timeout}; last read was "
            + $"status={last?.Status}, error={last?.ErrorCode}, attempt={last?.Attempt}.");
    }
}
