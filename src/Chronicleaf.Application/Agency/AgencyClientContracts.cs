using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Agency;

/// <summary>
/// One page of clients, ordered by name and then id, plus the next cursor.
/// </summary>
public sealed record ClientPage(IReadOnlyList<Client> Items, string? NextCursor);

/// <summary>One page of projects grouped under one client, plus the next cursor.</summary>
public sealed record ClientProjectPage(IReadOnlyList<Project> Items, string? NextCursor);

/// <summary>Everything the owner may write on a new client.</summary>
public sealed record CreateClientInput(string Name, string Notes);

/// <summary>
/// A partial client edit. An absent field is left alone; an edit never moves a project, archives
/// a client or changes a revision, so those are separate calls with their own preconditions.
/// </summary>
public sealed record ClientPatch(string? Name, string? Notes);

/// <summary>
/// The assignment the owner asks for: the client to group the project under, or <c>null</c> to
/// make it personal and unassigned again.
/// </summary>
public sealed record ClientAssignment(Guid? ClientId);

/// <summary>
/// One project as seen through a client's report context: the identity, the name and the revision
/// the report was built against. A report context is built from explicit project ids intersected
/// with this client's membership, never from the owner's whole narrative.
/// </summary>
public sealed record ClientReportEntry(Guid ProjectId, string ProjectName, long ProjectRevision);

/// <summary>
/// The report context of one client at one membership generation: the client revision for edits,
/// the context revision the report pins, and exactly the requested projects that belong to this
/// client. A requested id that is unassigned, belongs to another client or is outside the scope
/// is absent rather than refused, so a caller cannot probe for another client's projects by
/// comparing answers.
/// </summary>
public sealed record ClientReportContext(
    Guid ClientId,
    long ClientRevision,
    long ContextRevision,
    IReadOnlyList<ClientReportEntry> Projects);

/// <summary>The failure codes this package adds. They extend the envelope; they do not change it.</summary>
public static class AgencyError
{
    /// <summary>
    /// The client is archived, so it accepts no new assignments. Its history stays readable and a
    /// project may still be moved away from it.
    /// </summary>
    public const string ClientArchived = "client_archived";
}

/// <summary>
/// The client this assignment names is archived, so it accepts no new projects (409). The request
/// changed nothing; the archived client's own history is untouched.
/// </summary>
public sealed class ClientArchivedException : ApiException
{
    public ClientArchivedException()
        : base(AgencyError.ClientArchived, "The client is archived and accepts no new projects.")
    {
    }
}
