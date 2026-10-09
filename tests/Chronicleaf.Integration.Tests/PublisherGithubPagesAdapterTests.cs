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
using Chronicleaf.Publishers.GitHubPages;
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
/// PUBLISHER-GITHUB-PAGES-ADAPTER-001 and PUBLISHER-GITHUB-PAGES-ADAPTER-002 against a real
/// disposable PostgreSQL, the real <c>Chronicleaf.Api</c> host, the real operation queue and the
/// real asset, review, evidence, vault and operation contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the asset
/// store, the approvals table, the routes, the access boundary, the attempt table, the
/// operation queue with its real worker, the credential vault over a real operator key file,
/// and the first shipped publisher transport - the GitHub Pages adapter rendering the approved
/// snapshot through the delivered HTML export and pushing it through the Git Data API. The
/// approved story every scenario publishes is produced by a real generation job through the
/// same scripted provider the drafts suite drives. The publisher credential is genuinely
/// sealed by the vault and genuinely leased per call, and the scripted wire asserts the bearer
/// it receives equals the saved secret - which is what makes "the credential stays a vault
/// reference" a real assertion rather than a vacuous one.
/// </para>
/// <para>
/// <b>What is scripted.</b> Only the far end of the wire: a <see cref="ScriptedGitHubPagesHandler"/>
/// answers the exact Git Data API exchange - the repository read, the conditional ref read,
/// the blob, tree and commit writes and the force=false ref move - while every stage above the
/// socket is shipped code. No scenario reaches any real GitHub endpoint: the handler is the
/// boundary, and a scenario that needs an upstream refusal, a moved branch or a lost answer
/// scripts exactly that. No claim here is evidence that GitHub works, that any repository
/// exists, or that a Pages build follows a branch write.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A receipt is read out of the raw
/// <c>publication_attempts</c> table, not only from the receipt route, and an operation is
/// read out of the raw <c>operations</c> table. The wire is read out of the scripted handler's
/// recorded calls: methods, paths, bodies and the bearer header. Nothing upstream is inferred
/// from a row this system wrote.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class PublisherGithubPagesAdapterTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("f2b3c4d5-8c4d-4b5e-9f6c-1d2b3a4e5c88");

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

    private const string PictureCaption = "The pipeline digest view.";

    private const string RepositoryOwner = "octo";

    private const string RepositoryName = "pages";

    private const string Branch = "gh-pages";

    private const string Subdirectory = "site";

    private static readonly string BaseSha = new('b', 40);

    private static readonly string CommitSha = new('c', 40);

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Success: a confirmed current preview pushes the rendered
    // site to the configured branch with a conditional ref write.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_confirmed_preview_pushes_rendered_site_with_conditional_ref_write()
    {
        await using var fixture = await ArrangeAsync("gp_001_success");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");

        // The preview is pure: no repository was read, no blob was written, no ref moved.
        Assert.Empty(wire.Calls);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-001-success",
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

        // The exact Git Data API exchange, in order: the repository read, the base read, one
        // blob for the rendered document, one tree over that base, one commit over that tree,
        // and one conditional ref move. Six calls, and the push is the last one.
        Assert.Equal(6, wire.Calls.Count);

        Assert.Equal("GET", wire.Calls[0].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}", wire.Calls[0].Path);

        Assert.Equal("GET", wire.Calls[1].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}/git/ref/heads/{Branch}", wire.Calls[1].Path);

        Assert.Equal("POST", wire.Calls[2].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}/git/blobs", wire.Calls[2].Path);
        var blob = JsonDocument.Parse(wire.Calls[2].Body!);
        Assert.Equal("base64", blob.RootElement.GetProperty("encoding").GetString());
        var pushedDocument = Encoding.UTF8.GetString(Convert.FromBase64String(
            blob.RootElement.GetProperty("content").GetString()!));
        Assert.Contains("<!DOCTYPE html>", pushedDocument, StringComparison.Ordinal);

        Assert.Equal("POST", wire.Calls[3].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}/git/trees", wire.Calls[3].Path);
        var tree = JsonDocument.Parse(wire.Calls[3].Body!);
        Assert.Equal(BaseSha, tree.RootElement.GetProperty("base_tree").GetString());
        var entry = Assert.Single(tree.RootElement.GetProperty("tree").EnumerateArray());
        Assert.Equal($"{Subdirectory}/index.html", entry.GetProperty("path").GetString());
        Assert.Equal("100644", entry.GetProperty("mode").GetString());
        Assert.Equal("blob", entry.GetProperty("type").GetString());

        Assert.Equal("POST", wire.Calls[4].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}/git/commits", wire.Calls[4].Path);
        var commit = JsonDocument.Parse(wire.Calls[4].Body!);
        Assert.Equal(BaseSha, commit.RootElement.GetProperty("parents").EnumerateArray().Single().GetString());
        Assert.DoesNotContain(PublisherCredentialValue, commit.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        Assert.Equal("PATCH", wire.Calls[5].Method);
        Assert.Equal($"/repos/{RepositoryOwner}/{RepositoryName}/git/refs/heads/{Branch}", wire.Calls[5].Path);
        var move = JsonDocument.Parse(wire.Calls[5].Body!);
        Assert.Equal(CommitSha, move.RootElement.GetProperty("sha").GetString());
        Assert.False(move.RootElement.GetProperty("force").GetBoolean());

        // Every call went to the pinned host under the configured repository - owner and name
        // segments, never a URL from a request - carrying the leased credential as the one
        // bearer header.
        foreach (var call in wire.Calls)
        {
            Assert.StartsWith(
                $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}",
                call.AbsoluteUri,
                StringComparison.Ordinal);
            Assert.Equal("Bearer", call.AuthScheme);
            Assert.Equal(PublisherCredentialValue, call.AuthParameter);
        }

        // The receipt records the remote id and the exact content hash: the commit SHA the
        // upstream named, and the body hash of the bytes that were authorized.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
        Assert.Equal(CommitSha, attempt.RemoteId);
        Assert.Equal($"https://github.com/{RepositoryOwner}/{RepositoryName}/commit/{CommitSha}", attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(stored!.BodyHash, attempt.BodyHash);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("published", receiptBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(CommitSha, receiptBody.RootElement.GetProperty("remoteId").GetString());
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
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Success: approval-covered pictures travel as assets.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_approved_pictures_travel_as_assets_with_rewritten_sources()
    {
        await using var fixture = await ArrangeAsync("gp_001_picture", withPicture: true);
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        Assert.NotNull(fixture.MediaId);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        Assert.Equal([fixture.MediaId.Value], preview.MediaPlan);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-001-picture",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        // Three files: the document plus the one approval-covered picture under generated asset
        // paths - and the document's sources rewritten to them.
        var blobs = wire.Calls.Where(call => call is { Method: "POST" } && call.Path.EndsWith("/git/blobs", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, blobs.Count);

        var tree = JsonDocument.Parse(wire.Calls.Single(call =>
            call is { Method: "POST" } && call.Path.EndsWith("/git/trees", StringComparison.Ordinal)).Body!);
        var paths = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Select(element => element.GetProperty("path").GetString()).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Contains($"{Subdirectory}/assets/{fixture.MediaId.Value:N}.png", paths);
        Assert.Contains($"{Subdirectory}/index.html", paths);

        // Blobs travel in path order, so the document is identified by its content rather
        // than by its position: the one blob that renders HTML.
        string? document = null;
        byte[]? pictureBytes = null;
        foreach (var blob in blobs)
        {
            var bytes = Convert.FromBase64String(JsonDocument.Parse(blob.Body!)
                .RootElement.GetProperty("content").GetString()!);

            if (bytes.Length > 15 && Encoding.UTF8.GetString(bytes, 0, 15).StartsWith("<!DOCTYPE html>", StringComparison.Ordinal))
            {
                document = Encoding.UTF8.GetString(bytes);
            }
            else
            {
                pictureBytes = bytes;
            }
        }

        Assert.NotNull(document);
        Assert.Contains($"src=\"{ScriptedGitHubPagesHandler.AssetDir}/{fixture.MediaId.Value:N}.png\"", document, StringComparison.Ordinal);
        Assert.DoesNotContain("images/image-", document, StringComparison.Ordinal);

        Assert.NotNull(pictureBytes);
        Assert.Equal(TestPng.Create(64, 40), pictureBytes);
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Failure: a refused credential fails without exposure.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_refused_credential_fails_without_exposing_secret_or_moving_approval()
    {
        await using var fixture = await ArrangeAsync("gp_001_unauthorized");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        wire.RefuseWith(HttpStatusCode.Unauthorized);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-001-unauthorized",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GitHubPagesError.Unauthorized, operation.ErrorCode);

        // Refused before any blob existed: one repository read, no tree, no commit, no ref move.
        Assert.Single(wire.Calls);
        Assert.Equal("GET", wire.Calls[0].Method);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GitHubPagesError.Unauthorized, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);

        // No secret in the stored rows and no secret in the served responses.
        Assert.DoesNotContain(PublisherCredentialValue, attempt.RemoteId ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, attempt.ErrorCode ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ErrorCode ?? string.Empty, StringComparison.Ordinal);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The source approval did not move: the refused dispatch authorizes no edit.
        using var review = await owner.GetAsync($"/api/assets/{fixture.StoryAssetId:D}/review");
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await review.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reviewStatus").GetString());
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Failure: draft mode is not served by this target.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_draft_mode_is_refused_before_any_upstream_call()
    {
        await using var fixture = await ArrangeAsync("gp_001_draft");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // Pages has no draft: the preview itself is refused, before any adapter runs.
        using var preview = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = GitHubPagesPublisher.TargetId, mode = "draft" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_mode_unsupported", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "draft",
                previewHash = new string('0', 64),
                approvalId,
                idempotencyKey = "gp-001-draft",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        Assert.Equal("publisher_mode_unsupported", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Boundary: a target change after preview stales it.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_target_change_after_preview_stales_the_confirmation()
    {
        await using var fixture = await ArrangeAsync("gp_001_stale");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var monitor = new StubPagesOptionsMonitor(
            new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory));
        await using var host = PublisherGithubPagesApiHost.StartWithMonitor(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, monitor, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        // The operator reconfigures the branch after the owner read the preview: the confirmed
        // hash describes another target, so it authorizes no write.
        monitor.CurrentValue = new GitHubPagesOptions(RepositoryOwner, RepositoryName, "main", Subdirectory);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-001-stale",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Empty(wire.Calls);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-001 Boundary: a moved branch fails without a force push.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_moved_branch_fails_without_force_push()
    {
        await using var fixture = await ArrangeAsync("gp_001_moved");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        wire.RefuseRefMoveWith((HttpStatusCode)422);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-001-moved",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal(GitHubPagesError.UpstreamMoved, operation.ErrorCode);

        // The conditional write lost and nothing retried it: every ref move carried force=false,
        // and there was exactly one attempt at the branch.
        var moves = wire.Calls.Where(call => call is { Method: "PATCH" }).ToList();
        Assert.Single(moves);
        Assert.False(JsonDocument.Parse(moves[0].Body!).RootElement.GetProperty("force").GetBoolean());

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GitHubPagesError.UpstreamMoved, attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISH-FROM-VARIANT-004: a configured section binds through production registration.
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
        await using var fixture = await ArrangeAsync("gp_configured_section");
        await using var host = PublisherGithubPagesApiHost.StartConfigured(
            fixture.Database,
            new FixedTimeProvider(Clock),
            fixture.KeyFile.FilePath,
            RepositoryOwner,
            RepositoryName,
            Branch,
            Subdirectory);
        using var owner = await OwnerClient.CreateAsync(host);

        using var listed = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var entry = body.RootElement
            .GetProperty("targets")
            .EnumerateArray()
            .Single(target => target.GetProperty("targetId").GetString() == GitHubPagesPublisher.TargetId);
        Assert.False(entry.GetProperty("exportOnly").GetBoolean());
        Assert.False(entry.GetProperty("supportsDraft").GetBoolean());
        Assert.True(entry.GetProperty("supportsPublish").GetBoolean());
        Assert.True(entry.GetProperty("supportsReconcile").GetBoolean());

        // The preview renders against the bound target: CPU only, no wire, no 500.
        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        Assert.NotEmpty(preview.PreviewHash);
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-002 Failure: a lost ref move becomes unknown, then reconciles.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_lost_ref_move_becomes_unknown_then_reconciles_by_planned_sha()
    {
        await using var fixture = await ArrangeAsync("gp_002_unknown");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        wire.LoseRefMoveAnswer();
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-002-unknown",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // The bytes may have left: unknown, with the planned commit SHA retained for the
        // reconciler to ask the branch about - and exactly one dispatch, never retried.
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Equal(CommitSha, attempt.RemoteId);
        var dispatchCalls = wire.Calls.Count;

        // The branch holds the planned commit: the receipt resolves to published. The
        // reconciliation is one anonymous ref read - no credential travels, no second push.
        wire.AnswerRefWith(CommitSha);

        using var reconciled = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var receipt = JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync());
        Assert.Equal("published", receipt.RootElement.GetProperty("state").GetString());
        Assert.Equal(CommitSha, receipt.RootElement.GetProperty("remoteId").GetString());

        var refReads = wire.Calls.Skip(dispatchCalls).ToList();
        Assert.Single(refReads);
        Assert.Equal("GET", refReads[0].Method);
        Assert.Null(refReads[0].AuthScheme);

        var reread = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(reread);
        Assert.Equal("published", reread!.State);

        // The operation row keeps its failure: reconciling rewrites no history.
        var kept = await OperationRows.ReadAsync(fixture.Database, operationId);
        Assert.NotNull(kept);
        Assert.Equal("failed", kept!.Status);
        Assert.Equal("publication_unknown", kept.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-002 Boundary: a timeout while staging objects is a
    // definitive failure, because only the ref move publishes.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_timeout_while_staging_is_failed_not_unknown()
    {
        await using var fixture = await ArrangeAsync("gp_002_early");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        wire.LoseCommitAnswer();
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = GitHubPagesPublisher.TargetId,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "gp-002-early",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", TimeSpan.FromSeconds(5));

        // The commit answer was lost before any ref named it: unreachable objects are not a
        // publication, so this is failed rather than unknown - and the branch was never moved.
        Assert.Equal(GitHubPagesError.Unavailable, operation.ErrorCode);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("failed", attempt!.State);
        Assert.Equal(GitHubPagesError.Unavailable, attempt.ErrorCode);
        Assert.Null(attempt.RemoteId);
        Assert.DoesNotContain(
            wire.Calls,
            call => call is { Method: "PATCH" } || call.Path.EndsWith("/git/refs", StringComparison.Ordinal));
        var dispatchCalls = wire.Calls.Count;

        // A failed receipt is history: reconciling answers it unchanged with no upstream call.
        using var reconciled = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        Assert.Equal("failed", JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync())
            .RootElement.GetProperty("state").GetString());

        Assert.Equal(dispatchCalls, wire.Calls.Count);
    }

    // ==============================================================================================
    // PUBLISHER-GITHUB-PAGES-ADAPTER-002 Boundary: an identical resubmit returns the receipt.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_identical_resubmit_returns_receipt_without_second_push()
    {
        await using var fixture = await ArrangeAsync("gp_002_replay");
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var options = new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        await using var host = PublisherGithubPagesApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, options, wire);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, GitHubPagesPublisher.TargetId, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = GitHubPagesPublisher.TargetId,
            mode = "publish",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "gp-002-replay",
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
        Assert.True(dispatchCalls > 0);

        using var second = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(replay.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(attemptId, replay.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal("published", replay.RootElement.GetProperty("state").GetString());

        // The replay returned the stored receipt: no second repository read and no second push.
        Assert.Equal(dispatchCalls, wire.Calls.Count);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // Registry and options: unconfigured demand-gated off, invalid configuration refused.
    // ==============================================================================================

    [Fact]
    public async Task Registry_unconfigured_github_pages_is_unknown_and_invalid_options_are_refused()
    {
        await using var fixture = await ArrangeAsync("gp_reg_off");

        // The production composition root with no explicit target: the slot is unclaimed, so the
        // preview answers unknown rather than reaching any transport.
        await using var production = PublisherGithubPagesApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var productionOwner = await OwnerClient.CreateAsync(production);

        using var preview = await productionOwner.SendAsync(await productionOwner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = GitHubPagesPublisher.TargetId, mode = "publish" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_unknown_target", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        // A URL is never a repository, traversal is never a branch or a subdirectory, and an
        // empty section names no target at all.
        Assert.Throws<InvalidOperationException>(() =>
            GitHubPagesOptions.Create("https://api.github.com/evil", "pages", Branch, Subdirectory));
        Assert.Throws<InvalidOperationException>(() =>
            GitHubPagesOptions.Create(RepositoryOwner, RepositoryName, "../main", Subdirectory));
        Assert.Throws<InvalidOperationException>(() =>
            GitHubPagesOptions.Create(RepositoryOwner, RepositoryName, Branch, "/absolute"));
        Assert.Throws<InvalidOperationException>(() =>
            GitHubPagesOptions.Create(RepositoryOwner, RepositoryName, Branch, "a/../../b"));
        Assert.Throws<InvalidOperationException>(() =>
            GitHubPagesOptions.Create("owner!", RepositoryName, Branch, Subdirectory));

        var valid = GitHubPagesOptions.Create(RepositoryOwner, RepositoryName, Branch, Subdirectory);
        Assert.Equal($"{RepositoryOwner}/{RepositoryName}", valid.Repository.ToString());
        Assert.Contains($"{RepositoryOwner}/{RepositoryName}@{Branch}:{Subdirectory}", valid.TargetLine(1, 2));
    }

    [Fact]
    public async Task Reconcile_without_remote_id_stays_unknown_without_upstream_call()
    {
        var wire = ScriptedGitHubPagesHandler.Happy(BaseSha, CommitSha);
        var adapter = new GitHubPagesPublisher(
            new StubPagesOptionsMonitor(
                new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory)),
            wire,
            new HtmlExportAdapter());

        // No planned SHA was recorded, so there is nothing to ask the branch about: the
        // reconciler answers unknown without a single upstream call.
        var receipt = new PublicationReceipt(
            Guid.NewGuid(),
            Guid.NewGuid(),
            GitHubPagesPublisher.TargetId,
            Guid.NewGuid(),
            1,
            new string('0', 64),
            PublishModes.Publish,
            PublishStates.Unknown,
            RemoteId: null,
            RemoteUrl: null,
            ErrorCode: PublisherError.Unknown,
            ReconcileHint: "manual");

        var result = await ((IPublisherAdapter)adapter).ReconcileAsync(receipt);

        Assert.Equal(PublishStates.Unknown, result.State);
        Assert.Empty(wire.Calls);
    }

    [Fact]
    public void Transport_handler_pins_host_and_refuses_redirects()
    {
        using var handler = GitHubPagesTransport.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(GitHubPagesTransport.Timeout, handler.ConnectTimeout);

        var adapter = new GitHubPagesPublisher(
            new StubPagesOptionsMonitor(
                new GitHubPagesOptions(RepositoryOwner, RepositoryName, Branch, Subdirectory)),
            new ScriptedGitHubPagesHandler(),
            new HtmlExportAdapter());

        var capabilities = ((IPublisherAdapter)adapter).Describe();

        Assert.Equal(GitHubPagesPublisher.TargetId, ((IPublisherAdapter)adapter).TargetId);
        Assert.False(capabilities.SupportsDraft);
        Assert.True(capabilities.SupportsPublish);
        Assert.True(capabilities.SupportsReconcile);
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed draft credential, a real sealed publisher
    /// credential, a stored draft produced by a real generation job, and the approval it belongs to.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        bool approveStory = true,
        bool withPicture = false)
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
        Guid? mediaId = null;

        if (withPicture)
        {
            await using var capture = CaptureApiHost.Start(
                database, clock, new RecordingScreenshotProvider(clock));
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            capture.Provider.AnswerWithPng(width: 64, height: 40);

            using var requested = await pictureOwner.SendAsync(await pictureOwner.Post(
                $"/api/milestones/{milestoneId:D}/captures",
                new
                {
                    url = "https://example.com/pipeline",
                    viewports = new[] { "desktop" },
                    waitMs = 0,
                    fullPage = false,
                    expectedBundleRevision = (long?)null,
                    idempotencyKey = $"gp-capture-{purpose}",
                },
                0));

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var captureOperationId = Guid.Parse(JsonDocument
                .Parse(await requested.Content.ReadAsStringAsync())
                .RootElement.GetProperty("operationId").GetString()!);

            await capture.DispatchAsync();
            await OperationRows.AwaitAsync(database, captureOperationId, "succeeded", TimeSpan.FromSeconds(5));

            using var candidates = await pictureOwner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
            var candidate = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync())
                .RootElement.GetProperty("items").EnumerateArray().First();

            var screenshotId = Guid.Parse(candidate.GetProperty("id").GetString()!);
            var screenshotRevision = candidate.GetProperty("revision").GetInt64();
            mediaId = Guid.Parse(candidate.GetProperty("mediaId").GetString()!);

            using var selected = await pictureOwner.SendAsync(await pictureOwner.Patch(
                $"/api/screenshots/{screenshotId:D}",
                screenshotRevision,
                new
                {
                    status = "selected",
                    caption = PictureCaption,
                    expectedRevision = screenshotRevision,
                    expectedBundleRevision = 1,
                }));

            Assert.True(
                selected.StatusCode == HttpStatusCode.OK,
                $"select answered {(int)selected.StatusCode}: {await selected.Content.ReadAsStringAsync()}");

            // The select appended the second bundle revision: the carried manual fact has a new
            // id there, so the summary cites the head's ids rather than the first revision's.
            await using var selector = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var selectorOwner = await OwnerClient.CreateAsync(selector);
            head = await ReadHeadAsync(selectorOwner, milestoneId);
            Assert.Equal(2, head.Revision);
        }
        else
        {
            await using var reader = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
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
                    idempotencyKey = $"gp-{purpose}",
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
            head.Revision,
            mediaId);

        if (approveStory)
        {
            await using var approver = GroundedMarkdownDraftsApiHost.StartProduction(
                database, clock, keyFile.FilePath);
            using var approverOwner = await OwnerClient.CreateAsync(approver);
            await ApproveAsync(approverOwner, fixture.StoryAssetId, 1, fixture.BundleRevision);
        }

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
            [.. body.RootElement.GetProperty("mediaPlan").EnumerateArray().Select(entry => entry.GetGuid())]);
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

    private sealed record HeadView(Guid BundleId, long Revision, Guid ManualItemId);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    private sealed record PreviewView(string PreviewHash, string BodyHash, long ContentRevision, Guid[] MediaPlan);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared server.
    /// </summary>
    private sealed record Fixture(
        string Database,
        OperatorKeyFile KeyFile,
        Guid ProjectId,
        Guid MilestoneId,
        Guid StoryAssetId,
        long BundleRevision,
        Guid? MediaId) : IAsyncDisposable
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
public sealed class StubPagesOptionsMonitor(GitHubPagesOptions current) : IOptionsMonitor<GitHubPagesOptions>
{
    public GitHubPagesOptions CurrentValue { get; set; } = current;

    public GitHubPagesOptions Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<GitHubPagesOptions, string?> listener) =>
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
/// The scripted far end of the GitHub wire: records every call - method, path, body and bearer -
/// and answers from the scenario's script. The only substitution in this suite, and it stands in
/// for GitHub itself rather than for any Chronicleaf code.
/// </summary>
public sealed class ScriptedGitHubPagesHandler : HttpMessageHandler
{
    /// <summary>Where pictures are written under the target subdirectory in these scenarios.</summary>
    public const string AssetDir = "assets";

    private readonly List<RecordedCall> calls = new();
    private int blobCounter;
    private HttpStatusCode? refusedWith;
    private HttpStatusCode? refusedRefMoveWith;
    private bool loseRefMoveAnswer;
    private bool loseCommitAnswer;
    private string? reconcileSha;

    /// <summary>Every call the adapter made, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls => calls;

    /// <summary>One answered request, as the scenario reads it.</summary>
    /// <param name="Method">The HTTP method.</param>
    /// <param name="Path">The absolute path.</param>
    /// <param name="AbsoluteUri">The full request URI: always the pinned host in these scenarios.</param>
    /// <param name="Body">The request body, or null for reads.</param>
    /// <param name="AuthScheme">The authorization scheme, or null for anonymous reads.</param>
    /// <param name="AuthParameter">The bearer value, held in memory for this scenario only.</param>
    public sealed record RecordedCall(
        string Method,
        string Path,
        string AbsoluteUri,
        string? Body,
        string? AuthScheme,
        string? AuthParameter);

    /// <summary>Answers the happy Git Data API exchange with the given base and commit SHAs.</summary>
    public static ScriptedGitHubPagesHandler Happy(string baseSha, string commitSha) =>
        new() { BaseSha = baseSha, CommitSha = commitSha };

    public string BaseSha { get; private set; } = new('b', 40);

    public string CommitSha { get; private set; } = new('c', 40);

    /// <summary>Refuses the next repository read with the given status.</summary>
    public void RefuseWith(HttpStatusCode status) => refusedWith = status;

    /// <summary>Refuses the ref move with the given status: a moved branch answers 422.</summary>
    public void RefuseRefMoveWith(HttpStatusCode status) => refusedRefMoveWith = status;

    /// <summary>Loses the ref-move answer: the write may have happened before it was lost.</summary>
    public void LoseRefMoveAnswer() => loseRefMoveAnswer = true;

    /// <summary>Loses the commit answer: no planned SHA is recorded.</summary>
    public void LoseCommitAnswer() => loseCommitAnswer = true;

    /// <summary>Answers later ref reads with the given head SHA: the reconciler's branch.</summary>
    public void AnswerRefWith(string sha) => reconcileSha = sha;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var call = new RecordedCall(
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            request.RequestUri.AbsoluteUri,
            body,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter);

        calls.Add(call);

        if (call is { Method: "GET" } && call.Path.Contains("/git/ref/heads/", StringComparison.Ordinal)
            && reconcileSha is not null)
        {
            return Json(
                HttpStatusCode.OK,
                new { @object = new { sha = reconcileSha } });
        }

        if (refusedWith is { } refusal
            && call is { Method: "GET" } && !call.Path.Contains("/git/ref/heads/", StringComparison.Ordinal))
        {
            return Json(refusal, new { message = "Scripted refusal." });
        }

        if (call is { Method: "GET" } && !call.Path.Contains("/git/", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, new { id = 1, @private = false, default_branch = "main" });
        }

        if (call is { Method: "GET" })
        {
            return Json(HttpStatusCode.OK, new { @object = new { sha = BaseSha } });
        }

        if (call is { Method: "POST" } && call.Path.EndsWith("/git/blobs", StringComparison.Ordinal))
        {
            blobCounter++;
            return Json(HttpStatusCode.Created, new { sha = blobCounter.ToString("x40") });
        }

        if (call is { Method: "POST" } && call.Path.EndsWith("/git/trees", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, new { sha = new string('d', 40) });
        }

        if (call is { Method: "POST" } && call.Path.EndsWith("/git/commits", StringComparison.Ordinal))
        {
            if (loseCommitAnswer)
            {
                throw new TaskCanceledException("Simulated wire timeout before the commit answer.");
            }

            return Json(HttpStatusCode.Created, new { sha = CommitSha });
        }

        if (call is { Method: "PATCH" })
        {
            if (loseRefMoveAnswer)
            {
                throw new TaskCanceledException("Simulated wire timeout after the ref move.");
            }

            if (refusedRefMoveWith is { } moveRefusal)
            {
                return Json(moveRefusal, new { message = "Scripted ref-move refusal." });
            }

            return Json(HttpStatusCode.OK, new { @object = new { sha = CommitSha } });
        }

        if (call is { Method: "POST" } && call.Path.EndsWith("/git/refs", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, new { @object = new { sha = CommitSha } });
        }

        return Json(HttpStatusCode.NotFound, new { message = "No scripted answer." });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with the shipped GitHub Pages
/// adapter registered over a scripted wire.
/// </summary>
public sealed class PublisherGithubPagesApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly IOptionsMonitor<GitHubPagesOptions>? _targets;
    private readonly ScriptedGitHubPagesHandler? _wire;
    private readonly IReadOnlyDictionary<string, string?>? _configured;
    private readonly LogCapture _logs;

    private PublisherGithubPagesApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        IOptionsMonitor<GitHubPagesOptions>? targets,
        ScriptedGitHubPagesHandler? wire,
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
    public static PublisherGithubPagesApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        GitHubPagesOptions options,
        ScriptedGitHubPagesHandler wire)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherGithubPagesApiHost(
            connectionString, clock, keyFile, new StubPagesOptionsMonitor(options), wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host with a monitor the scenario may reconfigure between calls: the boundary
    /// between a preview and a publish the operator moved.
    /// </summary>
    public static PublisherGithubPagesApiHost StartWithMonitor(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        StubPagesOptionsMonitor monitor,
        ScriptedGitHubPagesHandler wire)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(wire);

        return new PublisherGithubPagesApiHost(
            connectionString, clock, keyFile, monitor, wire, new LogCapture());
    }

    /// <summary>
    /// Starts a host through the production registration path with an explicit target
    /// configured: the demand gate reads the section and <c>Configure</c> binds it, exactly
    /// as a deployment configures it. No scripted wire is installed, so nothing here may
    /// reach a remote side - the scenarios this host serves stop at acceptance.
    /// </summary>
    public static PublisherGithubPagesApiHost StartConfigured(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        string repositoryOwner,
        string repositoryName,
        string branch,
        string subdirectory) =>
        new(
            connectionString,
            clock,
            keyFile,
            targets: null,
            wire: null,
            new LogCapture(),
            configured: new Dictionary<string, string?>
            {
                [$"{GitHubPagesOptions.SectionName}:{nameof(GitHubPagesOptions.RepositoryOwner)}"] = repositoryOwner,
                [$"{GitHubPagesOptions.SectionName}:{nameof(GitHubPagesOptions.RepositoryName)}"] = repositoryName,
                [$"{GitHubPagesOptions.SectionName}:{nameof(GitHubPagesOptions.Branch)}"] = branch,
                [$"{GitHubPagesOptions.SectionName}:{nameof(GitHubPagesOptions.Subdirectory)}"] = subdirectory,
            });

    /// <summary>
    /// Starts a host with the production composition root and no explicit target: demand-gated
    /// off, so the slot answers unknown.
    /// </summary>
    public static PublisherGithubPagesApiHost StartProduction(
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
                // registration shape is production's own - one adapter for the github-pages slot
                // beside its descriptor - with only GitHub itself substituted.
                var targets = _targets;
                var wire = _wire;
                services.AddSingleton<IPublisherAdapter>(provider => new GitHubPagesPublisher(
                    targets,
                    wire,
                    new HtmlExportAdapter(),
                    provider.GetRequiredService<ILogger<GitHubPagesPublisher>>()));
                services.AddSingleton(new PublisherTargetDescriptor(
                    GitHubPagesPublisher.TargetId,
                    SupportsDraft: false,
                    SupportsPublish: true,
                    SupportsReconcile: true,
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
