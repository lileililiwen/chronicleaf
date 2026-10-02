using Chronicleaf.Core;
using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Milestones;

/// <summary>
/// The scope and the identity every Project and Milestone operation runs under
/// (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// Both values come from the access package, never from a request body: <see cref="ScopeId"/> is the
/// installation this owner belongs to, and it is the only scope a stored record may be read or
/// written in. A caller that presents a valid session cannot widen its own scope, because it does
/// not get to name one.
/// </remarks>
/// <param name="ScopeId">The installation owner scope.</param>
/// <param name="ActorId">The owner identity recorded in the audit trail for a mutation.</param>
public sealed record OwnerScope(Guid ScopeId, Guid ActorId);

/// <summary>One page of projects, newest order defined by the store, plus the next cursor.</summary>
public sealed record ProjectPage(IReadOnlyList<Project> Items, string? NextCursor);

/// <summary>One page of milestones, ordered <c>occurredAt desc, id desc</c>.</summary>
public sealed record MilestonePage(IReadOnlyList<Milestone> Items, string? NextCursor);

/// <summary>
/// The outcome of importing one upstream record. A repeated import of a key that already exists
/// reports <see cref="Created"/> as <c>false</c> and the id of the candidate that was already there,
/// so a caller can always point at the one record instead of creating a second.
/// </summary>
public sealed record ImportedCandidate(string SourceKey, Guid MilestoneId, bool Created);

/// <summary>The full outcome of one import call, in the order the candidates were submitted.</summary>
public sealed record ImportedCandidates(IReadOnlyList<ImportedCandidate> Items)
{
    /// <summary>True when at least one candidate was created by this call.</summary>
    public bool CreatedAny => Items.Any(item => item.Created);

    /// <summary>True when the call created nothing because every key was already present.</summary>
    public bool CreatedNone => !CreatedAny;
}

/// <summary>The bounded list input both lists accept. A page size is always clamped, never refused.</summary>
public sealed record PageRequest(int? Limit, string? Cursor);

/// <summary>A milestone list request, optionally narrowed to one status.</summary>
public sealed record MilestoneListRequest(int? Limit, string? Cursor, string? Status)
{
    public static MilestoneListRequest From(PageRequest page, string? status) => new(page.Limit, page.Cursor, status);
}

/// <summary>Everything the owner may write on a new project.</summary>
public sealed record CreateProjectInput(
    string Name,
    string Description,
    Guid? RepoConnectionId,
    string? ProjectUrl);

/// <summary>
/// A partial project edit. An absent field is left alone; <c>ClearProjectUrl</c> and
/// <c>ClearRepoConnectionId</c> are how the owner empties a field that is meaningful when present and
/// meaningless when it is not. A field that is not sent and not cleared is never touched, so a
/// concurrent edit of that field cannot be lost by a partial save.
/// </summary>
public sealed record ProjectPatch(
    string? Name,
    string? Description,
    string? ProjectUrl,
    bool ClearProjectUrl,
    Guid? RepoConnectionId,
    bool ClearRepoConnectionId);

/// <summary>Everything a milestone carries apart from where it came from and what it is.</summary>
public sealed record MilestoneDraft(
    string Title,
    string? Version,
    DateTimeOffset OccurredAt,
    string Type,
    string Notes,
    IReadOnlyList<string> KnownLimitations,
    IReadOnlyList<string> NextSteps);

/// <summary>
/// A partial milestone edit. As with a project, an absent field is left alone. The status is not a
/// field here at all: no edit can set it, so an edit can never turn a candidate into a confirmed
/// record, and confirmation stays the one explicit owner action it is.
/// </summary>
public sealed record MilestonePatch(
    string? Title,
    string? Version,
    bool ClearVersion,
    DateTimeOffset? OccurredAt,
    string? Type,
    string? Notes,
    IReadOnlyList<string>? KnownLimitations,
    IReadOnlyList<string>? NextSteps);

/// <summary>One upstream record submitted for intake. It has no status: it becomes a candidate.</summary>
public sealed record CandidateImport(string SourceKey, MilestoneDraft Draft);

/// <summary>One import call: a source label and the records that carry keys in it.</summary>
public sealed record ImportCandidatesInput(string Source, IReadOnlyList<CandidateImport> Candidates);

/// <summary>The failure codes this package adds. They extend the envelope; they do not change it.</summary>
public static class MilestoneError
{
    /// <summary>
    /// An id that does not exist, or exists in a scope this owner may not read. One answer for both,
    /// so a caller cannot probe for records outside its own installation.
    /// </summary>
    public const string NotFound = "not_found";

    /// <summary>The project is archived, so it is read-only. Its history stays readable.</summary>
    public const string ProjectArchived = "project_archived";
}

/// <summary>
/// An id this owner may not reach (404). The message never distinguishes "absent" from "somebody
/// else's", because that difference is exactly what must not leak.
/// </summary>
public sealed class ResourceNotFoundException(string resource)
    : ApiException(MilestoneError.NotFound, $"No such {resource}.");

/// <summary>
/// The project this record belongs to is archived, so the record cannot be changed (409). The
/// request changed nothing and the archived project's own history is untouched.
/// </summary>
public sealed class ProjectArchivedException : ApiException
{
    public ProjectArchivedException()
        : base(MilestoneError.ProjectArchived, "The project is archived and cannot be changed.")
    {
    }
}
