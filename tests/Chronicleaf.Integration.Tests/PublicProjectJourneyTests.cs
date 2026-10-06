using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// PUBLIC-PROJECT-JOURNEY-001 and PUBLIC-PROJECT-JOURNEY-002 against a real disposable
/// PostgreSQL, the real <c>Chronicleaf.Api</c> host and the real project, milestone, evidence,
/// asset, approval and media contracts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is real.</b> The composition root, the projects, the confirmed milestones, the
/// evidence store, the asset store, the approvals table, the media store, the journey table,
/// the routes, the access boundary and the database. The drafts a scenario publishes are
/// produced by real generation jobs through the same scripted provider the drafts suite
/// drives, and the picture one of them carries is produced by a real capture job through the
/// same recording provider the capture suite drives - the two declared substitutions, and no
/// stage here may be read as evidence that any provider works or that a real page was
/// photographed.
/// </para>
/// <para>
/// <b>Where a secret is proven absent.</b> The anonymous routes are read with a client that
/// holds no session at all, and every served body is searched for the private sentinels the
/// fixture planted: milestone notes, a candidate title, an unselected picture and the
/// credential value. A projection that carried any of them would fail here by name.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one, and
/// the slug, revision and approval bindings this package reuses rather than redefines are
/// covered as carry-over. Each test is named after the scenario it covers.
/// </para>
/// </remarks>
public sealed class PublicProjectJourneyTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("e7f9c2d4-3b15-6f8e-c6a5-2d9f4b8c1e73");

    private static readonly TimeSpan JobBudget = TimeSpan.FromSeconds(90);

    private const string PublicUrl = "https://example.com/pricing";

    /// <summary>
    /// The evidence every journey scenario drafts from. Free of digits on purpose, for the same
    /// reason the review suite's own note is: the numeral rule refuses a figure that appears in no
    /// evidence item, and a note full of numbers would let claims pass for the wrong reason.
    /// </summary>
    private const string EvidenceNote =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string SecondEvidenceNote =
        "The slice now ships from a tagged commit. The tag was cut after the checks passed.";

    private const string KnownLimitation = "Only Linux is covered.";

    private const string PictureCaption = "The pricing table after the redesign.";

    /// <summary>
    /// A milestone note that must never reach a projection. Milestone notes are the owner's
    /// private record, not evidence, and the journey never reads them.
    /// </summary>
    private const string PrivateMilestoneNotes = "PRIVATE-NOTE-NEVER-PUBLISHED-do-not-publish";

    /// <summary>A candidate title that must never reach a projection.</summary>
    private const string CandidateTitle = "CANDIDATE-TITLE-NEVER-PUBLISHED-do-not-publish";

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-001 Success: an enabled previewed selection serves exactly its
    // selected milestones and stories to an anonymous reader.
    // ==============================================================================================

    [Fact]
    public async Task Journey_001_Success_enabled_selection_serves_exactly_selected_entries()
    {
        await using var fixture = await ArrangeAsync("journey_001_success");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id, fixture.Asset2Id]);
        Assert.Equal(2, preview.Entries);

        // Order on the wire changes nothing: the hash is over the canonical order.
        var reversed = await PreviewAsync(fixture, [fixture.Asset2Id, fixture.Asset1Id]);
        Assert.Equal(preview.SelectionHash, reversed.SelectionHash);

        using var published = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "reproducible-pipeline",
                expectedRevision = (long?)null,
                selectionHash = preview.SelectionHash,
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var view = JsonDocument.Parse(await published.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("reproducible-pipeline", view.GetProperty("slug").GetString());
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, view.GetProperty("revision").GetInt64());
        Assert.Equal(2, view.GetProperty("entryCount").GetInt32());

        // The anonymous reader holds no session at all: a fresh client with no cookie.
        using var anonymous = fixture.Host.CreateClient();
        using var journey = await anonymous.GetAsync("/api/journey/reproducible-pipeline");

        Assert.Equal(HttpStatusCode.OK, journey.StatusCode);
        Assert.True(journey.Headers.CacheControl?.NoStore == true, "The public projection is never cached.");

        var body = await journey.Content.ReadAsStringAsync();
        var served = JsonDocument.Parse(body).RootElement;

        Assert.Equal("reproducible-pipeline", served.GetProperty("slug").GetString());
        var entries = served.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);

        // Oldest milestone first: the projection is chronological, not insertion-ordered.
        Assert.Equal("First reproducible release", entries[0].GetProperty("milestoneTitle").GetString());
        Assert.Equal("Second shippable slice", entries[1].GetProperty("milestoneTitle").GetString());

        Assert.Contains("Reproducible release pipeline", entries[0].GetProperty("bodyMarkdown").GetString(), StringComparison.Ordinal);
        Assert.Contains("Second shippable slice", entries[1].GetProperty("bodyMarkdown").GetString(), StringComparison.Ordinal);

        var pictures = entries[0].GetProperty("pictures").EnumerateArray().ToList();
        Assert.Single(pictures);
        Assert.Equal(PictureCaption, pictures[0].GetProperty("caption").GetString());
        Assert.EndsWith(
            $"/media/{fixture.SelectedMediaId:D}",
            pictures[0].GetProperty("mediaUrl").GetString(),
            StringComparison.Ordinal);

        Assert.Empty(entries[1].GetProperty("pictures").EnumerateArray().ToList());

        // The private read agrees with the public one, and names the revision the next
        // mutation must carry.
        using var read = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/journey");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var stored = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, stored.GetProperty("revision").GetInt64());
        Assert.Equal(preview.SelectionHash, stored.GetProperty("selectionHash").GetString());
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-001 Success (media): an approval-covered picture's bytes are served
    // anonymously; anything else is 404.
    // ==============================================================================================

    [Fact]
    public async Task Journey_001_Public_media_serves_approved_picture_bytes()
    {
        await using var fixture = await ArrangeAsync("journey_001_media");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id, fixture.Asset2Id]);
        await PublishAsync(fixture, "picture-bytes", null, preview.SelectionHash, [fixture.Asset1Id, fixture.Asset2Id]);

        using var anonymous = fixture.Host.CreateClient();
        using var picture = await anonymous.GetAsync($"/api/journey/picture-bytes/media/{fixture.SelectedMediaId:D}");

        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.True(picture.Headers.CacheControl?.NoStore == true);

        var bytes = await picture.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8, "A published picture serves bytes, not an empty body.");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // An unselected picture is not depicted, so it answers the same 404 as a missing one.
        using var unselected = await anonymous.GetAsync($"/api/journey/picture-bytes/media/{fixture.UnselectedMediaId:D}");
        Assert.Equal(HttpStatusCode.NotFound, unselected.StatusCode);

        // A foreign picture id is the same 404, not a distinguishable refusal.
        using var foreign = await anonymous.GetAsync($"/api/journey/picture-bytes/media/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-001 Failure: a project that was never enabled has no public route.
    // ==============================================================================================

    [Fact]
    public async Task Journey_001_Failure_never_enabled_slug_is_404()
    {
        await using var fixture = await ArrangeAsync("journey_001_never");

        using var anonymous = fixture.Host.CreateClient();
        using var missing = await anonymous.GetAsync("/api/journey/nothing-was-ever-published");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var envelope = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("not_found", envelope.GetProperty("code").GetString());

        using var missingMedia = await anonymous.GetAsync($"/api/journey/nothing-was-ever-published/media/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missingMedia.StatusCode);

        // The private preview route is private: an anonymous caller is refused before anything
        // about the selection is decided.
        using var refused = await anonymous.PostAsJsonAsync(
            $"/api/projects/{fixture.ProjectId:D}/journey/preview",
            new { approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") } });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-001 Boundary: private notes, candidate milestones and unselected
    // pictures never enter the public projection.
    // ==============================================================================================

    [Fact]
    public async Task Journey_001_Boundary_private_notes_candidates_and_unselected_media_absent()
    {
        await using var fixture = await ArrangeAsync("journey_001_boundary");

        using var imported = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/milestones/import",
            new
            {
                source = "github",
                candidates = new[]
                {
                    new
                    {
                        sourceKey = "candidate-never-published",
                        title = CandidateTitle,
                        occurredAt = "2026-10-05T09:00:00Z",
                        type = "release",
                    },
                },
            }));

        Assert.True(imported.IsSuccessStatusCode, await imported.Content.ReadAsStringAsync());

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id, fixture.Asset2Id]);
        await PublishAsync(fixture, "only-approved", null, preview.SelectionHash, [fixture.Asset1Id, fixture.Asset2Id]);

        using var anonymous = fixture.Host.CreateClient();
        using var journey = await anonymous.GetAsync("/api/journey/only-approved");
        Assert.Equal(HttpStatusCode.OK, journey.StatusCode);

        var body = await journey.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateMilestoneNotes, body, StringComparison.Ordinal);
        Assert.DoesNotContain(CandidateTitle, body, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.UnselectedMediaId.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-key-never-used-for-a-real-call", body, StringComparison.Ordinal);
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-002 Success: revocation stops the public routes immediately.
    // ==============================================================================================

    [Fact]
    public async Task Journey_002_Success_revoke_disables_public_read_immediately()
    {
        await using var fixture = await ArrangeAsync("journey_002_revoke");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id]);
        await PublishAsync(fixture, "revocable-journey", null, preview.SelectionHash, [fixture.Asset1Id]);

        using var anonymous = fixture.Host.CreateClient();
        using var before = await anonymous.GetAsync("/api/journey/revocable-journey");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var revoked = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = false,
                slug = (string?)null,
                expectedRevision = (long?)1,
                selectionHash = (string?)null,
                approvedAssetIds = (string[]?)null,
            }));

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var view = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()).RootElement;
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, view.GetProperty("revision").GetInt64());
        Assert.NotNull(view.GetProperty("revokedAt").GetString());

        // The app route stops immediately: the same 404 a missing slug answers.
        using var after = await anonymous.GetAsync("/api/journey/revocable-journey");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        using var media = await anonymous.GetAsync($"/api/journey/revocable-journey/media/{fixture.SelectedMediaId:D}");
        Assert.Equal(HttpStatusCode.NotFound, media.StatusCode);

        // The private read still names the journey, now disabled: revocation removed the
        // projection, never the records behind it.
        using var read = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/journey");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-002 Failure: a stale preview hash refuses publication and replaces
    // nothing.
    // ==============================================================================================

    [Fact]
    public async Task Journey_002_Failure_stale_selection_hash_is_409_without_replacing_snapshot()
    {
        await using var fixture = await ArrangeAsync("journey_002_stale");

        var full = await PreviewAsync(fixture, [fixture.Asset1Id, fixture.Asset2Id]);
        await PublishAsync(fixture, "stable-journey", null, full.SelectionHash, [fixture.Asset1Id, fixture.Asset2Id]);

        var partial = await PreviewAsync(fixture, [fixture.Asset1Id]);

        using var stale = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "stable-journey",
                expectedRevision = (long?)1,
                // The hash of the smaller selection, sent with the larger one: the selection the
                // owner reviewed is not the selection this request would store.
                selectionHash = partial.SelectionHash,
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D"), fixture.Asset2Id.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var envelope = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("journey_selection_stale", envelope.GetProperty("code").GetString());

        // Nothing was replaced: the public snapshot still depicts both entries at revision 1.
        using var anonymous = fixture.Host.CreateClient();
        using var journey = await anonymous.GetAsync("/api/journey/stable-journey");
        Assert.Equal(HttpStatusCode.OK, journey.StatusCode);

        var served = JsonDocument.Parse(await journey.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, served.GetProperty("revision").GetInt64());
        Assert.Equal(2, served.GetProperty("entries").EnumerateArray().Count());
    }

    // ==============================================================================================
    // PUBLIC-PROJECT-JOURNEY-002 Boundary: a lapsed approval suspends the public snapshot.
    // ==============================================================================================

    [Fact]
    public async Task Journey_002_Boundary_stale_approval_suspends_public_snapshot()
    {
        await using var fixture = await ArrangeAsync("journey_002_suspend");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id, fixture.Asset2Id]);
        await PublishAsync(fixture, "suspended-journey", null, preview.SelectionHash, [fixture.Asset1Id, fixture.Asset2Id]);

        // The owner's own edit appends a revision and invalidates the live approval: the frozen
        // snapshot no longer describes the stored state.
        using var edited = await fixture.Owner.SendAsync(await fixture.Owner.Patch(
            $"/api/assets/{fixture.Asset1Id:D}",
            1,
            new
            {
                expectedRevision = 1,
                bodyMarkdown = "# Reproducible release pipeline\n\nThe pipeline now reproduces from a clean checkout with an edited line.[^c1]\n\n[^c1]: The pipeline now reproduces from a clean checkout.\n",
                title = (string?)null,
            }));

        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());

        using var anonymous = fixture.Host.CreateClient();
        using var suspended = await anonymous.GetAsync("/api/journey/suspended-journey");
        Assert.Equal(HttpStatusCode.NotFound, suspended.StatusCode);

        using var media = await anonymous.GetAsync($"/api/journey/suspended-journey/media/{fixture.SelectedMediaId:D}");
        Assert.Equal(HttpStatusCode.NotFound, media.StatusCode);

        // The journey row itself is intact: suspension is a read-time refusal, not a deletion.
        using var read = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/journey");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
    }

    // ==============================================================================================
    // Carry-over: slug ownership, revision concurrency, approval bindings and validation.
    // ==============================================================================================

    [Fact]
    public async Task Journey_Slug_conflict_second_project_same_slug_is_409()
    {
        await using var fixture = await ArrangeAsync("journey_slug_conflict");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id]);
        await PublishAsync(fixture, "taken-slug", null, preview.SelectionHash, [fixture.Asset1Id]);

        var foreignAsset = await ArrangeSecondProjectAsync(fixture, "journey-slug-foreign");
        var foreignPreview = await PreviewForAsync(fixture, fixture.SecondProjectId, [foreignAsset]);

        using var conflict = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.SecondProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "taken-slug",
                expectedRevision = (long?)null,
                selectionHash = foreignPreview.SelectionHash,
                approvedAssetIds = new[] { foreignAsset.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "journey_slug_conflict",
            JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // The first journey still serves under the slug: the conflict replaced nothing.
        using var anonymous = fixture.Host.CreateClient();
        using var journey = await anonymous.GetAsync("/api/journey/taken-slug");
        Assert.Equal(HttpStatusCode.OK, journey.StatusCode);
    }

    [Fact]
    public async Task Journey_Revision_concurrent_update_is_409_and_missing_revision_is_428()
    {
        await using var fixture = await ArrangeAsync("journey_revision");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id]);
        await PublishAsync(fixture, "revision-journey", null, preview.SelectionHash, [fixture.Asset1Id]);

        // A second publication moves the revision to 2.
        await PublishAsync(fixture, "revision-journey-renamed", 1, preview.SelectionHash, [fixture.Asset1Id]);

        // The first writer's revision is now stale.
        using var conflict = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "revision-journey-stale",
                expectedRevision = (long?)1,
                selectionHash = preview.SelectionHash,
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "revision_conflict",
            JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // No revision at all on an existing journey is a 428, not a guess at one.
        using var required = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "revision-journey-renamed",
                expectedRevision = (long?)null,
                selectionHash = preview.SelectionHash,
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") },
            }));

        Assert.Equal(HttpStatusCode.PreconditionRequired, required.StatusCode);
    }

    [Fact]
    public async Task Journey_Preview_unapproved_asset_is_approval_required_409()
    {
        await using var fixture = await ArrangeAsync("journey_unapproved", approveSecond: false);

        using var preview = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/journey/preview",
            new { approvedAssetIds = new[] { fixture.Asset2Id.ToString("D") } }));

        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Equal(
            "approval_required",
            JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Journey_Preview_foreign_asset_is_404()
    {
        await using var fixture = await ArrangeAsync("journey_foreign");

        var foreignAsset = await ArrangeSecondProjectAsync(fixture, "journey-foreign-second");

        using var preview = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{fixture.ProjectId:D}/journey/preview",
            new { approvedAssetIds = new[] { foreignAsset.ToString("D") } }));

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
    }

    [Fact]
    public async Task Journey_Slug_validation_rejects_non_kebab()
    {
        await using var fixture = await ArrangeAsync("journey_slug_shape");

        var preview = await PreviewAsync(fixture, [fixture.Asset1Id]);

        foreach (var slug in new[] { "AB", "a", "has space", "UPPER", "trailing-", new string('a', 81) })
        {
            using var refused = await fixture.Owner.SendAsync(await fixture.Owner.Put(
                $"/api/projects/{fixture.ProjectId:D}/journey",
                new
                {
                    enabled = true,
                    slug,
                    expectedRevision = (long?)null,
                    selectionHash = preview.SelectionHash,
                    approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") },
                }));

            Assert.True(
                refused.StatusCode == HttpStatusCode.UnprocessableEntity,
                $"slug '{slug}' answered {(int)refused.StatusCode}: {await refused.Content.ReadAsStringAsync()}");
        }

        // Nothing was stored by the refused publications.
        using var read = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/journey");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task Journey_Publish_dismissed_milestone_selection_is_422()
    {
        await using var fixture = await ArrangeAsync("journey_dismissed");

        using var milestone = await fixture.Owner.GetAsync($"/api/milestones/{fixture.Milestone1Id:D}");
        milestone.EnsureSuccessStatusCode();
        var revision = JsonDocument.Parse(await milestone.Content.ReadAsStringAsync()).RootElement.GetProperty("revision").GetInt64();

        using var dismissed = await fixture.Owner.SendAsync(
            await fixture.Owner.Post($"/api/milestones/{fixture.Milestone1Id:D}/dismiss", new { }, revision));
        Assert.True(dismissed.IsSuccessStatusCode, await dismissed.Content.ReadAsStringAsync());

        using var publish = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug = "dismissed-selection",
                expectedRevision = (long?)null,
                selectionHash = new string('0', 64),
                approvedAssetIds = new[] { fixture.Asset1Id.ToString("D") },
            }));

        // A dismissed milestone is not history, so it is refused before any hash is compared.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, publish.StatusCode);
    }

    [Fact]
    public async Task Journey_Private_read_without_journey_is_404_and_revoke_is_disabled_noop()
    {
        await using var fixture = await ArrangeAsync("journey_absent");

        using var read = await fixture.Owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/journey");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        // Revoking what was never published is already revoked: a disabled no-op, not a 404
        // that would send the caller looking for a journey.
        using var revoked = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = false,
                slug = (string?)null,
                expectedRevision = (long?)null,
                selectionHash = (string?)null,
                approvedAssetIds = (string[]?)null,
            }));

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.False(JsonDocument.Parse(await revoked.Content.ReadAsStringAsync()).RootElement.GetProperty("enabled").GetBoolean());
    }

    // ==============================================================================================
    // The fixture.
    // ==============================================================================================

    /// <summary>
    /// Builds everything a scenario needs: a real database, a real project, two confirmed
    /// milestones with real recorded evidence, a real sealed credential, two stored drafts
    /// produced by real generation jobs, and the assets those drafts belong to - the first with
    /// a selected picture beside an unselected candidate, the second text-only. Both assets are
    /// approved unless a scenario says otherwise.
    /// </summary>
    private async Task<Fixture> ArrangeAsync(
        string purpose,
        bool approveFirst = true,
        bool approveSecond = true)
    {
        var database = await ProvisionedDatabaseAsync(purpose);
        var keyFile = OperatorKeyFile.Create();
        var clock = new FixedTimeProvider(Clock);

        Guid projectId;
        Guid milestone1Id;
        Guid milestone2Id;
        Guid credentialId;
        HeadView head1;
        HeadView head2;

        await using (var setup = GroundedMarkdownDraftsApiHost.StartProduction(database, clock, keyFile.FilePath))
        {
            using var setupOwner = await OwnerClient.CreateAsync(setup);

            projectId = await CreateProjectAsync(setupOwner, purpose);
            milestone1Id = await CreateMilestoneAsync(
                setupOwner, projectId, "First reproducible release", "2026-10-01T09:00:00Z", PrivateMilestoneNotes);
            milestone2Id = await CreateMilestoneAsync(
                setupOwner, projectId, "Second shippable slice", "2026-10-03T09:00:00Z", "A private working note.");

            foreach (var (milestoneId, notes) in new[]
                         {
                             (milestone1Id, EvidenceNote),
                             (milestone2Id, SecondEvidenceNote),
                         })
            {
                using var evidence = await setupOwner.SendAsync(await setupOwner.Post(
                    $"/api/milestones/{milestoneId:D}/evidence/manual",
                    new { notes, expectedRevision = 0 },
                    0));

                Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);
            }

            // The generated bodies are English, so the voice they are judged under must be too:
            // the default profile declares Chinese and would report language findings on every
            // field, which is a blocked approval rather than this fixture's success path.
            using var voiced = await setupOwner.SendAsync(await setupOwner.Put(
                $"/api/projects/{projectId:D}/voice-profile",
                new { presetId = "technical-blog", overrides = new { language = "en" } },
                0));

            Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);

            credentialId = await SaveCredentialAsync(setupOwner);
        }

        // One worker at a time shares this database: the capture host is disposed before the
        // drafts host starts, so a draft job can never be dequeued by a host holding no
        // generation double. The candidates, the selection and the media bytes it wrote persist
        // in the database and on disk.
        Guid selectedMediaId = Guid.Empty;
        Guid unselectedMediaId = Guid.Empty;

        await using (var capture = CaptureApiHost.Start(
                         database, clock, new RecordingScreenshotProvider(clock)))
        {
            using var pictureOwner = await OwnerClient.CreateAsync(capture);

            var candidates = await CaptureTwoAsync(capture, pictureOwner, milestone1Id);
            var first = candidates[0];

            using var selected = await pictureOwner.SendAsync(await pictureOwner.Patch(
                $"/api/screenshots/{first.Id:D}",
                first.Revision,
                new
                {
                    status = "selected",
                    caption = PictureCaption,
                    expectedRevision = first.Revision,
                    expectedBundleRevision = 1,
                }));

            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

            selectedMediaId = first.MediaId;
            unselectedMediaId = candidates[1].MediaId;
        }

        // The drafts host shares the database - and therefore the milestones, the evidence and
        // the stored media - with the capture host above. Only the seams differ: this host
        // answers generation, that one answers captures.
        var host = GroundedMarkdownDraftsApiHost.Start(
            database,
            clock,
            keyFile.FilePath,
            ScriptedDraftProvider.With("{}", "{}"));
        var owner = await OwnerClient.CreateAsync(host);

        head1 = await ReadHeadAsync(owner, milestone1Id);
        Assert.Equal(2, head1.Revision);
        head2 = await ReadHeadAsync(owner, milestone2Id);
        Assert.Equal(1, head2.Revision);

        // The script is replaced with the summaries citing the heads just read: a summary must
        // cite the frozen bundle's own item ids, so no honest script can exist before this read.
        host.Provider!.Rescript(
            SummaryJson(head1.ManualItemId, "Reproducible release pipeline", FirstSummaryText),
            BodyFor(head1.ManualItemId, "Reproducible release pipeline", FirstSummaryText));

        var asset1Id = await DraftAsync(
            host, owner, database, milestone1Id, credentialId, head1, $"journey-{purpose}-m1");

        host.Provider!.Rescript(
            SummaryJson(head2.ManualItemId, "Second shippable slice", SecondSummaryText),
            BodyFor(head2.ManualItemId, "Second shippable slice", SecondSummaryText));

        var asset2Id = await DraftAsync(
            host, owner, database, milestone2Id, credentialId, head2, $"journey-{purpose}-m2");

        if (approveFirst)
        {
            await ApproveAsync(owner, asset1Id, 1, head1.Revision);
        }

        if (approveSecond)
        {
            await ApproveAsync(owner, asset2Id, 1, head2.Revision);
        }

        return new Fixture(
            database,
            host,
            keyFile,
            owner,
            projectId,
            Guid.Empty,
            milestone1Id,
            milestone2Id,
            asset1Id,
            asset2Id,
            credentialId,
            selectedMediaId,
            unselectedMediaId);
    }

    /// <summary>
    /// A second project with one approved story, for the foreign-asset and slug-conflict
    /// boundaries: it shares the fixture's database, credential and provider double, and
    /// nothing else.
    /// </summary>
    private static async Task<Guid> ArrangeSecondProjectAsync(Fixture fixture, string purpose)
    {
        var owner = fixture.Owner;
        var projectId = await CreateProjectAsync(owner, purpose);

        using var voiced = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = "technical-blog", overrides = new { language = "en" } },
            0));

        Assert.Equal(HttpStatusCode.OK, voiced.StatusCode);
        fixture.SecondProjectId = projectId;

        var milestoneId = await CreateMilestoneAsync(
            owner, projectId, "Foreign milestone", "2026-10-04T09:00:00Z", "A foreign working note.");

        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = EvidenceNote, expectedRevision = 0 },
            0));

        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        var head = await ReadHeadAsync(owner, milestoneId);

        fixture.Host.Provider!.Rescript(
            SummaryJson(head.ManualItemId, "Reproducible release pipeline", FirstSummaryText),
            BodyFor(head.ManualItemId, "Reproducible release pipeline", FirstSummaryText));

        var assetId = await DraftAsync(
            fixture.Host, owner, fixture.Database, milestoneId, fixture.CredentialId, head,
            $"journey-{purpose}");

        await ApproveAsync(owner, assetId, 1, head.Revision);

        return assetId;
    }

    private static async Task<PreviewView> PreviewAsync(Fixture fixture, IReadOnlyList<Guid> assetIds) =>
        await PreviewForAsync(fixture, fixture.ProjectId, assetIds);

    private static async Task<PreviewView> PreviewForAsync(Fixture fixture, Guid projectId, IReadOnlyList<Guid> assetIds)
    {
        using var preview = await fixture.Owner.SendAsync(await fixture.Owner.Post(
            $"/api/projects/{projectId:D}/journey/preview",
            new { approvedAssetIds = assetIds.Select(id => id.ToString("D")).ToArray() }));

        Assert.True(
            preview.IsSuccessStatusCode,
            $"preview answered {(int)preview.StatusCode}: {await preview.Content.ReadAsStringAsync()}");

        var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement;

        return new PreviewView(
            body.GetProperty("selectionHash").GetString()!,
            body.GetProperty("entries").EnumerateArray().Count());
    }

    private static async Task PublishAsync(
        Fixture fixture,
        string slug,
        long? expectedRevision,
        string selectionHash,
        IReadOnlyList<Guid> assetIds)
    {
        using var published = await fixture.Owner.SendAsync(await fixture.Owner.Put(
            $"/api/projects/{fixture.ProjectId:D}/journey",
            new
            {
                enabled = true,
                slug,
                expectedRevision,
                selectionHash,
                approvedAssetIds = assetIds.Select(id => id.ToString("D")).ToArray(),
            }));

        Assert.True(
            published.IsSuccessStatusCode,
            $"publish answered {(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}");
    }

    private static async Task<Guid> DraftAsync(
        GroundedMarkdownDraftsApiHost host,
        OwnerClient owner,
        string database,
        Guid milestoneId,
        Guid credentialId,
        HeadView head,
        string idempotencyKey)
    {
        var preview = await CreatePreviewAsync(
            host, owner, milestoneId, credentialId, head.Revision, head.ManualItemId);

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
                idempotencyKey,
            }));

        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var accepted = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var operationId = accepted.RootElement.GetProperty("operationId").GetGuid();

        await host.DispatchAsync();

        try
        {
            await OperationRows.AwaitAsync(database, operationId, "succeeded", JobBudget);
        }
        catch (TimeoutException timeout)
        {
            var last = await OperationRows.ReadAsync(database, operationId);
            throw new TimeoutException(
                $"{timeout.Message} Recorded error code: {last?.ErrorCode ?? "none"}.",
                timeout);
        }

        var asset = await ContentRows.ReadAssetForMilestoneAsync(database, milestoneId);
        Assert.NotNull(asset);

        return asset!.Id;
    }

    private static async Task ApproveAsync(OwnerClient owner, Guid assetId, long contentRevision, long evidenceRevision)
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
            approved.IsSuccessStatusCode,
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

    private static async Task<Guid> CreateMilestoneAsync(
        OwnerClient owner,
        Guid projectId,
        string title,
        string occurredAt,
        string notes)
    {
        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt,
                type = "feature",
                notes,
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

    private static async Task<IReadOnlyList<CandidateView>> CaptureTwoAsync(
        CaptureApiHost host,
        OwnerClient owner,
        Guid milestoneId)
    {
        host.Provider.AnswerWithPng(width: 1440, height: 900);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/captures",
            new
            {
                url = PublicUrl,
                viewports = new[] { "desktop", "mobile" },
                waitMs = 0,
                fullPage = false,
                expectedBundleRevision = (long?)null,
                idempotencyKey = $"journey-capture-{Guid.NewGuid():N}",
            },
            0));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var operationId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("operationId").GetString()!);

        await host.DispatchAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await ChronicleafPostgresFixture.ReadTextAsync(
                host.Database,
                $"SELECT status FROM operations WHERE id = '{operationId}'");

            if (status is "succeeded" or "failed" or "cancelled" or "superseded")
            {
                break;
            }

            await Task.Delay(100);
        }

        using var operation = await owner.GetAsync($"/api/operations/{operationId:D}");
        var operationBody = JsonDocument.Parse(await operation.Content.ReadAsStringAsync());
        Assert.Equal(
            "succeeded",
            operationBody.RootElement.GetProperty("status").GetString());

        using var candidates = await owner.GetAsync($"/api/milestones/{milestoneId:D}/screenshots");
        Assert.Equal(HttpStatusCode.OK, candidates.StatusCode);

        using var document = JsonDocument.Parse(await candidates.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().Select(item => new CandidateView(
            Guid.Parse(item.GetProperty("id").GetString()!),
            Guid.Parse(item.GetProperty("mediaId").GetString()!),
            item.GetProperty("revision").GetInt64())).ToList();

        Assert.Equal(2, items.Count);
        return items;
    }

    private sealed record HeadView(Guid BundleId, long Revision, Guid ManualItemId);

    private sealed record PreviewBody(Guid PreviewId, string ConsentHash, long EvidenceRevision, long VoiceRevision);

    private sealed record CandidateView(Guid Id, Guid MediaId, long Revision);

    private sealed record PreviewView(string SelectionHash, int Entries);

    /// <summary>
    /// Everything one scenario holds, and the disposal it owes the shared servers.
    /// </summary>
    private sealed class Fixture(
        string database,
        GroundedMarkdownDraftsApiHost host,
        OperatorKeyFile keyFile,
        OwnerClient owner,
        Guid projectId,
        Guid secondProjectId,
        Guid milestone1Id,
        Guid milestone2Id,
        Guid asset1Id,
        Guid asset2Id,
        Guid credentialId,
        Guid selectedMediaId,
        Guid unselectedMediaId) : IAsyncDisposable
    {
        public string Database { get; } = database;

        public GroundedMarkdownDraftsApiHost Host { get; } = host;

        public OperatorKeyFile KeyFile { get; } = keyFile;

        public OwnerClient Owner { get; } = owner;

        public Guid ProjectId { get; } = projectId;

        public Guid SecondProjectId { get; set; } = secondProjectId;

        public Guid Milestone1Id { get; } = milestone1Id;

        public Guid Milestone2Id { get; } = milestone2Id;

        public Guid Asset1Id { get; } = asset1Id;

        public Guid Asset2Id { get; } = asset2Id;

        public Guid CredentialId { get; } = credentialId;

        public Guid SelectedMediaId { get; } = selectedMediaId;

        public Guid UnselectedMediaId { get; } = unselectedMediaId;

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
    private const string FirstSummaryText =
        "The pipeline now reproduces from a clean checkout. The build ran twice and produced the same digest.";

    private const string SecondSummaryText =
        "The slice now ships from a tagged commit. The tag was cut after the checks passed.";

    private static string SummaryJson(Guid evidenceId, string title, string summaryText)
    {
        var id = evidenceId.ToString("d");

        return $$"""
          {
            "milestoneTitle": {{Quote(title)}},
            "summary": {{Quote(summaryText)}},
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
    private static string BodyFor(Guid evidenceId, string title, string summaryText)
    {
        var summaryJson = SummaryJson(evidenceId, title, summaryText);
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
