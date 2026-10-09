using System.Net;
using System.Security.Cryptography;
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
using Chronicleaf.Publishers.Ghost;
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
/// PUBLISHER-GHOST-ADAPTER-001 and PUBLISHER-GHOST-ADAPTER-002 against a real disposable
/// PostgreSQL, the real <c>Chronicleaf.Api</c> host, the real operation queue and the real
/// asset, review, evidence, vault and operation contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the asset
/// store, the approvals table, the routes, the access boundary, the attempt table, the
/// operation queue with its real worker, the credential vault over a real operator key file,
/// the shared HTML export's sanitizing renderer, and the shipped Ghost adapter creating one
/// Admin post per confirmed publish - status draft or published by the authorized mode -
/// through the shipped transport. The approved story every scenario publishes is produced by
/// a real generation job through the same scripted provider the drafts suite drives. The
/// publisher credential is genuinely sealed by the vault and genuinely leased per call, and
/// the scripted wire asserts the Ghost-scheme token it receives was signed from the saved
/// secret - which is what makes "the credential stays a vault reference" a real assertion
/// rather than a vacuous one.
/// </para>
/// <para>
/// <b>What is scripted.</b> Only the far end of the wire: a <see cref="ScriptedGhostHandler"/>
/// answers the exact Admin posts exchange - one <c>POST /ghost/api/admin/posts/?source=html</c>
/// per confirmed publish - while every stage above the socket is shipped code. No scenario
/// reaches a real Ghost endpoint: the handler is the boundary, and a scenario that needs an
/// upstream refusal, a refused redirect or a lost answer scripts exactly that. No claim here
/// is evidence that Ghost works or that any post exists remotely.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A receipt is read out of the raw
/// <c>publication_attempts</c> table, not only from the receipt route, and an operation is
/// read out of the raw <c>operations</c> table. The wire is read out of the scripted handler's
/// recorded calls: method, path, query, body, the Ghost-scheme header and the Accept-Version.
/// Nothing upstream is inferred from a row this system wrote.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PublisherGhostAdapterTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("c5f9a3d4-6b7e-4c0a-9f8e-0a1b2c3d4e55");

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

    private const string GhostKeyId = "aaaaaaaaaaaaaaaaaaaaaaaa";

    private const string GhostKeySecretHex =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string PublisherCredentialValue = GhostKeyId + ":" + GhostKeySecretHex;

    private const string Origin = "https://ghost.test";

    private const string ApiVersion = "v5.0";

    private const string Tags = "release,pipeline";

    private const string PostId = "670f00000000000000000001";

    private const string PostSlug = "reproducible-release-pipeline";

    private const string PostUrl = "https://ghost.test/reproducible-release-pipeline/";

    private const string PostUpdatedAt = "2026-10-07T09:00:00.000Z";

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Success: a confirmed draft preview creates one post as draft.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_draft_preview_creates_post_as_draft_with_signed_token()
    {
        await using var fixture = await ArrangeAsync("gh_001_draft");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");

        // The preview is pure: no post was created and no token was leased for it.
        Assert.Empty(wire.Calls);
        Assert.Empty(preview.MediaPlan);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-draft",
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

        // Exactly one upstream call: the Admin posts create itself. No second hop, no update -
        // draft mode is one create with the draft status.
        var call = Assert.Single(wire.Calls);

        Assert.Equal("POST", call.Method);
        Assert.Equal("/ghost/api/admin/posts/", call.Path);
        Assert.Equal("?source=html", call.Query);
        Assert.StartsWith("https://ghost.test/", call.AbsoluteUri, StringComparison.Ordinal);

        // The leased key travels as one signed Ghost-scheme token - never a query parameter,
        // never the raw id:secret - and it was signed from the vault's secret, not from a
        // value in the request.
        Assert.Equal("Ghost", call.AuthScheme);
        Assert.NotNull(call.AuthParameter);
        Assert.NotEqual(PublisherCredentialValue, call.AuthParameter);
        Assert.DoesNotContain(PublisherCredentialValue, call.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(GhostKeySecretHex, call.Body ?? string.Empty, StringComparison.Ordinal);
        AssertToken(call.AuthParameter);

        // The configured API version travels as Accept-Version, verbatim.
        Assert.Equal(ApiVersion, call.AcceptVersion);
        Assert.Contains("application/json", call.Accept, StringComparison.Ordinal);

        // The posts shape is fixed: one entry carrying the approved title, the rendered HTML,
        // the draft status and the operator's tags - values only, no other Admin endpoint.
        var envelope = JsonDocument.Parse(call.Body!);
        var post = Assert.Single(envelope.RootElement.GetProperty("posts").EnumerateArray());
        Assert.Equal("Reproducible release pipeline", post.GetProperty("title").GetString());
        Assert.Equal(GhostPublisher.DraftStatus, post.GetProperty("status").GetString());

        var tags = post.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("name").GetString()!).ToList();
        Assert.Equal(new[] { "release", "pipeline" }, tags);

        var html = post.GetProperty("html").GetString();
        Assert.NotNull(html);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        // The sent body is the shared export's sanitized article: the approved text rendered
        // to HTML, carrying no picture section and no relative image path.
        Assert.Contains("<h1>", html, StringComparison.Ordinal);
        Assert.Contains("clean checkout", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved pictures", html, StringComparison.Ordinal);
        Assert.DoesNotContain("images/", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[^", html, StringComparison.Ordinal);

        // The receipt records the remote post id, the provider-named URL and the exact content
        // hash: the id the provider named, and the body hash of the bytes that were authorized.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("remote-draft", attempt!.State);
        Assert.Equal("draft", attempt.Mode);
        Assert.Equal(PostId, attempt.RemoteId);
        Assert.Equal(PostUrl, attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
        Assert.Equal(stored.BodyHash, attempt.BodyHash);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("remote-draft", receiptBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(PostId, receiptBody.RootElement.GetProperty("remoteId").GetString());
        Assert.Equal(PostUrl, receiptBody.RootElement.GetProperty("remoteUrl").GetString());
        Assert.Equal(stored.BodyHash, receiptBody.RootElement.GetProperty("bodyHash").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            receiptBody.RootElement.GetProperty("reconcileHint").ValueKind);

        Assert.DoesNotContain(
            PublisherCredentialValue, await published.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            GhostKeySecretHex, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Success: a confirmed publish creates one published post.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_publish_creates_published_post()
    {
        await using var fixture = await ArrangeAsync("gh_001_publish");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "publish");
        Assert.Contains($"ghost origin:{Origin}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains($"version:{ApiVersion}", preview.Warnings[0], StringComparison.Ordinal);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-publish",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        // One call: the same create, now with the published status. No update verb exists on
        // this transport, so a publish can never rewrite a remote post.
        var call = Assert.Single(wire.Calls);

        Assert.Equal("POST", call.Method);
        Assert.Equal("/ghost/api/admin/posts/", call.Path);
        Assert.Equal("?source=html", call.Query);
        Assert.StartsWith("https://ghost.test/", call.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Ghost", call.AuthScheme);
        Assert.NotEqual(PublisherCredentialValue, call.AuthParameter);
        AssertToken(call.AuthParameter!);

        var envelope = JsonDocument.Parse(call.Body!);
        var post = Assert.Single(envelope.RootElement.GetProperty("posts").EnumerateArray());
        Assert.Equal(GhostPublisher.PublishedStatus, post.GetProperty("status").GetString());
        Assert.Equal("Reproducible release pipeline", post.GetProperty("title").GetString());

        // The upstream URL is derived from the post's url, never guessed from a slug or a
        // title: it is exactly the address the provider named.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
        Assert.Equal("publish", attempt.Mode);
        Assert.Equal(PostId, attempt.RemoteId);
        Assert.Equal(PostUrl, attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Failure: a refused key fails without exposure.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_refused_key_fails_without_exposing_secret_or_moving_approval()
    {
        await using var fixture = await ArrangeAsync("gh_001_unauthorized");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        wire.RefuseWith(HttpStatusCode.Unauthorized);
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-unauthorized",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GhostError.CredentialRejected, operation.ErrorCode);

        // The provider's verdict arrived on the one create call: no second hop, no retry.
        Assert.Single(wire.Calls);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GhostError.CredentialRejected, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Null(attempt.RemoteUrl);

        // No secret in the stored rows and no secret in the served responses - and no provider
        // body echoed anywhere either, because error bodies are disposed unread.
        Assert.DoesNotContain(PublisherCredentialValue, attempt.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(GhostKeySecretHex, attempt.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ErrorCode ?? string.Empty, StringComparison.Ordinal);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            GhostKeySecretHex, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Scripted refusal", await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The source approval did not move: the refused dispatch authorizes no edit.
        using var review = await owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await review.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reviewStatus").GetString());
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Failure: a rejected body and a conflict map to redacted codes.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_rejected_body_and_conflict_map_to_redacted_codes()
    {
        await using var fixture = await ArrangeAsync("gh_001_refusals");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        wire.RefuseWith((HttpStatusCode)422);
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The provider refused the content itself: a redacted code, no remote id, no retry.
        using var rejected = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-invalid",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, rejected.StatusCode);
        var rejectedAccepted = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        var rejectedOperationId = rejectedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var rejectedAttemptId = rejectedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var rejectedOperation = await OperationRows.AwaitAsync(
            fixture.Database, rejectedOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GhostError.InvalidPayload, rejectedOperation.ErrorCode);

        var rejectedAttempt = await AttemptRows.ReadAsync(fixture.Database, rejectedAttemptId);
        Assert.NotNull(rejectedAttempt);
        Assert.Equal("failed", rejectedAttempt!.State);
        Assert.Equal(GhostError.InvalidPayload, rejectedAttempt.ErrorCode);
        Assert.Null(rejectedAttempt.RemoteId);

        using var rejectedReceipt = await owner.GetAsync($"/api/publications/{rejectedAttemptId:D}");
        Assert.DoesNotContain(
            "Scripted refusal", await rejectedReceipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A conflicting create collides rather than duplicating: the conflict code, still with
        // no remote id and no second create of the same post.
        wire.RefuseWith(HttpStatusCode.Conflict);

        using var conflicted = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-conflict",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, conflicted.StatusCode);
        var conflictedAccepted = JsonDocument.Parse(await conflicted.Content.ReadAsStringAsync());
        var conflictedOperationId = conflictedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var conflictedAttemptId = conflictedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var conflictedOperation = await OperationRows.AwaitAsync(
            fixture.Database, conflictedOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GhostError.UpdateConflict, conflictedOperation.ErrorCode);

        var conflictedAttempt = await AttemptRows.ReadAsync(fixture.Database, conflictedAttemptId);
        Assert.NotNull(conflictedAttempt);
        Assert.Equal("failed", conflictedAttempt!.State);
        Assert.Equal(GhostError.UpdateConflict, conflictedAttempt.ErrorCode);
        Assert.Null(conflictedAttempt.RemoteId);
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Boundary: an origin change after preview stales it.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_origin_change_after_preview_stales_the_confirmation()
    {
        await using var fixture = await ArrangeAsync("gh_001_stale");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        var monitor = new StubGhostOptionsMonitor(GhostOptions.Create(Origin, ApiVersion, Tags));
        await using var host = PublisherGhostApiHost.StartWithMonitor(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, monitor, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The operator reconfigures the origin after the owner read the preview: the confirmed
        // hash describes another target configuration, so it authorizes no write.
        monitor.CurrentValue = GhostOptions.Create("https://other.test", ApiVersion, Tags);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-stale",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Boundary: a redirect is refused without a second hop.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_redirect_is_refused_without_a_second_hop()
    {
        await using var fixture = await ArrangeAsync("gh_001_redirect");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        wire.RedirectTo("https://collector.example/x");
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-redirect",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GhostError.RedirectRefused, operation.ErrorCode);

        // The redirect was refused, not followed: exactly one call, and it went to the
        // operator's origin - the leased token never left Ghost for the Location the provider
        // named.
        var call = Assert.Single(wire.Calls);
        Assert.StartsWith("https://ghost.test/", call.AbsoluteUri, StringComparison.Ordinal);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GhostError.RedirectRefused, attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-001 Boundary: an unsupported API major is refused before any byte.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_unsupported_api_major_is_refused_without_wire()
    {
        await using var fixture = await ArrangeAsync("gh_001_version");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        var options = GhostOptions.Create(Origin, "v6.0", Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        // The preview still renders: it binds the version the owner is looking at, so fixing
        // the operator's target afterwards stales this confirmation.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        Assert.Contains("version:v6.0", preview.Warnings[0], StringComparison.Ordinal);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-001-version",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GhostError.IncompatibleTarget, operation.ErrorCode);

        // Refused before any byte left: no token was signed and no call was made.
        Assert.Empty(wire.Calls);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GhostError.IncompatibleTarget, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
    }

    // ==============================================================================================
    // PUBLISHER-GHOST-ADAPTER-002 Failure: a lost create answer becomes unknown, then stays
    // manual because this target cannot reconcile.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_lost_create_answer_becomes_unknown_then_stays_manual()
    {
        await using var fixture = await ArrangeAsync("gh_002_unknown");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        wire.LoseCreateAnswer();
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GhostPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gh-002-unknown",
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
    // PUBLISHER-GHOST-ADAPTER-002 Boundary: an identical resubmit returns the receipt.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_identical_resubmit_returns_receipt_without_second_call()
    {
        await using var fixture = await ArrangeAsync("gh_002_replay");
        var wire = ScriptedGhostHandler.Happy(PostId, PostSlug, PostUrl, PostUpdatedAt);
        var options = GhostOptions.Create(Origin, ApiVersion, Tags);
        await using var host = PublisherGhostApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = GhostPublisher.TargetId,
            mode = "draft",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "gh-002-replay",
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

        // The replay returned the stored receipt: no second create left this process.
        Assert.Equal(dispatchCalls, wire.Calls.Count);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // Registry and options: unconfigured demand-gated off, invalid configuration refused.
    // ==============================================================================================

    [Fact]
    public async Task Registry_unconfigured_ghost_is_unknown_and_invalid_options_are_refused()
    {
        await using var fixture = await ArrangeAsync("gh_reg_off");

        // The production composition root with no explicit target: the slot is unclaimed, so the
        // preview answers unknown rather than reaching any transport.
        await using var production = PublisherGhostApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var productionOwner = await OwnerClient.CreateAsync(production);

        using var preview = await productionOwner.SendAsync(await productionOwner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = GhostPublisher.TargetId, mode = "draft" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_unknown_target", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        // A missing origin, a non-https origin, credentials inside the origin and a version
        // outside the v{major}.{minor} shape are startup refusals, not refused publishes.
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(null, ApiVersion, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create("   ", ApiVersion, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create("http://ghost.test", ApiVersion, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create("ghost.test", ApiVersion, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create("https://deploy@ghost.test", ApiVersion, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, null, null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, "5.0", null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, "v5", null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, "latest", null));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, ApiVersion, "release,,pipeline"));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(Origin, ApiVersion, new string('t', GhostOptions.MaxTagLength + 1)));
        Assert.Throws<InvalidOperationException>(() =>
            GhostOptions.Create(
                Origin, ApiVersion, string.Join(",", Enumerable.Repeat("tag", GhostOptions.MaxTags + 1))));

        var configured = GhostOptions.Create(Origin, ApiVersion, Tags);
        Assert.Equal(new Uri(Origin), configured.ValidatedOrigin);
        Assert.Equal(ApiVersion, configured.ValidatedApiVersion);
        Assert.Equal(new[] { "release", "pipeline" }, configured.ParsedTags);
        Assert.True(GhostOptions.IsSupportedVersion(ApiVersion));
        Assert.True(GhostOptions.IsSupportedVersion("v5.99"));
        Assert.False(GhostOptions.IsSupportedVersion("v6.0"));
        Assert.False(GhostOptions.IsSupportedVersion(null));
        Assert.Contains($"ghost origin:{Origin}", configured.TargetLine(42), StringComparison.Ordinal);
        Assert.Contains($"version:{ApiVersion}", configured.TargetLine(42), StringComparison.Ordinal);

        var untagged = GhostOptions.Create(Origin, ApiVersion, null);
        Assert.Empty(untagged.ParsedTags);
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
        await using var fixture = await ArrangeAsync("gh_configured_section");
        await using var host = PublisherGhostApiHost.StartConfigured(
            fixture.Database,
            new FixedTimeProvider(Clock),
            fixture.KeyFile.FilePath,
            Origin,
            ApiVersion,
            Tags);
        using var owner = await OwnerClient.CreateAsync(host);

        using var listed = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var entry = body.RootElement
            .GetProperty("targets")
            .EnumerateArray()
            .Single(target => target.GetProperty("targetId").GetString() == GhostPublisher.TargetId);
        Assert.False(entry.GetProperty("exportOnly").GetBoolean());
        Assert.True(entry.GetProperty("supportsDraft").GetBoolean());
        Assert.True(entry.GetProperty("supportsPublish").GetBoolean());
        Assert.False(entry.GetProperty("supportsReconcile").GetBoolean());

        // The preview renders against the bound target: CPU only, no wire, no 500.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GhostPublisher.TargetId, "draft");
        Assert.NotEmpty(preview.PreviewHash);
    }

    [Fact]
    public async Task Prepare_pictures_are_named_not_carried_and_preview_binds_origin_version_bytes()
    {
        var adapter = new GhostPublisher(
            new StubGhostOptionsMonitor(GhostOptions.Create(Origin, ApiVersion, Tags)),
            new GhostTransport(new ScriptedGhostHandler(), new FixedTimeProvider(Clock)),
            new HtmlExportAdapter());

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
            new PublisherTargetConfig(GhostPublisher.TargetId));

        // Pictures are named in the warnings the owner confirms - never silently dropped - and
        // the media plan stays empty because the post create carries no uploads.
        Assert.Equal(GhostPublisher.TargetId, preview.TargetId);
        Assert.Empty(preview.MediaPlan);
        Assert.Equal(2, preview.Warnings.Count);
        Assert.Contains($"ghost origin:{Origin}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains($"version:{ApiVersion}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("not carried", preview.Warnings[1], StringComparison.Ordinal);

        // The same bytes preview twice: the byte count the target line binds is stable.
        var reread = await ((IPublisherAdapter)adapter).PrepareAsync(
            snapshot,
            new PublisherTargetConfig(GhostPublisher.TargetId));
        Assert.Equal(preview.Warnings[0], reread.Warnings[0]);
    }

    [Fact]
    public void Transport_handler_pins_host_and_adapter_describes_draft_and_publish()
    {
        using var handler = GhostTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(GhostTransport.Timeout, handler.ConnectTimeout);

        var adapter = new GhostPublisher(
            new StubGhostOptionsMonitor(GhostOptions.Create(Origin, ApiVersion, Tags)),
            new GhostTransport(new ScriptedGhostHandler(), new FixedTimeProvider(Clock)),
            new HtmlExportAdapter());

        var capabilities = ((IPublisherAdapter)adapter).Describe();

        Assert.Equal(GhostPublisher.TargetId, ((IPublisherAdapter)adapter).TargetId);
        Assert.True(capabilities.SupportsDraft);
        Assert.True(capabilities.SupportsPublish);
        Assert.False(capabilities.SupportsReconcile);
    }

    // ==============================================================================================
    // The token: one HS256 Admin token per call, signed from the leased key.
    // ==============================================================================================

    /// <summary>
    /// Asserts the recorded Ghost-scheme token is exactly one HS256 Admin token: the key id in
    /// the header, the fixed audience and a five-minute life in the claims, and a signature
    /// that verifies under the vault's secret - recomputed here from the saved value.
    /// </summary>
    private static void AssertToken(string token)
    {
        var segments = token.Split('.');
        Assert.Equal(3, segments.Length);

        var header = JsonDocument.Parse(Base64UrlDecode(segments[0]));
        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal(GhostKeyId, header.RootElement.GetProperty("kid").GetString());

        var payload = JsonDocument.Parse(Base64UrlDecode(segments[1]));
        var issued = payload.RootElement.GetProperty("iat").GetInt64();
        Assert.Equal(Clock.ToUnixTimeSeconds(), issued);
        Assert.Equal(issued + 300, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal("/admin/", payload.RootElement.GetProperty("aud").GetString());

        using var signer = new HMACSHA256(Convert.FromHexString(GhostKeySecretHex));
        var expected = signer.ComputeHash(Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}"));
        Assert.Equal(expected, Convert.FromBase64String(Pad(segments[2])));
    }

    private static string Base64UrlDecode(string segment) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(Pad(segment)));

    private static string Pad(string segment)
    {
        var restored = segment.Replace('-', '+').Replace('_', '/');
        var padding = (4 - restored.Length % 4) % 4;

        return restored + new string('=', padding);
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
                    idempotencyKey = $"gh-{purpose}",
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
public sealed class StubGhostOptionsMonitor(GhostOptions current) : IOptionsMonitor<GhostOptions>
{
    public GhostOptions CurrentValue { get; set; } = current;

    public GhostOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<GhostOptions, string?> listener) =>
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
/// The scripted far end of the Ghost wire: records every call - method, path, query, body, the
/// Ghost-scheme header and the Accept-Version - and answers the exact Admin posts exchange.
/// The only substitution in this suite, and it stands in for Ghost itself rather than for any
/// Chronicleaf code.
/// </summary>
public sealed class ScriptedGhostHandler : HttpMessageHandler
{
    private HttpStatusCode? refusedWith;
    private string? redirectLocation;
    private bool loseCreateAnswer;

    /// <summary>Every call the adapter made, in order.</summary>
    public List<RecordedCall> Calls { get; } = new();

    /// <summary>One answered request, as the scenario reads it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The absolute path: always the Admin posts path.</param>
    /// <param name="Query">The query string: always <c>?source=html</c>.</param>
    /// <param name="AbsoluteUri">The full request URI: always the operator's origin here.</param>
    /// <param name="Body">The request body: the fixed posts shape.</param>
    /// <param name="AuthScheme">Always <c>Ghost</c>: the only scheme this provider reads.</param>
    /// <param name="AuthParameter">The signed token, held in memory for this scenario only.</param>
    /// <param name="AcceptVersion">The Accept-Version the adapter sent.</param>
    /// <param name="Accept">The Accept header the adapter sent.</param>
    public sealed record RecordedCall(
        string Method,
        string Path,
        string Query,
        string AbsoluteUri,
        string? Body,
        string? AuthScheme,
        string? AuthParameter,
        string? AcceptVersion,
        string? Accept);

    /// <summary>Answers the happy Admin posts create with the provider's 201 shape.</summary>
    public static ScriptedGhostHandler Happy(string postId, string postSlug, string postUrl, string updatedAt) =>
        new() { PostId = postId, PostSlug = postSlug, PostUrl = postUrl, UpdatedAt = updatedAt };

    public string PostId { get; private set; } = "670f00000000000000000000";

    public string PostSlug { get; private set; } = "untitled";

    public string PostUrl { get; private set; } = "https://ghost.test/untitled/";

    public string UpdatedAt { get; private set; } = "2026-10-07T09:00:00.000Z";

    /// <summary>Refuses every call with the given status.</summary>
    public void RefuseWith(HttpStatusCode status) => refusedWith = status;

    /// <summary>Answers the call with a redirect to the given location, which must be refused.</summary>
    public void RedirectTo(string location) => redirectLocation = location;

    /// <summary>Loses the create answer: the write may have happened before it was lost.</summary>
    public void LoseCreateAnswer() => loseCreateAnswer = true;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        request.Headers.TryGetValues("Accept-Version", out var versions);

        var call = new RecordedCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.Query,
            request.RequestUri.AbsoluteUri,
            body,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            versions?.FirstOrDefault(),
            request.Headers.Accept.ToString());

        Calls.Add(call);

        if (loseCreateAnswer)
        {
            throw new TaskCanceledException("Simulated wire timeout after the create.");
        }

        if (redirectLocation is not null)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(redirectLocation, UriKind.Absolute);
            return redirect;
        }

        if (refusedWith is { } refusal)
        {
            return Json(refusal, new { errors = new[] { new { message = "Scripted refusal.", errorType = "ScriptedError" } } });
        }

        return Json(
            HttpStatusCode.Created,
            new
            {
                posts = new[]
                {
                    new { id = PostId, slug = PostSlug, url = PostUrl, updated_at = UpdatedAt },
                },
            });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with the shipped Ghost
/// adapter registered over a scripted wire.
/// </summary>
public sealed class PublisherGhostApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly IOptionsMonitor<GhostOptions>? _targets;
    private readonly ScriptedGhostHandler? _wire;
    private readonly IReadOnlyDictionary<string, string?>? _configured;
    private readonly LogCapture _logs;

    private PublisherGhostApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        IOptionsMonitor<GhostOptions>? targets,
        ScriptedGhostHandler? wire,
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
    public static PublisherGhostApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        GhostOptions options,
        ScriptedGhostHandler wire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherGhostApiHost(
            connectionString, clock, keyFile, new StubGhostOptionsMonitor(options), wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host with a monitor the scenario may reconfigure between calls: the boundary
    /// between a preview and a publish the operator moved.
    /// </summary>
    public static PublisherGhostApiHost StartWithMonitor(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        StubGhostOptionsMonitor monitor,
        ScriptedGhostHandler wire)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherGhostApiHost(
            connectionString, clock, keyFile, monitor, wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host through the production registration path with an explicit target
    /// configured: the demand gate reads the section and <c>Configure</c> binds it, exactly
    /// as a deployment configures it. No scripted wire is installed, so nothing here may
    /// reach a remote side - the scenarios this host serves stop at acceptance.
    /// </summary>
    public static PublisherGhostApiHost StartConfigured(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        string origin,
        string apiVersion,
        string? tags) =>
        new(
            connectionString,
            clock,
            keyFile,
            targets: null,
            wire: null,
            new LogCapture(),
            configured: new Dictionary<string, string?>
            {
                [$"{GhostOptions.SectionName}:{nameof(GhostOptions.Origin)}"] = origin,
                [$"{GhostOptions.SectionName}:{nameof(GhostOptions.ApiVersion)}"] = apiVersion,
                [$"{GhostOptions.SectionName}:{nameof(GhostOptions.Tags)}"] = tags,
            });

    /// <summary>
    /// Starts a host with the production composition root and no explicit target: demand-gated
    /// off, so the slot answers unknown.
    /// </summary>
    public static PublisherGhostApiHost StartProduction(
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
                // registration shape is production's own - one adapter for the ghost slot
                // beside its descriptor - with only Ghost itself substituted, and the
                // transport holding its own client so no bare-handler registration can rewire
                // another transport. The clock is the scenario's, so the signed token's
                // instants are asserted exactly.
                var targets = _targets;
                var wire = _wire;
                var clock = _clock;
                services.AddSingleton<IPublisherAdapter>(provider => new GhostPublisher(
                    targets,
                    new GhostTransport(wire, clock),
                    provider.GetRequiredService<IContentExportAdapter>(),
                    provider.GetRequiredService<ILogger<GhostPublisher>>()));
                services.AddSingleton(new PublisherTargetDescriptor(
                    GhostPublisher.TargetId,
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
