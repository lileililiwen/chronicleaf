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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// PUBLISHER-CONTRACT-001 and PUBLISHER-CONTRACT-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real asset, review,
/// evidence, vault and operation contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the asset
/// store, the approvals table, the routes, the access boundary, the attempt table, the
/// operation queue with its real worker, and the credential vault over a real operator key
/// file. The approved story every scenario publishes is produced by a real generation job
/// through the same scripted provider the drafts suite drives - the only substitution for
/// prose, and the same declared boundary: no stage here may be read as evidence that any
/// provider works. The publisher credential is genuinely sealed by the vault and genuinely
/// leased per call, and the fake adapter asserts the leased bytes equal the saved secret -
/// which is what makes "the credential stays a vault reference" a real assertion rather
/// than a vacuous one.
/// </para>
/// <para>
/// <b>What is never touched.</b> No remote side exists anywhere in this suite, because none
/// exists in the package: this contract ships no transport. The fake adapter is test-local,
/// on a target id production never takes, so no scenario can shadow a shipped transport.
/// A scenario that needs an export-only or unknown target exercises the registry, which
/// refuses before any adapter is resolved and before any job exists.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A receipt is read out of the raw
/// <c>publication_attempts</c> table, not only from the receipt route, and an operation is
/// read out of the raw <c>operations</c> table. The claims here are about what is
/// <em>stored</em> and what is <em>served</em>, and a value read back through the same
/// serializer that wrote it would agree with itself whether or not the bytes on disk were
/// right.
/// </para>
/// </remarks>
public sealed class PublisherContractTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("e8a1c4f2-7b3d-4a5e-9f6c-1d2b3a4e5c77");

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

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Success: a confirmed current preview and approval dispatches once.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_approved_head_dispatches_once_and_commits_published_receipt()
    {
        await using var fixture = await ArrangeAsync("pc_001_success");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-success",
                confirm = true,
            }));

        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        Assert.True(accepted.RootElement.GetProperty("created").GetBoolean());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", JobBudget);
        Assert.Equal(1, operation.Attempt);

        // One dispatch, through a credential leased for the call: the adapter saw the saved
        // secret's bytes, bound to this scope, and nothing else in the row or the response
        // carries them.
        Assert.Equal(1, adapter.Dispatches);
        Assert.Equal(PublisherCredentialValue, adapter.LastCredentialText);
        Assert.Equal(InstallationId, adapter.LastCredentialScope);

        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
        Assert.Equal("remote-1", attempt.RemoteId);
        Assert.Equal("https://test-fake.example/posts/remote-1", attempt.RemoteUrl);
        Assert.Null(attempt.ErrorCode);
        Assert.Equal(TestPublisherAdapter.FakeTarget, attempt.TargetId);
        Assert.Equal("publish", attempt.Mode);
        Assert.Equal(1, attempt.ContentRevision);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(stored!.BodyHash, attempt.BodyHash);

        Assert.DoesNotContain(
            PublisherCredentialValue, await published.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(PublisherCredentialValue, operation.ResultRef ?? string.Empty, StringComparison.Ordinal);

        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        var receiptBody = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal("published", receiptBody.RootElement.GetProperty("state").GetString());
        Assert.Equal("remote-1", receiptBody.RootElement.GetProperty("remoteId").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            receiptBody.RootElement.GetProperty("reconcileHint").ValueKind);
        Assert.DoesNotContain(
            PublisherCredentialValue, await receipt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Success: approval-covered pictures travel in the send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_approved_head_with_picture_carries_covered_pictures()
    {
        await using var fixture = await ArrangeAsync("pc_001_picture", withPicture: true);
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        Assert.NotNull(fixture.MediaId);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        Assert.Equal([fixture.MediaId.Value], preview.MediaPlan);
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-picture",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", JobBudget);

        // The send carried exactly the approval-covered pictures: the selected bytes under the
        // owner's caption, and nothing the approval does not cover.
        Assert.Equal(1, adapter.Dispatches);
        Assert.NotNull(adapter.LastSnapshot);
        var snapshot = adapter.LastSnapshot!;
        var picture = Assert.Single(snapshot.Pictures);
        Assert.Equal(fixture.MediaId.Value, picture.MediaId);
        Assert.Equal(PictureCaption, picture.Caption);
        Assert.Equal("image/png", picture.ContentType);
        Assert.Equal(TestPng.Create(64, 40), picture.Bytes);
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001/002 Success: an identical key resubmitted returns the receipt.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Success_identical_key_returns_original_receipt_without_second_dispatch()
    {
        await using var fixture = await ArrangeAsync("pc_001_replay");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = TestPublisherAdapter.FakeTarget,
            mode = "publish",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "pc-001-replay",
            confirm = true,
        };

        using var first = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstAccepted = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var operationId = firstAccepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = firstAccepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", JobBudget);
        Assert.Equal(1, adapter.Dispatches);

        using var second = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var replay = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.False(replay.RootElement.GetProperty("created").GetBoolean());
        Assert.Equal(attemptId, replay.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal(operationId, replay.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal("published", replay.RootElement.GetProperty("state").GetString());

        // The replay returned the stored receipt: no second job, no second dispatch, no second row.
        Assert.Equal(1, adapter.Dispatches);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: a draft without approval is refused before any send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_draft_without_approval_is_refused_before_send()
    {
        await using var fixture = await ArrangeAsync("pc_001_draft", approveStory: false);
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        using var preview = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = TestPublisherAdapter.FakeTarget, mode = "publish" }));
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Equal("approval_required", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = new string('0', 64),
                approvalId = Guid.NewGuid(),
                idempotencyKey = "pc-001-draft",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("approval_required", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        // Refused before a job existed: no dispatch, no attempt row, no operation for the key.
        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
        Assert.Equal(0, await AttemptRows.CountOperationsAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: a tampered preview is refused before any send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_tampered_preview_is_refused_before_send()
    {
        await using var fixture = await ArrangeAsync("pc_001_tampered");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var tampered = preview.PreviewHash[..63] + (preview.PreviewHash[63] == '0' ? '1' : '0');

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = tampered,
                approvalId,
                idempotencyKey = "pc-001-tampered",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("publisher_preview_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: an edit after the preview ends the approval before any send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_edited_head_is_refused_as_stale_before_send()
    {
        await using var fixture = await ArrangeAsync("pc_001_edited");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var stored = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.StoryAssetId, 1);
        Assert.NotNull(stored);

        using var edited = await owner.SendAsync(await owner.Patch(
            $"/api/assets/{fixture.StoryAssetId:D}",
            1,
            new
            {
                expectedRevision = 1,
                bodyMarkdown = stored!.BodyMarkdown + "\n\nAn owner note.\n",
                title = (string?)null,
            }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-edited",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("approval_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: a withdrawn approval is refused before any send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_revoked_approval_is_refused_before_send()
    {
        await using var fixture = await ArrangeAsync("pc_001_revoked");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var unapproved = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/unapprove", new { reason = "Second thoughts." }));
        Assert.Equal(HttpStatusCode.OK, unapproved.StatusCode);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-revoked",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, published.StatusCode);
        Assert.Equal("approval_stale", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Boundary: approving alone dispatches nothing.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Boundary_approval_alone_dispatches_nothing()
    {
        await using var fixture = await ArrangeAsync("pc_001_quiet");

        // Generating and approving ran inside ArrangeAsync. No publish was asked for, so no
        // attempt row and no publish operation may exist: approval is not publication consent.
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
        Assert.Equal(0, await AttemptRows.CountOperationsAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: approval without confirmation sends nothing.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_unconfirmed_publish_is_refused()
    {
        await using var fixture = await ArrangeAsync("pc_001_unconfirmed");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-unconfirmed",
                confirm = false,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        Assert.Equal(
            "publisher_confirmation_required", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-001 Failure: no credential, no send.
    // ==============================================================================================

    [Fact]
    public async Task Publish_001_Failure_missing_credential_is_refused_before_send()
    {
        await using var fixture = await ArrangeAsync("pc_001_nokey", savePublisherCredential: false);
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-001-nokey",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        Assert.Equal("publisher_credential_missing", EnvelopeCode(await published.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // Registry: Medium and LinkedIn are export-only, with the contract's own code.
    // ==============================================================================================

    [Fact]
    public async Task Registry_export_only_medium_is_refused_with_contract_code()
    {
        await using var fixture = await ArrangeAsync("pc_reg_medium");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        using var preview = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = "medium", mode = "publish" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        var previewCode = EnvelopeCode(await preview.Content.ReadAsStringAsync());
        Assert.Equal("publisher_target_export_only", previewCode);
        Assert.NotEqual("publisher_unsupported", previewCode);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = "medium",
                mode = "publish",
                previewHash = new string('0', 64),
                approvalId,
                idempotencyKey = "pc-reg-medium",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        var publishCode = EnvelopeCode(await published.Content.ReadAsStringAsync());
        Assert.Equal("publisher_target_export_only", publishCode);
        Assert.NotEqual("publisher_unsupported", publishCode);

        // Classified before any adapter, any credential lease and any job: no dispatch, no row.
        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
        Assert.Equal(0, await AttemptRows.CountOperationsAsync(fixture.Database));
    }

    [Fact]
    public async Task Registry_export_only_linkedin_is_refused_with_contract_code()
    {
        await using var fixture = await ArrangeAsync("pc_reg_linkedin");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = "linkedin",
                mode = "publish",
                previewHash = new string('0', 64),
                approvalId,
                idempotencyKey = "pc-reg-linkedin",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, published.StatusCode);
        var code = EnvelopeCode(await published.Content.ReadAsStringAsync());
        Assert.Equal("publisher_target_export_only", code);
        Assert.NotEqual("publisher_unsupported", code);

        Assert.Equal(0, adapter.Dispatches);
        Assert.Equal(0, await AttemptRows.CountAsync(fixture.Database));
    }

    [Fact]
    public async Task Registry_unknown_target_is_refused_and_fake_is_test_only()
    {
        await using var fixture = await ArrangeAsync("pc_reg_unknown");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        using var preview = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = "ghost", mode = "publish" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Equal("publisher_unknown_target", EnvelopeCode(await preview.Content.ReadAsStringAsync()));

        // The fake lives only in this suite's host: the production composition root knows no
        // target by its id, so the same preview there is unknown rather than shadowed.
        await using var production = PublisherContractApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var productionOwner = await OwnerClient.CreateAsync(production);

        using var productionPreview = await productionOwner.SendAsync(await productionOwner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish-preview",
            new { targetId = TestPublisherAdapter.FakeTarget, mode = "publish" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, productionPreview.StatusCode);
        Assert.Equal(
            "publisher_unknown_target",
            EnvelopeCode(await productionPreview.Content.ReadAsStringAsync()));

        Assert.Equal(0, adapter.Dispatches);
    }

    // ==============================================================================================
    // PUBLISH-FROM-VARIANT-001 Success: the registry classification is served verbatim.
    // ==============================================================================================

    [Fact]
    public async Task TargetList_001_Success_owner_reads_registry_classification()
    {
        await using var fixture = await ArrangeAsync("pc_targets_list");
        await using var host = PublisherContractApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var owner = await OwnerClient.CreateAsync(host);

        using var response = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var targets = body.RootElement.GetProperty("targets").EnumerateArray().ToList();

        // No transport is configured in this host, so the registry holds exactly the two
        // export-only descriptors this package registers. A configured transport appends its
        // own descriptor beside its adapter; the route serves whatever the registry holds,
        // which is what makes the pickers unable to drift from the server.
        Assert.Equal(2, targets.Count);

        foreach (var target in targets)
        {
            var names = target.EnumerateObject().Select(property => property.Name).Order().ToList();
            Assert.Equal(
                ["exportOnly", "supportsDraft", "supportsPublish", "supportsReconcile", "targetId"],
                names);
            Assert.True(target.GetProperty("exportOnly").GetBoolean());
            Assert.False(target.GetProperty("supportsDraft").GetBoolean());
            Assert.False(target.GetProperty("supportsPublish").GetBoolean());
            Assert.False(target.GetProperty("supportsReconcile").GetBoolean());
        }

        Assert.Equal(
            ["linkedin", "medium"],
            targets.Select(target => target.GetProperty("targetId").GetString()).Order().ToList());
    }

    // ==============================================================================================
    // PUBLISH-FROM-VARIANT-001 Failure: no session, no classification.
    // ==============================================================================================

    [Fact]
    public async Task TargetList_001_Failure_anonymous_read_is_refused()
    {
        await using var fixture = await ArrangeAsync("pc_targets_anonymous");
        await using var host = PublisherContractApiHost.StartProduction(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath);
        using var anonymous = host.CreateClient();

        using var response = await anonymous.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ==============================================================================================
    // PUBLISH-FROM-VARIANT-001 Boundary: an adapter with no contributed descriptor is served by
    // preview resolution but never listed - the list is the registry, not the adapters.
    // ==============================================================================================

    [Fact]
    public async Task TargetList_001_Boundary_adapter_without_descriptor_is_not_listed()
    {
        await using var fixture = await ArrangeAsync("pc_targets_unlisted");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        using var response = await owner.GetAsync("/api/publisher-targets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = body.RootElement
            .GetProperty("targets")
            .EnumerateArray()
            .Select(target => target.GetProperty("targetId").GetString())
            .ToList();

        Assert.DoesNotContain(TestPublisherAdapter.FakeTarget, ids);
        Assert.Equal(2, ids.Count);
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-002 Failure: an ambiguous timeout becomes unknown, never retried.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Failure_ambiguous_timeout_becomes_unknown_without_retry()
    {
        await using var fixture = await ArrangeAsync("pc_002_timeout");
        var adapter = new TestPublisherAdapter
        {
            OnPublish = (_, _, _) => throw new PublisherTimeoutException("remote-ambiguous-1"),
        };
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        var body = new
        {
            targetId = TestPublisherAdapter.FakeTarget,
            mode = "publish",
            previewHash = preview.PreviewHash,
            approvalId,
            idempotencyKey = "pc-002-timeout",
            confirm = true,
        };

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", JobBudget);
        Assert.Equal("publication_unknown", operation.ErrorCode);
        Assert.Equal(1, operation.Attempt);

        // The bytes may have left: unknown, retained, and the single attempt never retried.
        Assert.Equal(1, adapter.Dispatches);
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
        Assert.Equal("remote-ambiguous-1", attempt.RemoteId);

        // Resubmitting the identical request returns the unknown receipt: no reissue, no retry.
        using var replay = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish", body));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(attemptId, replayed.RootElement.GetProperty("attemptId").GetGuid());
        Assert.Equal("unknown", replayed.RootElement.GetProperty("state").GetString());

        Assert.Equal(1, adapter.Dispatches);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-002 Failure: a crash after the commit is one dispatch, then unknown.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Crash_after_commit_becomes_unknown_with_one_dispatch()
    {
        await using var fixture = await ArrangeAsync("pc_002_crash");
        var adapter = new TestPublisherAdapter
        {
            OnPublish = (_, _, _) => throw new InvalidOperationException("Simulated crash after upstream commit."),
        };
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-002-crash",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", JobBudget);
        Assert.Equal("publication_unknown", operation.ErrorCode);

        // A crash after the upstream commit is indistinguishable from an ambiguous timeout from
        // inside this process, so it becomes unknown rather than a retry: exactly one dispatch,
        // and the receipt - not a second post - is what a resubmission returns.
        Assert.Equal(1, adapter.Dispatches);
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
        Assert.Equal("publication_unknown", attempt.ErrorCode);
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-002 Boundary: the same key over different work is a conflict.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Boundary_same_key_different_mode_is_conflict_without_second_write()
    {
        await using var fixture = await ArrangeAsync("pc_002_conflict");
        var adapter = new TestPublisherAdapter();
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var publishPreview = await PreviewAsync(
            owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = publishPreview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-002-conflict",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "succeeded", JobBudget);
        Assert.Equal(1, adapter.Dispatches);

        // The same key over another mode: the operation fingerprint only covers the asset and
        // the revision, so the delivered enqueue answers with the original job - and the
        // attempt's own binding comparison refuses the wider difference as a conflict.
        var draftPreview = await PreviewAsync(
            owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "draft");

        using var conflict = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "draft",
                previewHash = draftPreview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-002-conflict",
                confirm = true,
            }));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "operation_idempotency_conflict", EnvelopeCode(await conflict.Content.ReadAsStringAsync()));

        Assert.Equal(1, adapter.Dispatches);
        Assert.Equal(1, await AttemptRows.CountAsync(fixture.Database));
    }

    // ==============================================================================================
    // PUBLISHER-CONTRACT-002 Reconcile: resolved where supported, manual where not.
    // ==============================================================================================

    [Fact]
    public async Task Publish_002_Reconcile_resolves_unknown_where_supported()
    {
        await using var fixture = await ArrangeAsync("pc_002_reconcile");
        var adapter = new TestPublisherAdapter
        {
            OnPublish = (_, _, _) => throw new PublisherTimeoutException(),
            OnReconcile = receipt => new PublishResult(
                PublishStates.Published, "remote-found-1", "https://test-fake.example/posts/remote-found-1"),
        };
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-002-reconcile",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", JobBudget);
        Assert.Equal("publication_unknown", operation.ErrorCode);

        using var reconciled = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var receipt = JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync());
        Assert.Equal("published", receipt.RootElement.GetProperty("state").GetString());
        Assert.Equal("remote-found-1", receipt.RootElement.GetProperty("remoteId").GetString());

        // The receipt moved; the operation row keeps its failure: history is appended to, never
        // rewritten, and reconciling is not a second dispatch.
        Assert.Equal(1, adapter.Dispatches);
        var reread = await OperationRows.ReadAsync(fixture.Database, operationId);
        Assert.NotNull(reread);
        Assert.Equal("failed", reread!.Status);
        Assert.Equal("publication_unknown", reread.ErrorCode);
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("published", attempt!.State);
    }

    [Fact]
    public async Task Publish_002_Reconcile_refused_where_unsupported_with_manual_hint()
    {
        await using var fixture = await ArrangeAsync("pc_002_manual");
        var adapter = new TestPublisherAdapter
        {
            SupportsReconcile = false,
            OnPublish = (_, _, _) => throw new PublisherTimeoutException(),
        };
        await using var host = PublisherContractApiHost.Start(
            fixture.Database, new FixedTimeProvider(Clock), fixture.KeyFile.FilePath, adapter);
        using var owner = await OwnerClient.CreateAsync(host);

        var preview = await PreviewAsync(owner, fixture.StoryAssetId, TestPublisherAdapter.FakeTarget, "publish");
        var approvalId = await ApprovalIdAsync(owner, fixture.StoryAssetId);

        using var published = await owner.SendAsync(await owner.Post(
            $"/api/assets/{fixture.StoryAssetId:D}/publish",
            new
            {
                targetId = TestPublisherAdapter.FakeTarget,
                mode = "publish",
                previewHash = preview.PreviewHash,
                approvalId,
                idempotencyKey = "pc-002-manual",
                confirm = true,
            }));
        var accepted = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();
        var attemptId = accepted.RootElement.GetProperty("attemptId").GetGuid();

        await host.DispatchAsync();
        _ = await OperationRows.AwaitAsync(fixture.Database, operationId, "failed", JobBudget);

        // No reliable reconcile keeps unknown, and the receipt says so: manual inspection, not
        // a reissue.
        using var receipt = await owner.GetAsync($"/api/publications/{attemptId:D}");
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal("manual", JsonDocument.Parse(await receipt.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reconcileHint").GetString());

        using var refused = await owner.SendAsync(await owner.Post(
            $"/api/publications/{attemptId:D}/reconcile", new { }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "publisher_reconcile_unavailable", EnvelopeCode(await refused.Content.ReadAsStringAsync()));

        Assert.Equal(1, adapter.Dispatches);
        var attempt = await AttemptRows.ReadAsync(fixture.Database, attemptId);
        Assert.NotNull(attempt);
        Assert.Equal("unknown", attempt!.State);
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
        bool savePublisherCredential = true,
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

            if (savePublisherCredential)
            {
                _ = await SaveCredentialAsync(setupOwner, PublisherService.CredentialProvider, PublisherCredentialValue);
            }
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
                    idempotencyKey = $"pc-capture-{purpose}",
                },
                0));

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var captureOperationId = Guid.Parse(JsonDocument
                .Parse(await requested.Content.ReadAsStringAsync())
                .RootElement.GetProperty("operationId").GetString()!);

            await capture.DispatchAsync();
            await OperationRows.AwaitAsync(database, captureOperationId, "succeeded", JobBudget);

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
                    idempotencyKey = $"pc-{purpose}",
                }));

            Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
            var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
            var draftOperationId = accepted.RootElement.GetProperty("operationId").GetGuid();

            await drafts.DispatchAsync();
            await OperationRows.AwaitAsync(database, draftOperationId, "succeeded", JobBudget);
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
        await ChronicleafPostgresFixture.MigrateAsync(database);

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
/// The test-local publisher adapter: the only adapter-shaped object in this suite, on a target
/// id production never takes (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>It converts the snapshot it is handed and records that it did.</b> The preview is a pure
/// function of the snapshot, the publish counts its own invocations and captures the leased
/// credential's bytes for the scenario to compare against the saved secret, and the reconcile
/// answers whatever the scenario scripted. It opens no socket and sends nothing: the scenarios
/// assert what Chronicleaf does with an answer - the approval bindings, the idempotency, the
/// unknown state and the receipt - and each of those is a property of this system rather than
/// of any transport.
/// </para>
/// <para>
/// The slot is <c>test-fake</c>, and that choice is load-bearing and asserted. The production
/// composition root registers no adapter for it, and the registry scenario proves a host
/// started without this instance answers <c>publisher_unknown_target</c> for it - so this
/// double can never shadow a shipped transport the way the drafts suite guards against.
/// </para>
/// </remarks>
public sealed class TestPublisherAdapter : IPublisherAdapter
{
    /// <summary>The slot this double serves. Production claims no adapter for it.</summary>
    public const string FakeTarget = "test-fake";

    private int _dispatches;

    /// <summary>Whether ambiguous receipts can be resolved by remote id.</summary>
    public bool SupportsReconcile { get; set; } = true;

    /// <summary>The scripted publish answer. Null answers a plain published receipt.</summary>
    public Func<PublishCommand, ApprovedAssetSnapshot, byte[], PublishResult>? OnPublish { get; set; }

    /// <summary>The scripted reconcile answer. Null echoes the receipt's own state.</summary>
    public Func<PublicationReceipt, PublishResult>? OnReconcile { get; set; }

    /// <summary>How many times the transport was invoked.</summary>
    public int Dispatches => _dispatches;

    /// <summary>The snapshot the last call was asked to send.</summary>
    public ApprovedAssetSnapshot? LastSnapshot { get; private set; }

    /// <summary>The leased credential's text, as the last call received it.</summary>
    public string? LastCredentialText { get; private set; }

    /// <summary>The scope the last leased credential belonged to.</summary>
    public Guid LastCredentialScope { get; private set; }

    /// <inheritdoc />
    public string TargetId => FakeTarget;

    /// <inheritdoc />
    public PublisherCapabilities Describe() => new(FakeTarget, true, true, SupportsReconcile);

    /// <inheritdoc />
    public Task<PublishPreview> PrepareAsync(
        ApprovedAssetSnapshot snapshot,
        PublisherTargetConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        return Task.FromResult(
            new PublishPreview(
                FakeTarget,
                snapshot.BodyHash,
                [.. snapshot.Pictures.Select(picture => picture.MediaId).OrderBy(id => id)],
                []));
    }

    /// <inheritdoc />
    public Task<PublishResult> PublishAsync(
        PublishCommand command,
        ApprovedAssetSnapshot snapshot,
        SecretLease credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(credential);

        Interlocked.Increment(ref _dispatches);
        var bytes = credential.Secret.ToArray();
        LastCredentialText = Encoding.UTF8.GetString(bytes);
        LastCredentialScope = credential.ScopeId;
        LastSnapshot = snapshot;

        if (OnPublish is not null)
        {
            return Task.FromResult(OnPublish(command, snapshot, bytes));
        }

        return Task.FromResult(new PublishResult(
            PublishStates.Published, "remote-1", "https://test-fake.example/posts/remote-1"));
    }

    /// <inheritdoc />
    public Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (OnReconcile is not null)
        {
            return Task.FromResult(OnReconcile(receipt));
        }

        return Task.FromResult(new PublishResult(receipt.State, receipt.RemoteId, receipt.RemoteUrl));
    }
}

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with a test-local
/// <see cref="TestPublisherAdapter"/> registered for a target id production never takes
/// (PUBLISHER-CONTRACT-001/002).
/// </summary>
public sealed class PublisherContractApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly TestPublisherAdapter? _adapter;
    private readonly LogCapture _logs;

    private PublisherContractApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        TestPublisherAdapter? adapter,
        LogCapture logs)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _adapter = adapter;
        _logs = logs;
    }

    /// <summary>Starts a host with the test-local fake as the adapter for its own slot.</summary>
    public static PublisherContractApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        TestPublisherAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        return new PublisherContractApiHost(connectionString, clock, keyFile, adapter, new LogCapture());
    }

    /// <summary>
    /// Starts a host with the production composition root and no fake at all.
    /// </summary>
    /// <remarks>
    /// This is what makes "the fake is test-only" a checked fact rather than a reviewer's
    /// assumption: the very same target id answers <c>publisher_unknown_target</c> here,
    /// because nothing was added and no configuration value can add it.
    /// </remarks>
    public static PublisherContractApiHost StartProduction(
        string connectionString,
        TimeProvider clock,
        string? keyFile) =>
        new(connectionString, clock, keyFile, adapter: null, new LogCapture());

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

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_adapter is not null)
            {
                // The one substitution, through the same registration seam a future transport
                // package uses: the fake is appended, not inserted, because production holds no
                // adapter for its slot and there is nothing to shadow. The registry scenario
                // proves the slot is empty without it.
                services.AddSingleton<IPublisherAdapter>(_adapter);
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

/// <summary>
/// One stored attempt row, read over a raw connection rather than through the API on purpose.
/// </summary>
internal static class AttemptRows
{
    public sealed record Attempt(
        Guid Id,
        string TargetId,
        Guid AssetId,
        long ContentRevision,
        string BodyHash,
        string Mode,
        string State,
        string? RemoteId,
        string? RemoteUrl,
        string? ErrorCode);

    public static async Task<Attempt?> ReadAsync(string connectionString, Guid attemptId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_id, asset_id, content_revision, body_hash, mode, state,
                   remote_id, remote_url, error_code
            FROM publication_attempts
            WHERE id = @id
            """;

        command.Parameters.AddWithValue("id", attemptId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Attempt(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM publication_attempts";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<long> CountOperationsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operations WHERE kind = 'publication.publish'";

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
