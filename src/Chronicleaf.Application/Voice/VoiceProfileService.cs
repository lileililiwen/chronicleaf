using System.Globalization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// The owner-facing voice use cases (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The scope and the actor come from the owner's session, never from a request body, and the ids are
/// resolved through the delivered project store so a project this owner may not reach is a 404
/// before any voice row is read. The revision comes from the delivered unit of work, and every
/// mutation stages exactly one audit row naming ids, action and revision - never a term, a title or
/// a line of the owner's text.
/// </para>
/// <para>
/// The write paths are deliberately thin. A profile save resolves nothing into a second stored
/// column: it stores the preset and the overrides and lets the reader resolve them, so there is
/// exactly one place where precedence is decided. A draft save stores the owner's bytes and a
/// snapshot taken once, and never touches either again.
/// </para>
/// </remarks>
public sealed class VoiceProfileService(
    IVoiceProfileStore voice,
    IProjectStore projects,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider clock) : IVoiceProfileService
{
    /// <summary>The audit action recorded when a project's voice is stored.</summary>
    public const string VoiceProfileSavedAction = "voice.profile_saved";

    /// <summary>The audit action recorded when a draft is created under a voice snapshot.</summary>
    public const string VoiceDraftCreatedAction = "voice.draft_created";

    /// <summary>The audit action recorded when a draft's own text is replaced.</summary>
    public const string VoiceDraftEditedAction = "voice.draft_edited";

    /// <summary>The audit action recorded when a draft passes every rule and is approved.</summary>
    public const string VoiceDraftApprovedAction = "voice.draft_approved";

    /// <inheritdoc />
    public IReadOnlyList<VoicePreset> Presets() => VoicePresets.All;

    /// <inheritdoc />
    public async Task<VoiceProfileView> GetAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        // Resolved first: a project this scope may not reach must be a 404 whether or not it happens
        // to have a voice profile, so a caller cannot probe for the profile before the project.
        await LiveProjectAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);

        var profile = await voice.FindProfileAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);

        return profile is null
            ? Defaults(projectId)
            : View(profile);
    }

    /// <inheritdoc />
    public async Task<VoiceProfileView> SaveAsync(
        Guid scopeId,
        Guid projectId,
        VoiceProfileWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        await LiveProjectAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);

        // Validation runs before the unit of work opens, so a refused request has written nothing
        // at all - not a profile, not a revision, not an audit row.
        var presetId = ValidatePreset(write.PresetId);
        var overrides = ValidateOverrides(write.Overrides);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var now = clock.GetUtcNow();
                var existing = await voice
                    .FindProfileAsync(scopeId, projectId, token)
                    .ConfigureAwait(false);

                VoiceProfile stored;

                if (existing is null)
                {
                    stored = new VoiceProfile(
                        NewId(),
                        scopeId,
                        projectId,
                        presetId,
                        overrides,
                        revision: 1,
                        createdAt: now,
                        updatedAt: now);

                    if (!await voice.InsertProfileAsync(stored, token).ConfigureAwait(false))
                    {
                        // The unique (scope_id, project_id) index decided a race this call lost. The
                        // other writer's row is authoritative, so the caller is told to re-read
                        // rather than handed a revision it did not write.
                        throw new RevisionConflictException(await CurrentRevisionAsync(scopeId, projectId, token).ConfigureAwait(false));
                    }
                }
                else
                {
                    RequireCurrentRevision(existing.Revision, write.ExpectedRevision);

                    stored = new VoiceProfile(
                        existing.Id,
                        scopeId,
                        projectId,
                        presetId,
                        overrides,
                        existing.Revision + 1,
                        existing.CreatedAt,
                        now);

                    var changed = await voice.ReplaceProfileAsync(
                        scopeId,
                        projectId,
                        write.ExpectedRevision,
                        presetId,
                        overrides,
                        stored.Revision,
                        now,
                        token).ConfigureAwait(false);

                    if (changed == 0)
                    {
                        throw new RevisionConflictException(
                            await CurrentRevisionAsync(scopeId, projectId, token).ConfigureAwait(false));
                    }
                }

                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: VoiceProfileSavedAction,
                    TargetId: projectId,
                    Revision: stored.Revision,
                    Detail: presetId));

                return View(stored);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VoiceCheckResult> CheckAsync(
        Guid scopeId,
        Guid projectId,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        // The stateless check resolves the project's *current* voice and names it in the answer, so
        // a caller can tell a clean result from a clean result measured against a different voice.
        var view = await GetAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);
        var snapshot = SnapshotOf(view, clock.GetUtcNow());

        return new VoiceCheckResult(snapshot, VoiceCheckService.Check(snapshot.Values, title, body));
    }

    /// <inheritdoc />
    public async Task<VoiceDraftView> CreateDraftAsync(
        Guid scopeId,
        Guid projectId,
        VoiceDraftCreate create,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);

        var view = await GetAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);
        var title = ValidateText(create.Title, "title", VoiceLimits.MaxTitleTextLength, required: true);
        var body = ValidateText(create.Body, "body", VoiceLimits.MaxBodyTextLength, required: true);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var now = clock.GetUtcNow();
                var draftId = NewId();

                // The snapshot is a copied value taken here, in the same unit of work that creates
                // the draft. Nothing later in this repository can reach it, which is what makes the
                // requirement's boundary scenario true rather than merely intended.
                var draft = VoiceDraft.Create(draftId, scopeId, projectId, title, body, now, SnapshotOf(view, now));

                if (!await voice.InsertDraftAsync(draft, token).ConfigureAwait(false))
                {
                    // The id is minted inside this transaction, so a collision here is not a caller
                    // error and not a conflict the caller can act on. It is recorded as what it is.
                    throw new InternalFailureException("The draft could not be stored.");
                }

                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: VoiceDraftCreatedAction,
                    TargetId: draftId,
                    Revision: draft.Revision,
                    Detail: draft.VoiceSnapshot.PresetId));

                return View(draft);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VoiceDraftView> GetDraftAsync(
        Guid scopeId,
        Guid draftId,
        CancellationToken cancellationToken = default) =>
        View(await DraftAsync(scopeId, draftId, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<VoiceCheckResult> CheckDraftAsync(
        Guid scopeId,
        Guid draftId,
        CancellationToken cancellationToken = default)
    {
        var draft = await DraftAsync(scopeId, draftId, cancellationToken).ConfigureAwait(false);

        // Judged under the draft's own snapshot, not the project's current profile. A read never
        // writes, so this call cannot alter the stored title or body by any path.
        return Check(draft);
    }

    /// <inheritdoc />
    public async Task<VoiceDraftView> EditDraftAsync(
        Guid scopeId,
        Guid draftId,
        VoiceDraftEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var title = ValidateText(edit.Title, "title", VoiceLimits.MaxTitleTextLength, required: true);
        var body = ValidateText(edit.Body, "body", VoiceLimits.MaxBodyTextLength, required: true);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var existing = await DraftAsync(scopeId, draftId, token).ConfigureAwait(false);
                RequireCurrentRevision(existing.Revision, edit.ExpectedRevision);

                if (existing.Status == VoiceDraftStatuses.Approved)
                {
                    // Approved text is terminal. Re-opening it would let a silent edit undo a
                    // judgement the owner already signed off on.
                    throw new ValidationFailedException(
                        "status",
                        "This draft is approved and its text can no longer be replaced.");
                }

                var now = clock.GetUtcNow();
                var changed = await voice.ReplaceDraftTextAsync(
                    scopeId,
                    draftId,
                    edit.ExpectedRevision,
                    title,
                    body,
                    existing.Revision + 1,
                    now,
                    token).ConfigureAwait(false);

                if (changed == 0)
                {
                    throw new RevisionConflictException(existing.Revision);
                }

                var stored = await voice.FindDraftAsync(scopeId, draftId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("voice draft");

                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: VoiceDraftEditedAction,
                    TargetId: draftId,
                    Revision: stored.Revision,
                    Detail: existing.VoiceSnapshot.PresetId));

                return View(stored);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VoiceDraftView> ApproveDraftAsync(
        Guid scopeId,
        Guid draftId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        await unitOfWork.ExecuteAsync(
            async token =>
            {
                var existing = await DraftAsync(scopeId, draftId, token).ConfigureAwait(false);
                RequireCurrentRevision(existing.Revision, expectedRevision);

                if (existing.Status == VoiceDraftStatuses.Approved)
                {
                    return View(existing);
                }

                var check = Check(existing);
                if (!check.IsClean)
                {
                    // The refusal names every rule that still fires, with its field and offset, and
                    // the stored title and body are not written to in either branch. Approval is a
                    // gate on the owner's words, never a change to them.
                    throw new ValidationFailedException(FindingsAsFieldErrors(check.Findings));
                }

                var now = clock.GetUtcNow();
                var changed = await voice.ApproveDraftAsync(
                    scopeId,
                    draftId,
                    expectedRevision,
                    existing.Revision + 1,
                    now,
                    token).ConfigureAwait(false);

                if (changed == 0)
                {
                    throw new RevisionConflictException(existing.Revision);
                }

                var stored = await voice.FindDraftAsync(scopeId, draftId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("voice draft");

                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: VoiceDraftApprovedAction,
                    TargetId: draftId,
                    Revision: stored.Revision,
                    Detail: stored.VoiceSnapshot.PresetId));

                return View(stored);
            },
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<VoiceDraftPage> ListDraftsAsync(
        Guid scopeId,
        Guid projectId,
        VoiceDraftPageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);

        var limit = ListQueryPolicy.Clamp(page.PageSize);
        var (createdBefore, idBefore) = string.IsNullOrEmpty(page.Cursor)
            ? ((DateTimeOffset?)null, (Guid?)null)
            : DraftCursor.Decode(page.Cursor, scopeId);

        await LiveProjectAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);

        // One row beyond the page, so a next cursor is handed out only when another row really
        // exists rather than on a full last page that happens to be the end.
        var fetched = await voice
            .ListDraftsAsync(scopeId, projectId, createdBefore, idBefore, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var items = fetched.Take(limit).ToList();
        var nextCursor = fetched.Count > limit && items.Count > 0
            ? DraftCursor.Encode(scopeId, items[^1].CreatedAt, items[^1].Id)
            : null;

        return new VoiceDraftPage(
            [.. items.Select(draft => Summary(draft, Check(draft).Findings.Count))],
            nextCursor,
            limit);
    }

    /// <summary>The findings a draft's own stored text produces under its own snapshot.</summary>
    private static VoiceCheckResult Check(VoiceDraft draft) =>
        new(draft.VoiceSnapshot, VoiceCheckService.Check(draft.VoiceSnapshot.Values, draft.Title, draft.Body));

    private static VoiceDraftView View(VoiceDraft draft)
    {
        var check = Check(draft);
        return new VoiceDraftView(draft, draft.VoiceSnapshot, check.Findings);
    }

    private static VoiceDraftSummary Summary(VoiceDraft draft, int findingCount) =>
        new(
            draft.Id,
            draft.ProjectId,
            draft.Status,
            draft.Revision,
            draft.CreatedAt,
            draft.UpdatedAt,
            draft.VoiceSnapshot.Fingerprint,
            findingCount);

    private static VoiceProfileView Defaults(Guid projectId)
    {
        // A project that has never had its voice configured resolves to the design's defaults with
        // no overrides at all, and says so through a null profile id rather than by inventing a row.
        var resolved = VoiceProfileResolver.Resolve(VoicePresetIds.TechnicalBlog, VoiceOverrides.Empty);

        return new VoiceProfileView(
            Id: Guid.Empty,
            ProjectId: projectId,
            PresetId: resolved.PresetId,
            Overrides: VoiceOverrides.Empty,
            Revision: 0,
            Stored: false,
            CreatedAt: default,
            UpdatedAt: default,
            Resolved: resolved);
    }

    private static VoiceProfileView View(VoiceProfile profile) =>
        new(
            profile.Id,
            profile.ProjectId,
            profile.PresetId,
            profile.Overrides,
            profile.Revision,
            Stored: true,
            profile.CreatedAt,
            profile.UpdatedAt,
            VoiceProfileResolver.Resolve(profile.PresetId, profile.Overrides));

    /// <summary>
    /// Copies a resolved profile into a snapshot value. This is the only place a snapshot is
    /// produced, and it produces a detached copy: nothing in the returned value points back at the
    /// profile row, so nothing can later change it.
    /// </summary>
    private static VoiceSnapshot SnapshotOf(VoiceProfileView view, DateTimeOffset capturedAt) =>
        new(view.PresetId, view.Revision, capturedAt, view.Resolved.Effective);

    private static IReadOnlyDictionary<string, string[]> FindingsAsFieldErrors(
        IReadOnlyList<VoiceFinding> findings)
    {
        // One key per field the findings point at, each naming every rule that still fires on that
        // field. A caller can therefore show the owner what to fix and where, without the envelope
        // carrying the text itself.
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var group in findings.GroupBy(finding => finding.Field, StringComparer.Ordinal))
        {
            errors[$"draft.{group.Key}"] = [.. group.Select(finding => finding.Message)];
        }

        return errors;
    }

    private async Task<VoiceDraft> DraftAsync(Guid scopeId, Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await voice.FindDraftAsync(scopeId, draftId, cancellationToken).ConfigureAwait(false);

        // Absent and out-of-scope are the same answer, so a caller cannot tell another scope's draft
        // id from a made-up one.
        if (draft is null)
        {
            throw new ResourceNotFoundException("voice draft");
        }

        return draft;
    }

    private async Task<Project> LiveProjectAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects.FindAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);

        if (project is null || project.ArchivedAt is not null)
        {
            throw new ResourceNotFoundException("project");
        }

        return project;
    }

    private async Task<long> CurrentRevisionAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken)
    {
        var current = await voice.FindProfileAsync(scopeId, projectId, cancellationToken).ConfigureAwait(false);
        return current?.Revision ?? 0;
    }

    private static void RequireCurrentRevision(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new RevisionConflictException(actual);
        }
    }

    private static string ValidatePreset(string? presetId) =>
        VoicePresetIds.Normalize(presetId) is { } known
            ? known
            : throw new ValidationFailedException("presetId", "The preset is not one this build offers.");

    private static VoiceOverrides ValidateOverrides(VoiceOverrides? overrides)
    {
        if (overrides is null || overrides.IsEmpty)
        {
            return VoiceOverrides.Empty;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (overrides.Language is not null && !VoiceVocabularies.IsLanguage(overrides.Language))
        {
            errors["language"] = ["The language is not one this build offers."];
        }

        if (overrides.Tone is not null && !VoiceVocabularies.IsTone(overrides.Tone))
        {
            errors["tone"] = ["The tone is not one this build offers."];
        }

        if (overrides.TechnicalDetail is not null && !VoiceVocabularies.IsTechnicalDetail(overrides.TechnicalDetail))
        {
            errors["technicalDetail"] = ["The technical detail is not one this build offers."];
        }

        if (overrides.Marketing is not null && !VoiceVocabularies.IsMarketing(overrides.Marketing))
        {
            errors["marketing"] = ["The marketing level is not one this build offers."];
        }

        if (overrides.TitleMaxChars is { } titleMaxChars && !VoiceLimits.IsTitleMaxChars(titleMaxChars))
        {
            errors["titleMaxChars"] = [string.Create(
                CultureInfo.InvariantCulture,
                $"The title budget must be between {VoiceLimits.MinTitleMaxChars} and {VoiceLimits.MaxTitleMaxChars} characters.")];
        }

        if (overrides.ForbiddenTerms is { } terms)
        {
            var termError = ValidateTerms(terms);
            if (termError is not null)
            {
                errors["forbiddenTerms"] = [termError];
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return overrides;
    }

    private static string? ValidateTerms(IReadOnlyList<string> terms)
    {
        if (terms.Count > VoiceLimits.MaxForbiddenTerms)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"At most {VoiceLimits.MaxForbiddenTerms} forbidden terms are accepted.");
        }

        for (var index = 0; index < terms.Count; index++)
        {
            var term = terms[index];

            if (string.IsNullOrWhiteSpace(term))
            {
                return "A forbidden term cannot be empty; it would match the whole text.";
            }

            if (term.Length > VoiceLimits.ForbiddenTermMaxLength)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"A forbidden term is at most {VoiceLimits.ForbiddenTermMaxLength} characters.");
            }
        }

        return null;
    }

    private static string ValidateText(string? value, string field, int maxLength, bool required)
    {
        // Returned unchanged. Nothing trims, folds or re-encodes an owner's text on the way in, and
        // the byte-identical guarantee a check makes depends on that being true here.
        var text = value ?? string.Empty;

        if (required && text.Length == 0)
        {
            throw new ValidationFailedException(field, $"The {field} cannot be empty.");
        }

        if (text.Length > maxLength)
        {
            throw new ValidationFailedException(
                field,
                string.Create(CultureInfo.InvariantCulture, $"The {field} is at most {maxLength} characters."));
        }

        return text;
    }

    private static Guid NewId() => Guid.NewGuid();
}
