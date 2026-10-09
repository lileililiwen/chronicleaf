using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// VOICE-PROFILES-001 and VOICE-PROFILES-002 against a real disposable PostgreSQL and the real
/// <c>Chronicleaf.Api</c> host.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated where it matters. The rows come from a real PostgreSQL server started
/// from a pinned image, the profile and draft writes go through the real
/// <see cref="IVoiceProfileService"/> over the real stores, and every claim is read back over the
/// wire - a status code, a header, a body - or over a real query against the real tables. There is no
/// EF in-memory provider and no stubbed store in this file.
/// </para>
/// <para>
/// Every scenario gets its own database and the clock is injected and frozen, so every stored
/// instant is deterministic. The identities are declared constants and each scenario names its
/// project and draft ids in advance, so the row a scenario expects is a fact rather than a guess.
/// </para>
/// <para>
/// The two things this package is actually about are asserted as sharply as the code allows: that an
/// explicit override beats the preset <em>for every field</em>, and that a finding reports a span
/// into text it did not change. The snapshot boundary is proven against the stored
/// <c>voice_snapshot</c> column itself, byte for byte, rather than by comparing two objects this
/// process built.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class VoiceProfilesTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("7a1e4c30-2b6d-4f18-9e05-1c2d3e4f5a06");

    /// <summary>A second installation, used only to prove another scope's rows are not reachable.</summary>
    private static readonly Guid ForeignScopeId = new("8b2f5d41-3c7e-4029-af16-2d3e4f5a6b17");

    /// <summary>A compliant Chinese title: inside the budget, no forbidden term, no emoji.</summary>
    private const string CompliantTitle = "把发布流程改成可复现的流水线";

    /// <summary>A compliant Chinese body: Han script, no forbidden term, no emoji.</summary>
    private const string CompliantBody =
        "我们把发布流程从手工脚本改成了一条可复现的流水线，每一次发布都能追溯到具体的提交，"
        + "回滚也不再依赖某个人记得当时的步骤。";

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-001 Success: a preset that requests emoji, and an owner who turns it off.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Explicit_override_beats_the_preset_for_every_field_it_names()
    {
        var database = await ProvisionedDatabaseAsync("voice_override");
        var projectId = await SeededProjectAsync(database, "override project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // The preset genuinely asks for emoji, so the override has something to win against.
        var presets = await ReadJsonAsync(await owner.GetAsync("/api/voice-presets"));
        var technicalBlog = presets.RootElement
            .GetProperty("presets")
            .EnumerateArray()
            .Single(preset => preset.GetProperty("id").GetString() == VoicePresetIds.TechnicalBlog);

        Assert.True(technicalBlog.GetProperty("values").GetProperty("emoji").GetBoolean());

        // Every field is overridden at once, each to a value the preset does not hold, so a resolver
        // that only honoured the booleans - or only honoured the first field it looked at - fails
        // here rather than passing on the one case the requirement happens to name.
        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new
            {
                presetId = VoicePresetIds.TechnicalBlog,
                overrides = new
                {
                    language = "en",
                    tone = "casual",
                    emoji = false,
                    titleMaxChars = 24,
                    technicalDetail = "low",
                    marketing = "high",
                    firstPerson = false,
                    forbiddenTerms = new[] { "groundbreaking", "unmatched" },
                },
            },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var profile = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
        var effective = profile.GetProperty("effectiveValues");

        Assert.False(effective.GetProperty("emoji").GetBoolean());
        Assert.Equal("en", effective.GetProperty("language").GetString());
        Assert.Equal("casual", effective.GetProperty("tone").GetString());
        Assert.Equal(24, effective.GetProperty("titleMaxChars").GetInt32());
        Assert.Equal("low", effective.GetProperty("technicalDetail").GetString());
        Assert.Equal("high", effective.GetProperty("marketing").GetString());
        Assert.False(effective.GetProperty("firstPerson").GetBoolean());
        Assert.Equal(
            new[] { "groundbreaking", "unmatched" },
            effective.GetProperty("forbiddenTerms").EnumerateArray().Select(term => term.GetString()).ToArray());

        // Field by field, the preset said something else and the override is what survived - which
        // is what makes "the override won" observable rather than inferred from the values.
        var fields = profile.GetProperty("fields").EnumerateArray().ToDictionary(
            field => field.GetProperty("field").GetString()!,
            field => field);

        foreach (var name in new[]
                 {
                     "language", "tone", "emoji", "titleMaxChars",
                     "technicalDetail", "marketing", "firstPerson", "forbiddenTerms",
                 })
        {
            Assert.True(fields[name].GetProperty("overridden").GetBoolean(), $"{name} was not overridden");
            Assert.NotEqual(
                fields[name].GetProperty("presetValue").GetString(),
                fields[name].GetProperty("effectiveValue").GetString());
        }

        // And the stored row holds the preset and the overrides, never a resolved copy that could
        // disagree with its own inputs.
        Assert.Equal(
            "en|false|24|low|high|false",
            await ReadTextAsync(
                database,
                $"""
                 SELECT overrides->>'language' || '|' || (overrides->>'emoji')
                        || '|' || (overrides->>'titleMaxChars') || '|' || (overrides->>'technicalDetail')
                        || '|' || (overrides->>'marketing') || '|' || (overrides->>'firstPerson')
                 FROM voice_profiles WHERE project_id = '{projectId:D}'
                 """));

        Assert.Equal(
            $"{VoicePresetIds.TechnicalBlog}|1",
            await ReadTextAsync(
                database,
                $"SELECT preset_id || '|' || revision FROM voice_profiles WHERE project_id = '{projectId:D}'"));
    }

    [Fact]
    public async Task A_profile_with_no_overrides_resolves_exactly_to_its_preset()
    {
        var database = await ProvisionedDatabaseAsync("voice_no_overrides");
        var projectId = await SeededProjectAsync(database, "no override project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Linkedin },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var profile = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement;
        var preset = profile.GetProperty("presetValues");
        var effective = profile.GetProperty("effectiveValues");

        // Every field equal, and no field claiming to be an override.
        foreach (var property in preset.EnumerateObject())
        {
            Assert.Equal(
                property.Value.ToString(),
                effective.GetProperty(property.Name).ToString());
        }

        Assert.Empty(profile.GetProperty("overriddenFields").EnumerateArray());

        // The stored overrides column is a record with no field *set*: every key is present and every
        // value is null. That is the distinction the whole package rests on - a stored false and a
        // stored "unset" have to be different bytes in the column, not the same missing value.
        Assert.Equal(
            "true",
            await ReadTextAsync(
                database,
                $"SELECT ((overrides->>'emoji') IS NULL)::text FROM voice_profiles WHERE project_id = '{projectId:D}'"));

        Assert.Equal(
            "true",
            await ReadTextAsync(
                database,
                $"SELECT ((overrides->>'firstPerson') IS NULL)::text FROM voice_profiles WHERE project_id = '{projectId:D}'"));
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-001 Failure: an unknown preset is refused and nothing is written.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_preset_is_refused_with_validation_failed_and_writes_nothing()
    {
        var database = await ProvisionedDatabaseAsync("voice_unknown_preset");
        var projectId = await SeededProjectAsync(database, "unknown preset project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var refused = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = "not-a-preset", overrides = new { emoji = false } },
            revision: 0));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(VoiceError.ValidationFailed, envelope.GetProperty("code").GetString());
        Assert.Equal(
            "The preset is not one this build offers.",
            envelope.GetProperty("fieldErrors").GetProperty("presetId")[0].GetString());
        Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("traceId").GetString()));

        // Nothing was persisted: no profile row, and no audit row claiming one was written.
        Assert.Equal("0", await ReadTextAsync(database, "SELECT count(*) FROM voice_profiles"));
        Assert.Equal(
            "0",
            await ReadTextAsync(
                database,
                $"SELECT count(*) FROM audit_events WHERE action = '{VoiceProfileService.VoiceProfileSavedAction}'"));

        // And the read still resolves to the defaults, because a refused save left nothing behind.
        using var read = await owner.GetAsync($"/api/projects/{projectId:D}/voice-profile");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(JsonDocument.Parse(await read.Content.ReadAsStringAsync())
            .RootElement.GetProperty("stored").GetBoolean());
    }

    [Theory]
    [InlineData("language", "fr")]
    [InlineData("tone", "shouty")]
    [InlineData("technicalDetail", "extreme")]
    [InlineData("marketing", "maximum")]
    public async Task An_override_outside_its_vocabulary_is_refused_and_writes_nothing(string field, string value)
    {
        var database = await ProvisionedDatabaseAsync($"voice_bad_{field}");
        var projectId = await SeededProjectAsync(database, $"bad {field} project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var refused = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Devlog, overrides = new Dictionary<string, object?> { [field] = value } },
            revision: 0));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            VoiceError.ValidationFailed,
            JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        Assert.Equal("0", await ReadTextAsync(database, "SELECT count(*) FROM voice_profiles"));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(161)]
    public async Task A_title_budget_outside_the_declared_range_is_refused(int titleMaxChars)
    {
        var database = await ProvisionedDatabaseAsync($"voice_budget_{titleMaxChars}");
        var projectId = await SeededProjectAsync(database, $"budget {titleMaxChars} project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var refused = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Devlog, overrides = new { titleMaxChars } },
            revision: 0));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("0", await ReadTextAsync(database, "SELECT count(*) FROM voice_profiles"));
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-001 Boundary: a voice change after a draft cannot reach that draft.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_voice_change_after_a_draft_leaves_the_stored_snapshot_byte_identical()
    {
        var database = await ProvisionedDatabaseAsync("voice_snapshot");
        var projectId = await SeededProjectAsync(database, "snapshot project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // The first draft is written under a voice that allows emoji and calls for a wide title.
        using var firstProfile = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, firstProfile.StatusCode);

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var draftId = draft.GetProperty("id").GetGuid();
        var firstFingerprint = draft.GetProperty("snapshot").GetProperty("fingerprint").GetString();

        // The exact stored bytes, read from the column rather than from a value this process built.
        var storedSnapshotBefore = await ReadTextAsync(
            database,
            $"SELECT voice_snapshot::text FROM voice_drafts WHERE id = '{draftId:D}'");
        var storedTextBefore = await ReadTextAsync(
            database,
            $"SELECT title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'");

        Assert.NotNull(storedSnapshotBefore);
        Assert.NotNull(storedTextBefore);

        // Now the owner changes the voice: emoji off, a narrow title budget, a different tone.
        using var secondProfile = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new
            {
                presetId = VoicePresetIds.TechnicalBlog,
                overrides = new { emoji = false, tone = "casual", titleMaxChars = 20, marketing = "high" },
            },
            revision: 1));

        Assert.Equal(HttpStatusCode.OK, secondProfile.StatusCode);
        Assert.Equal(2, JsonDocument.Parse(await secondProfile.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revision").GetInt32());

        // The existing draft is untouched: the same snapshot bytes, the same fingerprint, the same
        // text, and the draft's own revision - it was not even rewritten.
        var storedSnapshotAfter = await ReadTextAsync(
            database,
            $"SELECT voice_snapshot::text FROM voice_drafts WHERE id = '{draftId:D}'");
        var storedTextAfter = await ReadTextAsync(
            database,
            $"SELECT title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'");

        Assert.Equal(storedSnapshotBefore, storedSnapshotAfter);
        Assert.Equal(storedTextBefore, storedTextAfter);
        Assert.Equal("1", await ReadTextAsync(database, $"SELECT revision FROM voice_drafts WHERE id = '{draftId:D}'"));

        // Read back over the wire, the draft still reports the voice it was created under.
        using var reread = await owner.GetAsync($"/api/voice-drafts/{draftId:D}");

        Assert.Equal(HttpStatusCode.OK, reread.StatusCode);

        var rereadDraft = JsonDocument.Parse(await reread.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(firstFingerprint, rereadDraft.GetProperty("snapshot").GetProperty("fingerprint").GetString());
        Assert.Equal(1, rereadDraft.GetProperty("snapshot").GetProperty("profileRevision").GetInt32());
        Assert.True(rereadDraft.GetProperty("snapshot").GetProperty("values").GetProperty("emoji").GetBoolean());

        // A draft created after the change takes the new voice: the snapshot really is taken at
        // creation, and it really is a new value rather than a view of the profile.
        using var second = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var secondDraft = JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement;
        var secondSnapshot = secondDraft.GetProperty("snapshot");

        Assert.NotEqual(firstFingerprint, secondSnapshot.GetProperty("fingerprint").GetString());
        Assert.Equal(2, secondSnapshot.GetProperty("profileRevision").GetInt32());
        Assert.False(secondSnapshot.GetProperty("values").GetProperty("emoji").GetBoolean());
        Assert.Equal(20, secondSnapshot.GetProperty("values").GetProperty("titleMaxChars").GetInt32());
        Assert.Equal("casual", secondSnapshot.GetProperty("values").GetProperty("tone").GetString());

        // And the first draft's stored bytes are still exactly what they were after all of that.
        Assert.Equal(
            storedSnapshotBefore,
            await ReadTextAsync(database, $"SELECT voice_snapshot::text FROM voice_drafts WHERE id = '{draftId:D}'"));
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-002 Success: compliant text produces no finding at all.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Compliant_title_and_text_report_no_violation()
    {
        var database = await ProvisionedDatabaseAsync("voice_clean");
        var projectId = await SeededProjectAsync(database, "clean project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);

        var result = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync()).RootElement;

        Assert.True(result.GetProperty("clean").GetBoolean());
        Assert.Empty(result.GetProperty("findings").EnumerateArray());

        // The answer names the voice it was measured against, so a clean result is never a clean
        // result against an unnamed voice.
        Assert.Equal(
            VoicePresetIds.TechnicalBlog,
            result.GetProperty("snapshot").GetProperty("presetId").GetString());

        // The stateless check wrote nothing: no draft row exists for a check that stored no text.
        Assert.Equal("0", await ReadTextAsync(database, "SELECT count(*) FROM voice_drafts"));

        // The same text stored as a draft also reports clean, and that draft can be approved.
        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var draftId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/voice-drafts/{draftId:D}/approve",
            body: null,
            revision: 1));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var approvedDraft = JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(VoiceDraftStatuses.Approved, approvedDraft.GetProperty("status").GetString());
        Assert.Equal(2, approvedDraft.GetProperty("revision").GetInt32());
        Assert.Equal(CompliantTitle, approvedDraft.GetProperty("title").GetString());
        Assert.Equal(CompliantBody, approvedDraft.GetProperty("body").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-002 Failure: a case-variant forbidden term is still found, with its span.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_case_variant_forbidden_term_is_found_and_reported_with_its_span()
    {
        var database = await ProvisionedDatabaseAsync("voice_case_variant");
        var projectId = await SeededProjectAsync(database, "case variant project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // The configured term is lower-case; the owner's text is not. Matching is case-insensitive
        // over Latin, so this is a finding rather than a miss.
        const string body = "这是一次 Game-Changing 的发布，我们把流水线的构建时间从四十分钟压到了九分钟。";

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new
            {
                presetId = VoicePresetIds.TechnicalBlog,
                overrides = new { forbiddenTerms = new[] { "game-changing", VoiceDefaults.ForbiddenTerms[1] } },
            },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = CompliantTitle, body }));

        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);

        var result = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync()).RootElement;

        Assert.False(result.GetProperty("clean").GetBoolean());

        var finding = result.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal(VoiceFindingRules.ForbiddenTerm, finding.GetProperty("rule").GetString());
        Assert.Equal(VoiceFindingRules.BodyField, finding.GetProperty("field").GetString());

        // The finding names the configured term, the normalized form matching ran against, and the
        // span - and the span really does address the case variant in the owner's own characters.
        Assert.Equal("game-changing", finding.GetProperty("term").GetString());
        Assert.Equal("game-changing", finding.GetProperty("normalizedTerm").GetString());
        Assert.Equal("Game-Changing", finding.GetProperty("excerpt").GetString());
        Assert.Equal(body.IndexOf("Game-Changing", StringComparison.Ordinal), finding.GetProperty("start").GetInt32());
        Assert.Equal("Game-Changing".Length, finding.GetProperty("length").GetInt32());

        // The excerpt is a verbatim slice of the stored text, not a normalized copy of it.
        var start = finding.GetProperty("start").GetInt32();
        var length = finding.GetProperty("length").GetInt32();
        Assert.Equal("Game-Changing", body.Substring(start, length));

        // A Han term matches exactly, and is found without any case folding at all.
        using var han = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = CompliantTitle, body = "这一版的改动很震撼，回滚路径也短了很多。" }));

        Assert.Equal(HttpStatusCode.OK, han.StatusCode);

        var hanFinding = JsonDocument.Parse(await han.Content.ReadAsStringAsync())
            .RootElement.GetProperty("findings").EnumerateArray()
            .Single(item => item.GetProperty("term").GetString() == VoiceDefaults.ForbiddenTerms[1]);

        Assert.Equal("震撼", hanFinding.GetProperty("excerpt").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-002 Boundary: an owner edit is judged, not rewritten.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owner_edit_with_forbidden_wording_is_judged_never_rewritten_and_approval_is_refused()
    {
        var database = await ProvisionedDatabaseAsync("voice_edited");
        var projectId = await SeededProjectAsync(database, "edited draft project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var draftId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // The owner rewrites the body into wording the voice forbids. Leading and trailing spaces are
        // deliberate: nothing on the write path trims, so the byte comparison below is meaningful.
        const string editedBody = "  这一版的改动是震撼的，我们把构建时间从四十分钟压到了九分钟。  ";

        using var edited = await owner.SendAsync(await owner.Put(
            $"/api/voice-drafts/{draftId:D}",
            new { title = CompliantTitle, body = editedBody },
            revision: 1));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var editedDraft = JsonDocument.Parse(await edited.Content.ReadAsStringAsync()).RootElement;
        var storedTextBefore = await ReadTextAsync(
            database,
            $"SELECT title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'");

        // The edit was stored exactly as sent - including the surrounding spaces.
        Assert.Equal(editedBody, editedDraft.GetProperty("body").GetString());
        Assert.Equal($"{CompliantTitle}|{editedBody}", storedTextBefore);

        // Checking it reports the finding and changes nothing.
        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/voice-drafts/{draftId:D}/approve",
            body: null,
            revision: 2));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, checkedRequest.StatusCode);

        var envelope = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(VoiceError.ValidationFailed, envelope.GetProperty("code").GetString());
        Assert.Contains(
            "震撼",
            envelope.GetProperty("fieldErrors").GetProperty("draft.body")[0].GetString(),
            StringComparison.Ordinal);

        // The stored text is byte-identical after the refusal, and the draft is still a draft at the
        // same revision: a refused approval wrote nothing at all.
        Assert.Equal(
            storedTextBefore,
            await ReadTextAsync(database, $"SELECT title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'"));
        Assert.Equal(
            $"{VoiceDraftStatuses.Draft}|2",
            await ReadTextAsync(database, $"SELECT status || '|' || revision FROM voice_drafts WHERE id = '{draftId:D}'"));

        // The read-only check reports the same finding with the same span, and also writes nothing.
        using var read = await owner.GetAsync($"/api/voice-drafts/{draftId:D}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var readDraft = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement;
        var finding = readDraft.GetProperty("findings").EnumerateArray()
            .Single(item => item.GetProperty("rule").GetString() == VoiceFindingRules.ForbiddenTerm);

        Assert.False(readDraft.GetProperty("clean").GetBoolean());
        Assert.Equal("震撼", finding.GetProperty("excerpt").GetString());
        Assert.Equal(editedBody.IndexOf("震撼", StringComparison.Ordinal), finding.GetProperty("start").GetInt32());

        Assert.Equal(
            storedTextBefore,
            await ReadTextAsync(database, $"SELECT title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'"));

        // Correcting the wording by hand is what unblocks it, and the corrected text is then stored
        // byte for byte - the system never supplied the correction.
        using var corrected = await owner.SendAsync(await owner.Put(
            $"/api/voice-drafts/{draftId:D}",
            new { title = CompliantTitle, body = CompliantBody },
            revision: 2));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/voice-drafts/{draftId:D}/approve",
            body: null,
            revision: 3));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(
            $"{VoiceDraftStatuses.Approved}|{CompliantTitle}|{CompliantBody}",
            await ReadTextAsync(database, $"SELECT status || '|' || title || '|' || body FROM voice_drafts WHERE id = '{draftId:D}'"));
    }

    // ---------------------------------------------------------------------------------------------
    // VOICE-PROFILES-002: every structural constraint the design lists is evaluated.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_structural_constraint_is_evaluated_and_each_finding_names_what_it_flagged()
    {
        var database = await ProvisionedDatabaseAsync("voice_constraints");
        var projectId = await SeededProjectAsync(database, "constraints project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // A narrow Chinese voice with emoji off, so the shape rules have a small target to miss.
        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new
            {
                presetId = VoicePresetIds.TechnicalBlog,
                overrides = new
                {
                    language = "zh",
                    emoji = false,
                    titleMaxChars = 10,
                    forbiddenTerms = new[] { "震撼" },
                },
            },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // One body trips three of the four rules at once: a past-budget title, a forbidden term and
        // an emoji. The fourth - the declared language - cannot fire on Chinese text under a voice
        // that declares Chinese, so it is proven on its own input below rather than forced into a
        // body it cannot honestly violate.
        const string longTitle = "把发布流程改成一条完全可复现的构建流水线";
        const string body = "这一版的改动很震撼，构建时间从四十分钟降到了九分钟，🚀 上线也快了。";

        var observed = new SortedSet<string>(StringComparer.Ordinal);

        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = longTitle, body }));

        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);

        var byRule = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("findings").EnumerateArray()
            .GroupBy(finding => finding.GetProperty("rule").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var rule in byRule.Keys)
        {
            observed.Add(rule);
        }

        // The forbidden term: names the configured term, its normalized form, and the span.
        var term = byRule[VoiceFindingRules.ForbiddenTerm].Single();
        Assert.Equal("震撼", term.GetProperty("term").GetString());
        Assert.Equal("震撼", term.GetProperty("normalizedTerm").GetString());
        Assert.Equal(VoiceFindingRules.BodyField, term.GetProperty("field").GetString());
        Assert.Equal(body.IndexOf("震撼", StringComparison.Ordinal), term.GetProperty("start").GetInt32());
        Assert.Equal("震撼".Length, term.GetProperty("length").GetInt32());

        // The emoji: names the character it found and where it is, in the body's own coordinates.
        var emoji = byRule[VoiceFindingRules.EmojiNotAllowed].Single();
        Assert.Equal("🚀", emoji.GetProperty("excerpt").GetString());
        Assert.Equal(body.IndexOf("🚀", StringComparison.Ordinal), emoji.GetProperty("start").GetInt32());

        // The title budget: names the budget it exceeded, and points at the overflow rather than at
        // the whole title - the span runs from the first character past the budget to the end.
        var budget = byRule[VoiceFindingRules.TitleTooLong].Single();
        Assert.Equal("10", budget.GetProperty("term").GetString());
        Assert.Equal(VoiceFindingRules.TitleField, budget.GetProperty("field").GetString());
        Assert.True(longTitle.Length > 10, "the fixture title has to exceed the budget to be a finding");
        Assert.Equal(10, budget.GetProperty("start").GetInt32());
        Assert.Equal(longTitle.Length - 10, budget.GetProperty("length").GetInt32());

        // The declared language: a voice that declares Chinese and reads with no Han character in it.
        using var languageChecked = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = "A reproducible pipeline", body = "We replaced the hand-run release script." }));

        Assert.Equal(HttpStatusCode.OK, languageChecked.StatusCode);

        var languageFindings = JsonDocument.Parse(await languageChecked.Content.ReadAsStringAsync())
            .RootElement.GetProperty("findings").EnumerateArray()
            .Where(item => item.GetProperty("rule").GetString() == VoiceFindingRules.LanguageMismatch)
            .ToArray();

        observed.Add(VoiceFindingRules.LanguageMismatch);

        // Both fields are flagged, each naming the declared language and the script it looked for.
        Assert.Equal(2, languageFindings.Length);
        Assert.All(languageFindings, item =>
        {
            Assert.Equal("zh", item.GetProperty("term").GetString());
            Assert.Contains("no Han characters", item.GetProperty("message").GetString()!, StringComparison.Ordinal);
        });

        // Every rule the checker publishes was evaluated by one of these two calls, and none of the
        // rules it does not publish was invented.
        Assert.Equal(VoiceFindingRules.All.OrderBy(rule => rule, StringComparer.Ordinal), observed);

        // And the same four rules do not fire for a compliant title and body under the same voice.
        using var clean = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = "可复现构建流水线", body = "构建时间从四十分钟降到了九分钟，回滚路径也短了。" }));

        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.True(JsonDocument.Parse(await clean.Content.ReadAsStringAsync())
            .RootElement.GetProperty("clean").GetBoolean());
    }

    [Fact]
    public async Task A_language_mismatch_is_reported_rather_than_guessed_at()
    {
        var database = await ProvisionedDatabaseAsync("voice_language");
        var projectId = await SeededProjectAsync(database, "language project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The default voice declares Chinese, and this title and body are entirely English.
        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new
            {
                title = "A reproducible release pipeline",
                body = "We replaced the hand-run release script with one reproducible pipeline.",
            }));

        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);

        var findings = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync())
            .RootElement.GetProperty("findings").EnumerateArray()
            .Where(finding => finding.GetProperty("rule").GetString() == VoiceFindingRules.LanguageMismatch)
            .ToArray();

        // Both fields are reported, and the message says what kind of check this is rather than
        // claiming to have understood the text.
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding =>
            Assert.Contains("no Han characters", finding.GetProperty("message").GetString()!, StringComparison.Ordinal));
        Assert.All(findings, finding =>
            Assert.Contains("script check", finding.GetProperty("message").GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_emoji_is_accepted_when_the_effective_voice_allows_it()
    {
        var database = await ProvisionedDatabaseAsync("voice_emoji_allowed");
        var projectId = await SeededProjectAsync(database, "emoji allowed project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // The xiaohongshu preset asks for emoji, and the owner does not override it.
        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Xiaohongshu },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var checkedRequest = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = "今天的构建时间终于降下来了✨", body = "把发布脚本换成了流水线，回滚也快了。" }));

        Assert.Equal(HttpStatusCode.OK, checkedRequest.StatusCode);

        var result = JsonDocument.Parse(await checkedRequest.Content.ReadAsStringAsync()).RootElement;

        Assert.DoesNotContain(
            result.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("rule").GetString() == VoiceFindingRules.EmojiNotAllowed);

        // With emoji turned off, exactly the same text is reported.
        using var strict = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Xiaohongshu, overrides = new { emoji = false } },
            revision: 1));

        Assert.Equal(HttpStatusCode.OK, strict.StatusCode);

        using var rechecked = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-check",
            new { title = "今天的构建时间终于降下来了✨", body = "把发布脚本换成了流水线，回滚也快了。" }));

        Assert.Equal(HttpStatusCode.OK, rechecked.StatusCode);

        var emojiFinding = JsonDocument.Parse(await rechecked.Content.ReadAsStringAsync())
            .RootElement.GetProperty("findings").EnumerateArray()
            .Single(finding => finding.GetProperty("rule").GetString() == VoiceFindingRules.EmojiNotAllowed);

        Assert.Equal("✨", emojiFinding.GetProperty("excerpt").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // Scope, precondition and concurrency.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_foreign_scoped_profile_and_draft_are_both_404()
    {
        var database = await ProvisionedDatabaseAsync("voice_foreign");
        var projectId = await SeededProjectAsync(database, "foreign scope project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        var draftId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // A second installation's row, seeded directly, is not this owner's to read.
        var foreignProjectId = new Guid("9c305e52-4d8f-413a-b027-3e4f5a6b7c28");
        await ChronicleafPostgresFixture.ExecuteAsync(
            database,
            $$"""
             INSERT INTO projects (id, scope_id, name, description, project_url, revision)
             VALUES ('{{foreignProjectId:D}}', '{{ForeignScopeId:D}}', 'Foreign project', 'Owned elsewhere.', NULL, 1);
             INSERT INTO voice_profiles
                 (id, scope_id, project_id, preset_id, overrides, revision, created_at, updated_at)
             VALUES (gen_random_uuid(), '{{ForeignScopeId:D}}', '{{foreignProjectId:D}}', '{{VoicePresetIds.Linkedin}}',
                     '{}'::jsonb, 1, now(), now());
             """);

        // The foreign profile is 404, and so is the draft read through a foreign-scoped id, so a
        // caller cannot probe another installation by comparing answers.
        using var foreignRead = await owner.GetAsync($"/api/projects/{foreignProjectId:D}/voice-profile");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal(VoiceError.NotFound, JsonDocument.Parse(await foreignRead.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        using var foreignList = await owner.GetAsync($"/api/projects/{foreignProjectId:D}/voice-drafts");
        Assert.Equal(HttpStatusCode.NotFound, foreignList.StatusCode);

        using var absentDraft = await owner.GetAsync($"/api/voice-drafts/{draftId:D}/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, absentDraft.StatusCode);

        using var unknownDraft = await owner.GetAsync($"/api/voice-drafts/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, unknownDraft.StatusCode);
    }

    [Fact]
    public async Task A_missing_If_Match_is_428_and_a_stale_one_is_409_with_the_profile_preserved()
    {
        var database = await ProvisionedDatabaseAsync("voice_precondition");
        var projectId = await SeededProjectAsync(database, "precondition project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // The first save presents 0, the token the read publishes for a project with no profile.
        using var first = await owner.GetAsync($"/api/projects/{projectId:D}/voice-profile");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"0\"", first.Headers.ETag?.ToString());

        using var noMatch = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog }));

        Assert.Equal((HttpStatusCode)428, noMatch.StatusCode);
        Assert.Equal(
            VoiceError.IfMatchRequired,
            JsonDocument.Parse(await noMatch.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // Nothing was written by the refused request.
        Assert.Equal("0", await ReadTextAsync(database, "SELECT count(*) FROM voice_profiles"));

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.TechnicalBlog, overrides = new { emoji = false } },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"1\"", saved.Headers.ETag?.ToString());

        // A stale revision is a conflict that names the stored one, and the stored profile is
        // exactly what it was.
        var preserved = await ReadTextAsync(
            database,
            $"SELECT preset_id || '|' || overrides::text || '|' || revision FROM voice_profiles WHERE project_id = '{projectId:D}'");

        using var stale = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new { presetId = VoicePresetIds.Linkedin },
            revision: 0));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var conflict = JsonDocument.Parse(await stale.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(VoiceError.RevisionConflict, conflict.GetProperty("code").GetString());
        Assert.Contains("1", conflict.GetProperty("fieldErrors").GetProperty("If-Match")[0].GetString()!, StringComparison.Ordinal);

        Assert.Equal(
            preserved,
            await ReadTextAsync(database, $"SELECT preset_id || '|' || overrides::text || '|' || revision FROM voice_profiles WHERE project_id = '{projectId:D}'"));

        // The same precondition rules apply to a draft.
        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        var draftId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var draftNoMatch = await owner.SendAsync(await owner.Put(
            $"/api/voice-drafts/{draftId:D}",
            new { title = CompliantTitle, body = CompliantBody }));

        Assert.Equal((HttpStatusCode)428, draftNoMatch.StatusCode);

        using var draftStale = await owner.SendAsync(await owner.Put(
            $"/api/voice-drafts/{draftId:D}",
            new { title = CompliantTitle, body = CompliantBody },
            revision: 99));

        Assert.Equal(HttpStatusCode.Conflict, draftStale.StatusCode);
        Assert.Equal(
            $"{VoiceDraftStatuses.Draft}|1",
            await ReadTextAsync(database, $"SELECT status || '|' || revision FROM voice_drafts WHERE id = '{draftId:D}'"));
    }

    // ---------------------------------------------------------------------------------------------
    // The list, the audit trail and the schema identity.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_draft_list_defaults_to_25_caps_at_100_and_uses_a_scope_bound_cursor()
    {
        var database = await ProvisionedDatabaseAsync("voice_list");
        var projectId = await SeededProjectAsync(database, "list project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        // More rows than one page, so the default page size and the cap are both observable.
        for (var index = 0; index < 27; index++)
        {
            using var created = await owner.SendAsync(await owner.Post(
                $"/api/projects/{projectId:D}/voice-drafts",
                new { title = $"{CompliantTitle}{index}", body = CompliantBody }));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var firstPage = await owner.GetAsync($"/api/projects/{projectId:D}/voice-drafts");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);

        var page = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(25, page.GetProperty("items").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);

        // A page beyond the cap is clamped, not refused and not unbounded.
        using var capped = await owner.GetAsync($"/api/projects/{projectId:D}/voice-drafts?limit=500");
        Assert.Equal(HttpStatusCode.OK, capped.StatusCode);
        Assert.Equal(100, JsonDocument.Parse(await capped.Content.ReadAsStringAsync())
            .RootElement.GetProperty("pageSize").GetInt32());

        // The second page continues exactly where the first ended, and then stops.
        var cursor = page.GetProperty("nextCursor").GetString()!;
        var lastId = page.GetProperty("items").EnumerateArray().Last().GetProperty("id").GetGuid();

        using var secondPage = await owner.GetAsync(
            $"/api/projects/{projectId:D}/voice-drafts?limit=25&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);

        var second = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.DoesNotContain(
            second.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == lastId);

        // A cursor issued for this scope is refused for another one, so pagination cannot walk
        // outside the installation it was issued for.
        var foreign = ScopeCursor.Encode(ForeignScopeId, "2026-10-03T15:00:00.0000000Z", Guid.NewGuid());

        using var wrongScope = await owner.GetAsync(
            $"/api/projects/{projectId:D}/voice-drafts?cursor={Uri.EscapeDataString(foreign)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongScope.StatusCode);
        Assert.Equal(VoiceError.ValidationFailed, JsonDocument.Parse(await wrongScope.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());

        using var malformed = await owner.GetAsync(
            $"/api/projects/{projectId:D}/voice-drafts?cursor=not-a-cursor");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
    }

    [Fact]
    public async Task Every_mutation_audits_the_ids_action_and_revision_and_no_owner_text()
    {
        var database = await ProvisionedDatabaseAsync("voice_audit");
        var projectId = await SeededProjectAsync(database, "audit project");

        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await owner.SendAsync(await owner.Put(
            $"/api/projects/{projectId:D}/voice-profile",
            new
            {
                presetId = VoicePresetIds.TechnicalBlog,
                overrides = new { forbiddenTerms = new[] { "confidential-term" } },
            },
            revision: 0));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var created = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody }));

        var draftId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var approved = await owner.SendAsync(await owner.Post(
            $"/api/voice-drafts/{draftId:D}/approve",
            body: null,
            revision: 1));

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        // One row per mutation, each naming the action, the target id and the revision it produced.
        Assert.Equal(
            $"{VoiceProfileService.VoiceProfileSavedAction}|{projectId:D}|1",
            await ReadTextAsync(
                database,
                $"SELECT action || '|' || target_id::text || '|' || (safe_metadata->>'revision') FROM audit_events"
                + $" WHERE action = '{VoiceProfileService.VoiceProfileSavedAction}'"));

        Assert.Equal(
            $"{VoiceProfileService.VoiceDraftCreatedAction}|{draftId:D}|1",
            await ReadTextAsync(
                database,
                $"SELECT action || '|' || target_id::text || '|' || (safe_metadata->>'revision') FROM audit_events"
                + $" WHERE action = '{VoiceProfileService.VoiceDraftCreatedAction}'"));

        Assert.Equal(
            $"{VoiceProfileService.VoiceDraftApprovedAction}|{draftId:D}|2",
            await ReadTextAsync(
                database,
                $"SELECT action || '|' || target_id::text || '|' || (safe_metadata->>'revision') FROM audit_events"
                + $" WHERE action = '{VoiceProfileService.VoiceDraftApprovedAction}'"));

        // And the trail carries neither a forbidden term nor a line of the owner's text, because a
        // log of the audit is readable more widely than the record it describes.
        var trail = await ReadTextAsync(database, "SELECT action || safe_metadata::text FROM audit_events");

        Assert.NotNull(trail);
        Assert.DoesNotContain("confidential-term", trail, StringComparison.Ordinal);
        Assert.DoesNotContain(CompliantBody, trail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_voice_migration_is_part_of_the_schema_this_build_requires()
    {
        var database = await ProvisionedDatabaseAsync("voice_schema");

        // The tables this package owns exist, with the constraints the domain relies on.
        Assert.Equal(
            "ux_voice_profiles_scope_id_project_id",
            await ReadTextAsync(
                database,
                """
                 SELECT indexname FROM pg_indexes
                 WHERE tablename = 'voice_profiles' AND indexname LIKE 'ux_%'
                 """));

        Assert.Equal(
            "jsonb",
            await ReadTextAsync(
                database,
                """
                 SELECT data_type FROM information_schema.columns
                 WHERE table_name = 'voice_profiles' AND column_name = 'overrides'
                 """));

        Assert.Equal(
            "jsonb",
            await ReadTextAsync(
                database,
                """
                 SELECT data_type FROM information_schema.columns
                 WHERE table_name = 'voice_drafts' AND column_name = 'voice_snapshot'
                 """));

        // Readiness counts the voice migration among the ids this build requires, so a database
        // without it is reported by name rather than failing on the first read. The newest required
        // id has moved on several times since the voice migration - generation-previews, then
        // github-evidence, then screenshot-capture, then grounded-markdown-drafts - so this scenario
        // asserts the newest by its position rather than naming a value that goes stale on the next
        // package. Naming one is what every one of these three scenarios used to do, and it is why
        // each of them went red the moment a later migration arrived: the fix was a value somebody
        // had to remember to update. The position cannot go stale - it is the definition.
        Assert.Contains(ChronicleafSchema.VoiceProfilesMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.GitHubEvidenceMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Contains(ChronicleafSchema.ScreenshotCaptureMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(
            ChronicleafSchema.ExpectedMigrationId,
            ChronicleafSchema.RequiredMigrationIds[^1]);
        Assert.True(ChronicleafSchema.IsCurrent(
        [
            .. ChronicleafSchema.RequiredMigrationIds,
        ]));

        // A database that stopped at the credential-vault migration is missing exactly this one.
        var missing = ChronicleafSchema.MissingMigrationIds(
            ChronicleafSchema.RequiredMigrationIds.Where(id => id != ChronicleafSchema.VoiceProfilesMigrationId).ToArray());

        Assert.Equal([ChronicleafSchema.VoiceProfilesMigrationId], missing);
    }

    [Fact]
    public async Task The_voice_routes_stay_behind_the_delivered_owner_session_boundary()
    {
        var database = await ProvisionedDatabaseAsync("voice_boundary");

        await using var host = Host(database);
        using var client = host.CreateClient();

        // No session at all: every voice route is 401, and the shell's public routes still answer.
        foreach (var path in new[]
                 {
                     "/api/voice-presets",
                     $"/api/projects/{Guid.NewGuid():D}/voice-profile",
                     $"/api/projects/{Guid.NewGuid():D}/voice-drafts",
                     $"/api/voice-drafts/{Guid.NewGuid():D}",
                 })
        {
            using var anonymous = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(
                "unauthenticated",
                JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("code").GetString());
        }

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // A mutation is refused the same way, and writes nothing: the boundary runs before any
        // handler body, so a refused request is a request that changed no row.
        using var anonymousSave = await client.PostAsJsonAsync(
            $"/api/projects/{Guid.NewGuid():D}/voice-drafts",
            new { title = CompliantTitle, body = CompliantBody });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSave.StatusCode);

        using var anonymousApprove = await client.PostAsJsonAsync(
            $"/api/voice-drafts/{Guid.NewGuid():D}/approve",
            new { });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousApprove.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private static ChronicleafApiHost Host(string database) =>
        ChronicleafApiHost.StartWithAccess(
            database,
            Clock,
            OwnerClient.AccessTokenHash,
            OwnerClient.PublicOrigin);

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

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
    /// Creates one project through the real route, so a voice profile always hangs off a project the
    /// delivered package owns.
    /// </summary>
    private static async Task<Guid> SeededProjectAsync(string database, string name)
    {
        await using var host = Host(database);
        using var owner = await OwnerClient.CreateAsync(host);

        using var response = await owner.SendAsync(
            await owner.Post("/api/projects", new { name, description = $"{name} description." }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return Guid.Parse(JsonDocument
            .Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("id")
            .GetString()!);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<string?> ReadTextAsync(string database, string sql) =>
        ChronicleafPostgresFixture.ReadTextAsync(database, sql);
}
