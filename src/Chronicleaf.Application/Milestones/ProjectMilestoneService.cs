using Chronicleaf.Application.Common;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// The Project and Milestone use cases over real PostgreSQL stores
/// (PROJECTS-AND-MILESTONES-001, PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// <para><b>Status.</b> A record is created confirmed or created as a candidate, and the choice is
/// made here, at the only two creation paths, from what the caller is: the owner saving a record the
/// owner typed, or a source delivering one. There is no third path, no request field that sets a
/// status and no code path in this package other than <see cref="ConfirmMilestoneAsync"/> and
/// <see cref="DismissMilestoneAsync"/> that writes one, so an imported record cannot be read or
/// written as confirmed before the owner confirms it. A patch carries no status at all, which is the
/// structural reason an edit cannot promote a candidate.</para>
///
/// <para><b>Revisions.</b> Every accepted edit advances the revision by exactly one, and every write
/// to a stored record is a compare-and-set on the revision the caller presented. Two edits that
/// present the same revision are therefore decided by the database: the first commits, the second
/// updates no row, re-reads what is now stored and is told the truth in a 409. Nothing in this file
/// computes a new revision from a value it read before the write, so one edit can never overwrite
/// another, and the losing edit's own values are discarded rather than merged.</para>
///
/// <para><b>Archive.</b> Archiving stamps <c>archived_at</c> and advances the revision. The row, its
/// milestones and their notes are untouched, so the history stays readable; and because every
/// milestone write joins its project and refuses an archived one, the archived project becomes
/// read-only in the database rather than only in this file. The archived stamp is also a revision
/// change, so a client holding a pre-archive revision is told it is stale instead of being allowed
/// to write.</para>
///
/// <para>Nothing here reaches outside the process. There is no network call, no clock other than the
/// injected <see cref="TimeProvider"/>, and no id or scope taken from a request body: the scope and
/// the actor arrive in <see cref="OwnerScope"/> from the validated session.</para>
/// </remarks>
public sealed class ProjectMilestoneService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider clock) : IProjectMilestoneService
{
    /// <summary>The audit action recorded when a project is created.</summary>
    public const string ProjectCreatedAction = "project.created";

    /// <summary>The audit action recorded when a project is edited.</summary>
    public const string ProjectUpdatedAction = "project.updated";

    /// <summary>The audit action recorded when a project is archived.</summary>
    public const string ProjectArchivedAction = "project.archived";

    /// <summary>The audit action recorded when the owner creates a milestone by hand.</summary>
    public const string MilestoneCreatedAction = "milestone.created";

    /// <summary>The audit action recorded when a source delivers a candidate.</summary>
    public const string MilestoneImportedAction = "milestone.imported";

    /// <summary>The audit action recorded when a milestone's fields are edited.</summary>
    public const string MilestoneUpdatedAction = "milestone.updated";

    /// <summary>The audit action recorded when the owner confirms a milestone.</summary>
    public const string MilestoneConfirmedAction = "milestone.confirmed";

    /// <summary>The audit action recorded when the owner dismisses a milestone.</summary>
    public const string MilestoneDismissedAction = "milestone.dismissed";

    /// <summary>The revision every newly created record starts at.</summary>
    private const long InitialRevision = 1;

    public async Task<ProjectPage> ListProjectsAsync(
        OwnerScope owner,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var limit = MilestonePolicy.ClampPageSize(request.Limit);
        var (afterName, afterId) = DecodeProjectCursor(owner, request.Cursor);

        // One row beyond the page, so the caller is only told there is a next page when there is
        // genuinely another row to read.
        var rows = await projects
            .ListAsync(owner.ScopeId, afterName, afterId, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        return Page(rows, limit, (name, id) => ScopeCursor.Encode(owner.ScopeId, name, id));
    }

    public async Task<Project> CreateProjectAsync(
        OwnerScope owner,
        CreateProjectInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        Throw(MilestonePolicy.ValidateProject(input));

        var project = new Project(
            Guid.NewGuid(),
            owner.ScopeId,
            input.Name.Trim(),
            input.Description ?? string.Empty,
            input.RepoConnectionId,
            Trimmed(input.ProjectUrl),
            InitialRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await projects.InsertAsync(project, token).ConfigureAwait(false))
                {
                    // The only way a fresh id collides is another writer holding the same one, which
                    // is reported as an internal failure rather than retried into a different record.
                    throw new InternalFailureException("The new project could not be stored.");
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ProjectCreatedAction,
                    project.Id,
                    project.Revision,
                    "project_created"));

                return project;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Project> GetProjectAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return await projects
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound("project");
    }

    public async Task<Project> UpdateProjectAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        ProjectPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(patch);

        Throw(MilestonePolicy.ValidateProjectPatch(patch));
        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveProjectAsync(owner, projectId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                var name = patch.Name is null ? current.Name : patch.Name.Trim();
                var description = patch.Description is null ? current.Description : patch.Description;
                var projectUrl = patch.ClearProjectUrl ? null : patch.ProjectUrl ?? current.ProjectUrl;
                var repoConnectionId = patch.ClearRepoConnectionId
                    ? null
                    : patch.RepoConnectionId ?? current.RepoConnectionId;

                var affected = await projects.UpdateAsync(
                    owner.ScopeId,
                    projectId,
                    expectedRevision,
                    name,
                    description,
                    projectUrl,
                    repoConnectionId,
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostProjectWriteAsync(owner, projectId, token).ConfigureAwait(false);
                }

                var updated = await ReloadProjectAsync(owner, projectId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ProjectUpdatedAction,
                    projectId,
                    updated.Revision,
                    "project_updated"));

                return updated;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Project> ArchiveProjectAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveProjectAsync(owner, projectId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                var affected = await projects.ArchiveAsync(
                    owner.ScopeId,
                    projectId,
                    expectedRevision,
                    clock.GetUtcNow(),
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostProjectWriteAsync(owner, projectId, token).ConfigureAwait(false);
                }

                var archived = await ReloadProjectAsync(owner, projectId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ProjectArchivedAction,
                    projectId,
                    archived.Revision,
                    "project_archived"));

                // The rows stay. The archive is a stamp on the project, and nothing is deleted.
                return archived;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MilestonePage> ListMilestonesAsync(
        OwnerScope owner,
        Guid projectId,
        MilestoneListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        Throw(MilestonePolicy.ValidateStatusFilter(request.Status));

        // The project is read first, so a project outside the owner's scope is a 404 whether or not
        // it happens to have milestones.
        await GetProjectAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        var limit = MilestonePolicy.ClampPageSize(request.Limit);
        DateTimeOffset? occurredBefore = null;
        Guid? idBefore = null;

        if (request.Cursor is { } cursor)
        {
            (occurredBefore, idBefore) = ScopeCursor.DecodeMilestone(cursor, owner.ScopeId);
        }

        var rows = await milestones
            .ListAsync(owner.ScopeId, projectId, request.Status, occurredBefore, idBefore, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        return Page(
            rows,
            limit,
            (occurredAt, id) => ScopeCursor.EncodeMilestone(owner.ScopeId, occurredAt, id));
    }

    public async Task<Milestone> CreateManualMilestoneAsync(
        OwnerScope owner,
        Guid projectId,
        MilestoneDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(draft);

        Throw(MilestonePolicy.ValidateDraft(draft));

        // The owner typed this and pressed save. That explicit action is the confirmation, so there
        // is nothing left to review and the record is stored confirmed.
        var milestone = NewMilestone(projectId, draft, MilestoneStatus.Confirmed, MilestoneSource.Manual, sourceKey: null);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                await LiveProjectAsync(owner, projectId, token).ConfigureAwait(false);

                if (!await milestones.InsertAsync(milestone, token).ConfigureAwait(false))
                {
                    throw new InternalFailureException("The new milestone could not be stored.");
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    MilestoneCreatedAction,
                    milestone.Id,
                    milestone.Revision,
                    "milestone_created"));

                return milestone;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportedCandidates> ImportCandidatesAsync(
        OwnerScope owner,
        Guid projectId,
        ImportCandidatesInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        Throw(MilestonePolicy.ValidateImport(input));

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                await LiveProjectAsync(owner, projectId, token).ConfigureAwait(false);

                var outcomes = new List<ImportedCandidate>(input.Candidates.Count);

                foreach (var candidate in input.Candidates)
                {
                    // Every record that arrives from a source starts as a candidate. The request has
                    // no field that could say otherwise, and the status below is written here rather
                    // than taken from the caller.
                    var record = NewMilestone(
                        projectId,
                        candidate.Draft,
                        MilestoneStatus.Candidate,
                        input.Source,
                        candidate.SourceKey);

                    // The insert is the decision: the partial unique index makes a repeated key a
                    // no-op and returns the caller's own id when the row is new. Two imports of the
                    // same key that race each other therefore produce one row and one id.
                    var created = await milestones.InsertAsync(record, token).ConfigureAwait(false);

                    if (created)
                    {
                        audit.Record(new DomainAudit(
                            owner.ActorId,
                            MilestoneImportedAction,
                            record.Id,
                            record.Revision,
                            "milestone_imported"));
                    }

                    var milestoneId = created
                        ? record.Id
                        : (await milestones
                            .FindBySourceAsync(owner.ScopeId, projectId, input.Source, candidate.SourceKey, token)
                            .ConfigureAwait(false))?.Id
                          ?? record.Id;

                    outcomes.Add(new ImportedCandidate(candidate.SourceKey, milestoneId, created));
                }

                return new ImportedCandidates(outcomes);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Milestone> GetMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return await milestones
            .FindAsync(owner.ScopeId, milestoneId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound("milestone");
    }

    public async Task<Milestone> UpdateMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        MilestonePatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(patch);

        Throw(MilestonePolicy.ValidateMilestonePatch(patch));
        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                var title = patch.Title is null ? current.Title : patch.Title.Trim();
                var version = patch.ClearVersion ? null : patch.Version ?? current.Version;
                var occurredAt = patch.OccurredAt?.ToUniversalTime() ?? current.OccurredAt;
                var type = patch.Type ?? current.Type;
                var notes = patch.Notes ?? current.Notes;
                var knownLimitations = patch.KnownLimitations ?? current.KnownLimitations;
                var nextSteps = patch.NextSteps ?? current.NextSteps;

                var affected = await milestones.UpdateAsync(
                    owner.ScopeId,
                    milestoneId,
                    expectedRevision,
                    title,
                    version,
                    occurredAt,
                    type,
                    notes,
                    knownLimitations,
                    nextSteps,
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostMilestoneWriteAsync(owner, milestoneId, token).ConfigureAwait(false);
                }

                var updated = await ReloadMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    MilestoneUpdatedAction,
                    milestoneId,
                    updated.Revision,
                    "milestone_updated"));

                return updated;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<Milestone> ConfirmMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            owner,
            milestoneId,
            expectedRevision,
            MilestoneStatus.Confirmed,
            MilestoneConfirmedAction,
            cancellationToken);

    public Task<Milestone> DismissMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            owner,
            milestoneId,
            expectedRevision,
            MilestoneStatus.Dismissed,
            MilestoneDismissedAction,
            cancellationToken);

    /// <summary>
    /// The one write that can change a status, reached only from the owner's explicit action.
    /// </summary>
    /// <remarks>
    /// Repeating an action that has already been applied is not a second change: when the record
    /// already holds the target status at the revision the caller presented, it is returned unchanged
    /// with no write and no new audit row. A record that has to be moved goes through the same
    /// compare-and-set as any other edit, so a confirmation that races a dismissal still leaves one
    /// winner rather than a record that is neither.
    /// <para>
    /// A dismissed record is not a dead end: confirming it is the explicit owner action that brings
    /// it back, and it is kept in full while it is dismissed. Nothing else in this package, and no
    /// automated process anywhere in it, can change a status.
    /// </para>
    /// </remarks>
    private async Task<Milestone> ChangeStatusAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        string status,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                if (string.Equals(current.Status, status, StringComparison.Ordinal))
                {
                    return current;
                }

                var affected = await milestones.SetStatusAsync(
                    owner.ScopeId,
                    milestoneId,
                    expectedRevision,
                    status,
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostMilestoneWriteAsync(owner, milestoneId, token).ConfigureAwait(false);
                }

                var updated = await ReloadMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    action,
                    milestoneId,
                    updated.Revision,
                    "milestone_status_changed"));

                return updated;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------
    // Reads inside a write. Every mutation re-reads its own target, so it can tell an absent record
    // from an archived one from a revision that moved, and answer with the right refusal.
    // -----------------------------------------------------------------------------------------

    private async Task<Project> LiveProjectAsync(OwnerScope owner, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projects
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound("project");

        // An archived project is readable and unchangeable. The stamp is checked here, so the
        // refusal happens before anything is written, and again in the SQL guard, so it still holds
        // if the archive lands between this read and the write.
        if (project.IsArchived)
        {
            throw new ProjectArchivedException();
        }

        return project;
    }

    private async Task<Milestone> LiveMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken)
    {
        var milestone = await milestones
            .FindAsync(owner.ScopeId, milestoneId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound("milestone");

        await LiveProjectAsync(owner, milestone.ProjectId, cancellationToken).ConfigureAwait(false);

        return milestone;
    }

    private async Task<Project> ReloadProjectAsync(OwnerScope owner, Guid projectId, CancellationToken cancellationToken) =>
        await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
        ?? throw NotFound("project");

    private async Task<Milestone> ReloadMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken) =>
        await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
        ?? throw NotFound("milestone");

    /// <summary>
    /// Works out which refusal a write that changed no row deserves, from what is stored now: gone,
    /// archived, or a revision that moved on. This is what turns a lost race into an honest answer
    /// instead of a generic failure.
    /// </summary>
    private async Task<ApiException> LostProjectWriteAsync(OwnerScope owner, Guid projectId, CancellationToken token)
    {
        var latest = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);

        return latest switch
        {
            null => NotFound("project"),
            { IsArchived: true } => new ProjectArchivedException(),
            _ => new RevisionConflictException(latest.Revision),
        };
    }

    private async Task<ApiException> LostMilestoneWriteAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken token)
    {
        var latest = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false);

        if (latest is null)
        {
            return NotFound("milestone");
        }

        var project = await projects.FindAsync(owner.ScopeId, latest.ProjectId, token).ConfigureAwait(false);
        if (project is { IsArchived: true })
        {
            return new ProjectArchivedException();
        }

        return new RevisionConflictException(latest.Revision);
    }

    // -----------------------------------------------------------------------------------------
    // Shared rules.
    // -----------------------------------------------------------------------------------------

    private static Milestone NewMilestone(
        Guid projectId,
        MilestoneDraft draft,
        string status,
        string source,
        string? sourceKey) =>
        new(
            Guid.NewGuid(),
            projectId,
            draft.Title.Trim(),
            Trimmed(draft.Version),
            draft.OccurredAt.ToUniversalTime(),
            draft.Type,
            source,
            Trimmed(sourceKey),
            status,
            InitialRevision,
            draft.Notes ?? string.Empty,
            [.. (draft.KnownLimitations ?? []).Select(item => item.Trim()).Where(item => item.Length > 0)],
            [.. (draft.NextSteps ?? []).Select(item => item.Trim()).Where(item => item.Length > 0)]);

    private static ProjectPage Page(
        IReadOnlyList<Project> rows,
        int limit,
        Func<string, Guid, string> encode)
    {
        var items = rows.Take(limit).ToArray();
        var last = items.Length == limit ? items[^1] : null;

        return new ProjectPage(items, last is null ? null : encode(last.Name, last.Id));
    }

    private static MilestonePage Page(
        IReadOnlyList<Milestone> rows,
        int limit,
        Func<DateTimeOffset, Guid, string> encode)
    {
        var items = rows.Take(limit).ToArray();
        var last = items.Length == limit ? items[^1] : null;

        return new MilestonePage(items, last is null ? null : encode(last.OccurredAt, last.Id));
    }

    private static (string? Name, Guid? Id) DecodeProjectCursor(OwnerScope owner, string? cursor)
    {
        if (cursor is null)
        {
            return (null, null);
        }

        var (sortKey, id) = ScopeCursor.Decode(cursor, owner.ScopeId);

        return (sortKey, id);
    }

    private static void RequireRevision(long expectedRevision)
    {
        if (expectedRevision < 1)
        {
            // A revision is always the one a caller read. Zero and below are not revisions any record
            // has ever had, so they are refused before the store is touched.
            throw new ValidationFailedException("If-Match", "the revision must be a positive integer.");
        }
    }

    private static void RequireCurrent(long expectedRevision, long storedRevision)
    {
        if (expectedRevision != storedRevision)
        {
            // The newer edit is kept and the stale one is discarded: nothing in this path merges one
            // revision's values into another's.
            throw new RevisionConflictException(storedRevision);
        }
    }

    private static void Throw(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ResourceNotFoundException NotFound(string resource) => new(resource);
}
