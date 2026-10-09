using System.Net;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// MARKDOWN-REVIEW-EXPORT-001 and MARKDOWN-REVIEW-EXPORT-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice and
/// asset contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice service,
/// the asset store, the approvals table, the routes, the access boundary and the database. The
/// draft a scenario reviews is produced by a real generation job through the same scripted
/// provider the drafts suite drives - the only substitution, and the same declared boundary: no
/// stage here may be read as evidence that any provider works.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> An approval is read out of the raw <c>content_approvals</c>
/// table, not only from the approve route, and an export is compared byte for byte against the
/// stored revision. The claims here are about what is <em>stored</em> and what is <em>served</em>,
/// and a value read back through the same serializer that wrote it would agree with itself
/// whether or not the bytes on disk were right.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one and each
/// test is named after the scenario it covers.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class MarkdownReviewExportTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("c8e3a5b2-1d04-5f7c-a683-2f9e4d6b8c11");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every review scenario drafts from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in no
    /// evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-001 Success: approving the current draft binds actor, content hash and
    // evidence revision.
    // ==============================================================================================

    [Fact]
    public async Task Review_001_Success_approving_the_current_draft_binds_actor_hash_and_evidence()
    {
        await using var fixture = await ArrangeAsync("rev_001_success");

        using var approved = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new
            {
                contentRevision = 1,
                evidenceRevision = fixture.BundleRevision,
                voiceFindingsAcknowledged = false,
            }));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());

        Assert.Equal(fixture.AssetId, body.RootElement.GetProperty("assetId").GetGuid());
        Assert.Equal(1, body.RootElement.GetProperty("contentRevision").GetInt64());
        Assert.Equal("text", body.RootElement.GetProperty("payloadKind").GetString());
        Assert.Equal(fixture.BundleRevision, body.RootElement.GetProperty("evidenceRevision").GetInt64());

        // The binding, read out of the row rather than the route: the digest is exactly the stored
        // body's, the evidence revision is the head the approver saw, and the voice and media
        // hashes are digests rather than copies of anything.
        var stored = await ReviewRows.ReadLiveAsync(fixture.Database, fixture.AssetId, 1);
        Assert.NotNull(stored);
        Assert.Equal(body.RootElement.GetProperty("approvalId").GetGuid(), stored!.Id);
        Assert.Equal(fixture.BodyHash, stored.BodyHash);
        Assert.Equal(body.RootElement.GetProperty("bodyHash").GetString(), stored.BodyHash);
        Assert.NotNull(stored.ActorId);
        Assert.Equal(64, stored.VoiceSnapshotHash.Length);
        Assert.Equal(64, stored.MediaSelectionHash.Length);
        Assert.Null(stored.ArtifactHash);
        Assert.Null(stored.InvalidatedAt);
        Assert.Null(stored.Reason);

        // And the review state derived from it.
        using var review = await fixture.Owner.GetAsync($"/api/assets/{fixture.AssetId:D}/review");
        var state = await ReadOkAsync(review);
        Assert.Equal("approved", state.RootElement.GetProperty("reviewStatus").GetString());
        Assert.Equal(1, state.RootElement.GetProperty("currentRevision").GetInt64());
        Assert.NotEqual(
            Guid.Empty,
            state.RootElement.GetProperty("approval").GetProperty("approvalId").GetGuid());
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-001 Failure: export without approval is 409 approval_required.
    // ==============================================================================================

    [Fact]
    public async Task Review_001_Failure_export_without_approval_returns_approval_required()
    {
        await using var fixture = await ArrangeAsync("rev_001_no_approval");

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");

        Assert.Equal(HttpStatusCode.Conflict, exported.StatusCode);
        var envelope = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal("approval_required", envelope.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-001 Boundary: editing the body after approval ends the approval, and the
    // export stays refused until a new approval.
    // ==============================================================================================

    [Fact]
    public async Task Review_001_Boundary_editing_the_body_after_approval_requires_reapproval()
    {
        await using var fixture = await ArrangeAsync("rev_001_edit_stale");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        var editedBody = $"{fixture.BodyText}\n\nA human correction, in the owner's own words.\n";
        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            1,
            new { expectedRevision = 1, bodyMarkdown = editedBody, title = (string?)null }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var edit = JsonDocument.Parse(await edited.Content.ReadAsStringAsync());
        Assert.Equal(2, edit.RootElement.GetProperty("revision").GetInt64());

        // The earlier approval is history now: the row stays, marked with why it ended, and no
        // live approval names any revision.
        var rows = await ReviewRows.ListAsync(fixture.Database, fixture.AssetId);
        Assert.Single(rows);
        Assert.NotNull(rows[0].InvalidatedAt);
        Assert.Equal("content_edited", rows[0].Reason);

        using var stale = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", envelope.RootElement.GetProperty("code").GetString());

        // A new approval of the edited revision authorizes the export again.
        await ApproveAsync(fixture, 2, fixture.BundleRevision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal(editedBody, await exported.Content.ReadAsStringAsync());
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-001 Boundary: appending evidence after approval ends the approval.
    // The old claims cite the old item ids - a carried-forward fact gets a new id in the new
    // bundle - so an edit cannot rebase them and only a fresh grounded generation can be
    // approved again.
    // ==============================================================================================

    [Fact]
    public async Task Review_001_Boundary_appending_evidence_after_approval_requires_reapproval()
    {
        await using var fixture = await ArrangeAsync("rev_001_evidence_stale");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        using var appended = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/evidence/manual",
            new { notes = "A limitation the owner recorded by hand.", expectedRevision = fixture.BundleRevision },
            fixture.BundleRevision));

        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);
        var head = await ReadEvidenceHeadAsync(fixture.Owner, fixture.MilestoneId);
        Assert.Equal(fixture.BundleRevision + 1, head.Revision);

        // The evidence the approval was bound to is no longer the head, so the export is refused
        // synchronously on read: there is no window in which a moved bundle still exports.
        using var stale = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", envelope.RootElement.GetProperty("code").GetString());

        // Approving the old revision against the new head is refused: the revision is grounded in
        // evidence the milestone has moved past, and the request names a binding that is no
        // longer true.
        using var rebinding = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new { contentRevision = 1, evidenceRevision = head.Revision, voiceFindingsAcknowledged = false }));

        Assert.Equal(HttpStatusCode.Conflict, rebinding.StatusCode);

        // An edit rebases the text onto the new head but cannot rebase the citations: the carried
        // facts carry new ids, so the old claims dangle and the approval is blocked with the
        // finding that names exactly that. This is the binding working as designed - an edit
        // must not smuggle stale citations into a fresh approval.
        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            1,
            new { expectedRevision = 1, bodyMarkdown = fixture.BodyText, title = (string?)null }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var smuggled = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new { contentRevision = 2, evidenceRevision = head.Revision, voiceFindingsAcknowledged = false }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, smuggled.StatusCode);
        var blocked = JsonDocument.Parse(await smuggled.Content.ReadAsStringAsync());
        Assert.Equal("approval_blocked", blocked.RootElement.GetProperty("code").GetString());

        // The honest remedy is a fresh grounded generation against the new head: its claims cite
        // the new ids, so it approves and exports.
        var summary = SummaryJson(head.Ids[0]);
        fixture.Provider.Rescript(summary, BodyFor(summary));

        var preview = await CreatePreviewAsync(
            fixture.Host, fixture.Owner, fixture.MilestoneId, fixture.CredentialId, head.Revision, head.Ids[0]);

        using var requested = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/drafts",
            new
            {
                previewId = preview.PreviewId.ToString("D"),
                consentHash = preview.ConsentHash,
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                evidenceRevision = preview.EvidenceRevision,
                voiceRevision = preview.VoiceRevision,
                idempotencyKey = "rev-001-evidence-stale-second",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());

        await fixture.Host.DispatchAsync();
        await OperationRows.AwaitAsync(
            fixture.Database, accepted.RootElement.GetProperty("operationId").GetGuid(), "succeeded", TimeSpan.FromSeconds(5));

        // The generation appended beside the human edit rather than overwriting it: the edited
        // revision is byte-identical and the new head is the grounded one.
        var editedRevision = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.AssetId, 2);
        Assert.Equal(fixture.BodyText, editedRevision!.BodyMarkdown);

        await ApproveAsync(fixture, 3, head.Revision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);

        var regenerated = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.AssetId, 3);
        Assert.Equal(regenerated!.BodyMarkdown, await exported.Content.ReadAsStringAsync());
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-001 Failure: deterministic findings block approval, and no
    // acknowledgment clears that.
    // ==============================================================================================

    [Fact]
    public async Task Review_001_Failure_findings_block_approval_and_no_acknowledgment_clears_it()
    {
        // The default voice declares Chinese and the generated body is English, so the stored
        // revision carries language findings the owner has not corrected.
        await using var fixture = await ArrangeAsync("rev_001_blocked", englishVoice: false);

        using var read = await fixture.Owner.GetAsync($"/api/assets/{fixture.AssetId:D}");
        var asset = await ReadOkAsync(read);
        Assert.NotEmpty(asset.RootElement.GetProperty("voiceFindings").EnumerateArray());

        using var blocked = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new { contentRevision = 1, evidenceRevision = fixture.BundleRevision, voiceFindingsAcknowledged = false }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var envelope = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("approval_blocked", envelope.RootElement.GetProperty("code").GetString());

        // The bypass the design forbids: acknowledging the findings instead of correcting them is
        // refused by name, and still stores no approval.
        using var bypass = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new { contentRevision = 1, evidenceRevision = fixture.BundleRevision, voiceFindingsAcknowledged = true }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bypass.StatusCode);
        var refused = JsonDocument.Parse(await bypass.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", refused.RootElement.GetProperty("code").GetString());

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, fixture.AssetId));
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-002 Success: the export serves the exact approved bytes and publishes
    // nothing.
    // ==============================================================================================

    [Fact]
    public async Task Review_002_Success_export_serves_the_exact_approved_bytes_and_publishes_nothing()
    {
        await using var fixture = await ArrangeAsync("rev_002_export");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        var operationsBefore = await CountRowsAsync(fixture.Database, "operations");

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.StartsWith("text/markdown", exported.Content.Headers.ContentType?.ToString() ?? string.Empty);
        Assert.NotNull(exported.Content.Headers.ContentDisposition);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition!.DispositionType);

        // The attachment bytes equal the approved revision, exactly: no rewritten link, no added
        // path, no envelope around them.
        var served = await exported.Content.ReadAsStringAsync();
        Assert.Equal(fixture.BodyText, served);

        var revision = await ContentRows.ReadRevisionAsync(fixture.Database, fixture.AssetId, 1);
        Assert.Equal(revision!.BodyMarkdown, served);

        // Owner-private and credential-free: no absolute host path, no credential the owner stored,
        // and no operation beyond the ones the draft already owned - an export serves bytes to the
        // owner that asked for them and calls nothing upstream.
        Assert.DoesNotContain("http://", served, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-never-used-for-a-real-call", served, StringComparison.Ordinal);
        Assert.Equal(operationsBefore, await CountRowsAsync(fixture.Database, "operations"));

        // The audit row records that bytes left the building, not a copy of them: the
        // delivered writer stores the detail inside its fixed metadata shape.
        var metadata = await ChronicleafPostgresFixture.ReadTextAsync(
            fixture.Database,
            "SELECT safe_metadata::text FROM audit_events WHERE action = 'content.review.exported' "
            + "ORDER BY occurred_at DESC LIMIT 1");
        Assert.False(string.IsNullOrEmpty(metadata));
        var detail = JsonDocument.Parse(metadata!).RootElement.GetProperty("detail").GetString()!;
        Assert.Contains($"revision=1", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.BodyText, metadata!, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-002 Failure: unsafe markup is stored as the owner's words and served as
    // text the browser saves rather than as a document it runs.
    // ==============================================================================================

    [Fact]
    public async Task Review_002_Failure_unsafe_markup_is_stored_inert_and_served_as_text()
    {
        await using var fixture = await ArrangeAsync("rev_002_xss");

        var hostile = $"{fixture.BodyText}\n\n<script>alert('review')</script>\n\n"
            + "[an unsafe link](javascript:alert('review'))\n\n"
            + "[a safe link](https://example.invalid/notes)\n";

        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            1,
            new { expectedRevision = 1, bodyMarkdown = hostile, title = (string?)null }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        await ApproveAsync(fixture, 2, fixture.BundleRevision);

        using var exported = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");

        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);

        // Served as Markdown the browser saves, never as HTML it runs: the tag and the unsafe
        // link travel as characters in an attachment, which is what keeps them inert on the way
        // out. The preview half of this scenario - that the panel renders the same characters
        // rather than running them - is the browser fixture's, which drives the shipped panel.
        var mediaType = exported.Content.Headers.ContentType?.ToString() ?? string.Empty;
        Assert.StartsWith("text/markdown", mediaType);
        Assert.DoesNotContain("text/html", mediaType, StringComparison.OrdinalIgnoreCase);

        var served = await exported.Content.ReadAsStringAsync();
        Assert.Equal(hostile, served);
        Assert.Contains("<script>alert('review')</script>", served, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // MARKDOWN-REVIEW-EXPORT-002 Boundary: a concurrent edit wins over a late approval, and the
    // unreviewed head is never exportable.
    // ==============================================================================================

    [Fact]
    public async Task Review_002_Boundary_a_late_approval_of_a_stale_revision_exports_nothing_unreviewed()
    {
        await using var fixture = await ArrangeAsync("rev_002_race");

        // The edit lands first and moves the head. The approval that names the revision the
        // approver read is now an approval of text nobody is looking at, so it is refused.
        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            1,
            new { expectedRevision = 1, bodyMarkdown = $"{fixture.BodyText}\n", title = (string?)null }));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        using var late = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
            new { contentRevision = 1, evidenceRevision = fixture.BundleRevision, voiceFindingsAcknowledged = false }));

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);

        // Nothing live authorizes anything, so neither the old revision nor the new head exports.
        foreach (var query in new[] { "format=markdown", "format=markdown&revision=2" })
        {
            using var refused = await fixture.Owner.GetAsync($"/api/assets/{fixture.AssetId:D}/export?{query}");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, fixture.AssetId));
    }

    // ==============================================================================================
    // Edit refusals: the conditional-write contract.
    // ==============================================================================================

    [Fact]
    public async Task Edit_refuses_a_missing_precondition_a_stale_head_and_an_empty_body()
    {
        await using var fixture = await ArrangeAsync("rev_edit_refusals");

        // No If-Match is a 428 answered before anything is written. The body carries no
        // revision either, so the refusal is about the missing precondition rather than a
        // disagreement between two values.
        using var missing = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            null,
            new { expectedRevision = (long?)null, bodyMarkdown = "words without a precondition", title = (string?)null }));

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        // A stale head is a 409, and the stored head is untouched.
        using var stale = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            99,
            new { expectedRevision = 99, bodyMarkdown = "stale words", title = (string?)null }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // An empty body is a 422 naming the field.
        using var empty = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.AssetId:D}",
            1,
            new { expectedRevision = 1, bodyMarkdown = "   ", title = (string?)null }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        var envelope = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", envelope.RootElement.GetProperty("code").GetString());

        var asset = await ContentRows.ReadAssetForMilestoneAsync(fixture.Database, fixture.MilestoneId);
        Assert.Equal(1, asset!.CurrentRevision);
    }

    // ==============================================================================================
    // Withdrawing an approval ends exports and keeps the row as history.
    // ==============================================================================================

    [Fact]
    public async Task Unapprove_withdraws_the_live_approval_and_ends_exports()
    {
        await using var fixture = await ArrangeAsync("rev_unapprove");
        await ApproveAsync(fixture, 1, fixture.BundleRevision);

        using var withdrawn = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/unapprove",
            new { reason = "The numbers need a second look." }));

        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);

        var rows = await ReviewRows.ListAsync(fixture.Database, fixture.AssetId);
        Assert.Single(rows);
        Assert.NotNull(rows[0].InvalidatedAt);
        Assert.Equal("The numbers need a second look.", rows[0].Reason);

        using var review = await fixture.Owner.GetAsync($"/api/assets/{fixture.AssetId:D}/review");
        var state = await ReadOkAsync(review);
        Assert.Equal("invalidated", state.RootElement.GetProperty("reviewStatus").GetString());

        using var refused = await fixture.Owner.GetAsync(
            $"/api/assets/{fixture.AssetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    // ==============================================================================================
    // Migration and schema: the approvals table, the one-approval index and the vocabulary.
    // ==============================================================================================

    [Fact]
    public async Task The_approval_table_index_and_vocabulary_come_from_the_forward_migration_and_readiness_requires_it()
    {
        var database = await postgres.CreateDatabaseAsync("rev_migration");

        // Readiness requires the review migration by id, and it is the newest required one by
        // position rather than by name - a named "newest" is a value somebody has to remember to
        // update, which is exactly what made the older scenarios in this repository go red.
        Assert.Contains(ChronicleafSchema.MarkdownReviewExportMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // A database stopped at the screenshot-curation schema is short of exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds
                .Where(id => id != ChronicleafSchema.MarkdownReviewExportMigrationId)
                .ToArray());
        Assert.Equal([ChronicleafSchema.MarkdownReviewExportMigrationId], missing);

        // The columns this package binds exist with the bounds this package enforces.
        Assert.Equal(
            "32",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT character_maximum_length::text FROM information_schema.columns " +
                "WHERE table_name = 'content_approvals' AND column_name = 'payload_kind'"));
        Assert.Equal(
            "ux_content_approvals_scope_id_asset_id_revision",
            await ChronicleafPostgresFixture.ReadTextAsync(
                database,
                "SELECT indexname FROM pg_indexes WHERE tablename = 'content_approvals' " +
                "AND indexname = 'ux_content_approvals_scope_id_asset_id_revision'"));

        // The digest and revision vocabulary, enforced by the database rather than by a caller
        // being careful: an approval that could name revision zero or carry a digest that is not
        // a digest is a row the export could not honestly re-check.
        foreach (var constraint in new[]
                 {
                     "ck_content_approvals_content_revision_at_least_one",
                     "ck_content_approvals_evidence_revision_at_least_one",
                     "ck_content_approvals_body_hash_sha256_hex",
                     "ck_content_approvals_voice_snapshot_hash_sha256_hex",
                     "ck_content_approvals_media_selection_hash_sha256_hex",
                 })
        {
            Assert.Equal(
                constraint,
                await ChronicleafPostgresFixture.ReadTextAsync(
                    database,
                    "SELECT conname FROM pg_constraint WHERE conname = '" + constraint + "'"));
        }
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, a confirmed milestone
    /// with real recorded evidence, a real sealed credential, a real authorized preview, a stored
    /// draft produced by a real generation job, and the asset that draft belongs to.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(string purpose, bool englishVoice = true)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;
        Guid credentialId;
        EvidenceHead head;

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

            if (englishVoice)
            {
                // The generated body is English, so the voice it is judged under must be too:
                // the default profile declares Chinese and would report language findings on
                // every field, which is the blocked scenario's own condition rather than this
                // fixture's.
                using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                    $"/api/projects/{projectId:D}/voice-profile",
                    new { presetId = "technical-blog", overrides = new { language = "en" } },
                    0));

                Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);
            }

            credentialId = await SaveCredentialAsync(setupOwner);
            head = await ReadEvidenceHeadAsync(setupOwner, milestoneId);
        }

        var summary = SummaryJson(head.Ids[0]);
        var scripted = ScriptedDraftProvider.With(summary, BodyFor(summary));

        var host = GroundedMarkdownDraftsApiHost.Start(database, clock, keyFile.FilePath, scripted);
        var owner = await OwnerClient.CreateAsync(host);

        var preview = await CreatePreviewAsync(host, owner, milestoneId, credentialId, head.Revision, head.Ids[0]);

        using var requested = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/drafts",
            new
            {
                previewId = preview.PreviewId.ToString("D"),
                consentHash = preview.ConsentHash,
                providerId = ScriptedDraftProvider.Slot,
                model = ScriptedDraftProvider.KnownModel,
                evidenceRevision = preview.EvidenceRevision,
                voiceRevision = preview.VoiceRevision,
                idempotencyKey = $"rev-{purpose}",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        var revision = await ContentRows.ReadRevisionAsync(database, asset!.Id, 1);
        Assert.NotNull(revision);

        var result = new Fixture(
            database,
            host,
            keyFile,
            owner,
            scripted,
            projectId,
            milestoneId,
            asset.Id,
            credentialId,
            revision!.BodyMarkdown,
            revision.BodyHash,
            head.Id,
            head.Revision);

        return result;
    }

    private static async Task ApproveAsync(Fixture fixture, long contentRevision, long evidenceRevision)
    {
        using var approved = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{fixture.AssetId:D}/approve",
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
            new { name = $"{purpose} 项目", description = $"{purpose} 说明。" }));

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
                title = "第一次可复现发布",
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = "记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> SaveCredentialAsync(OwnerClient owner)
    {
        using var response = await owner.SendAsync(await owner.Put(
            $"/api/settings/credentials/{ScriptedDraftProvider.Slot}",
            new { value = "test-key-never-used-for-a-real-call" },
            0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        Assert.DoesNotContain("test-key-never-used-for-a-real-call", raw, StringComparison.Ordinal);

        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<EvidenceHead> ReadEvidenceHeadAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        var body = await ReadOkAsync(response);

        var bundle = body.RootElement.GetProperty("items")[0];

        return new EvidenceHead(
            bundle.GetProperty("id").GetGuid(),
            bundle.GetProperty("revision").GetInt64(),
            [.. bundle.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())]);
    }

    private static async Task<PreviewBody> CreatePreviewAsync(
        GroundedMarkdownDraftsApiHost host,
        OwnerClient owner,
        Guid milestoneId,
        Guid credentialId,
        long evidenceRevision,
        Guid evidenceId)
    {
        _ = host;

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

    private static async Task<JsonDocument> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<long> CountRowsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record EvidenceHead(Guid Id, long Revision, IReadOnlyList<Guid> Ids);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared server.
    /// </summary>
    private sealed record Fixture(
        string Database,
        GroundedMarkdownDraftsApiHost Host,
        OperatorKeyFile KeyFile,
        OwnerClient Owner,
        ScriptedDraftProvider Provider,
        Guid ProjectId,
        Guid MilestoneId,
        Guid AssetId,
        Guid CredentialId,
        string BodyText,
        string BodyHash,
        Guid EvidenceBundleId,
        long BundleRevision) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }

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
}

/// <summary>
/// The approvals table read back out, for the assertions about what is <em>stored</em>.
/// </summary>
internal static class ReviewRows
{
    public sealed record Approval(
        Guid Id,
        Guid ScopeId,
        Guid AssetId,
        long ContentRevision,
        Guid? ActorId,
        string PayloadKind,
        string BodyHash,
        long EvidenceRevision,
        string VoiceSnapshotHash,
        string MediaSelectionHash,
        string? ArtifactHash,
        DateTimeOffset ApprovedAt,
        DateTimeOffset? InvalidatedAt,
        string? Reason);

    public static async Task<Approval?> ReadLiveAsync(string connectionString, Guid assetId, long revision)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
                   evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
                   approved_at, invalidated_at, reason
            FROM content_approvals
            WHERE asset_id = @asset AND content_revision = @revision AND invalidated_at IS NULL
            """;

        command.Parameters.AddWithValue("asset", assetId);
        command.Parameters.AddWithValue("revision", revision);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return Read(reader);
    }

    public static async Task<IReadOnlyList<Approval>> ListAsync(string connectionString, Guid assetId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
                   evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
                   approved_at, invalidated_at, reason
            FROM content_approvals
            WHERE asset_id = @asset
            ORDER BY content_revision DESC
            """;

        command.Parameters.AddWithValue("asset", assetId);

        var rows = new List<Approval>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static Approval Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetGuid(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt64(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetString(13));
}
