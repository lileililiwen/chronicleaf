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
/// CONTENT-ASSET-VARIANTS-001 and CONTENT-ASSET-VARIANTS-002 against a real disposable PostgreSQL,
/// the real <c>Chronicleaf.Api</c> host, the real operation queue and the real evidence, voice,
/// narrative, asset and review contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the milestone, the evidence store, the voice service,
/// the narrative context, the asset store, the approvals table, the variant service, the routes,
/// the access boundary and the database. The approved story every scenario derives from is produced
/// by a real generation job through the same scripted provider the drafts suite drives - the only
/// substitution, and the same declared boundary: no stage here may be read as evidence that any
/// provider works. Deriving itself calls no provider at all.
/// </para>
/// <para>
/// <b>Where a result is read from.</b> A variant is read out of the raw <c>content_assets</c> and
/// <c>content_revisions</c> tables, not only from the derive route, and an approval is read out of
/// the raw <c>content_approvals</c> table. The claims here are about what is <em>stored</em> and
/// what is <em>served</em>, and a value read back through the same serializer that wrote it would
/// agree with itself whether or not the bytes on disk were right.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one and each
/// test is named after the scenario it covers.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class ContentAssetVariantsTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("d9f4b6c3-2e15-607d-b794-3f0a5e7c9d22");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The evidence every variant scenario derives from. Free of digits on purpose, for the same
    /// reason the drafts suite's own note is: the numeral rule refuses a figure that appears in no
    /// evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string UnknownLabel = "Unknown — no source evidence recorded.";

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Success: every format derives with its sections and citations.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Success_all_seven_formats_carry_sections_and_citations()
    {
        await using var fixture = await ArrangeAsync("var_001_all");

        var expectedSections = new Dictionary<string, string[]>
        {
            ["changelog"] = ["## Added", "## Improved", "## Fixed", "## Known limitations"],
            ["technical-blog"] = ["## Why", "## Problem", "## Solution", "## Effect", "## Limitations"],
            ["social-short"] = ["## Known limitations"],
            ["github-release"] = ["## What's changed", "## Added", "## Fixed", "## Known limitations"],
            ["readme-update"] = ["README proposal", "```proposal", "## Source changes", "## Known limitations"],
            ["portfolio"] = ["## Context", "## Contribution", "## Result", "## What is next", "## Known limitations"],
            ["weekly-progress"] = ["## Completed", "## Resolved", "## In progress", "## Next week", "## Known limitations"],
        };

        foreach (var (type, sections) in expectedSections)
        {
            using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
                $"/api/milestones/{fixture.MilestoneId:D}/assets",
                new
                {
                    type,
                    sourceAssetId = (string?)null,
                    evidenceRevision = fixture.BundleRevision,
                    voiceRevision = fixture.VoiceRevision,
                }));

            Assert.True(
                derived.StatusCode == HttpStatusCode.Created,
                $"derive {type} answered {(int)derived.StatusCode}: {await derived.Content.ReadAsStringAsync()}");

            var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
            var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

            Assert.NotEqual(fixture.AssetId, assetId);
            Assert.Equal(type, answer.RootElement.GetProperty("type").GetString());
            Assert.Equal(fixture.AssetId, answer.RootElement.GetProperty("sourceAssetId").GetGuid());
            Assert.Equal(1, answer.RootElement.GetProperty("sourceRevision").GetInt64());

            // The stored bytes, read out of the row rather than the route.
            var stored = await ContentRows.ReadRevisionAsync(fixture.Database, assetId, 1);
            Assert.NotNull(stored);
            Assert.Equal(answer.RootElement.GetProperty("bodyHash").GetString(), stored!.BodyHash);
            Assert.Equal(ContentRevision.Hash(stored.BodyMarkdown), stored.BodyHash);

            foreach (var section in sections)
            {
                Assert.Contains(section, stored.BodyMarkdown, StringComparison.Ordinal);
            }

            // Every footnote the body cites is defined, and every defined footnote is a claim of
            // the source summary: the variant cites the evidence it came from, nothing else.
            Assert.Contains(KnownLimitation, stored.BodyMarkdown, StringComparison.Ordinal);
            Assert.Contains("[^", stored.BodyMarkdown, StringComparison.Ordinal);

            var sourceClaims = await SourceClaimIdsAsync(fixture);
            foreach (var cited in CitedIds(stored.BodyMarkdown))
            {
                Assert.Contains(cited, sourceClaims);
            }

            Assert.Equal(sourceClaims.Count, DefinedIds(stored.BodyMarkdown).Count);
        }
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Success: narrative lines arrive attributed, never as new facts.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Success_narrative_lines_arrive_attributed_without_new_facts()
    {
        await using var fixture = await ArrangeAsync("var_001_narrative");

        using var entry = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/narrative",
            new
            {
                kind = "decision",
                occurredAt = "2026-10-01T09:00:00Z",
                text = "The team chose the digest comparison over log inspection.",
                sourceEvidenceIds = new[] { fixture.EvidenceId.ToString("D") },
                visibility = "exportable",
            }));

        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "technical-blog",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var stored = await ContentRows.ReadRevisionAsync(
            fixture.Database, answer.RootElement.GetProperty("assetId").GetGuid(), 1);

        Assert.NotNull(stored);
        Assert.Contains("## Project history", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(
            "The team chose the digest comparison over log inspection.",
            stored.BodyMarkdown,
            StringComparison.Ordinal);
        Assert.Contains("(from project history)", stored.BodyMarkdown, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Failure: a metric with no source evidence is not invented.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Failure_missing_fields_are_labeled_unknown_never_invented()
    {
        await using var fixture = await ArrangeAsync("var_001_unknown");

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "weekly-progress",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var stored = await ContentRows.ReadRevisionAsync(
            fixture.Database, answer.RootElement.GetProperty("assetId").GetGuid(), 1);

        Assert.NotNull(stored);

        // No source section feeds "In progress", so it reads the unknown label rather than a
        // fabricated status.
        Assert.Contains("## In progress", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains(UnknownLabel, stored.BodyMarkdown, StringComparison.Ordinal);

        // And no figure anywhere in the body is one the evidence never stated. Citation labels are
        // stripped first, because a footnote id is an identifier, not a metric.
        var prose = System.Text.RegularExpressions.Regex.Replace(
            stored.BodyMarkdown, @"\[\^[^\]]+\]", string.Empty);

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(prose, @"\d[\d.,]*"))
        {
            Assert.Contains(
                match.Value,
                EvidenceNote,
                StringComparison.Ordinal);
        }
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Failure: an unknown format is refused before any row exists.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Failure_unknown_format_is_refused_before_any_row_exists()
    {
        await using var fixture = await ArrangeAsync("var_001_unknown_type");

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "press-release",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, derived.StatusCode);
        var body = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());

        using var listed = await fixture.Owner.GetAsync($"/api/milestones/{fixture.MilestoneId:D}/assets");
        var list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Empty(list.RootElement.GetProperty("variants").EnumerateArray());
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Failure: deriving from an unapproved story is refused.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Failure_derive_without_approval_returns_approval_required()
    {
        await using var fixture = await ArrangeAsync("var_001_no_approval", approve: false);

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "changelog",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Conflict, derived.StatusCode);
        var body = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        Assert.Equal("approval_required", body.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-001 Boundary: readme-update is a proposal, never a repository write.
    // ==============================================================================================

    [Fact]
    public async Task Variants_001_Boundary_readme_update_is_a_proposal_without_a_repo_write()
    {
        await using var fixture = await ArrangeAsync("var_001_readme");

        var milestoneBefore = await ReadMilestoneRevisionAsync(fixture);

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "readme-update",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var stored = await ContentRows.ReadRevisionAsync(
            fixture.Database, answer.RootElement.GetProperty("assetId").GetGuid(), 1);

        Assert.NotNull(stored);
        Assert.Contains("no repository was changed", stored!.BodyMarkdown, StringComparison.Ordinal);
        Assert.Contains("```proposal", stored.BodyMarkdown, StringComparison.Ordinal);

        // Nothing about the milestone moved: a proposal proposes.
        Assert.Equal(milestoneBefore, await ReadMilestoneRevisionAsync(fixture));
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-002 Success: an approved source adapts into a draft.
    // ==============================================================================================

    [Fact]
    public async Task Variants_002_Success_an_approved_source_adapts_into_a_draft()
    {
        await using var fixture = await ArrangeAsync("var_002_draft");

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "changelog",
                sourceAssetId = fixture.AssetId.ToString("D"),
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

        // A new asset, and the review package reports what it is: a draft that was never approved.
        using var review = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/review");
        var state = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.Equal("draft", state.RootElement.GetProperty("reviewStatus").GetString());

        using var read = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}");
        var view = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("draft", view.RootElement.GetProperty("status").GetString());
        Assert.Equal("changelog", view.RootElement.GetProperty("type").GetString());

        Assert.Empty(await ReviewRows.ListAsync(fixture.Database, assetId));

        // And the source approval is exactly where it was: deriving published nothing about it.
        var sourceApprovals = await ReviewRows.ListAsync(fixture.Database, fixture.AssetId);
        Assert.Single(sourceApprovals);
        Assert.Null(sourceApprovals[0].InvalidatedAt);
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-002 Failure: a preset conflicting with a project setting loses.
    // ==============================================================================================

    [Fact]
    public async Task Variants_002_Failure_project_voice_setting_wins_over_the_preset()
    {
        await using var fixture = await ArrangeAsync("var_002_voice");

        // The technical-blog preset allows sixty title characters. The project shortens that to
        // twenty-four, and the variant's title must obey the project, not the preset.
        using var saved = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/voice-profile",
            new
            {
                presetId = "technical-blog",
                overrides = new { language = "en", titleMaxChars = 24 },
            },
            1));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var profile = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var voiceRevision = profile.RootElement.GetProperty("revision").GetInt64();
        Assert.Equal(2, voiceRevision);

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "social-short",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision,
            }));

        Assert.True(
            derived.StatusCode == HttpStatusCode.Created,
            $"derive answered {(int)derived.StatusCode}: {await derived.Content.ReadAsStringAsync()}");

        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var stored = await ContentRows.ReadRevisionAsync(
            fixture.Database, answer.RootElement.GetProperty("assetId").GetGuid(), 1);

        Assert.NotNull(stored);
        Assert.Equal(2, stored!.VoiceRevision);

        var heading = stored.BodyMarkdown.Split('\n')[0];
        Assert.StartsWith("# ", heading, StringComparison.Ordinal);
        Assert.True(
            heading.Length - 2 <= 24,
            $"the project allows 24 title characters and the preset allows 60, but the heading is {heading.Length - 2}: {heading}");
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-002 Boundary: moved evidence refuses the derivation as stale.
    // ==============================================================================================

    [Fact]
    public async Task Variants_002_Boundary_moved_evidence_refuses_derive_as_stale()
    {
        await using var fixture = await ArrangeAsync("var_002_stale");

        using var appended = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/evidence/manual",
            new { notes = "A second note, recorded after the approval.", expectedRevision = fixture.BundleRevision },
            fixture.BundleRevision));

        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);
        var head = await ReadEvidenceHeadAsync(fixture.Owner, fixture.MilestoneId);
        Assert.Equal(2, head.Revision);

        // The revision the owner was looking at is no longer the head: a 409 naming the conflict.
        using var conflicted = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "changelog",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Conflict, conflicted.StatusCode);

        // And naming the new head does not help either: the source approval is bound to the first
        // revision, so the derivation is stale rather than current.
        using var stale = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "changelog",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision + 1,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", body.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // CONTENT-ASSET-VARIANTS-002 Boundary: a variant's own approval binds like its source's.
    // ==============================================================================================

    [Fact]
    public async Task Variants_002_Boundary_variant_approval_binds_and_stales_like_the_source()
    {
        await using var fixture = await ArrangeAsync("var_002_bindings");

        using var derived = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/assets",
            new
            {
                type = "portfolio",
                sourceAssetId = (string?)null,
                evidenceRevision = fixture.BundleRevision,
                voiceRevision = fixture.VoiceRevision,
            }));

        Assert.Equal(HttpStatusCode.Created, derived.StatusCode);
        var answer = JsonDocument.Parse(await derived.Content.ReadAsStringAsync());
        var assetId = answer.RootElement.GetProperty("assetId").GetGuid();

        using var approved = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/assets/{assetId:D}/approve",
            new
            {
                contentRevision = 1,
                evidenceRevision = fixture.BundleRevision,
                voiceFindingsAcknowledged = false,
            }));

        Assert.True(
            approved.StatusCode == HttpStatusCode.OK,
            $"approve answered {(int)approved.StatusCode}: {await approved.Content.ReadAsStringAsync()}");

        using var review = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/review");
        var state = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
        Assert.Equal("approved", state.RootElement.GetProperty("reviewStatus").GetString());

        // Evidence moves after the variant's approval: the export is stale until a new approval,
        // exactly as for the story it came from. Approval bindings are preserved, not inherited.
        using var appended = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/evidence/manual",
            new { notes = "A later note, recorded after the variant approval.", expectedRevision = fixture.BundleRevision },
            fixture.BundleRevision));

        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);

        using var exported = await fixture.Owner.GetAsync($"/api/assets/{assetId:D}/export?format=markdown");
        Assert.Equal(HttpStatusCode.Conflict, exported.StatusCode);
        var stale = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal("approval_stale", stale.RootElement.GetProperty("code").GetString());
    }

    // ==============================================================================================
    // Fixture. The approved story every scenario derives from.
    // ==============================================================================================

    private async Task<Fixture> ArrangeAsync(string purpose, bool approve = true)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestoneId;
        Guid credentialId;
        EvidenceHead head;
        long voiceRevision;

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

            // The generated body is English, so the voice it is judged under must be too:
            // the default profile declares Chinese and would report language findings on
            // every field, which would refuse the approval this fixture needs.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "technical-blog", overrides = new { language = "en" } },
                0));

            Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);
            voiceRevision = JsonDocument
                .Parse(await voiced.Content.ReadAsStringAsync())
                .RootElement.GetProperty("revision").GetInt64();

            credentialId = await SaveCredentialAsync(setupOwner);
            head = await ReadEvidenceHeadAsync(setupOwner, milestoneId);
        }

        // The draft a scenario derives from, through the same scripted provider the drafts suite
        // drives. The host below also serves the variant routes on the same composition root, and
        // deriving calls no provider - so no second host is needed and no double is ever read by
        // the scenarios.
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
                idempotencyKey = $"var-{purpose}",
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, operationId, "succeeded", TimeSpan.FromSeconds(5));

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        var fixture = new Fixture(
            database,
            host,
            keyFile,
            owner,
            projectId,
            milestoneId,
            asset!.Id,
            credentialId,
            head.Id,
            head.Revision,
            voiceRevision,
            head.Ids[0]);

        if (approve)
        {
            await ApproveAsync(fixture, 1, head.Revision);
        }

        return fixture;
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
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bundle = body.RootElement.GetProperty("items")[0];

        return new EvidenceHead(
            bundle.GetProperty("id").GetGuid(),
            bundle.GetProperty("revision").GetInt64(),
            [.. bundle.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())]);
    }

    private static async Task<long> ReadMilestoneRevisionAsync(Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(fixture.Database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision FROM milestones WHERE id = @id";
        command.Parameters.AddWithValue("id", fixture.MilestoneId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<HashSet<string>> SourceClaimIdsAsync(Fixture fixture)
    {
        using var response = await fixture.Owner.GetAsync($"/api/assets/{fixture.AssetId:D}");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return
        [
            .. body.RootElement.GetProperty("claims").EnumerateArray()
                .Select(claim => claim.GetProperty("id").GetString()!),
        ];
    }

    private static List<string> CitedIds(string body)
    {
        var cited = new List<string>();

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(body, @"\[\^([^\]]+)\]"))
        {
            // A definition is not a citation: only the runs outside a definition line count.
            var lineStart = body.LastIndexOf('\n', match.Index) + 1;
            if (body.Substring(lineStart, match.Index - lineStart).TrimStart().StartsWith("[^", StringComparison.Ordinal))
            {
                continue;
            }

            cited.Add(match.Groups[1].Value);
        }

        return cited;
    }

    private static List<string> DefinedIds(string body)
    {
        var defined = new List<string>();

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("[^", StringComparison.Ordinal))
            {
                continue;
            }

            var end = trimmed.IndexOf(']', StringComparison.Ordinal);
            if (end > 2)
            {
                defined.Add(trimmed.Substring(2, end - 2));
            }
        }

        return defined;
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
        Guid ProjectId,
        Guid MilestoneId,
        Guid AssetId,
        Guid CredentialId,
        Guid EvidenceBundleId,
        long BundleRevision,
        long VoiceRevision,
        Guid EvidenceId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            await Host.DisposeAsync();
            KeyFile.Dispose();
        }
    }
}
