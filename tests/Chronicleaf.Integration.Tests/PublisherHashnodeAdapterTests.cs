using System.Net;
using System.Text;
using System.Text.Json;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Publishers.Hashnode;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// PUBLISHER-HASHNODE-ADAPTER-001 and PUBLISHER-HASHNODE-ADAPTER-002 against a real disposable
/// PostgreSQL, the real <c>Chronicleaf.Api</c> host, the real operation queue and the real
/// asset, review, evidence, vault and operation contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the asset
/// store, the approvals table, the routes, the access boundary, the attempt table, the
/// operation queue with its real worker, the credential vault over a real operator key file,
/// and the second shipped GraphQL publisher transport - the Hashnode adapter creating one
/// draft per confirmed publish through the shipped transport, continuing to a publish mutation
/// for the created draft on an explicitly confirmed publish. The approved story every
/// scenario publishes is produced by a real generation job through the same scripted provider
/// the drafts suite drives. The publisher credential is genuinely sealed by the vault and
/// genuinely leased per call, and the scripted wire asserts the bearer token it receives
/// equals the saved secret - which is what makes "the credential stays a vault reference" a
/// real assertion rather than a vacuous one.
/// </para>
/// <para>
/// <b>What is scripted.</b> Only the far end of the wire: a <see cref="ScriptedHashnodeHandler"/>
/// answers the exact GraphQL exchange - one <c>createDraft</c> call for a draft, the same
/// create followed by one <c>publishDraft</c> for the created draft on a publish - while every
/// stage above the socket is shipped code. No scenario reaches the real Hashnode endpoint:
/// the handler is the boundary, and a scenario that needs an upstream refusal, a refused
/// redirect or a lost answer scripts exactly that. No claim here is evidence that Hashnode
/// works or that any draft or post exists remotely.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A receipt is read out of the raw
/// <c>publication_attempts</c> table, not only from the receipt route, and an operation is
/// read out of the raw <c>operations</c> table. The wire is read out of the scripted handler's
/// recorded calls: method, path, body and the bearer header. Nothing upstream is inferred
/// from a row this system wrote.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PublisherHashnodeAdapterTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("b4e8f2c3-5a6d-4b9f-8e3d-6f7a8b9c0d22");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every publish scenario derives from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in
    /// no evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string DraftCredentialValue = "test-key-never-used-for-a-real-call";

    private const string PublisherCredentialValue = "test-publisher-key-never-used-for-a-real-call";

    private const string PublicationId = "hn-test-publication-01";

    private const string DraftId = "draft-hn-001";

    private const string PostId = "post-hn-001";

    private const string PostSlug = "reproducible-release-pipeline-abc";

    private const string PostUrl = "https://testowner.hashnode.dev/reproducible-release-pipeline-abc";

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Success: a confirmed draft preview creates one remote draft.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_draft_preview_creates_remote_draft_with_bearer()
    {
        await using var fixture = await ArrangeAsync("hn_001_draft");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");

        // The preview is pure: no draft was created and no token was leased for it.
        Assert.Empty(wire.Calls);
        Assert.Empty(preview.MediaPlan);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-draft",
                confirm = true,
            }));

        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        Assert.True(accepted.RootElement.GetProperty("created").GetBoolean());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));
        Assert.Equal(1, operation.Attempt);

        // Exactly one upstream call: the draft create itself. No second hop, no publish
        // mutation - draft mode stops after the create.
        var call = Assert.Single(wire.Calls);

        Assert.Equal("POST", call.Method);
        Assert.Equal("/", call.Path);
        Assert.StartsWith("https://gql-beta.hashnode.com/", call.AbsoluteUri, StringComparison.Ordinal);

        // The leased token travels as the one bearer header - never a query parameter - and it
        // is the vault's secret, not a value from the request.
        Assert.Equal("Bearer", call.AuthScheme);
        Assert.Equal(PublisherCredentialValue, call.AuthParameter);
        Assert.DoesNotContain(PublisherCredentialValue, call.AbsoluteUri, StringComparison.Ordinal);

        // The query is the fixed literal the transport owns: the wire carries it verbatim, and
        // every value travels in variables, never in the document.
        var envelope = JsonDocument.Parse(call.Body!);
        Assert.Equal(
            HashnodeTransport.CreateDraftDocument,
            envelope.RootElement.GetProperty("query").GetString());
        var input = envelope.RootElement.GetProperty("variables").GetProperty("input");
        Assert.Equal(PublicationId, input.GetProperty("publicationId").GetString());
        Assert.Equal("Reproducible release pipeline", input.GetProperty("title").GetString());

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(stored!.BodyMarkdown, input.GetProperty("contentMarkdown").GetString());

        // The receipt records the remote draft id and the exact content hash: the draft id the
        // provider named, and the body hash of the bytes that were authorized.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("remote-draft", attempt!.State);
        Assert.Equal("draft", attempt.Mode);
        Assert.Equal(DraftId, attempt.RemoteId);
        Assert.Null(attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
        Assert.Equal(stored.BodyHash, attempt.BodyHash);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("remote-draft", receiptBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(DraftId, receiptBody.RootElement.GetProperty("remoteId").GetString());
        Assert.Equal(stored.BodyHash, receiptBody.RootElement.GetProperty("bodyHash").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            receiptBody.RootElement.GetProperty("reconcileHint").ValueKind);

        Assert.DoesNotContain(
            PublisherCredentialValue, await published.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Success: a confirmed publish drafts then publishes.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_publish_creates_draft_then_publishes_post()
    {
        await using var fixture = await ArrangeAsync("hn_001_publish");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "publish");
        Assert.Contains($"hashnode publication:{PublicationId}", preview.Warnings[0], StringComparison.Ordinal);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-publish",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        // Two calls, in order: the draft create, then the publish of the created draft.
        Assert.Equal(2, wire.Calls.Count);
        var create = wire.Calls[0];
        var publish = wire.Calls[1];

        Assert.Equal("POST", create.Method);
        Assert.Equal("/", create.Path);
        Assert.StartsWith("https://gql-beta.hashnode.com/", create.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", create.AuthScheme);
        Assert.Equal(PublisherCredentialValue, create.AuthParameter);

        var createEnvelope = JsonDocument.Parse(create.Body!);
        Assert.Equal(
            HashnodeTransport.CreateDraftDocument,
            createEnvelope.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            PublicationId,
            createEnvelope.RootElement.GetProperty("variables").GetProperty("input")
                .GetProperty("publicationId").GetString());

        // The publish mutation is the fixed literal too, addressing the draft this run
        // created - never an id from another run, never a guessed post address.
        var publishEnvelope = JsonDocument.Parse(publish.Body!);
        Assert.Equal(
            HashnodeTransport.PublishDraftDocument,
            publishEnvelope.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            DraftId,
            publishEnvelope.RootElement.GetProperty("variables").GetProperty("input")
                .GetProperty("draftId").GetString());
        Assert.Equal("Bearer", publish.AuthScheme);
        Assert.Equal(PublisherCredentialValue, publish.AuthParameter);

        // The upstream URL is derived from post.url, never guessed from a slug: it is exactly
        // the address the provider named.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
        Assert.Equal("publish", attempt.Mode);
        Assert.Equal(PostId, attempt.RemoteId);
        Assert.Equal(PostUrl, attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Failure: a refused token fails without exposure.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_refused_token_fails_without_exposing_secret_or_moving_approval()
    {
        await using var fixture = await ArrangeAsync("hn_001_unauthorized");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.RefuseWith(HttpStatusCode.Unauthorized);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-unauthorized",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.CredentialRejected, operation.ErrorCode);

        // The provider's verdict arrived on the one create call: no second hop, no retry.
        Assert.Single(wire.Calls);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(HashnodeError.CredentialRejected, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Null(attempt.RemoteUrl);

        // No secret in the stored rows and no secret in the served responses - and no provider
        // body echoed anywhere either, because error bodies are disposed unread.
        Assert.DoesNotContain(PublisherCredentialValue, attempt.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ErrorCode ?? string.Empty, StringComparison.Ordinal);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Scripted refusal", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The source approval did not move: the refused dispatch authorizes no edit.
        using var review = await owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await review.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reviewStatus").GetString());
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Failure: GraphQL errors on a 200 are answered refusals.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_graphql_errors_fail_as_answered_refusals()
    {
        await using var fixture = await ArrangeAsync("hn_001_graphql");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.GraphQLErrors("BAD_USER_INPUT", "The content did not validate.");
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-graphql",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.InvalidPayload, operation.ErrorCode);

        // A GraphQL errors array on an HTTP 200 is an answered refusal: nothing was created, so
        // there is no second call and no remote id - and the provider's message is never quoted.
        Assert.Single(wire.Calls);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(HashnodeError.InvalidPayload, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.DoesNotContain(
            "did not validate", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Failure: a forbidden role and a plan gate are permanent.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_forbidden_role_and_plan_gate_are_permanent_refusals()
    {
        await using var fixture = await ArrangeAsync("hn_001_forbidden");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.RefuseWith(HttpStatusCode.Forbidden);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // A contributor role never publishes: the 403 is a permanent refusal, not a retry.
        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-forbidden",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, refused.StatusCode);
        var refusedAccepted = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        var forbiddenOperationId = refusedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var forbiddenAttemptId = refusedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var forbiddenOperation = await OperationRows.AwaitAsync(
            fixture.Database, forbiddenOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.PublishForbidden, forbiddenOperation.ErrorCode);

        var forbiddenAttempt = await AttemptRows.ReadAsync(fixture.Database, forbiddenAttemptId);
        Assert.NotNull(forbiddenAttempt);
        Assert.Equal("failed", forbiddenAttempt!.State);
        Assert.Equal(HashnodeError.PublishForbidden, forbiddenAttempt.ErrorCode);

        // A Pro-gated write is a capability outcome, not a retry and never an upgrade: the
        // FORBIDDEN caused by the plan maps to the capability code.
        wire.GraphQLErrors("FORBIDDEN", "Writes are gated behind the Pro plan.");

        using var gated = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-plan-gated",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, gated.StatusCode);
        var gatedAccepted = JsonDocument.Parse(await gated.Content.ReadAsStringAsync());
        var gatedOperationId = gatedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var gatedAttemptId = gatedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var gatedOperation = await OperationRows.AwaitAsync(
            fixture.Database, gatedOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.CapabilityUnavailable, gatedOperation.ErrorCode);

        var gatedAttempt = await AttemptRows.ReadAsync(fixture.Database, gatedAttemptId);
        Assert.NotNull(gatedAttempt);
        Assert.Equal("failed", gatedAttempt!.State);
        Assert.Equal(HashnodeError.CapabilityUnavailable, gatedAttempt.ErrorCode);

        using var receipt = await owner.GetAsync($"/api/publications/{gatedAttemptId:D}");
        Assert.DoesNotContain(
            "Pro plan", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Boundary: a publication change after preview stales it.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_publication_change_after_preview_stales_the_confirmation()
    {
        await using var fixture = await ArrangeAsync("hn_001_stale");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        var monitor = new StubHashnodeOptionsMonitor(HashnodeOptions.Create(PublicationId));
        await using var host = PublisherHashnodeApiHost.StartWithMonitor(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, monitor, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The operator reconfigures the publication after the owner read the preview: the
        // confirmed hash describes another target configuration, so it authorizes no write.
        monitor.CurrentValue = HashnodeOptions.Create("hn-other-publication-02");

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-stale",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-001 Boundary: a redirect is refused without a second hop.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_redirect_is_refused_without_a_second_hop()
    {
        await using var fixture = await ArrangeAsync("hn_001_redirect");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.RedirectTo("https://collector.example/x");
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-001-redirect",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.RedirectRefused, operation.ErrorCode);

        // The redirect was refused, not followed: exactly one call, and it went to the pinned
        // host - the leased token never left Hashnode for the Location the provider named.
        var call = Assert.Single(wire.Calls);
        Assert.StartsWith("https://gql-beta.hashnode.com/", call.AbsoluteUri, StringComparison.Ordinal);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(HashnodeError.RedirectRefused, attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-002 Failure: a lost create answer becomes unknown, then stays
    // manual because this target cannot reconcile.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_lost_create_answer_becomes_unknown_then_stays_manual()
    {
        await using var fixture = await ArrangeAsync("hn_002_unknown");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.LoseCreateAnswer();
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-002-unknown",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // The bytes may have left and a create has no reliable idempotency: unknown, with no
        // remote id to ask about - and exactly one dispatch, never retried.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Single(wire.Calls);
        var dispatchCalls = wire.Calls.Count;

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("manual", JsonDocument.Parse(await receipt.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reconcileHint").GetString());

        // This target cannot reconcile: the receipt stays unknown with no further upstream
        // call, and the owner inspects the publication by hand.
        using var reconciled = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reconciled.StatusCode);
        Assert.Equal(
            "publisher_reconcile_unavailable",
            EnvelopeCode(await reconciled.Content.ReadAsStringAsync()));

        Assert.Equal(dispatchCalls, wire.Calls.Count);

        var reread = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(reread);
        Assert.Equal("unknown", reread!.State);

        // The operation row keeps its failure: reconciling - refused here - rewrites no history.
        var kept = await OperationRows.ReadAsync(fixture.Database, operationId);
        Assert.NotNull(kept);
        Assert.Equal("failed", kept!.Status);
        Assert.Equal("publication_unknown", kept.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-002 Failure: a lost publish answer keeps its draft id.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_lost_publish_answer_keeps_draft_id_then_stays_manual()
    {
        await using var fixture = await ArrangeAsync("hn_002_publish_lost");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.LosePublishAnswer();
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-002-publish-lost",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // The create of this run is known, so the unknown carries its draft id: the owner
        // knows which remote draft to look for by hand. Two calls left this process - the
        // create and the publish - and no third one ever will.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Equal(DraftId, attempt.RemoteId);
        Assert.Equal(2, wire.Calls.Count);
        var dispatchCalls = wire.Calls.Count;

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("manual", receiptBody.RootElement.GetProperty("reconcileHint").GetString());
        Assert.Equal(DraftId, receiptBody.RootElement.GetProperty("remoteId").GetString());

        using var reconciled = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reconciled.StatusCode);
        Assert.Equal(
            "publisher_reconcile_unavailable",
            EnvelopeCode(await reconciled.Content.ReadAsStringAsync()));

        Assert.Equal(dispatchCalls, wire.Calls.Count);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-002 Failure: a refused publish keeps a recoverable draft.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_refused_publish_keeps_recoverable_draft_receipt()
    {
        await using var fixture = await ArrangeAsync("hn_002_publish_refused");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        wire.RefusePublishWith((HttpStatusCode)422);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = HashnodePublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "hn-002-publish-refused",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(HashnodeError.InvalidPayload, operation.ErrorCode);

        // The publish mutation was refused after this run's create succeeded: the receipt is a
        // recoverable remote draft - the refusal code beside the created draft id - with no new
        // create call. Exactly the create and the publish left this process.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(HashnodeError.InvalidPayload, attempt.ErrorCode);
        Assert.Equal(DraftId, attempt.RemoteId);
        Assert.Equal(2, wire.Calls.Count);
    }

    // ==============================================================================================
    // PUBLISHER-HASHNODE-ADAPTER-002 Boundary: an identical resubmit returns the receipt.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_identical_resubmit_returns_receipt_without_second_call()
    {
        await using var fixture = await ArrangeAsync("hn_002_replay");
        var wire = ScriptedHashnodeHandler.Happy(DraftId, PostId, PostSlug, PostUrl);
        var options = HashnodeOptions.Create(PublicationId);
        await using var host = PublisherHashnodeApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = HashnodePublisher.TargetId,
            mode = "draft",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "hn-002-replay",
            confirm = true,
        };

        using var first = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstAccepted = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var operationId = firstAccepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = firstAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));
        var dispatchCalls = wire.Calls.Count;
        Assert.Equal(1, dispatchCalls);

        using var second = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(replay.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(attemptId, replay.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal("remote-draft", replay.RootElement.GetProperty("state").GetString());

        // The replay returned the stored receipt: no second mutation left this process.
        Assert.Equal(dispatchCalls, wire.Calls.Count);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // Registry and options: unconfigured demand-gated off, invalid configuration refused.
    // ==============================================================================================

    [Fact]
    public async Task Registry_unconfigured_hashnode_is_unknown_and_invalid_options_are_refused()
    {
        await using var fixture = await ArrangeAsync("hn_reg_off");

        // The production composition root with no explicit target: the slot is unclaimed, so the
        // preview answers unknown rather than reaching any transport.
        await using var production = PublisherHashnodeApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var productionOwner = await OwnerClient.CreateAsync(production);

        using var preview = await productionOwner.SendAsync(await productionOwner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = HashnodePublisher.TargetId, mode = "draft" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_unknown_target", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        // A missing id, an overlong id and characters outside the id alphabet are startup
        // refusals, not refused publishes.
        Assert.Throws<InvalidOperationException>(() =>
            HashnodeOptions.Create(null));
        Assert.Throws<InvalidOperationException>(() =>
            HashnodeOptions.Create("   "));
        Assert.Throws<InvalidOperationException>(() =>
            HashnodeOptions.Create("https://hashnode.com/pub"));
        Assert.Throws<InvalidOperationException>(() =>
            HashnodeOptions.Create("publication id"));
        Assert.Throws<InvalidOperationException>(() =>
            HashnodeOptions.Create(new string('a', HashnodeOptions.MaxPublicationIdLength + 1)));

        var configured = HashnodeOptions.Create(PublicationId);
        Assert.Equal(PublicationId, configured.ValidatedPublicationId);
        Assert.Contains($"hashnode publication:{PublicationId}", configured.TargetLine(), StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-ADAPTER-PANELS-001: a configured section binds through production registration.
    // ==============================================================================================

    /// <summary>
    /// The configured section must bind through the production registration path - the demand
    /// gate plus <c>Configure</c> - rather than only through the stub monitor this suite's
    /// other hosts use. Without a binder-visible constructor the target registers and then
    /// every preview against it 500s inside the options factory, which is a publishing
    /// dead-end no picker can route around.
    /// </summary>
    [Fact]
    public async Task Configured_section_registers_target_and_serves_preview()
    {
        await using var fixture = await ArrangeAsync("hn_configured_section");
        await using var host = PublisherHashnodeApiHost.StartConfigured(
            fixture.Database,
            new FixedTimeProvider(Clock),
            fixture.KeyFile.FilePath,
            PublicationId);
        using var owner = await OwnerClient.CreateAsync(host);

        using var listed = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var entry = body.RootElement
            .GetProperty("targets")
            .EnumerateArray()
            .Single(target => target.GetProperty("targetId").GetString() == HashnodePublisher.TargetId);
        Assert.False(entry.GetProperty("exportOnly").GetBoolean());
        Assert.True(entry.GetProperty("supportsDraft").GetBoolean());
        Assert.True(entry.GetProperty("supportsPublish").GetBoolean());
        Assert.False(entry.GetProperty("supportsReconcile").GetBoolean());

        // The preview renders against the bound target: CPU only, no wire, no 500.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, HashnodePublisher.TargetId, "draft");
        Assert.NotEmpty(preview.PreviewHash);
    }

    [Fact]
    public async Task Prepare_pictures_are_named_not_carried_and_preview_binds_publication()
    {
        var adapter = new HashnodePublisher(
            new StubHashnodeOptionsMonitor(HashnodeOptions.Create(PublicationId)),
            new HashnodeTransport(new ScriptedHashnodeHandler()));

        var pictureId = Guid.NewGuid();
        var snapshot = new ApprovedAssetSnapshot(
            Guid.NewGuid(),
            1,
            "Reproducible release pipeline",
            "# Body",
            new string('0', 64),
            1,
            [new ApprovedExportPicture(pictureId, "The pipeline digest view.", "image/png", [1, 2, 3])]);

        var preview = await ((IPublisherAdapter)adapter).PrepareAsync(
            snapshot,
            new PublisherTargetConfig(HashnodePublisher.TargetId));

        // Pictures are named in the warnings the owner confirms - never silently dropped - and
        // the media plan stays empty because the draft call carries no uploads.
        Assert.Equal(HashnodePublisher.TargetId, preview.TargetId);
        Assert.Empty(preview.MediaPlan);
        Assert.Equal(2, preview.Warnings.Count);
        Assert.Contains($"hashnode publication:{PublicationId}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("not carried", preview.Warnings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Transport_handler_pins_host_and_refuses_redirects()
    {
        using var handler = HashnodeTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(HashnodeTransport.Timeout, handler.ConnectTimeout);

        var adapter = new HashnodePublisher(
            new StubHashnodeOptionsMonitor(HashnodeOptions.Create(PublicationId)),
            new HashnodeTransport(new ScriptedHashnodeHandler()));

        var capabilities = ((IPublisherAdapter)adapter).Describe();

        Assert.Equal(HashnodePublisher.TargetId, ((IPublisherAdapter)adapter).TargetId);
        Assert.True(capabilities.SupportsDraft);
        Assert.True(capabilities.SupportsPublish);
        Assert.False(capabilities.SupportsReconcile);
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed draft credential, a real sealed publisher
    /// credential, a stored draft produced by a real generation job, and the approval it belongs to.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(string purpose)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;

        await using (var setup = GroundedMarkdownDraftsApiHost.StartProduction(database, clock, keyFile.FilePath))
        {
            using var setupOwner = await OwnerClient.CreateAsync(setup);

            projectId = await CreateProjectAsync(setupOwner, purpose);
            milestoneId = await CreateMilestoneAsync(setupOwner, projectId);

            using var evidence = await setupOwner.SendAsync(await setupOwner.Post(
                $"/api/milestones/{milestoneId:D}/evidence/manual",
                new { notes = EvidenceNote, expectedRevision = 0 },
                0));

            Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

            // The generated body is English, so the voice it is judged under must be too: the
            // default profile declares Chinese and would report language findings on every field,
            // which would refuse the approval this fixture needs.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "linkedin", overrides = new { } },
                0));

            Assert.True(
                voiced.StatusCode == HttpStatusCode.OK,
                $"voice answered {(int)voiced.StatusCode}: {await voiced.Content.ReadAsStringAsync()}");

            _ = await SaveCredentialAsync(setupOwner, ScriptedDraftProvider.Slot, DraftCredentialValue);
            _ = await SaveCredentialAsync(setupOwner, PublisherService.CredentialProvider, PublisherCredentialValue);
        }

        HeadView head;

        await using (var reader = GroundedMarkdownDraftsApiHost.StartProduction(
            database, clock, keyFile.FilePath))
        {
            using var readerOwner = await OwnerClient.CreateAsync(reader);
            head = await ReadHeadAsync(readerOwner, milestoneId);
        }

        var summary = SummaryJson(head.ManualItemId);
        var scripted = ScriptedDraftProvider.With(summary, BodyFor(summary));

        await using (var drafts = GroundedMarkdownDraftsApiHost.Start(database, clock, keyFile.FilePath, scripted))
        {
            using var draftsOwner = await OwnerClient.CreateAsync(drafts);

            var credentialId = await CredentialIdAsync(draftsOwner, ScriptedDraftProvider.Slot);
            var preview = await CreatePreviewAsync(
                draftsOwner, milestoneId, credentialId, head.Revision, head.ManualItemId);

            using var requested = await draftsOwner.SendAsync(await draftsOwner.Post(
                $"/api/milestones/{milestoneId:D}/drafts",
                new
                {
                    previewId = preview.PreviewId.ToString("D"),
                    consentHash = preview.ConsentHash,
                    providerId = ScriptedDraftProvider.Slot,
                    model = ScriptedDraftProvider.KnownModel,
                    evidenceRevision = preview.EvidenceRevision,
                    voiceRevision = preview.VoiceRevision,
                    idempotencyKey = $"hn-{purpose}",
                }));

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
            var draftOperationId = accepted.RootElement.GetProperty("operationId").GetGuid();

            await drafts.DispatchAsync();
            await OperationRows.AwaitAsync(database, draftOperationId, "succeeded", TimeSpan.FromSeconds(5));
        }

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        var fixture = new Fixture(
            database,
            keyFile,
            projectId,
            milestoneId,
            asset!.Id,
            head.Revision);

        await using var approver = GroundedMarkdownDraftsApiHost.StartProduction(
            database, clock, keyFile.FilePath);
        using var approverOwner = await OwnerClient.CreateAsync(approver);
        await ApproveAsync(approverOwner, fixture.StoryAssetId, 1, fixture.BundleRevision);

        return fixture;
    }

    private static async Task<PreviewView> PreviewAsync(
        OwnerClient owner,
        Guid assetId,
        string targetId,
        string mode)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/assets/{assetId:D}/publish-preview",
            new { targetId, mode }));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"preview answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new PreviewView(
            body.RootElement.GetProperty("previewHash").GetString()!,
            body.RootElement.GetProperty("bodyHash").GetString()!,
            body.RootElement.GetProperty("contentRevision").GetInt64(),
            [.. body.RootElement.GetProperty("mediaPlan").EnumerateArray().Select(entry => entry.GetGuid())],
            body.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(entry => entry.GetString()!).ToList());
    }

    private static async Task<Guid> ApprovalIdAsync(OwnerClient owner, Guid assetId)
    {
        using var response = await owner.GetAsync($"/api/assets/{assetId:D}/review");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("approved", body.RootElement.GetProperty("reviewStatus").GetString());

        return body.RootElement.GetProperty("approval").GetProperty("approvalId").GetGuid();
    }

    private static async Task ApproveAsync(
        OwnerClient owner, Guid assetId, long contentRevision, long evidenceRevision)
    {
        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/assets/{assetId:D}/approve",
            new
            {
                contentRevision,
                evidenceRevision,
                voiceFindingsAcknowledged = false,
            }));

        Assert.True(
            approved.StatusCode == HttpStatusCode.OK,
            $"approve {contentRevision}@{evidenceRevision} answered {(int)approved.StatusCode}: "
            + await approved.Content.ReadAsStringAsync());
    }

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

        await using var host = GroundedMarkdownDraftsApiHost.StartProduction(
            database,
            new FixedTimeProvider(Clock),
            keyFile: null);

        using var scope = host.Services.CreateScope();
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

        return database;
    }

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string purpose)
    {
        using var response = await owner.SendAsync(await owner.Post(
            "/api/projects",
            new { name = $"{purpose} project", description = $"{purpose} record." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> CreateMilestoneAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title = "Reproducible release pipeline",
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = "Record of a pipeline that reproduces from a clean checkout.",
                knownLimitations = new[] { KnownLimitation },
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> SaveCredentialAsync(OwnerClient owner, string provider, string value)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{provider}",
            new { value },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        Assert.DoesNotContain(value, raw, StringComparison.Ordinal);

        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CredentialIdAsync(OwnerClient owner, string provider)
    {
        using var response = await owner.GetAsync("/api/settings/credentials");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var entry in JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items").EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("provider").GetString(), provider, StringComparison.Ordinal))
            {
                return entry.GetProperty("id").GetGuid();
            }
        }

        throw new InvalidOperationException($"No credential is configured for provider {provider}.");
    }

    private static async Task<HeadView> ReadHeadAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bundles = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var head = bundles.MaxBy(bundle => bundle.GetProperty("revision").GetInt64())!;

        var manual = head.GetProperty("items").EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "manual");

        return new HeadView(
            milestoneId,
            head.GetProperty("id").GetGuid(),
            head.GetProperty("revision").GetInt64(),
            Guid.Parse(manual.GetProperty("id").GetString()!));
    }

    private static async Task<PreviewBody> CreatePreviewAsync(
        OwnerClient owner,
        Guid milestoneId,
        Guid credentialId,
        long evidenceRevision,
        Guid evidenceId)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/generation-preview",
            new
            {
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                credentialRef = credentialId.ToString("D"),
                evidenceRevision,
                selectedEvidenceIds = new[] { evidenceId.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new PreviewBody(
            body.RootElement.GetProperty("previewId").GetGuid(),
            body.RootElement.GetProperty("consentHash").GetString()!,
            body.RootElement.GetProperty("evidenceRevision").GetInt64(),
            body.RootElement.GetProperty("voiceRevision").GetInt64());
    }

    private static string EnvelopeCode(string raw) =>
        JsonDocument.Parse(raw).RootElement.GetProperty("code").GetString()!;

    /// <summary>
    /// A valid structured summary citing the real evidence id. Every claim's text is free of
    /// digits and of every default forbidden term, because the numeral rule and the voice rules
    /// refuse what they cannot trace - and a fixture that tripped them would be testing the
    /// wrong thing on the success path.
    /// </summary>
    private static string SummaryJson(Guid? evidenceId)
    {
        var id = evidenceId is { } value ? value.ToString("d") : null;

        return $$"""
          {
            "milestoneTitle": "Reproducible release pipeline",
            "summary": "The pipeline now reproduces from a clean checkout.",
            "changes": [{"text": "The pipeline runs from a clean checkout.", "evidenceIds": ["{{id}}"]}],
            "problemsSolved": [{"text": "A release could not be rebuilt from its source.", "evidenceIds": ["{{id}}"]}],
            "knownLimitations": [{"text": {{Quote(KnownLimitation)}}, "evidenceIds": ["{{id}}"]}],
            "nextSteps": [{"text": "Cover the other platform.", "evidenceIds": ["{{id}}"]}]
          }
          """;
    }

    /// <summary>
    /// A body that satisfies every local citation rule, built from the claim map the summary
    /// produces rather than hand-written, so the footnote ids are the ones the package assigned.
    /// </summary>
    private static string BodyFor(string summaryJson)
    {
        var summary = GroundedSummarySchema.Read(summaryJson);
        var claims = ClaimMap.Build(summary);
        var builder = new StringBuilder();

        builder.Append("# ").Append(summary.MilestoneTitle).Append("\n\n");
        builder.Append(summary.Summary).Append("[^").Append(claims[0].Id).Append("]\n");

        AppendSection(builder, "What changed", GroundedSummarySection.Changes, summary, claims);
        AppendSection(builder, "Problems solved", GroundedSummarySection.ProblemsSolved, summary, claims);
        AppendSection(builder, "Known limitations", GroundedSummarySection.KnownLimitations, summary, claims);
        AppendSection(builder, "Next steps", GroundedSummarySection.NextSteps, summary, claims);

        builder.Append('\n');
        foreach (var claim in claims)
        {
            builder.Append("[^").Append(claim.Id).Append("]: ").Append(claim.Text).Append('\n');
        }

        return JsonSerializer.Serialize(new { bodyMarkdown = builder.ToString() });
    }

    private static void AppendSection(
        StringBuilder builder,
        string heading,
        GroundedSummarySection section,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims)
    {
        var items = summary.ClaimsIn(section);
        if (items.Count == 0)
        {
            return;
        }

        builder.Append("\n## ").Append(heading).Append('\n');

        for (var index = 0; index < items.Count; index++)
        {
            var claim = claims.Single(candidate => candidate.Section == section && candidate.Index == index);
            builder.Append("- ").Append(items[index].Text).Append("[^").Append(claim.Id).Append("]\n");
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private sealed record HeadView(Guid MilestoneId, Guid BundleId, long Revision, Guid ManualItemId);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    private sealed record PreviewView(
        string PreviewHash,
        string BodyHash,
        long ContentRevision,
        IReadOnlyList<Guid> MediaPlan,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared server.
    /// </summary>
    private sealed record Fixture(
        string Database,
        OperatorKeyFile KeyFile,
        Guid ProjectId,
        Guid MilestoneId,
        Guid StoryAssetId,
        long BundleRevision) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            KeyFile.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A reloadable target holder for scenarios: the production equivalent is the bound
/// configuration section, re-read on every adapter call.
/// </summary>
public sealed class StubHashnodeOptionsMonitor(HashnodeOptions current) : IOptionsMonitor<HashnodeOptions>
{
    public HashnodeOptions CurrentValue { get; set; } = current;

    public HashnodeOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<HashnodeOptions, string?> listener) =>
        NullChangeSubscription.Instance;

    private sealed class NullChangeSubscription : IDisposable
    {
        public static readonly NullChangeSubscription Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The scripted far end of the Hashnode wire: records every call - method, path, body, the
/// bearer header and the Accept header - and answers from the scenario's script. The only
/// substitution in this suite, and it stands in for Hashnode itself rather than for any
/// Chronicleaf code.
/// </summary>
public sealed class ScriptedHashnodeHandler : HttpMessageHandler
{
    private readonly List<RecordedCall> calls = new();
    private HttpStatusCode? refusedWith;
    private HttpStatusCode? publishRefusedWith;
    private (string Code, string Message)? createErrors;
    private (string Code, string Message)? publishErrors;
    private string? redirectLocation;
    private bool loseCreateAnswer;
    private bool losePublishAnswer;

    /// <summary>Every call the adapter made, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls => calls;

    /// <summary>One answered request, as the scenario reads it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The absolute path: always <c>/</c> for this GraphQL transport.</param>
    /// <param name="AbsoluteUri">The full request URI: always the pinned host in these scenarios.</param>
    /// <param name="Body">The request body: the fixed query with its variables.</param>
    /// <param name="AuthScheme">Always <c>Bearer</c>: the only scheme this provider reads.</param>
    /// <param name="AuthParameter">The token value, held in memory for this scenario only.</param>
    /// <param name="Accept">The Accept header the adapter sent.</param>
    public sealed record RecordedCall(
        string Method,
        string Path,
        string AbsoluteUri,
        string? Body,
        string? AuthScheme,
        string? AuthParameter,
        string? Accept);

    /// <summary>Answers the happy draft create and the happy draft publish.</summary>
    public static ScriptedHashnodeHandler Happy(string draftId, string postId, string postSlug, string postUrl) =>
        new() { DraftId = draftId, PostId = postId, PostSlug = postSlug, PostUrl = postUrl };

    public string DraftId { get; private set; } = "draft-hn-000";

    public string PostId { get; private set; } = "post-hn-000";

    public string PostSlug { get; private set; } = "untitled-abc";

    public string PostUrl { get; private set; } = "https://testowner.hashnode.dev/untitled-abc";

    /// <summary>Refuses every call with the given status.</summary>
    public void RefuseWith(HttpStatusCode status) => refusedWith = status;

    /// <summary>Refuses only the publish mutation with the given status; the create stays happy.</summary>
    public void RefusePublishWith(HttpStatusCode status) => publishRefusedWith = status;

    /// <summary>Answers the create with a GraphQL errors array on an HTTP 200.</summary>
    public void GraphQLErrors(string code, string message) => createErrors = (code, message);

    /// <summary>Answers only the publish mutation with a GraphQL errors array on an HTTP 200.</summary>
    public void GraphQLPublishErrors(string code, string message) => publishErrors = (code, message);

    /// <summary>Answers the call with a redirect to the given location, which must be refused.</summary>
    public void RedirectTo(string location) => redirectLocation = location;

    /// <summary>Loses the create answer: the write may have happened before it was lost.</summary>
    public void LoseCreateAnswer() => loseCreateAnswer = true;

    /// <summary>Loses the publish answer after the create succeeded: the draft id is known.</summary>
    public void LosePublishAnswer() => losePublishAnswer = true;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var isPublish = body?.Contains("PublishDraft", StringComparison.Ordinal) is true;

        var call = new RecordedCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.AbsoluteUri,
            body,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.Accept.ToString());

        calls.Add(call);

        if (!isPublish && loseCreateAnswer)
        {
            throw new TaskCanceledException("Simulated wire timeout after the create.");
        }

        if (isPublish && losePublishAnswer)
        {
            throw new TaskCanceledException("Simulated wire timeout after the publish.");
        }

        if (redirectLocation is not null)
        {
            var redirect = new HttpResponseMessage((HttpStatusCode)307);
            redirect.Headers.Location = new Uri(redirectLocation, UriKind.Absolute);
            return redirect;
        }

        if (!isPublish && createErrors is { } errors)
        {
            return Errors(errors.Code, errors.Message);
        }

        if (isPublish && publishErrors is { } publishError)
        {
            return Errors(publishError.Code, publishError.Message);
        }

        if (refusedWith is { } refusal)
        {
            return Json(refusal, new { error = "Scripted refusal." });
        }

        if (isPublish && publishRefusedWith is { } publishRefusal)
        {
            return Json(publishRefusal, new { error = "Scripted refusal." });
        }

        if (isPublish)
        {
            return Json(
                HttpStatusCode.OK,
                new
                {
                    data = new
                    {
                        publishDraft = new
                        {
                            post = new { id = PostId, slug = PostSlug, url = PostUrl },
                        },
                    },
                });
        }

        return Json(
            HttpStatusCode.OK,
            new
            {
                data = new
                {
                    createDraft = new
                    {
                        draft = new { id = DraftId },
                    },
                },
            });
    }

    private static HttpResponseMessage Errors(string code, string message) => Json(
        HttpStatusCode.OK,
        new
        {
            errors = new[]
            {
                new { message, extensions = new { code } },
            },
        });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with the shipped Hashnode
/// adapter registered over a scripted wire.
/// </summary>
public sealed class PublisherHashnodeApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly IOptionsMonitor<HashnodeOptions>? _targets;
    private readonly ScriptedHashnodeHandler? _wire;
    private readonly IReadOnlyDictionary<string, string?>? _configured;
    private readonly LogCapture _logs;

    private PublisherHashnodeApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        IOptionsMonitor<HashnodeOptions>? targets,
        ScriptedHashnodeHandler? wire,
        LogCapture logs,
        IReadOnlyDictionary<string, string?>? configured = null)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _targets = targets;
        _wire = wire;
        _logs = logs;
        _configured = configured;
    }

    /// <summary>Starts a host with the shipped adapter over the scenario's scripted wire.</summary>
    public static PublisherHashnodeApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        HashnodeOptions options,
        ScriptedHashnodeHandler wire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherHashnodeApiHost(
            connectionString, clock, keyFile, new StubHashnodeOptionsMonitor(options), wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host with a monitor the scenario may reconfigure between calls: the boundary
    /// between a preview and a publish the operator moved.
    /// </summary>
    public static PublisherHashnodeApiHost StartWithMonitor(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        StubHashnodeOptionsMonitor monitor,
        ScriptedHashnodeHandler wire)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherHashnodeApiHost(
            connectionString, clock, keyFile, monitor, wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host through the production registration path with an explicit target
    /// configured: the demand gate reads the section and <c>Configure</c> binds it, exactly
    /// as a deployment configures it. No scripted wire is installed, so nothing here may
    /// reach a remote side - the scenarios this host serves stop at acceptance.
    /// </summary>
    public static PublisherHashnodeApiHost StartConfigured(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        string publicationId) =>
        new(
            connectionString,
            clock,
            keyFile,
            targets: null,
            wire: null,
            new LogCapture(),
            configured: new Dictionary<string, string?>
            {
                [$"{HashnodeOptions.SectionName}:{nameof(HashnodeOptions.PublicationId)}"] = publicationId,
            });

    /// <summary>
    /// Starts a host with the production composition root and no explicit target: demand-gated
    /// off, so the slot answers unknown.
    /// </summary>
    public static PublisherHashnodeApiHost StartProduction(
        string connectionString,
        TimeProvider clock,
        string? keyFile) =>
        new(connectionString, clock, keyFile, targets: null, wire: null, new LogCapture());

    /// <summary>Every log line this host has written, so a scenario can prove what did not reach it.</summary>
    public LogCapture Logs => _logs;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a publish can be staged
        // either side of the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // A poll interval, not a behaviour: the queue, the workers, the claiming, the lease and
        // the conditional commit are all production's.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        if (_configured is not null)
        {
            foreach (var setting in _configured)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_targets is not null && _wire is not null)
            {
                // The shipped adapter, over the scenario's scripted far end of the wire. The
                // registration shape is production's own - one adapter for the hashnode slot
                // beside its descriptor - with only Hashnode itself substituted, and the
                // transport holding its own client so no bare-handler registration can rewire
                // another transport.
                var targets = _targets;
                var wire = _wire;
                services.AddSingleton<IPublisherAdapter>(provider => new HashnodePublisher(
                    targets,
                    new HashnodeTransport(wire),
                    provider.GetRequiredService<ILogger<HashnodePublisher>>()));
                services.AddSingleton(new PublisherTargetDescriptor(
                    HashnodePublisher.TargetId,
                    SupportsDraft: true,
                    SupportsPublish: true,
                    SupportsReconcile: false,
                    ExportOnly: false));
            }
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass, the loop's own body called by hand.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return results;
    }
}
