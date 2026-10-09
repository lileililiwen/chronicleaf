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
using Chronicleaf.Publishers.CustomBlog;
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
/// PUBLISHER-CUSTOM-BLOG-ADAPTER-001 and PUBLISHER-CUSTOM-BLOG-ADAPTER-002 against a real disposable
/// PostgreSQL, the real <c>Chronicleaf.Api</c> host, the real operation queue and the real
/// asset, review, evidence, vault and operation contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the asset
/// store, the approvals table, the routes, the access boundary, the attempt table, the
/// operation queue with its real worker, the credential vault over a real operator key file,
/// and the shipped custom-blog adapter probing capabilities and creating one v1 post per
/// confirmed publish - mode draft or publish by the authorized mode - through the shipped
/// transport. The approved story every scenario publishes is produced by a real generation
/// job through the same scripted provider the drafts suite drives. The publisher credential
/// is genuinely sealed by the vault and genuinely leased per call, and the scripted wire
/// asserts the bearer header it receives carries the saved API key verbatim, which is what
/// makes "the credential stays a vault reference" a real assertion rather than a vacuous one.
/// </para>
/// <para>
/// <b>What is scripted.</b> Only the far end of the wire: a <see cref="ScriptedCustomBlogHandler"/>
/// answers the exact v1 exchange - one <c>GET /chronicleaf/v1/capabilities</c> probe and one
/// <c>POST /chronicleaf/v1/posts</c> per confirmed publish - while every stage above the
/// socket is shipped code. No scenario reaches a real server: the handler is the boundary,
/// and a scenario that needs an upstream refusal, an incompatible contract, a refused
/// redirect or a lost answer scripts exactly that. No claim here is evidence that any
/// custom-blog server works or that any post exists remotely.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A receipt is read out of the raw
/// <c>publication_attempts</c> table, not only from the receipt route, and an operation is
/// read out of the raw <c>operations</c> table. The wire is read out of the scripted handler's
/// recorded calls: method, path, query, body and the bearer-scheme header. Nothing upstream
/// is inferred from a row this system wrote.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PublisherCustomBlogAdapterTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("d6a1b4e5-7c8f-4d1b-a09f-1b2c3d4e5f66");

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

    /// <summary>
    /// The leased API key. Verbatim on purpose: a transport that trimmed or encoded it would
    /// sign the wrong credential, and the wire assertion below would catch the difference.
    /// </summary>
    private const string PublisherCredentialValue = "cb-live-key-never-used-for-a-real-call";

    private const string Origin = "https://blog.test";

    private const string RemoteId = "post-7f3a";

    private const string PostUrl = "https://blog.test/posts/post-7f3a";

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Success: a confirmed draft preview creates one post as draft.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_draft_preview_creates_post_as_draft_with_bearer_credential()
    {
        await using var fixture = await ArrangeAsync("cb_001_draft");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");

        // The preview is pure: no capabilities probe, no post, no leased key.
        Assert.Empty(wire.Calls);
        Assert.Empty(preview.MediaPlan);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-draft",
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

        // Exactly two upstream calls: the capabilities probe, then the posts create. No second
        // hop, no update - draft mode is one create with the draft mode.
        Assert.Equal(2, wire.Calls.Count);

        var probe = wire.Calls[0];
        Assert.Equal("GET", probe.Method);
        Assert.Equal("/chronicleaf/v1/capabilities", probe.Path);
        Assert.Equal(string.Empty, probe.Query);
        Assert.StartsWith("https://blog.test/", probe.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", probe.AuthScheme);
        Assert.Equal(PublisherCredentialValue, probe.AuthParameter);

        var call = wire.Calls[1];
        Assert.Equal("POST", call.Method);
        Assert.Equal("/chronicleaf/v1/posts", call.Path);
        Assert.Equal(string.Empty, call.Query);
        Assert.StartsWith("https://blog.test/", call.AbsoluteUri, StringComparison.Ordinal);

        // The leased key travels as one bearer credential - never a query parameter, never
        // encoded - and it carries the vault's value verbatim.
        Assert.Equal("Bearer", call.AuthScheme);
        Assert.Equal(PublisherCredentialValue, call.AuthParameter);
        Assert.DoesNotContain(PublisherCredentialValue, call.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, call.Body ?? string.Empty, StringComparison.Ordinal);

        // The operation id is the idempotency key the capabilities document promises the server
        // honors: the same operation retried names the same post.
        Assert.Equal(operationId.ToString("D"), call.IdempotencyKey);

        // The shape is fixed: the contract version, the operation id, the exact content hash,
        // the approved title and Markdown verbatim, an empty media manifest and the draft mode.
        var payload = JsonDocument.Parse(call.Body!);
        Assert.Equal(1, payload.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(operationId.ToString("D"), payload.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("draft", payload.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Reproducible release pipeline", payload.RootElement.GetProperty("title").GetString());
        Assert.Empty(payload.RootElement.GetProperty("mediaManifest").EnumerateArray());

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        // The sent Markdown is the approved bytes verbatim - no render, no pictures section.
        Assert.Equal(stored.BodyMarkdown, payload.RootElement.GetProperty("markdown").GetString());
        Assert.Equal(stored.BodyHash, payload.RootElement.GetProperty("contentHash").GetString());
        Assert.Contains("clean checkout", payload.RootElement.GetProperty("markdown").GetString()!, StringComparison.Ordinal);

        // The receipt records the remote post id, the server-named URL and the exact content
        // hash: the id the server named, and the body hash of the bytes that were authorized.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("remote-draft", attempt!.State);
        Assert.Equal("draft", attempt.Mode);
        Assert.Equal(RemoteId, attempt.RemoteId);
        Assert.Equal(PostUrl, attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
        Assert.Equal(stored.BodyHash, attempt.BodyHash);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("remote-draft", receiptBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(RemoteId, receiptBody.RootElement.GetProperty("remoteId").GetString());
        Assert.Equal(PostUrl, receiptBody.RootElement.GetProperty("remoteUrl").GetString());
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
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Success: a confirmed publish creates one published post.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_publish_creates_published_post()
    {
        await using var fixture = await ArrangeAsync("cb_001_publish");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        var options = CustomBlogOptions.Create(Origin, "markdown");
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "publish");
        Assert.Contains($"custom-blog origin:{Origin}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("formats:[markdown]", preview.Warnings[0], StringComparison.Ordinal);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-publish",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        // The probe plus one call: the same create, now with the publish mode. No update verb
        // exists on this transport, so a publish can never rewrite a remote post.
        Assert.Equal(2, wire.Calls.Count);
        var call = wire.Calls[1];

        Assert.Equal("POST", call.Method);
        Assert.Equal("/chronicleaf/v1/posts", call.Path);
        Assert.Equal(string.Empty, call.Query);
        Assert.StartsWith("https://blog.test/", call.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", call.AuthScheme);
        Assert.Equal(PublisherCredentialValue, call.AuthParameter);
        Assert.Equal(operationId.ToString("D"), call.IdempotencyKey);

        var payload = JsonDocument.Parse(call.Body!);
        Assert.Equal(CustomBlogPublisher.PublishedMode, payload.RootElement.GetProperty("mode").GetString());
        Assert.Equal("Reproducible release pipeline", payload.RootElement.GetProperty("title").GetString());

        // The upstream URL is the address the server named, never guessed from a title.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
        Assert.Equal("publish", attempt.Mode);
        Assert.Equal(RemoteId, attempt.RemoteId);
        Assert.Equal(PostUrl, attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Failure: a refused key fails without exposure.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_refused_key_fails_without_exposing_secret_or_moving_approval()
    {
        await using var fixture = await ArrangeAsync("cb_001_unauthorized");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.RefuseCapabilitiesWith(HttpStatusCode.Unauthorized);
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-unauthorized",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.CredentialRejected, operation.ErrorCode);

        // The key was refused on the probe: no post call followed, no retry.
        var call = Assert.Single(wire.Calls);
        Assert.Equal("/chronicleaf/v1/capabilities", call.Path);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(CustomBlogError.CredentialRejected, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Null(attempt.RemoteUrl);

        // No secret in the stored rows and no secret in the served responses - and no server
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
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Failure: a rejected body, a forbidden write and a hash
    // conflict map to redacted codes.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_rejected_body_forbidden_write_and_hash_conflict_map_to_redacted_codes()
    {
        await using var fixture = await ArrangeAsync("cb_001_refusals");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.RefusePostsWith((HttpStatusCode)422);
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The server refused the content itself: a redacted code, no remote id, no retry.
        using var rejected = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-invalid",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, rejected.StatusCode);
        var rejectedAccepted = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        var rejectedOperationId = rejectedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var rejectedAttemptId = rejectedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var rejectedOperation = await OperationRows.AwaitAsync(
            fixture.Database, rejectedOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.InvalidPayload, rejectedOperation.ErrorCode);

        var rejectedAttempt = await AttemptRows.ReadAsync(fixture.Database, rejectedAttemptId);
        Assert.NotNull(rejectedAttempt);
        Assert.Equal("failed", rejectedAttempt!.State);
        Assert.Equal(CustomBlogError.InvalidPayload, rejectedAttempt.ErrorCode);
        Assert.Null(rejectedAttempt.RemoteId);

        using var rejectedReceipt = await owner.GetAsync($"/api/publications/{rejectedAttemptId:D}");
        Assert.DoesNotContain(
            "Scripted refusal", await rejectedReceipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The key may not write: a different refusal, still with no remote id and no second
        // create of the same post.
        wire.RefusePostsWith(HttpStatusCode.Forbidden);

        using var forbidden = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-forbidden",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, forbidden.StatusCode);
        var forbiddenAccepted = JsonDocument.Parse(await forbidden.Content.ReadAsStringAsync());
        var forbiddenOperationId = forbiddenAccepted.RootElement.GetProperty("operationId").GetGuid();
        var forbiddenAttemptId = forbiddenAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var forbiddenOperation = await OperationRows.AwaitAsync(
            fixture.Database, forbiddenOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.PublishForbidden, forbiddenOperation.ErrorCode);

        var forbiddenAttempt = await AttemptRows.ReadAsync(fixture.Database, forbiddenAttemptId);
        Assert.NotNull(forbiddenAttempt);
        Assert.Equal("failed", forbiddenAttempt!.State);
        Assert.Equal(CustomBlogError.PublishForbidden, forbiddenAttempt.ErrorCode);
        Assert.Null(forbiddenAttempt.RemoteId);

        // A hash conflict collides rather than duplicating: the conflict code, still with
        // no remote id and no second create of the same post.
        wire.RefusePostsWith(HttpStatusCode.Conflict);

        using var conflicted = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-conflict",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, conflicted.StatusCode);
        var conflictedAccepted = JsonDocument.Parse(await conflicted.Content.ReadAsStringAsync());
        var conflictedOperationId = conflictedAccepted.RootElement.GetProperty("operationId").GetGuid();
        var conflictedAttemptId = conflictedAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var conflictedOperation = await OperationRows.AwaitAsync(
            fixture.Database, conflictedOperationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.ContentConflict, conflictedOperation.ErrorCode);

        var conflictedAttempt = await AttemptRows.ReadAsync(fixture.Database, conflictedAttemptId);
        Assert.NotNull(conflictedAttempt);
        Assert.Equal("failed", conflictedAttempt!.State);
        Assert.Equal(CustomBlogError.ContentConflict, conflictedAttempt.ErrorCode);
        Assert.Null(conflictedAttempt.RemoteId);
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Failure: an incompatible server is refused before any post.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_incompatible_server_is_refused_before_any_post()
    {
        await using var fixture = await ArrangeAsync("cb_001_incompatible");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.ServeCapabilities(new { schemaVersion = 2, idempotentWrites = true, formats = new[] { "markdown" } });
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-incompatible",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.IncompatibleRemoteContract, operation.ErrorCode);

        // The probe refused the contract, so no post call followed: exactly one call, and it
        // was the capabilities read.
        var call = Assert.Single(wire.Calls);
        Assert.Equal("/chronicleaf/v1/capabilities", call.Path);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(CustomBlogError.IncompatibleRemoteContract, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Boundary: an origin change after preview stales it.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_origin_change_after_preview_stales_the_confirmation()
    {
        await using var fixture = await ArrangeAsync("cb_001_stale");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        var monitor = new StubCustomBlogOptionsMonitor(CustomBlogOptions.Create(Origin, null));
        await using var host = PublisherCustomBlogApiHost.StartWithMonitor(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, monitor, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The operator reconfigures the origin after the owner read the preview: the confirmed
        // hash describes another target configuration, so it authorizes no write.
        monitor.CurrentValue = CustomBlogOptions.Create("https://other-blog.test", null);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-stale",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Boundary: a redirect is refused without a second hop.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_redirect_is_refused_without_a_second_hop()
    {
        await using var fixture = await ArrangeAsync("cb_001_redirect");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.RedirectPostsTo("https://collector.example/x");
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-redirect",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(CustomBlogError.RedirectRefused, operation.ErrorCode);

        // The redirect was refused, not followed: the probe plus the one post call, both to the
        // operator's origin - the leased key never left the blog for the Location the server
        // named.
        Assert.Equal(2, wire.Calls.Count);
        foreach (var call in wire.Calls)
        {
            Assert.StartsWith("https://blog.test/", call.AbsoluteUri, StringComparison.Ordinal);
        }

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(CustomBlogError.RedirectRefused, attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-001 Boundary: a formats change after preview stales it.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_formats_change_after_preview_stales_the_confirmation()
    {
        await using var fixture = await ArrangeAsync("cb_001_formats");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        var monitor = new StubCustomBlogOptionsMonitor(CustomBlogOptions.Create(Origin, "markdown"));
        await using var host = PublisherCustomBlogApiHost.StartWithMonitor(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, monitor, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        // The preview binds the allowlist the owner is looking at, so widening the operator's
        // formats afterwards stales this confirmation.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        Assert.Contains("formats:[markdown]", preview.Warnings[0], StringComparison.Ordinal);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        monitor.CurrentValue = CustomBlogOptions.Create(Origin, "markdown,html");

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "draft",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-001-formats",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        // Refused before any byte left: no key was paired and no call was made.
        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-002 Failure: a lost create answer becomes unknown, then stays
    // manual because this target cannot reconcile.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_lost_create_answer_becomes_unknown_then_stays_manual()
    {
        await using var fixture = await ArrangeAsync("cb_002_unknown");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.LoseCreateAnswer();
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-002-unknown",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // The bytes may have left and a create has no reliable idempotency from here: unknown,
        // with no remote id to ask about - and the probe plus the one dispatch, never retried.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Equal(2, wire.Calls.Count);
        var dispatchCalls = wire.Calls.Count;

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("manual", JsonDocument.Parse(await receipt.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reconcileHint").GetString());

        // This target cannot reconcile: the receipt stays unknown with no further upstream
        // call, and the owner inspects the server by hand through its operations endpoint,
        // carrying the operation id the accept response already named.
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
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-002 Failure: an unreadable success shape becomes unknown.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_unreadable_success_shape_becomes_unknown()
    {
        await using var fixture = await ArrangeAsync("cb_002_shape");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        wire.AnswerPostsWithUnmappedShape();
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = CustomBlogPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "cb-002-shape",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // Answered success in a shape this client does not understand: the post may exist but
        // its id cannot be named - unknown with a manual hint, never a second create.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.Equal(2, wire.Calls.Count);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal("manual", JsonDocument.Parse(await receipt.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reconcileHint").GetString());
    }

    // ==============================================================================================
    // PUBLISHER-CUSTOM-BLOG-ADAPTER-002 Boundary: an identical resubmit returns the receipt.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_identical_resubmit_returns_receipt_without_second_call()
    {
        await using var fixture = await ArrangeAsync("cb_002_replay");
        var wire = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        var options = CustomBlogOptions.Create(Origin, null);
        await using var host = PublisherCustomBlogApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = CustomBlogPublisher.TargetId,
            mode = "draft",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "cb-002-replay",
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
        Assert.Equal(2, dispatchCalls);

        using var second = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(replay.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(attemptId, replay.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal("remote-draft", replay.RootElement.GetProperty("state").GetString());

        // The replay returned the stored receipt: no second probe and no second create left
        // this process.
        Assert.Equal(dispatchCalls, wire.Calls.Count);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // Registry and options: unconfigured demand-gated off, invalid configuration refused.
    // ==============================================================================================

    [Fact]
    public async Task Registry_unconfigured_custom_blog_is_unknown_and_invalid_options_are_refused()
    {
        await using var fixture = await ArrangeAsync("cb_reg_off");

        // The production composition root with no explicit target: the slot is unclaimed, so the
        // preview answers unknown rather than reaching any transport.
        await using var production = PublisherCustomBlogApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var productionOwner = await OwnerClient.CreateAsync(production);

        using var preview = await productionOwner.SendAsync(await productionOwner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = CustomBlogPublisher.TargetId, mode = "draft" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_unknown_target", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        // A missing origin, a non-https origin and credentials inside the origin are startup
        // refusals, not refused publishes.
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create(null, null));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create("   ", null));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create("http://blog.test", null));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create("blog.test", null));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create("https://deploy@blog.test", null));

        // An allowlist that cannot authorize any send - empty entries, too many, a bad token,
        // or no markdown - is a startup refusal, not a refused publish.
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create(Origin, "markdown,,html"));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create(Origin, "Markdown!"));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create(
                Origin, string.Join(",", Enumerable.Repeat("markdown", CustomBlogOptions.MaxFormats + 1))));
        Assert.Throws<InvalidOperationException>(() =>
            CustomBlogOptions.Create(Origin, "html"));

        var configured = CustomBlogOptions.Create(Origin, "markdown,html");
        Assert.Equal(new Uri(Origin), configured.ValidatedOrigin);
        Assert.Equal(new[] { "markdown", "html" }, configured.ParsedFormats);
        Assert.Contains($"custom-blog origin:{Origin}", configured.TargetLine(42), StringComparison.Ordinal);
        Assert.Contains("formats:[markdown,html]", configured.TargetLine(42), StringComparison.Ordinal);

        // The allowlist normalizes case and drops duplicates, so two spellings of one target
        // describe one target.
        var normalized = CustomBlogOptions.Create(Origin, "Markdown,markdown,HTML");
        Assert.Equal(new[] { "markdown", "html" }, normalized.ParsedFormats);

        var minimal = CustomBlogOptions.Create(Origin, null);
        Assert.Equal(new[] { "markdown" }, minimal.ParsedFormats);
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
        await using var fixture = await ArrangeAsync("cb_configured_section");
        await using var host = PublisherCustomBlogApiHost.StartConfigured(
            fixture.Database,
            new FixedTimeProvider(Clock),
            fixture.KeyFile.FilePath,
            Origin,
            "markdown,html");
        using var owner = await OwnerClient.CreateAsync(host);

        using var listed = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var entry = body.RootElement
            .GetProperty("targets")
            .EnumerateArray()
            .Single(target => target.GetProperty("targetId").GetString() == CustomBlogPublisher.TargetId);
        Assert.False(entry.GetProperty("exportOnly").GetBoolean());
        Assert.True(entry.GetProperty("supportsDraft").GetBoolean());
        Assert.True(entry.GetProperty("supportsPublish").GetBoolean());
        Assert.False(entry.GetProperty("supportsReconcile").GetBoolean());

        // The preview renders against the bound target: CPU only, no wire, no 500.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, CustomBlogPublisher.TargetId, "draft");
        Assert.NotEmpty(preview.PreviewHash);
    }

    [Fact]
    public async Task Prepare_pictures_are_named_not_carried_and_preview_binds_origin_formats_bytes()
    {
        var adapter = new CustomBlogPublisher(
            new StubCustomBlogOptionsMonitor(CustomBlogOptions.Create(Origin, "markdown,html")),
            new CustomBlogTransport(new ScriptedCustomBlogHandler()));

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
            new PublisherTargetConfig(CustomBlogPublisher.TargetId));

        // Pictures are named in the warnings the owner confirms - never silently dropped - and
        // the media plan stays empty because the posts create carries no uploads.
        Assert.Equal(CustomBlogPublisher.TargetId, preview.TargetId);
        Assert.Empty(preview.MediaPlan);
        Assert.Equal(2, preview.Warnings.Count);
        Assert.Contains($"custom-blog origin:{Origin}", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("formats:[markdown,html]", preview.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("not carried", preview.Warnings[1], StringComparison.Ordinal);

        // The same bytes preview twice: the target line the contract hashes is stable.
        var reread = await ((IPublisherAdapter)adapter).PrepareAsync(
            snapshot,
            new PublisherTargetConfig(CustomBlogPublisher.TargetId));
        Assert.Equal(preview.Warnings[0], reread.Warnings[0]);
    }

    [Fact]
    public void Transport_handler_pins_host_and_adapter_describes_draft_and_publish()
    {
        using var handler = CustomBlogTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(CustomBlogTransport.Timeout, handler.ConnectTimeout);

        var adapter = new CustomBlogPublisher(
            new StubCustomBlogOptionsMonitor(CustomBlogOptions.Create(Origin, null)),
            new CustomBlogTransport(new ScriptedCustomBlogHandler()));

        var capabilities = ((IPublisherAdapter)adapter).Describe();

        Assert.Equal(CustomBlogPublisher.TargetId, ((IPublisherAdapter)adapter).TargetId);
        Assert.True(capabilities.SupportsDraft);
        Assert.True(capabilities.SupportsPublish);
        Assert.False(capabilities.SupportsReconcile);
    }

    // ==============================================================================================
    // The transport contract: capabilities variants, refused posts and unusable references.
    // ==============================================================================================

    [Fact]
    public async Task Transport_capabilities_variants_posts_refusals_and_unusable_references()
    {
        var options = CustomBlogOptions.Create(Origin, null);
        var operationId = Guid.NewGuid();

        // A server that will not promise idempotent writes is incompatible, before any post.
        var noIdempotent = new ScriptedCustomBlogHandler();
        noIdempotent.ServeCapabilities(new { schemaVersion = 1, idempotentWrites = false, formats = new[] { "markdown" } });
        using (var transport = new CustomBlogTransport(noIdempotent))
        {
            var refused = await Assert.ThrowsAsync<CustomBlogUpstreamException>(() =>
                transport.CreatePostAsync(
                    options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                    operationId, new string('0', 64), "Title", "# Body", "draft"));
            Assert.Equal(CustomBlogError.IncompatibleRemoteContract, refused.ErrorCode);
        }
        Assert.Single(noIdempotent.Calls);

        // A server advertising no allowed format is incompatible, before any post.
        var noFormat = new ScriptedCustomBlogHandler();
        noFormat.ServeCapabilities(new { schemaVersion = 1, idempotentWrites = true, formats = new[] { "html" } });
        using (var transport = new CustomBlogTransport(noFormat))
        {
            var refused = await Assert.ThrowsAsync<CustomBlogUpstreamException>(() =>
                transport.CreatePostAsync(
                    options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                    operationId, new string('0', 64), "Title", "# Body", "draft"));
            Assert.Equal(CustomBlogError.IncompatibleRemoteContract, refused.ErrorCode);
        }
        Assert.Single(noFormat.Calls);

        // A capabilities probe that fails without an answer is unavailable, not incompatible:
        // the probe is a GET that creates nothing, so there is no ambiguous write to report.
        var probeDown = new ScriptedCustomBlogHandler();
        probeDown.RefuseCapabilitiesWith(HttpStatusCode.InternalServerError);
        using (var transport = new CustomBlogTransport(probeDown))
        {
            var refused = await Assert.ThrowsAsync<CustomBlogUpstreamException>(() =>
                transport.CreatePostAsync(
                    options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                    operationId, new string('0', 64), "Title", "# Body", "draft"));
            Assert.Equal(CustomBlogError.IncompatibleRemoteContract, refused.ErrorCode);
        }
        Assert.Single(probeDown.Calls);

        // A redirect on the probe is refused without a second hop.
        var probeRedirect = new ScriptedCustomBlogHandler();
        probeRedirect.RedirectCapabilitiesTo("https://collector.example/caps");
        using (var transport = new CustomBlogTransport(probeRedirect))
        {
            var refused = await Assert.ThrowsAsync<CustomBlogUpstreamException>(() =>
                transport.CreatePostAsync(
                    options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                    operationId, new string('0', 64), "Title", "# Body", "draft"));
            Assert.Equal(CustomBlogError.RedirectRefused, refused.ErrorCode);
        }
        var probeCall = Assert.Single(probeRedirect.Calls);
        Assert.StartsWith("https://blog.test/", probeCall.AbsoluteUri, StringComparison.Ordinal);

        // A 412 on the create is the server refusing the contract at write time.
        var goneContract = ScriptedCustomBlogHandler.Happy(RemoteId, PostUrl);
        goneContract.RefusePostsWith(HttpStatusCode.PreconditionFailed);
        using (var transport = new CustomBlogTransport(goneContract))
        {
            var refused = await Assert.ThrowsAsync<CustomBlogUpstreamException>(() =>
                transport.CreatePostAsync(
                    options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                    operationId, new string('0', 64), "Title", "# Body", "draft"));
            Assert.Equal(CustomBlogError.IncompatibleRemoteContract, refused.ErrorCode);
        }
        Assert.Equal(2, goneContract.Calls.Count);

        // A remote id longer than the attempt column cannot be stored: the post may exist but
        // its reference cannot be named, so the answer is unknown rather than a second create.
        var longId = ScriptedCustomBlogHandler.Happy(new string('r', CustomBlogTransport.MaxRemoteIdLength + 1), PostUrl);
        using (var transport = new CustomBlogTransport(longId))
        {
            var created = await transport.CreatePostAsync(
                options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                operationId, new string('0', 64), "Title", "# Body", "draft");
            Assert.Null(created);
        }

        // A non-https URL is not an address this product follows: the id is kept, the URL is
        // dropped rather than stored.
        var plainUrl = ScriptedCustomBlogHandler.Happy(RemoteId, "http://blog.test/posts/post-7f3a");
        using (var transport = new CustomBlogTransport(plainUrl))
        {
            var created = await transport.CreatePostAsync(
                options.ValidatedOrigin, options.ParsedFormats, PublisherCredentialValue,
                operationId, new string('0', 64), "Title", "# Body", "draft");
            Assert.NotNull(created);
            Assert.Equal(RemoteId, created!.Id);
            Assert.Null(created.Url);
        }
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
                    idempotencyKey = $"cb-{purpose}",
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
public sealed class StubCustomBlogOptionsMonitor(CustomBlogOptions current) : IOptionsMonitor<CustomBlogOptions>
{
    public CustomBlogOptions CurrentValue { get; set; } = current;

    public CustomBlogOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<CustomBlogOptions, string?> listener) =>
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
/// The scripted far end of the custom-blog wire: records every call - method, path, query,
/// body, the bearer-scheme header and the idempotency key - and answers the exact v1
/// exchange. The only substitution in this suite, and it stands in for the custom-blog
/// server itself rather than for any Chronicleaf code.
/// </summary>
public sealed class ScriptedCustomBlogHandler : HttpMessageHandler
{
    private HttpStatusCode? refusedCapabilitiesWith;
    private HttpStatusCode? refusedPostsWith;
    private string? capabilitiesRedirectLocation;
    private string? postsRedirectLocation;
    private bool loseCreateAnswer;
    private bool unmappedPostShape;
    private object? capabilitiesDocument;

    /// <summary>Every call the adapter made, in order.</summary>
    public List<RecordedCall> Calls { get; } = new();

    /// <summary>One answered request, as the scenario reads it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The absolute path: the capabilities or the posts path.</param>
    /// <param name="Query">The query string: always empty.</param>
    /// <param name="AbsoluteUri">The full request URI: always the operator's origin here.</param>
    /// <param name="Body">The request body: the fixed post shape, or null for the probe.</param>
    /// <param name="AuthScheme">Always <c>Bearer</c>: the only scheme this server reads.</param>
    /// <param name="AuthParameter">The API key, held in memory for this scenario only.</param>
    /// <param name="IdempotencyKey">The operation id, on the posts create only.</param>
    public sealed record RecordedCall(
        string Method,
        string Path,
        string Query,
        string AbsoluteUri,
        string? Body,
        string? AuthScheme,
        string? AuthParameter,
        string? IdempotencyKey);

    /// <summary>Answers the compatible capabilities probe and the happy posts create.</summary>
    public static ScriptedCustomBlogHandler Happy(string remoteId, string postUrl) =>
        new() { RemoteId = remoteId, PostUrl = postUrl };

    public string RemoteId { get; private set; } = "post-1";

    public string PostUrl { get; private set; } = "https://blog.test/posts/post-1";

    /// <summary>Serves the given capabilities document instead of the compatible one.</summary>
    public void ServeCapabilities(object document) => capabilitiesDocument = document;

    /// <summary>Refuses the capabilities probe with the given status.</summary>
    public void RefuseCapabilitiesWith(HttpStatusCode status) => refusedCapabilitiesWith = status;

    /// <summary>Refuses the posts create with the given status.</summary>
    public void RefusePostsWith(HttpStatusCode status) => refusedPostsWith = status;

    /// <summary>Answers the capabilities probe with a redirect, which must be refused.</summary>
    public void RedirectCapabilitiesTo(string location) => capabilitiesRedirectLocation = location;

    /// <summary>Answers the posts create with a redirect, which must be refused.</summary>
    public void RedirectPostsTo(string location) => postsRedirectLocation = location;

    /// <summary>Loses the create answer: the write may have happened before it was lost.</summary>
    public void LoseCreateAnswer() => loseCreateAnswer = true;

    /// <summary>Answers the create with success in a shape this client does not understand.</summary>
    public void AnswerPostsWithUnmappedShape() => unmappedPostShape = true;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string? idempotencyKey = null;

        if (request.Headers.TryGetValues("Idempotency-Key", out var keys))
        {
            idempotencyKey = string.Join(",", keys);
        }

        var call = new RecordedCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.Query,
            request.RequestUri.AbsoluteUri,
            body,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            idempotencyKey);

        Calls.Add(call);

        if (string.Equals(request.RequestUri.AbsolutePath, CustomBlogTransport.CapabilitiesPath, StringComparison.Ordinal))
        {
            if (capabilitiesRedirectLocation is not null)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(capabilitiesRedirectLocation, UriKind.Absolute);
                return redirect;
            }

            if (refusedCapabilitiesWith is { } probeRefusal)
            {
                return Json(probeRefusal, new { code = "scripted_refusal", message = "Scripted refusal." });
            }

            if (capabilitiesDocument is not null)
            {
                return Json(HttpStatusCode.OK, capabilitiesDocument);
            }

            return Json(HttpStatusCode.OK, new
            {
                schemaVersion = CustomBlogTransport.SchemaVersion,
                idempotentWrites = true,
                formats = new[] { "markdown" },
            });
        }

        if (loseCreateAnswer)
        {
            throw new TaskCanceledException("Simulated wire timeout after the create.");
        }

        if (postsRedirectLocation is not null)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(postsRedirectLocation, UriKind.Absolute);
            return redirect;
        }

        if (refusedPostsWith is { } refusal)
        {
            return Json(refusal, new { code = "scripted_refusal", message = "Scripted refusal." });
        }

        if (unmappedPostShape)
        {
            return Json(HttpStatusCode.OK, new { unfamiliar = true });
        }

        return Json(
            HttpStatusCode.Created,
            new
            {
                remoteId = RemoteId,
                url = PostUrl,
            });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with the shipped custom-blog
/// adapter registered over a scripted wire.
/// </summary>
public sealed class PublisherCustomBlogApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly IOptionsMonitor<CustomBlogOptions>? _targets;
    private readonly ScriptedCustomBlogHandler? _wire;
    private readonly IReadOnlyDictionary<string, string?>? _configured;
    private readonly LogCapture _logs;

    private PublisherCustomBlogApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        IOptionsMonitor<CustomBlogOptions>? targets,
        ScriptedCustomBlogHandler? wire,
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
    public static PublisherCustomBlogApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        CustomBlogOptions options,
        ScriptedCustomBlogHandler wire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherCustomBlogApiHost(
            connectionString, clock, keyFile, new StubCustomBlogOptionsMonitor(options), wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host with a monitor the scenario may reconfigure between calls: the boundary
    /// between a preview and a publish the operator moved.
    /// </summary>
    public static PublisherCustomBlogApiHost StartWithMonitor(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        StubCustomBlogOptionsMonitor monitor,
        ScriptedCustomBlogHandler wire)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherCustomBlogApiHost(
            connectionString, clock, keyFile, monitor, wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host through the production registration path with an explicit target
    /// configured: the demand gate reads the section and <c>Configure</c> binds it, exactly
    /// as a deployment configures it. No scripted wire is installed, so nothing here may
    /// reach a remote side - the scenarios this host serves stop at acceptance.
    /// </summary>
    public static PublisherCustomBlogApiHost StartConfigured(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        string origin,
        string? formats) =>
        new(
            connectionString,
            clock,
            keyFile,
            targets: null,
            wire: null,
            new LogCapture(),
            configured: new Dictionary<string, string?>
            {
                [$"{CustomBlogOptions.SectionName}:{nameof(CustomBlogOptions.Origin)}"] = origin,
                [$"{CustomBlogOptions.SectionName}:{nameof(CustomBlogOptions.Formats)}"] = formats,
            });

    /// <summary>
    /// Starts a host with the production composition root and no explicit target: demand-gated
    /// off, so the slot answers unknown.
    /// </summary>
    public static PublisherCustomBlogApiHost StartProduction(
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
                // registration shape is production's own - one adapter for the custom-blog slot
                // beside its descriptor - with only the server itself substituted, and the
                // transport holding its own client so no bare-handler registration can rewire
                // another transport.
                var targets = _targets;
                var wire = _wire;
                services.AddSingleton<IPublisherAdapter>(provider => new CustomBlogPublisher(
                    targets,
                    new CustomBlogTransport(wire),
                    provider.GetRequiredService<ILogger<CustomBlogPublisher>>()));
                services.AddSingleton(new PublisherTargetDescriptor(
                    CustomBlogPublisher.TargetId,
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
