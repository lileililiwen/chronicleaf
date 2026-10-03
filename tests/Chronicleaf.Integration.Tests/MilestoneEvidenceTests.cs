using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// MILESTONE-EVIDENCE-001 and MILESTONE-EVIDENCE-002 against a real disposable PostgreSQL, a real
/// temporary filesystem and the real <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. The rows come from a real PostgreSQL server, the media
/// bytes are written to a real directory, the collector's appends go through the real
/// <see cref="IAppendEvidence"/> against the real stores, and every claim is read back over the wire
/// - a status code, a header, a body - or over a real query against the real tables. There is no EF
/// in-memory provider, no stubbed store and no scripted reference check in this file: the reference
/// check under test is the production one, reading the production <c>evidence_items</c> table.
/// </para>
/// <para>
/// Every test gets its own database, and the media scenarios also get their own MediaRoot, both
/// temporary. The clock is injected and frozen, so every stored instant is deterministic. The
/// identities are declared constants - the installation and its scope, a foreign scope - and each
/// scenario names its project, milestone and media ids in advance, so the row and the key a scenario
/// expects are facts rather than guesses.
/// </para>
/// <para>
/// Two absences are asserted as deliberately as two presences, because both are the point of this
/// package: an unavailable source is stored as unavailable with its payload nowhere, and a refused
/// append leaves no bundle behind.
/// </para>
/// </remarks>
public sealed class MilestoneEvidenceTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("3b7c1d90-4a68-4f21-8c05-9d6e7f8a9b01");

    /// <summary>A second installation, used only to prove another scope's media is not referenceable.</summary>
    private static readonly Guid ForeignScopeId = new("4c8d2ea1-5b79-4032-9d16-ae7f8a9b0c12");

    /// <summary>The owner identity every scenario acts as; it never chooses its own scope.</summary>
    private static readonly OwnerScope Owner =
        new(InstallationId, new Guid("5e4d3c2b-1a09-4f8e-9753-4d3c2b1a0987"));

    private readonly List<string> _temporaryRoots = [];

    public void Dispose()
    {
        foreach (var root in _temporaryRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary directory that will not delete is not a failed scenario; the fixture
                // asserts on the files that matter while the test is still running.
            }
        }
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-001 Success: appending source material creates a new immutable bundle that
    // identifies the source revision and the hash of what was collected.
    // ===========================================================================================

    [Fact]
    public async Task Appending_source_material_creates_a_new_immutable_bundle_naming_revision_and_hash()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_success");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "First release");

        var commitText = "Add the ledger export and its tests.";
        var commitHash = Sha256Hex(commitText);

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();

        // The first bundle of a milestone is revision 1, and the revision that says "nothing recorded
        // yet" is 0 - so a collector's first append presents 0.
        var bundle = await scope.ServiceProvider.GetRequiredService<IAppendEvidence>().AppendAsync(
            Owner,
            milestoneId,
            0,
            [Commit(commitText, "9f1c2d3e4b5a69788796a5b4c3d2e1f009182736", commitHash, Clock)]);

        Assert.Equal(1, bundle.Revision);
        Assert.Equal(milestoneId, bundle.MilestoneId);
        Assert.Equal(Clock, bundle.CreatedAt);

        var item = Assert.Single(bundle.Items);
        Assert.Equal(EvidenceKinds.Commit, item.Kind);
        Assert.Equal("9f1c2d3e4b5a69788796a5b4c3d2e1f009182736", item.SourceRevision);
        Assert.Equal(commitHash, item.PayloadSha256);
        Assert.Equal(commitText, item.Text);
        Assert.Equal(EvidenceAvailability.Complete, item.Availability);
        Assert.True(item.WasCollected);
        Assert.Null(item.MediaId);
        Assert.Empty(item.Redactions);

        // What a collector collected is what the database holds. Both halves are read over a raw
        // connection, not taken from the object the append returned.
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await CountRowsAsync(database, "evidence_items"));
        Assert.Equal(1, await ReadRevisionAsync(database, milestoneId));
        Assert.Equal(commitHash, await ReadTextAsync(database, "SELECT payload_sha256 FROM evidence_items"));
        Assert.Equal(1, await CountOutboxRowsAsync(database, MilestoneEvidenceService.EvidenceChangedKind));

        // The outbox event carries the milestone and the revision and nothing else: a consumer has to
        // read the snapshot to learn what changed, and no collected text travels in the event.
        var payload = await ReadTextAsync(
            database,
            $"SELECT payload::text FROM outbox WHERE kind = '{MilestoneEvidenceService.EvidenceChangedKind}'");

        Assert.NotNull(payload);
        Assert.Contains(milestoneId.ToString("D"), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(commitText, payload, StringComparison.Ordinal);

        // The audit trail names the action, the bundle and the revision - and carries no collected
        // text, so a log of it never becomes a second copy of the evidence.
        var audit = await ReadTextAsync(
            database,
            $"SELECT action || '|' || safe_metadata::text FROM audit_events WHERE target_id = '{bundle.Id:D}'");

        Assert.NotNull(audit);
        Assert.StartsWith(MilestoneEvidenceService.EvidenceAppendedAction, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(commitText, audit, StringComparison.Ordinal);

        // And the same snapshot comes back over the wire, at the revision the append produced.
        using var owner = await OwnerClient.CreateAsync(host);
        using var read = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("\"1\"", read.Headers.ETag?.ToString());

        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var responseItem = document.RootElement.GetProperty("items")[0].GetProperty("items")[0];

        Assert.Equal(EvidenceKinds.Commit, responseItem.GetProperty("kind").GetString());
        Assert.Equal(commitHash, responseItem.GetProperty("payloadSha256").GetString());
        Assert.Equal(commitText, responseItem.GetProperty("text").GetString());
        Assert.Equal(EvidenceAvailability.Complete, responseItem.GetProperty("availability").GetString());
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-001 Boundary: the same source revision and hash appended twice leaves the
    // bundle revision unchanged and creates no second bundle.
    // ===========================================================================================

    [Fact]
    public async Task Appending_the_same_source_revision_and_hash_twice_leaves_the_revision_unchanged()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_idempotent");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Idempotent");

        var commitText = "Rename the import command.";
        const string sourceRevision = "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d";

        // A collector hashes the payload the canonical way, which is the one rule the repository has
        // for what a payload's hash is.
        var commitHash = EvidencePolicy.ItemPayloadSha256(
            Commit(commitText, sourceRevision, Sha256Hex(commitText), Clock));

        var commit = Commit(commitText, sourceRevision, commitHash, Clock);

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        var first = await evidence.AppendAsync(Owner, milestoneId, 0, [commit]);

        // The identical fact, presented against the revision the first append produced. The same fact
        // at the same revision of the same source is the same fact, so this call changes nothing.
        var second = await evidence.AppendAsync(Owner, milestoneId, first.Revision, [commit]);

        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await CountRowsAsync(database, "evidence_items"));

        // The same fact again, rebuilt from a separately constructed item: same fields, same canonical
        // hash, a different object and a different instant. It is still the same fact, so this is a
        // no-op too - which is the point of hashing the canonical form rather than the arrival order.
        var rebuilt = new EvidenceAppendItem(
            EvidenceKinds.Commit,
            "owner/repo",
            sourceRevision,
            Clock.AddMinutes(5),
            EvidencePolicy.ItemPayloadSha256(Commit(commitText, sourceRevision, commitHash, Clock)),
            commitText,
            mediaId: null,
            redactions: null,
            availability: EvidenceAvailability.Complete);

        Assert.Equal(commitHash, rebuilt.PayloadSha256);

        var third = await evidence.AppendAsync(Owner, milestoneId, second.Revision, [rebuilt]);

        Assert.Equal(second.Revision, third.Revision);
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));

        // An append that adds something new does move the revision, so the no-ops above were the
        // idempotence and not a frozen record.
        var fourth = await evidence.AppendAsync(
            Owner,
            milestoneId,
            third.Revision,
            [Commit("Document the export format.", "2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e", Sha256Hex("Document the export format."), Clock)]);

        Assert.Equal(2, fourth.Revision);
        Assert.Equal(2, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(3, await CountRowsAsync(database, "evidence_items"));
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-001 Failure: an unavailable source is recorded as unavailable. Neither the
    // payload it did not collect nor a hash of one appears anywhere in the record.
    // ===========================================================================================

    [Fact]
    public async Task An_unavailable_source_is_recorded_as_unavailable_and_claims_no_payload()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_unavailable");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Unreachable source");

        const string reason = "The remote could not be reached within the collection window.";

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        var item = new EvidenceAppendItem(
            EvidenceKinds.Release,
            "https://example.invalid/owner/repo",
            "v0.9.0",
            Clock,
            Sha256Hex(reason),
            reason,
            mediaId: null,
            redactions: null,
            availability: EvidenceAvailability.Unavailable);

        var bundle = await evidence.AppendAsync(Owner, milestoneId, 0, [item]);

        Assert.Equal(1, bundle.Revision);

        var stored = Assert.Single(bundle.Items);
        Assert.Equal(EvidenceAvailability.Unavailable, stored.Availability);
        Assert.False(stored.WasCollected);

        // The item's text is the reason it could not be read, and it is visible as such.
        Assert.Equal(reason, stored.Text);
        Assert.Null(stored.MediaId);
        Assert.Equal("unavailable", await ReadTextAsync(database, "SELECT availability FROM evidence_items"));
        Assert.Equal(reason, await ReadTextAsync(database, "SELECT text FROM evidence_items"));
        Assert.Equal(Sha256Hex(reason), await ReadTextAsync(database, "SELECT payload_sha256 FROM evidence_items"));

        // A source that produced nothing cannot point at media. The service refuses it, and so does the
        // schema - which is the difference between a rule this package follows and a rule it owns.
        var refused = await Record.ExceptionAsync(() => evidence.AppendAsync(
            Owner,
            milestoneId,
            bundle.Revision,
            [
                new EvidenceAppendItem(
                    EvidenceKinds.Release,
                    "https://example.invalid/owner/repo",
                    "v0.9.0",
                    Clock,
                    Sha256Hex(reason),
                    reason,
                    mediaId: Guid.NewGuid(),
                    redactions: null,
                    availability: EvidenceAvailability.Unavailable),
            ]));

        Assert.IsType<ValidationFailedException>(refused);

        // And the three honesty constraints hold for a writer that never read the service: a gap with
        // no reason, a gap that claims a payload, a partial that names no exclusion, and collected
        // evidence with no text are all refused by the database.
        await AssertRefusedByConstraintAsync(
            database,
            InsertItem(availability: "unavailable", text: string.Empty, redactions: true),
            "ck_evidence_items_unavailable_has_a_reason");

        await AssertRefusedByConstraintAsync(
            database,
            InsertItem(availability: "complete", text: string.Empty, redactions: true),
            "ck_evidence_items_collected_has_text");

        await AssertRefusedByConstraintAsync(
            database,
            InsertItem(availability: "partial", text: "a partial with no named exclusion", redactions: false),
            "ck_evidence_items_partial_names_an_exclusion");

        await AssertRefusedByConstraintAsync(
            database,
            InsertItem(availability: "completee", text: "a status this build does not know", redactions: true),
            "ck_evidence_items_availability");

        // Every refusal left the stored evidence exactly as it was.
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await CountRowsAsync(database, "evidence_items"));
        Assert.Equal(reason, await ReadTextAsync(database, "SELECT text FROM evidence_items"));
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-001: a partial source keeps what was collected and names what was left out.
    // ===========================================================================================

    [Fact]
    public async Task A_partial_source_is_preserved_as_partial_with_its_exclusions_named()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_partial");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Truncated diff");

        const string collected = "diff --git a/ledger.cs b/ledger.cs\n+partial hunk only";
        string[] redactions = ["binary patch elided", "secrets redacted"];

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        var bundle = await evidence.AppendAsync(
            Owner,
            milestoneId,
            0,
            [
                new EvidenceAppendItem(
                    EvidenceKinds.Diff,
                    "owner/repo",
                    "3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f",
                    Clock,
                    Sha256Hex(collected),
                    collected,
                    mediaId: null,
                    redactions: redactions,
                    availability: EvidenceAvailability.Partial),
            ]);

        var stored = Assert.Single(bundle.Items);

        Assert.Equal(EvidenceAvailability.Partial, stored.Availability);
        Assert.True(stored.WasCollected);
        Assert.Equal(collected, stored.Text);
        Assert.Equal(redactions, stored.Redactions);

        // The stored row says the same thing, which is what makes a reader's "is this the whole
        // story?" answerable from the database and not only from the returned object.
        Assert.Equal(
            "partial|2|binary patch elided;secrets redacted",
            await ReadTextAsync(
                database,
                """
                SELECT availability || '|' || cardinality(redactions) || '|' || array_to_string(redactions, ';')
                FROM evidence_items
                """));

        // A partial source that names no exclusion is refused by the service before it reaches the
        // database, with the delivered envelope's code and nothing written.
        var refused = await Record.ExceptionAsync(() => evidence.AppendAsync(
            Owner,
            milestoneId,
            bundle.Revision,
            [
                new EvidenceAppendItem(
                    EvidenceKinds.Diff,
                    "owner/repo",
                    "4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f70",
                    Clock,
                    Sha256Hex("a silent partial"),
                    "a silent partial",
                    mediaId: null,
                    redactions: null,
                    availability: EvidenceAvailability.Partial),
            ]));

        var validation = Assert.IsType<ValidationFailedException>(refused);
        Assert.Equal(ApiError.ValidationFailed, validation.Code);
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-002 Boundary: two updates presenting the same revision produce exactly one
    // committed bundle, and the other is a 409.
    // ===========================================================================================

    [Fact]
    public async Task Two_updates_targeting_the_same_revision_commit_one_bundle_and_refuse_the_other()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_concurrency");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Racing appends");

        await using var host = Host(database);

        using var first = await OwnerClient.CreateAsync(host);
        using var second = await OwnerClient.CreateAsync(host);

        // Both requests present revision 0, the revision that says nothing is recorded yet, and they
        // are different facts, so neither can be satisfied by the other.
        var firstRequest = await first.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The first writer's note.", expectedRevision = 0 },
            0);

        var secondRequest = await second.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The second writer's note.", expectedRevision = 0 },
            0);

        var responses = await Task.WhenAll(
            first.SendAsync(firstRequest),
            second.SendAsync(secondRequest));

        try
        {
            var statuses = responses.Select(response => response.StatusCode).Order().ToArray();

            Assert.Equal(HttpStatusCode.Created, statuses[0]);
            Assert.Equal(HttpStatusCode.Conflict, statuses[1]);

            var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            using var document = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());

            Assert.Equal(ApiError.RevisionConflict, document.RootElement.GetProperty("code").GetString());

            // Exactly one new bundle exists, at revision 1, and its stored text is the winner's alone -
            // not a merge of both, and not the loser's.
            Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
            Assert.Equal(1, await ReadRevisionAsync(database, milestoneId));

            // Whichever request won, exactly one of the two notes is stored, and it is stored whole:
            // not a merge, and not the loser's text.
            var stored = await ReadTextAsync(database, "SELECT text FROM evidence_items");

            Assert.Contains(stored, new[] { "The first writer's note.", "The second writer's note." });
            Assert.Equal(1, await CountRowsAsync(database, "evidence_items"));
            Assert.Equal(1, await CountOutboxRowsAsync(database, MilestoneEvidenceService.EvidenceChangedKind));

            // The loser can read the newer evidence, so the 409 told it what to re-read.
            using var read = await second.GetAsync($"/api/milestones/{milestoneId:D}/evidence");

            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-002 Failure: another project's media cannot be referenced, and the refused
    // append leaves no bundle behind.
    // ===========================================================================================

    [Fact]
    public async Task Referencing_another_projects_media_fails_the_append_and_creates_no_bundle()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_foreign_media");
        var mediaRoot = TemporaryRoot();

        var ownedMediaId = new Guid("2a1b0c9d-8e7f-4051-9a2b-3c4d5e6f7081");
        var foreignMediaId = new Guid("2a1b0c9d-8e7f-4051-9a2b-3c4d5e6f7082");

        await using var host = MediaApiHost.Start(
            database,
            Clock,
            mediaRoot,
            ids: new SequenceMediaIdSource(ownedMediaId, foreignMediaId));

        var (projectId, otherProjectId, milestoneId) = await CreateMilestoneAsync(database, "Scoped media");

        using var owner = await OwnerClient.CreateAsync(host);

        // One image per project, so each id is referenceable only from its own project.
        var ownedKey = await UploadAsync(owner, projectId, TestPng.Create(6, 6));
        var neighbourKey = await UploadAsync(owner, otherProjectId, TestPng.Create(9, 9));

        Assert.Equal(MediaValues.ReadyStorageKey(InstallationId, projectId, ownedMediaId), ownedKey);
        Assert.Equal(MediaValues.ReadyStorageKey(InstallationId, otherProjectId, foreignMediaId), neighbourKey);

        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        // The owner's own project's image is referenceable, and the append commits.
        var own = await evidence.AppendAsync(
            Owner,
            milestoneId,
            0,
            [Screenshot("owner/repo", "abc123", ownedMediaId, Clock)]);

        Assert.Equal(1, own.Revision);
        Assert.Equal(ownedMediaId, Assert.Single(own.Items).MediaId);
        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));

        // The other project's image is not. One answer - the same 404 an absent id gets - and no new
        // bundle: the revision the owner was on is still the revision they are on.
        var refused = await Record.ExceptionAsync(() => evidence.AppendAsync(
            Owner,
            milestoneId,
            own.Revision,
            [Screenshot("owner/repo", "def456", foreignMediaId, Clock)]));

        var notFound = Assert.IsType<ResourceNotFoundException>(refused);
        Assert.Equal(MilestoneError.NotFound, notFound.Code);

        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await ReadRevisionAsync(database, milestoneId));

        // The message discloses none of the three reasons it could have been: absent, another
        // project's, or another installation's.
        Assert.DoesNotContain("project", notFound.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scope", notFound.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignMediaId.ToString("D"), notFound.Message, StringComparison.Ordinal);

        // The same refusal over the wire, so it is the route a client sees and not only the contract.
        // The manual route has no media field at all, so this is the honest answer for it: the owner
        // can add a note, and only a collector can add a reference - and the collector's path is the
        // one refused above.
        using var posted = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "A note beside the screenshot.", expectedRevision = 1 },
            1));

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        Assert.Equal(2, await ReadRevisionAsync(database, milestoneId));

        // The committed note is a second snapshot that still carries the screenshot reference, so the
        // media stays in use after the manual append too.
        using var read = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items")[0].GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.GetProperty("mediaId").GetString() == ownedMediaId.ToString("D"));
    }

    /// <summary>
    /// An absent media id and one in another installation are the same 404 as another project's, and
    /// neither leaves a bundle behind - which is what lets a caller retry with a corrected id.
    /// </summary>
    [Fact]
    public async Task An_absent_or_foreign_scope_media_id_is_the_same_404_and_writes_nothing()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_absent_media");

        var absentMediaId = new Guid("9c8b7a65-4321-4fed-9876-0123456789ab");
        var foreignMediaId = new Guid("8b7a6543-2109-4fdc-8765-fedcba987654");

        var (_, otherProjectId, milestoneId) = await CreateMilestoneAsync(database, "Absent media");

        // A real ready media row that belongs to another installation: inserted over a raw connection
        // so the scenario is about the append's scope check and not about a row that happens to be
        // missing. It names a real project, because the media row references one.
        await InsertMediaRowAsync(database, foreignMediaId, ForeignScopeId, otherProjectId, MediaStatus.Ready, Clock);

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        foreach (var mediaId in new[] { absentMediaId, foreignMediaId })
        {
            var refused = await Record.ExceptionAsync(() => evidence.AppendAsync(
                Owner,
                milestoneId,
                0,
                [Screenshot("owner/repo", $"rev-{mediaId:N}", mediaId, Clock)]));

            var notFound = Assert.IsType<ResourceNotFoundException>(refused);
            Assert.Equal(MilestoneError.NotFound, notFound.Code);

            Assert.Equal(0, await CountRowsAsync(database, "evidence_bundles"));
            Assert.Equal(0, await CountRowsAsync(database, "evidence_items"));
        }

        // The foreign row is still there and still ready: refusing to reference it is not the same as
        // touching it, and one installation's evidence must not be able to retire another's media.
        Assert.Equal(MediaStatus.Ready, await ReadTextAsync(
            database,
            $"SELECT status FROM media WHERE id = '{foreignMediaId:D}'"));
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-002 Success: a manual append keeps the previous evidence and adds the
    // limitation the owner stated.
    // ===========================================================================================

    [Fact]
    public async Task A_manual_append_retains_the_previous_evidence_and_adds_the_limitations()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_manual");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Manual limitations");

        var commitText = "Add the CSV import.";
        var commitHash = Sha256Hex(commitText);

        await using (var host = Host(database))
        {
            using var scope = host.Services.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IAppendEvidence>().AppendAsync(
                Owner,
                milestoneId,
                0,
                [Commit(commitText, "5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708", commitHash, Clock)]);
        }

        await using var paged = Host(database);
        using var owner = await OwnerClient.CreateAsync(paged);

        using var appended = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new
            {
                notes = "The import works for a single account only.",
                knownLimitations = new[] { "Dates arrive in the browser's locale.", "No undo yet." },
                nextSteps = new[] { "Normalise to UTC on ingest." },
                expectedRevision = 1,
            },
            1));

        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);
        Assert.Equal("\"2\"", appended.Headers.ETag?.ToString());

        using var document = JsonDocument.Parse(await appended.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();

        // Revision 2 holds the commit from revision 1 plus everything the owner wrote: five items, and
        // the commit is still there with the same source revision and the same hash.
        Assert.Equal(5, items.Length);
        Assert.Contains(
            items,
            item => item.GetProperty("kind").GetString() == EvidenceKinds.Commit
                && item.GetProperty("payloadSha256").GetString() == commitHash
                && item.GetProperty("text").GetString() == commitText);

        Assert.Equal(2, CountOf(items, EvidenceKinds.Limitations));
        Assert.Equal(1, CountOf(items, EvidenceKinds.NextSteps));
        Assert.Equal(1, CountOf(items, EvidenceKinds.Manual));

        // The owner's own source reference and source revision are the ones they were working from,
        // so a reader can see which evidence state the statement was answering. And an owner-written
        // statement is complete by definition: the owner asserted it, so it is not a gap.
        Assert.All(
            items.Where(item => item.GetProperty("kind").GetString() != EvidenceKinds.Commit),
            item =>
            {
                Assert.Equal(MilestoneEvidenceService.ManualSourceRef, item.GetProperty("sourceRef").GetString());
                Assert.Equal("1", item.GetProperty("sourceRevision").GetString());
                Assert.Equal(EvidenceAvailability.Complete, item.GetProperty("availability").GetString());
            });

        // Both revisions exist and each is a whole snapshot: the earlier one is smaller, not emptied.
        Assert.Equal(2, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await CountItemsAsync(database, revision: 1));
        Assert.Equal(5, await CountItemsAsync(database, revision: 2));
        Assert.Equal(6, await CountRowsAsync(database, "evidence_items"));

        // Revision 1 is not merely still counted: it still says what it said, unchanged.
        Assert.Equal(commitText, await ReadTextAsync(
            database,
            """
            SELECT i.text
            FROM evidence_items i
            INNER JOIN evidence_bundles b ON b.id = i.bundle_id
            WHERE b.revision = 1
            """));

        // A manual append of nothing is refused, so a revision can never be committed that claims to
        // add evidence and adds none.
        using var empty = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new
            {
                notes = "   ",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
                expectedRevision = 2,
            },
            2));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(2, await CountRowsAsync(database, "evidence_bundles"));
    }

    // ===========================================================================================
    // The design's correction rule: a correction adds a new item and keeps the one it corrects, so a
    // reader can still see what was corrected.
    // ===========================================================================================

    [Fact]
    public async Task A_correction_adds_an_item_and_keeps_the_one_it_corrects()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_correction");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Corrected claim");

        const string original = "Reported 42 users.";
        const string corrected = "Reported 24 users; the 42 figure was a test account.";

        await using var host = Host(database);
        using var scope = host.Services.CreateScope();
        var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

        var first = await evidence.AppendAsync(
            Owner,
            milestoneId,
            0,
            [Commit(original, "7a6b5c4310924edb7654cba9876543210fedcba", Sha256Hex(original), Clock)]);

        var originalItem = Assert.Single(first.Items);

        var second = await evidence.AppendAsync(
            Owner,
            milestoneId,
            first.Revision,
            [
                new EvidenceAppendItem(
                    EvidenceKinds.Commit,
                    "owner/repo",
                    "7a6b5c4310924edb7654cba9876543210fedcba",
                    Clock,
                    Sha256Hex(corrected),
                    corrected,
                    mediaId: null,
                    redactions: null,
                    availability: EvidenceAvailability.Complete,
                    supersedesId: originalItem.Id),
            ]);

        // The new snapshot holds both, and the new item names what it replaced.
        Assert.Equal(2, second.Revision);
        Assert.Equal(2, second.Items.Count);
        Assert.Contains(second.Items, item => item.Text == original);
        Assert.Contains(second.Items, item => item.Text == corrected && item.SupersedesId == originalItem.Id);

        // Revision 1 still says what it said, unchanged, and revision 2 says both: the correction is
        // added to the record, never written over it.
        Assert.Equal(1, await CountItemsWithTextAsync(database, revision: 1, text: original));
        Assert.Equal(0, await CountItemsWithTextAsync(database, revision: 1, text: corrected));
        Assert.Equal(1, await CountItemsWithTextAsync(database, revision: 2, text: original));
        Assert.Equal(1, await CountItemsWithTextAsync(database, revision: 2, text: corrected));

        // A correction of something the milestone does not hold is refused, so a caller cannot record
        // a retraction of a fact nobody collected.
        var refused = await Record.ExceptionAsync(() => evidence.AppendAsync(
            Owner,
            milestoneId,
            second.Revision,
            [
                new EvidenceAppendItem(
                    EvidenceKinds.Manual,
                    MilestoneEvidenceService.ManualSourceRef,
                    "2",
                    Clock,
                    Sha256Hex("retracting nothing"),
                    "retracting nothing",
                    mediaId: null,
                    redactions: null,
                    availability: EvidenceAvailability.Complete,
                    supersedesId: Guid.NewGuid()),
            ]));

        Assert.IsType<ValidationFailedException>(refused);
        Assert.Equal(2, await CountRowsAsync(database, "evidence_bundles"));
    }

    // ===========================================================================================
    // MILESTONE-EVIDENCE-002 and LOCAL-MEDIA-STORAGE-002: media that evidence references cannot be
    // deleted, through the real reference checker, and media nothing references still deletes.
    // ===========================================================================================

    [Fact]
    public async Task Referenced_media_cannot_be_deleted_and_unreferenced_media_still_can()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_media_in_use");
        var mediaRoot = TemporaryRoot();

        var referencedId = new Guid("5d4c3b2a-1908-4e7f-8654-c3b2a1908e76");
        var unreferencedId = new Guid("6e5d4c3b-2a19-4f80-9765-d4c3b2a19087");

        var (projectId, _, milestoneId) = await CreateMilestoneAsync(database, "Media in use");

        await using var host = MediaApiHost.Start(
            database,
            Clock,
            mediaRoot,
            ids: new SequenceMediaIdSource(referencedId, unreferencedId));

        using var owner = await OwnerClient.CreateAsync(host);

        var referencedKey = await UploadAsync(owner, projectId, TestPng.Create(8, 6));
        var unreferencedKey = await UploadAsync(owner, projectId, TestPng.Create(6, 8));

        // The stored keys are the ones the uploads reported, so the files on disk below are the
        // objects the API registered rather than paths this test invented.
        Assert.Equal(MediaValues.ReadyStorageKey(InstallationId, projectId, referencedId), referencedKey);
        Assert.Equal(MediaValues.ReadyStorageKey(InstallationId, projectId, unreferencedId), unreferencedKey);

        var referencedOnDisk = OnDisk(mediaRoot, referencedKey);
        var unreferencedOnDisk = OnDisk(mediaRoot, unreferencedKey);
        var referencedHash = Sha256Hex(await File.ReadAllBytesAsync(referencedOnDisk));

        Assert.True(File.Exists(referencedOnDisk));
        Assert.True(File.Exists(unreferencedOnDisk));

        // The reference is a real evidence row, created by the real append path. Nothing scripts the
        // reference check here: the 409 below is the production checker reading evidence_items.
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAppendEvidence>().AppendAsync(
                Owner,
                milestoneId,
                0,
                [Screenshot("owner/repo", "9f8e7d6c", referencedId, Clock)]);
        }

        // Referenced: refused with the delivered code, and the bytes survive.
        using (var refused = await owner.SendAsync(await owner.Archive($"/api/media/{referencedId:D}", null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("media_in_use", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(MediaError.MediaInUse, document.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(MediaStatus.Ready, await ReadMediaStatusAsync(database, referencedId));
        Assert.True(File.Exists(referencedOnDisk));
        Assert.Equal(referencedHash, Sha256Hex(await File.ReadAllBytesAsync(referencedOnDisk)));

        // Unreferenced: the very same request succeeds, and then the bytes really are gone.
        using var retired = await owner.SendAsync(await owner.Archive($"/api/media/{unreferencedId:D}", null));

        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using var retiredDocument = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.Equal(MediaStatus.Deleted, retiredDocument.RootElement.GetProperty("status").GetString());
        Assert.True(retiredDocument.RootElement.GetProperty("bytesReleased").GetBoolean());
        Assert.False(File.Exists(unreferencedOnDisk));
        Assert.Equal(MediaStatus.Deleted, await ReadMediaStatusAsync(database, unreferencedId));

        // And the evidence that references the surviving image is still readable and still says so.
        using var read = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var evidenceDocument = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(
            referencedId.ToString("D"),
            evidenceDocument.RootElement.GetProperty("items")[0].GetProperty("items")[0].GetProperty("mediaId").GetString());
    }

    // ===========================================================================================
    // Append-only is a property of the schema, not of the service agreeing to behave.
    // ===========================================================================================

    [Fact]
    public async Task An_evidence_row_cannot_be_updated_or_deleted_by_any_writer()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_append_only");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Append only");

        await using (var host = Host(database))
        {
            using var scope = host.Services.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IAppendEvidence>().AppendAsync(
                Owner,
                milestoneId,
                0,
                [Commit(
                    "The original text.",
                    "6f708192a3b4c5d6e7f8091a2b3c4d5e6f7081",
                    Sha256Hex("The original text."),
                    Clock)]);
        }

        Assert.Equal("The original text.", await ReadTextAsync(database, "SELECT text FROM evidence_items"));

        // An UPDATE through a raw connection, which is what a future package, a migration or a psql
        // session would do. The trigger refuses it, so the stored fact is what it was.
        await AssertRefusedAsAppendOnlyAsync(database, "UPDATE evidence_items SET text = 'rewritten'");

        Assert.Equal("The original text.", await ReadTextAsync(database, "SELECT text FROM evidence_items"));

        // And so is a DELETE, on either table.
        await AssertRefusedAsAppendOnlyAsync(database, "DELETE FROM evidence_items");
        await AssertRefusedAsAppendOnlyAsync(database, "DELETE FROM evidence_bundles");

        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal(1, await CountRowsAsync(database, "evidence_items"));
    }

    // ===========================================================================================
    // The precondition itself: no If-Match is a 428, a stale one is a 409, and the newer evidence is
    // left intact by both.
    // ===========================================================================================

    [Fact]
    public async Task A_missing_if_match_is_428_and_a_stale_revision_is_409_with_the_evidence_intact()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_precondition");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Preconditions");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // No If-Match at all. The delivered build method omits the header when no revision is
        // presented, so this is a well-formed signed-in request that simply offers no precondition -
        // and the point is that the route itself demands one.
        using (var withoutHeader = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "No precondition offered.", expectedRevision = 0 })))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, withoutHeader.StatusCode);

            using var document = JsonDocument.Parse(await withoutHeader.Content.ReadAsStringAsync());
            Assert.Equal(ApiError.IfMatchRequired, document.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(0, await CountRowsAsync(database, "evidence_bundles"));

        // A committed append, so there is a newer revision to be stale against.
        using (var first = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The committed note.", expectedRevision = 0 },
            0)))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // A stale revision: refused, and the stored evidence is exactly what it was.
        using (var stale = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "Written against an old revision.", expectedRevision = 0 },
            0)))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            using var document = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal(ApiError.RevisionConflict, document.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
        Assert.Equal("The committed note.", await ReadTextAsync(database, "SELECT text FROM evidence_items"));

        // A body that names a different revision from the header is refused rather than resolved in
        // favour of either: two answers to one question would let a client act on a revision it did
        // not read.
        using (var disagreeing = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "Body says one revision.", expectedRevision = 7 },
            1)))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, disagreeing.StatusCode);

            using var document = JsonDocument.Parse(await disagreeing.Content.ReadAsStringAsync());
            Assert.Contains(
                document.RootElement.GetProperty("fieldErrors").EnumerateObject(),
                field => field.Name == "expectedRevision");
        }

        Assert.Equal(1, await CountRowsAsync(database, "evidence_bundles"));
    }

    // ===========================================================================================
    // Scope: both routes are private, and a milestone out of scope is the same 404 as one that does
    // not exist.
    // ===========================================================================================

    [Fact]
    public async Task The_evidence_routes_are_private_and_a_milestone_out_of_scope_is_the_same_404()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_scope");
        var (projectId, _, milestoneId) = await CreateMilestoneAsync(database, "Private");

        await using var host = Host(database);

        using var anonymous = host.CreateClient();

        // No session: both routes are refused before any handler runs.
        using (var read = await anonymous.GetAsync($"/api/milestones/{milestoneId:D}/evidence"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        }

        using (var append = await anonymous.PostAsync(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new StringContent("{}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, append.StatusCode);
        }

        // The milestone that exists reads, and an id that does not is the same 404 every unreachable
        // id is - so the route cannot be used to find what the installation holds by guessing ids.
        using var owner = await OwnerClient.CreateAsync(host);

        using (var found = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence"))
        {
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        }

        using (var refused = await owner.GetAsync($"/api/milestones/{Guid.NewGuid():D}/evidence"))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal(MilestoneError.NotFound, document.RootElement.GetProperty("code").GetString());
        }

        // An archived project's milestone stops accepting evidence, the way every other milestone
        // write does, and its stored history stays readable.
        using var project = await owner.GetAsync($"/api/projects/{projectId:D}");

        Assert.Equal(HttpStatusCode.OK, project.StatusCode);

        using var projectDocument = JsonDocument.Parse(await project.Content.ReadAsStringAsync());
        var projectRevision = projectDocument.RootElement.GetProperty("revision").GetInt64();

        using (var archived = await owner.SendAsync(await owner.Archive($"/api/projects/{projectId:D}", projectRevision)))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        using (var refused = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "Evidence for an archived project.", expectedRevision = 0 },
            0)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            using var document = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal(MilestoneError.ProjectArchived, document.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(0, await CountRowsAsync(database, "evidence_bundles"));
    }

    // ===========================================================================================
    // The list is bounded and cursor-paged, and the cursor is scope-bound and validated.
    // ===========================================================================================

    [Fact]
    public async Task The_evidence_list_is_bounded_at_twenty_five_and_paged_by_an_opaque_cursor()
    {
        Assert.True(postgres.IsRealPostgres, "the evidence scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("evidence_list");
        var (_, _, milestoneId) = await CreateMilestoneAsync(database, "Many revisions");

        await using (var host = Host(database))
        {
            using var scope = host.Services.CreateScope();
            var evidence = scope.ServiceProvider.GetRequiredService<IAppendEvidence>();

            for (var index = 1; index <= 30; index++)
            {
                var text = $"Fact number {index}.";
                await evidence.AppendAsync(
                    Owner,
                    milestoneId,
                    index - 1,
                    [Commit(text, $"rev{index:D2}", Sha256Hex(text), Clock)]);
            }
        }

        await using var paged = Host(database);
        using var owner = await OwnerClient.CreateAsync(paged);

        // The default is 25, so 30 bundles need two pages. No limit is sent.
        using var first = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var firstDocument = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstPage = firstDocument.RootElement;

        Assert.Equal(25, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(30, firstPage.GetProperty("revision").GetInt64());
        Assert.Equal(30, firstPage.GetProperty("items")[0].GetProperty("revision").GetInt64());

        var cursor = firstPage.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        // The cursor is opaque and bound to the scope: another scope cannot continue this page, and
        // something that is not a cursor at all is a 422 rather than a silently ignored filter.
        Assert.Throws<ValidationFailedException>(() => EvidenceCursor.Decode(cursor!, Guid.NewGuid()));

        using var malformed = await owner.GetAsync(
            $"/api/milestones/{milestoneId:D}/evidence?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);

        using var second = await owner.GetAsync(
            $"/api/milestones/{milestoneId:D}/evidence?cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var secondDocument = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondPage = secondDocument.RootElement;

        Assert.Equal(5, secondPage.GetProperty("items").GetArrayLength());
        Assert.Equal(5, secondPage.GetProperty("items")[0].GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);

        // The two pages neither overlap nor skip: 30 distinct revisions, newest first.
        var revisions = firstPage.GetProperty("items").EnumerateArray()
            .Concat(secondPage.GetProperty("items").EnumerateArray())
            .Select(bundle => bundle.GetProperty("revision").GetInt64())
            .ToArray();

        Assert.Equal(30, revisions.Length);
        Assert.Equal(30, revisions.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 30).Reverse().Select(value => (long)value).ToArray(), revisions);

        // The limit is clamped rather than refused, and its maximum is 100.
        using var clamped = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence?limit=1000");
        Assert.Equal(HttpStatusCode.OK, clamped.StatusCode);

        using var clampedDocument = JsonDocument.Parse(await clamped.Content.ReadAsStringAsync());
        Assert.Equal(30, clampedDocument.RootElement.GetProperty("items").GetArrayLength());
    }

    // ===========================================================================================
    // Helpers.
    // ===========================================================================================

    /// <summary>
    /// A host with the real operator access token hash and the real public origin, which is what a
    /// configured deployment runs. Nothing here weakens the boundary: the scenarios sign in through
    /// the same exchange every other fixture uses.
    /// </summary>
    private static ChronicleafApiHost Host(string database) =>
        ChronicleafApiHost.StartWithAccess(
            database,
            Clock,
            OwnerClient.AccessTokenHash,
            OwnerClient.PublicOrigin);

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using (var host = Host(database))
        {
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
        }

        return database;
    }

    /// <summary>
    /// Creates two projects and one confirmed milestone in the first, through the real routes. The
    /// second project exists so a scenario can prove that media belonging to a project other than the
    /// milestone's is not referenceable.
    /// </summary>
    private async Task<(Guid ProjectId, Guid OtherProjectId, Guid MilestoneId)> CreateMilestoneAsync(
        string database,
        string title)
    {
        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        var projectId = await CreateProjectAsync(owner, $"{title} project");
        var otherProjectId = await CreateProjectAsync(owner, $"{title} neighbour");

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt = "2026-10-01T09:00:00Z",
                type = "feature",
                notes = $"{title} notes.",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var milestoneId = Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("id")
            .GetString()!);

        Assert.Equal(projectId, await ReadGuidAsync(database, $"SELECT project_id FROM milestones WHERE id = '{milestoneId:D}'"));

        return (projectId, otherProjectId, milestoneId);
    }

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("id")
            .GetString()!);
    }

    private string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chronicleaf-evidence-{Guid.NewGuid():N}", "media");
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);

        return root;
    }

    private static EvidenceAppendItem Commit(
        string text,
        string sourceRevision,
        string payloadSha256,
        DateTimeOffset capturedAt) =>
        new(
            EvidenceKinds.Commit,
            "owner/repo",
            sourceRevision,
            capturedAt,
            payloadSha256,
            text,
            mediaId: null,
            redactions: null,
            availability: EvidenceAvailability.Complete);

    private static EvidenceAppendItem Screenshot(
        string sourceRef,
        string sourceRevision,
        Guid mediaId,
        DateTimeOffset capturedAt) =>
        new(
            EvidenceKinds.Screenshot,
            sourceRef,
            sourceRevision,
            capturedAt,
            Sha256Hex(mediaId.ToString("D")),
            "The captured milestone screenshot.",
            mediaId: mediaId,
            redactions: null,
            availability: EvidenceAvailability.Complete);

    private static int CountOf(IEnumerable<JsonElement> items, string kind) =>
        items.Count(item => item.GetProperty("kind").GetString() == kind);

    /// <summary>Uploads a real image and returns the storage key the API registered it under.</summary>
    private static async Task<string> UploadAsync(OwnerClient owner, Guid projectId, byte[] png)
    {
        ArgumentNullException.ThrowIfNull(owner);

        using var response = await owner.SendAsync(await owner.PostBytes(
            $"/api/media?projectId={projectId:D}",
            png,
            "image/png"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("storageKey")
            .GetString()!;
    }

    private static string OnDisk(string mediaRoot, string storageKey) =>
        Path.Combine(mediaRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));

    private static Task<string?> ReadTextAsync(string database, string sql) =>
        ChronicleafPostgresFixture.ReadTextAsync(database, sql);

    private static Task<long> CountRowsAsync(string database, string table) =>
        ChronicleafPostgresFixture.CountRowsAsync(database, table);

    private static async Task<long> ReadRevisionAsync(string database, Guid milestoneId) =>
        await ReadLongAsync(
            database,
            $"SELECT revision FROM evidence_bundles WHERE milestone_id = '{milestoneId:D}' ORDER BY revision DESC LIMIT 1");

    private static Task<long> CountItemsAsync(string database, long revision) =>
        ReadLongAsync(
            database,
            $"""
             SELECT count(*)
             FROM evidence_items i
             INNER JOIN evidence_bundles b ON b.id = i.bundle_id
             WHERE b.revision = {revision}
             """);

    private static Task<long> CountOutboxRowsAsync(string database, string kind) =>
        ReadLongAsync(database, $"SELECT count(*) FROM outbox WHERE kind = '{kind}'");

    private static Task<long> CountItemsWithTextAsync(string database, long revision, string text) =>
        ReadLongAsync(
            database,
            $"""
             SELECT count(*)
             FROM evidence_items i
             INNER JOIN evidence_bundles b ON b.id = i.bundle_id
             WHERE b.revision = {revision} AND i.text = '{text}'
             """);

    private static Task<string?> ReadMediaStatusAsync(string database, Guid mediaId) =>
        ReadTextAsync(database, $"SELECT status FROM media WHERE id = '{mediaId:D}'");

    private static async Task<long> ReadLongAsync(string database, string sql)
    {
        var text = await ChronicleafPostgresFixture.ReadTextAsync(database, sql);

        Assert.NotNull(text);
        return long.Parse(text);
    }

    private static async Task<Guid> ReadGuidAsync(string database, string sql)
    {
        var text = await ChronicleafPostgresFixture.ReadTextAsync(database, sql);

        Assert.NotNull(text);
        return Guid.Parse(text);
    }

    private static Task InsertMediaRowAsync(
        string database,
        Guid mediaId,
        Guid scopeId,
        Guid projectId,
        string status,
        DateTimeOffset createdAt) =>
        ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $"""
             INSERT INTO media (id, scope_id, project_id, storage_key, media_type, byte_length, sha256, status, created_at, owner_operation_id, revision)
             VALUES ('{mediaId:D}', '{scopeId:D}', '{projectId:D}',
                     'scopes/{scopeId:D}/projects/{projectId:D}/media/{mediaId:N}/object.png',
                     '{MediaTypes.Png}', 16, '{Sha256Hex(mediaId.ToString("D"))}', '{status}',
                     '{createdAt.UtcDateTime:O}', NULL, 1)
             """);

    /// <summary>
    /// An item row that attaches itself to the milestone's newest bundle, so a scenario can try to
    /// write a row the schema is supposed to refuse. The availability and text are supplied by the
    /// caller, because the refusals under test are about the content of a row rather than about SQL.
    /// </summary>
    private static string InsertItem(string availability, string text, bool redactions)
    {
        var redactionsLiteral = redactions
            ? "ARRAY['nothing was left out']"
            : "ARRAY[]::text[]";

        return $"""
                INSERT INTO evidence_items
                    (id, bundle_id, milestone_id, kind, source_ref, source_revision, captured_at,
                     payload_sha256, text, media_id, redactions, availability, supersedes_id)
                SELECT gen_random_uuid(), b.id, b.milestone_id, 'commit', 'owner/repo', 'refused',
                       '2026-10-03T14:00:00Z', repeat('a', 64), '{text}', NULL, {redactionsLiteral}, '{availability}', NULL
                FROM evidence_bundles b
                ORDER BY b.revision DESC
                LIMIT 1
                """;
    }

    /// <summary>
    /// Runs a statement the schema is supposed to refuse, and asserts that it was refused and that the
    /// refusal names the check constraint that refused it.
    /// </summary>
    private static async Task AssertRefusedByConstraintAsync(string database, string sql, string constraint)
    {
        var error = await Record.ExceptionAsync(() => ChronicleafPostgresFixture.ExecuteAsync(database, sql));

        Assert.NotNull(error);
        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a statement the append-only trigger is supposed to refuse, and asserts that it was - by the
    /// trigger, not by some other rule, so the refusal is about immutability and not about content.
    /// </summary>
    private static async Task AssertRefusedAsAppendOnlyAsync(string database, string sql)
    {
        var error = await Record.ExceptionAsync(() => ChronicleafPostgresFixture.ExecuteAsync(database, sql));

        Assert.NotNull(error);
        Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
