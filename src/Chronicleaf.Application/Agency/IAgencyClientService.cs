using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Agency;

/// <summary>
/// The agency client and project-organization use cases over real PostgreSQL stores
/// (AGENCY-CLIENT-PROJECTS-001, AGENCY-CLIENT-PROJECTS-002).
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> The scope and the actor arrive in <see cref="OwnerScope"/> from the validated
/// session, reusing the single-user owner boundary the access package owns: per-client views are
/// filters over the owner's own records, not a new identity. A client id from another scope is
/// answered 404 without naming it, so a caller cannot probe for another installation's clients by
/// comparing answers.</para>
///
/// <para><b>Revisions.</b> A client edit is a compare-and-set on the revision the caller presented,
/// exactly like a project edit. A project assignment is a compare-and-set on the <em>project</em>
/// revision the caller presented: two assignments that present the same project revision are
/// decided by the database, and an assignment that repeats the stored client at the stored
/// revision is a no-op that writes nothing and audits nothing.</para>
///
/// <para><b>History.</b> Archive stamps the client and advances its revision; the row and every
/// project grouped under it stay exactly as they are. Assigning a project to an archived client is
/// refused, but moving one away (or clearing it) is still allowed, so no project can be stranded
/// under an archive. Every membership change advances the context revision of the old client and
/// the new one, which is what invalidates a stale unexported report context without touching a
/// client edit in flight.</para>
///
/// <para>Nothing here reaches outside the process. There is no network call, no clock other than
/// the injected <see cref="TimeProvider"/>, and no id or scope taken from a request body.</para>
/// </remarks>
public interface IAgencyClientService
{
    /// <summary>Lists the owner's clients ordered by name and then id.</summary>
    Task<ClientPage> ListClientsAsync(
        OwnerScope owner,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Creates one client in the owner's scope.</summary>
    Task<Client> CreateClientAsync(
        OwnerScope owner,
        CreateClientInput input,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one client in the owner's scope, or 404 when it is absent or out of scope.</summary>
    Task<Client> GetClientAsync(
        OwnerScope owner,
        Guid clientId,
        CancellationToken cancellationToken = default);

    /// <summary>Edits a live client at exactly <paramref name="expectedRevision"/>.</summary>
    Task<Client> UpdateClientAsync(
        OwnerScope owner,
        Guid clientId,
        long expectedRevision,
        ClientPatch patch,
        CancellationToken cancellationToken = default);

    /// <summary>Archives a live client at exactly <paramref name="expectedRevision"/>.</summary>
    Task<Client> ArchiveClientAsync(
        OwnerScope owner,
        Guid clientId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Groups a live project under a client (or clears it back to personal) at exactly the
    /// project's <paramref name="expectedRevision"/>. The client must be live and in the same
    /// scope; a foreign client is 404 and an archived target is 409.
    /// </summary>
    Task<Project> SetProjectClientAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        ClientAssignment assignment,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the projects grouped under one client, ordered by name and then id.</summary>
    Task<ClientProjectPage> ListClientProjectsAsync(
        OwnerScope owner,
        Guid clientId,
        PageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the report context of one client from explicit project ids: exactly the requested
    /// ids that name live projects of this client are included, pinned to the client's current
    /// context revision. Anything else is absent, never an error that names another client.
    /// </summary>
    Task<ClientReportContext> BuildReportContextAsync(
        OwnerScope owner,
        Guid clientId,
        IReadOnlyList<Guid> projectIds,
        CancellationToken cancellationToken = default);
}
