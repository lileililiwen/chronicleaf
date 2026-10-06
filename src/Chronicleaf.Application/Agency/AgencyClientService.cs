using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Agency;

/// <summary>
/// The agency client and project-organization use cases over real PostgreSQL stores
/// (AGENCY-CLIENT-PROJECTS-001, AGENCY-CLIENT-PROJECTS-002).
/// </summary>
public sealed class AgencyClientService(
    IClientStore clients,
    IProjectStore projects,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider clock) : IAgencyClientService
{
    /// <summary>The audit action recorded when a client is created.</summary>
    public const string ClientCreatedAction = "client.created";

    /// <summary>The audit action recorded when a client is edited.</summary>
    public const string ClientUpdatedAction = "client.updated";

    /// <summary>The audit action recorded when a client is archived.</summary>
    public const string ClientArchivedAction = "client.archived";

    /// <summary>The audit action recorded when a project is grouped under a client or cleared.</summary>
    public const string ProjectClientAssignedAction = "project.client_assigned";

    /// <summary>The revision every newly created client starts at.</summary>
    private const long InitialRevision = 1;

    /// <summary>The membership generation every newly created client starts at.</summary>
    private const long InitialContextRevision = 1;

    public async Task<ClientPage> ListClientsAsync(
        OwnerScope owner,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var limit = AgencyClientPolicy.ClampPageSize(request.Limit);
        var (afterName, afterId) = DecodeCursor(owner, request.Cursor);

        // One row beyond the page, so the caller is only told there is a next page when there is
        // genuinely another row to read.
        var rows = await clients
            .ListAsync(owner.ScopeId, afterName, afterId, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        return Page(rows, limit, (name, id) => ScopeCursor.Encode(owner.ScopeId, name, id));
    }

    public async Task<Client> CreateClientAsync(
        OwnerScope owner,
        CreateClientInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        Throw(AgencyClientPolicy.ValidateClient(input));

        var client = new Client(
            Guid.NewGuid(),
            owner.ScopeId,
            input.Name.Trim(),
            input.Notes ?? string.Empty,
            InitialRevision,
            InitialContextRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await clients.InsertAsync(client, token).ConfigureAwait(false))
                {
                    // The only way a fresh id collides is another writer holding the same one, which
                    // is reported as an internal failure rather than retried into a different record.
                    throw new InternalFailureException("The new client could not be stored.");
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ClientCreatedAction,
                    client.Id,
                    client.Revision,
                    "client_created"));

                return client;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Client> GetClientAsync(
        OwnerScope owner,
        Guid clientId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return await clients
            .FindAsync(owner.ScopeId, clientId, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound("client");
    }

    public async Task<Client> UpdateClientAsync(
        OwnerScope owner,
        Guid clientId,
        long expectedRevision,
        ClientPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(patch);

        Throw(AgencyClientPolicy.ValidateClientPatch(patch));
        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveClientAsync(owner, clientId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                var name = patch.Name is null ? current.Name : patch.Name.Trim();
                var notes = patch.Notes ?? current.Notes;

                var affected = await clients.UpdateAsync(
                    owner.ScopeId,
                    clientId,
                    expectedRevision,
                    name,
                    notes,
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostClientWriteAsync(owner, clientId, token).ConfigureAwait(false);
                }

                var updated = await ReloadClientAsync(owner, clientId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ClientUpdatedAction,
                    clientId,
                    updated.Revision,
                    "client_updated"));

                return updated;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Client> ArchiveClientAsync(
        OwnerScope owner,
        Guid clientId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await LiveClientAsync(owner, clientId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, current.Revision);

                var affected = await clients.ArchiveAsync(
                    owner.ScopeId,
                    clientId,
                    expectedRevision,
                    clock.GetUtcNow(),
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostClientWriteAsync(owner, clientId, token).ConfigureAwait(false);
                }

                var archived = await ReloadClientAsync(owner, clientId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ClientArchivedAction,
                    clientId,
                    archived.Revision,
                    "client_archived"));

                // The row stays, and so does every project grouped under it. The stamp is what
                // refuses new assignments; nothing grouped is moved or cleared by the archive.
                return archived;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Project> SetProjectClientAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        ClientAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(assignment);

        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await LiveScopedProjectAsync(owner, projectId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, project.Revision);

                if (project.ClientId == assignment.ClientId)
                {
                    // Repeating the stored association at the stored revision changes nothing: no
                    // write, no context bump, no audit row. A retry is a read of what is stored.
                    return project;
                }

                Client? target = null;
                if (assignment.ClientId is { } targetId)
                {
                    target = await clients
                        .FindAsync(owner.ScopeId, targetId, token)
                        .ConfigureAwait(false) ?? throw NotFound("client");

                    if (target.IsArchived)
                    {
                        throw new ClientArchivedException();
                    }
                }

                var affected = await projects.SetClientAsync(
                    owner.ScopeId,
                    projectId,
                    expectedRevision,
                    assignment.ClientId,
                    token).ConfigureAwait(false);

                if (affected == 0)
                {
                    throw await LostProjectWriteAsync(owner, projectId, token).ConfigureAwait(false);
                }

                // The membership generation of both sides moves, so a report context that pinned
                // the old generation no longer matches. The client revisions do not move, so an
                // owner holding a client edit is not told it is stale by a project moving.
                if (project.ClientId is { } oldClientId)
                {
                    await clients.TouchContextAsync(owner.ScopeId, oldClientId, token).ConfigureAwait(false);
                }

                if (target is not null && target.Id != project.ClientId)
                {
                    await clients.TouchContextAsync(owner.ScopeId, target.Id, token).ConfigureAwait(false);
                }

                var updated = await ReloadProjectAsync(owner, projectId, token).ConfigureAwait(false);
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ProjectClientAssignedAction,
                    projectId,
                    updated.Revision,
                    "project_client_assigned"));

                return updated;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientProjectPage> ListClientProjectsAsync(
        OwnerScope owner,
        Guid clientId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        // The client is read first, so a client outside the owner's scope is a 404 whether or not
        // it happens to have projects.
        await GetClientAsync(owner, clientId, cancellationToken).ConfigureAwait(false);

        var limit = AgencyClientPolicy.ClampPageSize(request.Limit);
        var (afterName, afterId) = DecodeCursor(owner, request.Cursor);

        var rows = await projects
            .ListByClientAsync(owner.ScopeId, clientId, afterName, afterId, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Take(limit).ToArray();
        var last = items.Length == limit ? items[^1] : null;

        return new ClientProjectPage(
            items,
            last is null ? null : ScopeCursor.Encode(owner.ScopeId, last.Name, last.Id));
    }

    public async Task<ClientReportContext> BuildReportContextAsync(
        OwnerScope owner,
        Guid clientId,
        IReadOnlyList<Guid> projectIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(projectIds);

        var client = await GetClientAsync(owner, clientId, cancellationToken).ConfigureAwait(false);

        var entries = new List<ClientReportEntry>(projectIds.Count);
        var seen = new HashSet<Guid>();

        foreach (var projectId in projectIds)
        {
            if (!seen.Add(projectId))
            {
                continue;
            }

            var project = await projects
                .FindAsync(owner.ScopeId, projectId, cancellationToken)
                .ConfigureAwait(false);

            // Only a live project grouped under exactly this client is depicted. Anything else -
            // unassigned, grouped elsewhere, archived, absent or out of scope - is absent rather
            // than an error, so the answer never names another client's projects.
            if (project is { IsArchived: false } && project.ClientId == clientId)
            {
                entries.Add(new ClientReportEntry(project.Id, project.Name, project.Revision));
            }
        }

        entries.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.ProjectName, right.ProjectName, StringComparison.Ordinal);
            return byName != 0 ? byName : left.ProjectId.CompareTo(right.ProjectId);
        });

        return new ClientReportContext(client.Id, client.Revision, client.ContextRevision, entries);
    }

    // -----------------------------------------------------------------------------------------
    // Reads inside a write. Every mutation re-reads its own target, so it can tell an absent record
    // from an archived one from a revision that moved, and answer with the right refusal.
    // -----------------------------------------------------------------------------------------

    private async Task<Client> LiveClientAsync(OwnerScope owner, Guid clientId, CancellationToken token)
    {
        var client = await clients
            .FindAsync(owner.ScopeId, clientId, token)
            .ConfigureAwait(false) ?? throw NotFound("client");

        // An archived client is readable and unchangeable. The stamp is checked here, so the
        // refusal happens before anything is written, and again in the SQL guard, so it still holds
        // if the archive lands between this read and the write.
        if (client.IsArchived)
        {
            throw new ClientArchivedException();
        }

        return client;
    }

    private async Task<Project> LiveScopedProjectAsync(OwnerScope owner, Guid projectId, CancellationToken token)
    {
        var project = await projects
            .FindAsync(owner.ScopeId, projectId, token)
            .ConfigureAwait(false) ?? throw NotFound("project");

        // An archived project is readable and unchangeable, and that rule does not bend for a
        // client assignment: regrouping history that is frozen would rewrite what the archive
        // preserves.
        if (project.IsArchived)
        {
            throw new ProjectArchivedException();
        }

        return project;
    }

    private async Task<Client> ReloadClientAsync(OwnerScope owner, Guid clientId, CancellationToken token) =>
        await clients.FindAsync(owner.ScopeId, clientId, token).ConfigureAwait(false)
        ?? throw NotFound("client");

    private async Task<Project> ReloadProjectAsync(OwnerScope owner, Guid projectId, CancellationToken token) =>
        await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
        ?? throw NotFound("project");

    /// <summary>
    /// Works out which refusal a client write that changed no row deserves, from what is stored
    /// now: gone, archived, or a revision that moved on.
    /// </summary>
    private async Task<ApiException> LostClientWriteAsync(OwnerScope owner, Guid clientId, CancellationToken token)
    {
        var latest = await clients.FindAsync(owner.ScopeId, clientId, token).ConfigureAwait(false);

        return latest switch
        {
            null => NotFound("client"),
            { IsArchived: true } => new ClientArchivedException(),
            _ => new RevisionConflictException(latest.Revision),
        };
    }

    private async Task<ApiException> LostProjectWriteAsync(OwnerScope owner, Guid projectId, CancellationToken token)
    {
        var latest = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);

        if (latest is null)
        {
            return NotFound("project");
        }

        if (latest.IsArchived)
        {
            return new ProjectArchivedException();
        }

        return new RevisionConflictException(latest.Revision);
    }

    // -----------------------------------------------------------------------------------------
    // Shared rules.
    // -----------------------------------------------------------------------------------------

    private static ClientPage Page(
        IReadOnlyList<Client> rows,
        int limit,
        Func<string, Guid, string> encode)
    {
        var items = rows.Take(limit).ToArray();
        var last = items.Length == limit ? items[^1] : null;

        return new ClientPage(items, last is null ? null : encode(last.Name, last.Id));
    }

    private static (string? Name, Guid? Id) DecodeCursor(OwnerScope owner, string? cursor)
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

    private static ResourceNotFoundException NotFound(string resource) => new(resource);
}
