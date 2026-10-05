using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The local Git evidence use cases over the delivered stores, queue and evidence append
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Committed revisions only, and that is a property of how the facts are read rather than a filter
/// applied afterwards.</b> Every fact comes from comparing two resolved commits: <c>git diff</c> between
/// two SHAs, and <c>git log</c> over a range of them. Neither command is given a working tree, an index
/// or a file path, so an untracked file, a staged change and a dirty edit are not candidates for
/// anything - there is no point in the code where they could be read and then decided against. The one
/// place the working tree is touched at all is the notice that says how many uncommitted changes
/// existed and were not collected, and that is a count, never a name.
/// </para>
/// <para>
/// <b>Every promise about how much becomes evidence is enforced here.</b> The caps in
/// <see cref="LocalGitLimits"/> are passed to the transport on the request, and the exclusions and
/// truncations the transport reports back become evidence items here. A reader of the bundle sees which
/// files were excluded and which segments stopped at a cap because those words are in the items
/// themselves.
/// </para>
/// <para>
/// <b>A repository is read, never written.</b> This class starts no mutating Git command, and neither
/// does the transport it calls. The read-only claim is enforced in three places at once: the transport's
/// argument list is a closed vocabulary of <c>rev-parse</c>, <c>log</c> and <c>diff</c>; the process
/// carries <c>GIT_OPTIONAL_LOCKS=0</c> so Git will not even take its index lock; and a scenario proves
/// the repository's own bytes and its <c>.git</c> directory are identical before and after a
/// collection.
/// </para>
/// <para>
/// <b>Nothing here is a prompt.</b> Everything a commit message or a diff line contributes is text in an
/// evidence item. This package has no call to a model and no path to one, so a commit message that reads
/// like an instruction is stored as text and acts on nothing.
/// </para>
/// </remarks>
public sealed class LocalGitEvidenceService(
    IProjectStore projects,
    IMilestoneStore milestones,
    ILocalRepositoryStore repositories,
    ILocalSyncSourceStore syncSources,
    ILocalGitSource source,
    ILocalRepositoryCatalog catalog,
    IAppendEvidence evidence,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<LocalGitEvidenceService> logger) : ILocalGitEvidenceService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to, for
    /// the same reason the GitHub package's does: a milestone, so the delivered supersede check reads the
    /// milestone's own revision and the delivered conditional commit already knows what to do with a
    /// target that moved.
    /// </summary>
    public const string SyncOperationKind = "milestone.local-git-sync";

    /// <summary>The audit action recorded when a local repository is registered or replaced.</summary>
    public const string RepositoryUpsertedAction = "localgit.repository.upserted";

    /// <summary>The audit action recorded when a collection is asked for.</summary>
    public const string SyncRequestedAction = "localgit.sync.requested";

    // ---------------------------------------------------------------------------------------------
    // Registering a local repository.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LocalRepository> RegisterAsync(
        OwnerScope owner,
        Guid projectId,
        LocalRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        // The deployment is checked first, before the alias is resolved or a path is read. In Hosted the
        // registered roots are other tenants' directories, so a refusal that still resolved the alias
        // would already have told this caller something about a machine it does not share.
        RefuseInHosted();

        var alias = request.RootAlias?.Trim() ?? string.Empty;
        var relativePath = request.RelativePath?.Trim() ?? string.Empty;

        var errors = LocalGitPolicy.ValidateConnection(alias, relativePath);
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        // The alias is resolved against the operator's table, and a name that is not in it is a name this
        // server will not turn into a directory. The response never carries the path it resolved to.
        var root = catalog.Find(alias)
            ?? throw new LocalGitRootUnknownException();

        // Containment is a filesystem question, not a string one. The path has already been checked for
        // traversal; this answers where a symbolic link inside it actually points.
        var repository = RepoRef.Local(alias, relativePath)
            ?? throw new ValidationFailedException(
                "relativePath",
                "That path is not a path inside a registered root.");

        // The resolved directory is deliberately not kept in a local that outlives this statement. It is
        // the answer to a containment check, and a variable that lived on would be one more place an
        // operator's path could reach; the transport resolves the same containment again immediately before
        // every read, because a path inside a root when it was registered can be a link out of it by the
        // time a job runs.
        ThrowFor(LocalGitPolicy.ResolveWithin(root, repository.Name).Resolution);

        var now = clock.GetUtcNow();

        var stored = await unitOfWork.ExecuteAsync(
                async token =>
                {
                    var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException("project");

                    if (project.ArchivedAt is not null)
                    {
                        throw new ProjectArchivedException();
                    }

                    // The read is a real one: a registration this package stores is a directory Git
                    // answered for. Nothing is written when Git refuses, and a directory that is not a
                    // repository is refused before a row exists.
                    var described = await source
                        .DescribeRepositoryAsync(repository, credential: null, token)
                        .ConfigureAwait(false);

                    ThrowForRoute(described);

                    var existing = await repositories
                        .FindAsync(owner.ScopeId, projectId, token)
                        .ConfigureAwait(false);

                    var registration = new LocalRepository(
                        existing?.Id ?? Guid.NewGuid(),
                        owner.ScopeId,
                        projectId,
                        repository.Owner,
                        repository.Name,
                        described.DefaultBranch,
                        described.ResolvedHeadSha,
                        existing?.CreatedAt ?? now,
                        now);

                    var saved = await repositories.UpsertAsync(registration, token).ConfigureAwait(false);

                    // The audit row names the alias and the relative path, which is everything the owner
                    // typed and nothing about the machine. `directory` is deliberately not interpolated
                    // anywhere on this path: it is the answer to a containment check, not a thing to log.
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        RepositoryUpsertedAction,
                        saved.Id,
                        now.ToUnixTimeMilliseconds(),
                        $"local repository {saved.RootAlias}/{saved.RelativePath} registered"));

                    return saved;
                },
                cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Local repository registered for project {ProjectId} as {RootAlias}/{RelativePath}.",
            projectId,
            stored.RootAlias,
            stored.RelativePath);

        return stored;
    }

    /// <inheritdoc />
    public async Task<LocalRepository?> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RefuseInHosted();

        // A project this owner may not reach is a 404 rather than an empty registration, so the read
        // cannot be used to discover another installation's project.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        return await repositories
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Asking for a collection.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LocalGitSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid milestoneId,
        LocalGitSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        RefuseInHosted();

        var errors = LocalGitPolicy.ValidateSync(
            request.HeadRef,
            request.BaseRef,
            request.ExpectedBundleRevision);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > LocalGitLimits.MaxIdempotencyKeyLength)
        {
            errors["idempotencyKey"] = [$"An idempotency key must be 1 to {LocalGitLimits.MaxIdempotencyKeyLength} characters."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        // The milestone, its project and the registration are all read before a job exists, so a request
        // that could never run is refused here rather than as a failed job an owner has to go and read.
        // The milestone's revision leaves with them, because it is what the job binds to.
        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("milestone");

                var project = await projects
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                var found = await repositories
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new LocalGitRepositoryMissingException();

                return new PreparedSync(found, milestone.Revision);
            },
            cancellationToken).ConfigureAwait(false);

        // The job binds to the milestone's revision, and the reason is the delivered GitHub package's
        // reason, which holds here: binding to the evidence head instead would be wrong in a way no
        // offset can fix, because this run's own append advances that head. The evidence head is
        // protected separately and exactly, by the delivered append, which carries the revision this run
        // must extend.
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                SyncOperationKind,
                milestoneId,
                prepared.MilestoneRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The run row is written after the job exists, because it names the job. A replay under the same
        // idempotency key gets the original job back and its own insert changes no row, so the run log
        // cannot grow a second entry for one collection.
        var now = clock.GetUtcNow();
        var run = new LocalSyncSource(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            milestoneId,
            request.ExpectedBundleRevision,
            prepared.Repository.RootAlias,
            prepared.Repository.RelativePath,
            request.HeadRef!.Trim(),
            request.BaseRef?.Trim(),
            now);

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await syncSources.TryInsertAsync(run, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        SyncRequestedAction,
                        run.Id,
                        request.ExpectedBundleRevision,
                        "local git sync requested"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Local git sync requested for milestone {MilestoneId} as operation {OperationId} ({Created}).",
            milestoneId,
            accepted.Operation.Id,
            inserted);

        return new LocalGitSyncAccepted(accepted.Operation, accepted.Created);
    }

    // ---------------------------------------------------------------------------------------------
    // Performing the collection.
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LocalGitCollected> CollectAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        RefuseInHosted();

        // The job is handed an operation id and nothing else, so the words the owner typed are read back
        // from the run row. A job whose run row is missing cannot be collected honestly and says so as a
        // permanent failure rather than guessing at refs.
        var run = await syncSources
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(LocalGitJobError.RequestMissing);

        var registered = await repositories
            .FindForMilestoneAsync(owner.ScopeId, run.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(LocalGitJobError.RepositoryMissing);

        var repository = RepoRef.Local(registered.RootAlias, registered.RelativePath)
            ?? throw new OperationPermanentException(LocalGitJobError.OutsideRoot);

        // Re-resolved here rather than trusted from the run row. The root's directory may have been
        // reconfigured, and a run that read a path nobody registered any more would be a collection from
        // an unlisted directory.
        _ = catalog.Find(repository.Owner)
            ?? throw new OperationPermanentException(LocalGitJobError.RootUnknown);

        GitEvidenceResult collected;
        try
        {
            collected = await source
                .FetchAsync(
                    repository,
                    new GitEvidenceRequest(
                        run.BaseRef ?? string.Empty,
                        run.HeadRef,
                        LocalGitLimits.MaxCommits,
                        LocalGitLimits.MaxChangedFiles,
                        LocalGitLimits.MaxDiffBytes),
                    credential: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LocalGitOutsideRootException)
        {
            // The containment check failed at the moment of the read, which is the window that matters: a
            // path inside the root when it was registered can be a symlink out of it by the time a job
            // runs. Refused as permanent, because the row would be refused again the same way.
            throw new OperationPermanentException(LocalGitJobError.OutsideRoot);
        }
        catch (LocalGitNotARepositoryException)
        {
            throw new OperationPermanentException(LocalGitJobError.NotARepository);
        }
        catch (LocalGitTimeoutException)
        {
            throw new OperationTransientException(LocalGitJobError.TimedOut);
        }
        catch (LocalGitUnavailableException)
        {
            throw new OperationTransientException(LocalGitJobError.GitUnavailable);
        }

        // A head that moved under the read is checked first and on its own, because it is the one refusal
        // that is permanent for a reason no attempt changes, and because it must not fall through the generic
        // classification below - a mixed-revision bundle is not a dependency that may come back.
        if (collected.Outcome is GitEvidenceOutcome.Superseded)
        {
            logger.LogInformation(
                "Local git sync {OperationId} was superseded: the repository head moved during the read.",
                operationId);

            throw new OperationPermanentException(LocalGitJobError.Superseded);
        }

        ThrowFor(collected);

        if (collected.Outcome is not GitEvidenceOutcome.Collected)
        {
            throw new OperationTransientException(LocalGitJobError.GitUnavailable);
        }

        var headSha = collected.ResolvedHeadSha
            ?? throw new OperationPermanentException(LocalGitJobError.RefNotFound);

        var items = Items(repository, run, collected, headSha);
        var expected = run.ExpectedBundleRevision;

        EvidenceBundle bundle;
        try
        {
            bundle = await evidence
                .AppendAsync(owner, run.MilestoneId, expected, items, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RevisionConflictException)
        {
            // Somebody else's append reached this milestone first. Nothing was written, the stored bundle is
            // exactly as it was, and this run has no result of its own to attach. The job ends as a permanent
            // failure named for the situation, which is the honest terminal state for "the input this run
            // was built on no longer exists".
            logger.LogInformation(
                "Local git sync {OperationId} was superseded: the milestone's evidence moved to a revision this run did not extend.",
                operationId);

            throw new OperationPermanentException(LocalGitJobError.BundleSuperseded);
        }

        // The run row records what was read, in the same unit of work as the audit row. It is completed
        // once: a second attempt on the same operation cannot overwrite the first run's record.
        run.Complete(
            collected.ResolvedBaseSha,
            headSha,
            // The subcommand is the endpoint's `Method` and the step within it is the `Path`, which is the
            // same split the delivered GitHub run log uses for its API paths. Only the subcommand is
            // persisted: a step name is this package's own vocabulary and an operand is the operator's
            // filesystem, and neither belongs in a row.
            LocalGitPolicy.CommandList(collected.Endpoints.Select(endpoint => endpoint.Method)),
            collected.FetchedAt,
            collected.Commits.Count,
            collected.Compare?.Files.Count ?? 0,
            collected.Truncations.Count > 0,
            collected.Exclusions.Count,
            collected.UncommittedChanges ?? 0);

        var completed = await unitOfWork.ExecuteAsync(
            async token => await syncSources.TryCompleteAsync(run, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Local git sync {OperationId} appended {ItemCount} items to milestone {MilestoneId} at revision {Revision} (completed={Completed}).",
            operationId,
            items.Count,
            run.MilestoneId,
            bundle.Revision,
            completed);

        return new LocalGitCollected(
            bundle.Revision,
            items.Count,
            collected.Commits.Count,
            collected.Compare?.Files.Count ?? 0,
            collected.Truncations.Count > 0,
            collected.Exclusions.Count,
            collected.UncommittedChanges ?? 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Turning a collection into evidence items.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The items one collection becomes. Every item says what it is about and whether it is whole: a
    /// file with no patch is an <c>unavailable</c> item carrying the reason, and a segment that stopped
    /// at a cap is a <c>partial</c> item that names the cap. Nothing is silently dropped and nothing is
    /// invented to fill a gap.
    /// </summary>
    private static IReadOnlyList<EvidenceAppendItem> Items(
        RepoRef repository,
        LocalSyncSource run,
        GitEvidenceResult collected,
        string headSha)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(collected);

        var items = new List<EvidenceAppendItem>();
        var sourceRef = repository.ToSourceRef();
        var capturedAt = collected.FetchedAt;
        var head = Short(headSha) ?? "(unresolved)";

        // The repository's own identity, the two revisions the collection resolved to, the Git commands it
        // ran and what the working tree held. This is the item that makes the rest of them checkable, and
        // it carries no absolute path: the root's directory is the operator's and is not in the bundle.
        items.Add(Item(
            EvidenceKinds.ProjectMetadata,
            sourceRef,
            head,
            capturedAt,
            [
                $"Repository: {repository.Owner}/{repository.Name}",
                $"Registered root: {repository.Owner}",
                $"Relative path: {repository.Name}",
                $"Default branch: {collected.DefaultBranch ?? "not reported"}",
                $"Base revision: {(string.IsNullOrWhiteSpace(collected.ResolvedBaseSha) ? "not compared" : collected.ResolvedBaseSha)}",
                $"Head revision: {head}",
                $"Fetched at: {capturedAt.UtcDateTime:O}",
                $"Uncommitted changes in the working tree: {DescribeUncommitted(collected.UncommittedChanges)}",
                "Git commands consulted:",
                .. collected.Endpoints.Select(endpoint => $"  {endpoint.Method} {endpoint.Path} -> {endpoint.Status}"),
            ],
            EvidenceAvailability.Complete,
            null));

        foreach (var commit in collected.Commits)
        {
            items.Add(Item(
                EvidenceKinds.Commit,
                sourceRef,
                Short(commit.Sha) ?? head,
                commit.AuthoredAt ?? capturedAt,
                [
                    $"Commit: {commit.Sha}",
                    $"Summary: {commit.Summary}",
                    $"Author: {commit.Author ?? "not reported"}",
                ],
                EvidenceAvailability.Complete,
                null));
        }

        if (collected.Compare is { } compare)
        {
            foreach (var file in compare.Files)
            {
                items.Add(FileItem(sourceRef, head, capturedAt, file));
            }
        }

        // Every segment that stopped at a cap, named as a partial item that carries the cap in a redaction
        // label. A partial item with no exclusion would be indistinguishable from a whole one, and the
        // delivered evidence policy refuses that.
        foreach (var truncation in collected.Truncations)
        {
            items.Add(Item(
                KindFor(truncation.Segment),
                sourceRef,
                head,
                capturedAt,
                [
                    $"{truncation.Segment} stopped at the limit.",
                    $"Kept: {truncation.Kept}.",
                    $"Limit: {truncation.Cap}.",
                    $"Requested: {run.HeadRef}.",
                ],
                EvidenceAvailability.Partial,
                [$"{truncation.Segment} truncated at {truncation.Cap}"]));
        }

        // Every part that was asked for and could not be read, named as unavailable with its reason.
        foreach (var missing in collected.Unavailable)
        {
            items.Add(Item(
                KindFor(missing.Segment),
                sourceRef,
                head,
                capturedAt,
                [
                    $"{missing.Segment} was requested and could not be read.",
                    $"Reason: {missing.Reason}.",
                ],
                EvidenceAvailability.Unavailable,
                null));
        }

        return items;
    }

    /// <summary>
    /// The working-tree notice, in words that cannot be misread.
    /// </summary>
    /// <remarks>
    /// A count and never a list of names, because one of those names could be a file the owner did not
    /// mean to publish - and because "there were changes" is the honest claim: this package read committed
    /// revisions only, so the directory's current state is not represented in this bundle at all.
    /// </remarks>
    private static string DescribeUncommitted(int? count) => count switch
    {
        null => "not reported by the repository reader",
        0 => "none; this bundle describes committed revisions, not the directory's current state",
        _ => $"{count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, deliberately not read: "
            + "this bundle describes committed revisions only, and a working-tree change is not a commit",
    };

    private static EvidenceAppendItem FileItem(
        string sourceRef,
        string head,
        DateTimeOffset capturedAt,
        GitFile file)
    {
        var summary = $"File: {file.Path}";
        var counts = $"Status: {file.Status}. Additions: {file.Additions}. Deletions: {file.Deletions}.";

        if (file.Patch is not null)
        {
            return Item(
                EvidenceKinds.Diff,
                sourceRef,
                head,
                capturedAt,
                [summary, counts, file.Patch],
                EvidenceAvailability.Complete,
                null);
        }

        // A file with no patch is unavailable evidence that says why. A binary file, a file whose name says
        // it holds a secret and a file past the diff budget are three different reasons and none of them
        // is "nothing changed".
        var reason = file.ExclusionReason switch
        {
            null => $"Git reported no patch for this file. Reason: {GitUnavailableReasons.PatchAbsent}.",
            SecretFilenames.Reason =>
                $"This file was not read because its name marks it as a secret or a private key. "
                + $"Reason: {SecretFilenames.Reason}.",
            _ when file.ExclusionReason == GitSegments.DiffText =>
                "This file's patch was not recorded because the diff text limit had been reached. "
                + $"Reason: {GitUnavailableReasons.DiffTextLimit}.",
            _ => $"This file was not read. Reason: {file.ExclusionReason}.",
        };

        return Item(
            EvidenceKinds.Diff,
            sourceRef,
            head,
            capturedAt,
            [summary, counts, reason],
            EvidenceAvailability.Unavailable,
            null);
    }

    private static string KindFor(string segment) => segment switch
    {
        GitSegments.Commits => EvidenceKinds.Commit,
        GitSegments.Files or GitSegments.DiffText => EvidenceKinds.Diff,
        _ => EvidenceKinds.ProjectMetadata,
    };

    /// <summary>
    /// Builds one item, hashing the payload with the delivered policy so the same collected fact is the
    /// same item however it was assembled.
    /// </summary>
    private static EvidenceAppendItem Item(
        string kind,
        string sourceRef,
        string sourceRevision,
        DateTimeOffset capturedAt,
        IReadOnlyList<string> lines,
        string availability,
        IReadOnlyList<string>? redactions)
    {
        var text = string.Join('\n', lines);

        if (text.Length > EvidenceLimits.ItemTextMaxLength)
        {
            // A single item that is too long is cut at the delivered bound and says so, rather than being
            // refused: the fact is real and the cut is declared in the item's own last line.
            text = string.Concat(
                text.AsSpan(0, EvidenceLimits.ItemTextMaxLength - DeclaredCut.Length),
                Environment.NewLine,
                DeclaredCut);
        }

        var item = new EvidenceAppendItem(
            kind,
            sourceRef,
            sourceRevision,
            capturedAt,
            string.Empty,
            text,
            mediaId: null,
            redactions,
            availability);

        return new EvidenceAppendItem(
            kind,
            sourceRef,
            sourceRevision,
            capturedAt,
            EvidencePolicy.ItemPayloadSha256(item),
            text,
            mediaId: null,
            redactions,
            availability);
    }

    private const string DeclaredCut = "[truncated at the evidence item limit]";

    private static string Short(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? string.Empty : sha.Trim();

    // ---------------------------------------------------------------------------------------------
    // Refusals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Refuses local collection in a deployment where the registered roots are not this caller's. Read
    /// before the alias is resolved, so a refusal teaches nothing about the machine.
    /// </summary>
    private void RefuseInHosted()
    {
        if (catalog.DeploymentMode is LocalGitDeploymentMode.Hosted)
        {
            throw new LocalGitNotAvailableInHostedException();
        }
    }

    /// <summary>
    /// Turns the three answers a containment check can give into the two refusals a route can send.
    /// </summary>
    /// <remarks>
    /// <b>An absent path and an escaping path are not the same answer, and the difference is the whole
    /// value of asking the filesystem separately from checking the string.</b> A path that is not there is
    /// a fact about a directory, and a caller told it had tried to escape a root would go looking for a
    /// traversal that never happened. A path that escapes is refused as such, with a code of its own.
    /// </remarks>
    private static void ThrowFor(LocalPathResolution resolution)
    {
        switch (resolution)
        {
            case LocalPathResolution.Resolved:
                return;

            case LocalPathResolution.Outside:
                throw new LocalGitOutsideRootException();

            default:
                throw new LocalGitNotARepositoryException();
        }
    }

    /// <summary>
    /// Turns a transport refusal into the delivered classified job failure. The classification is made
    /// once, here, because only this method knows the difference between "will never work" and "worth
    /// another attempt".
    /// </summary>
    private static void ThrowFor(GitEvidenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case GitEvidenceOutcome.Collected:
                return;

            // Handled by the caller before it gets here, and repeated here so that a future edit which
            // reorders those two lines turns a moved head into a named permanent failure rather than into the
            // generic "unavailable" below - which would be retried and would then succeed by collecting a
            // revision the owner never asked for.
            case GitEvidenceOutcome.Superseded:
                throw new OperationPermanentException(LocalGitJobError.Superseded);

            // A path that escapes its root, or is not a repository, will not become either between two
            // attempts. Both are permanent and both are named, because "the container is empty" and "the
            // alias is not configured" are different problems with different repairs.
            case GitEvidenceOutcome.OutsideRoot:
                throw new OperationPermanentException(LocalGitJobError.OutsideRoot);

            case GitEvidenceOutcome.NotARepository:
                throw new OperationPermanentException(LocalGitJobError.NotARepository);

            case GitEvidenceOutcome.RootUnknown:
                throw new OperationPermanentException(LocalGitJobError.RootUnknown);

            // A ref that does not resolve will not resolve on a second attempt.
            case GitEvidenceOutcome.Rejected:
                throw new OperationPermanentException(LocalGitJobError.RefNotFound);

            // Git is missing, or a process outlived its bound. Both are things an operator can change, and
            // both are worth another attempt within the delivered budget.
            case GitEvidenceOutcome.TimedOut:
                throw new OperationTransientException(LocalGitJobError.TimedOut);

            default:
                throw new OperationTransientException(LocalGitJobError.GitUnavailable);
        }
    }

    /// <summary>
    /// Turns a transport refusal on the registration route into the route's own answer.
    /// </summary>
    /// <remarks>
    /// Deliberately a different mapping from <see cref="ThrowFor"/>, which is for a job, for the delivered
    /// GitHub package's reason: a request that cannot answer its caller has to produce a status and an
    /// envelope, and a job has to produce a classified failure on its row.
    /// </remarks>
    private static void ThrowForRoute(GitEvidenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case GitEvidenceOutcome.Collected:
                return;

            case GitEvidenceOutcome.OutsideRoot:
                throw new LocalGitOutsideRootException();

            case GitEvidenceOutcome.NotARepository:
                throw new LocalGitNotARepositoryException();

            case GitEvidenceOutcome.RootUnknown:
                throw new LocalGitRootUnknownException();

            case GitEvidenceOutcome.Rejected:
                throw new LocalGitNotARepositoryException();

            case GitEvidenceOutcome.TimedOut:
                throw new LocalGitTimeoutException();

            default:
                throw new LocalGitUnavailableException();
        }
    }

    /// <summary>What a request has to establish before a job for it may exist.</summary>
    private sealed record PreparedSync(LocalRepository Repository, long MilestoneRevision);
}
