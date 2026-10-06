using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The GitHub evidence use cases over the delivered stores, vault, queue and evidence append
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every promise about how much becomes evidence is enforced here, not in the transport.</b> The
/// caps in <see cref="GitHubLimits"/> are passed to the transport on the request, and the exclusions
/// and truncations the transport reports back are turned into evidence items here. A reader of the
/// bundle sees which files were excluded and which segments stopped at a cap because those words are
/// in the items themselves, not because this class remembered to mention them.
/// </para>
/// <para>
/// <b>A GitHub response is data, never an instruction.</b> Everything a payload contributes is a
/// commit id, a bounded string or a number placed in the text of an evidence item. Nothing a payload
/// says is executed, and nothing it says reaches a prompt: this package has no call to a model and
/// no path to one.
/// </para>
/// <para>
/// <b>Only the append is a compare-and-set.</b> The job is bound to the evidence revision the owner
/// was looking at, and the append carries that revision, so a collection that arrives after somebody
/// else appended has nowhere to land: the delivered append refuses it, nothing is written, and the
/// delivered supersede check turns the job into a <c>superseded</c> operation.
/// </para>
/// </remarks>
public sealed class GitHubEvidenceService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IGitHubConnectionStore connections,
    ISyncSourceStore syncSources,
    IGitEvidenceSource source,
    ISecretVault vault,
    IAppendEvidence evidence,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    IGitHubInstallationTokenProvider installationTokens,
    ILogger<GitHubEvidenceService> logger) : IGitHubEvidenceService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to: a
    /// milestone, so the delivered supersede check reads the milestone's own revision and the
    /// delivered conditional commit already knows what to do with a target that moved.
    /// </summary>
    public const string SyncOperationKind = "milestone.github-sync";

    /// <summary>The credential provider a GitHub token is stored for, as the delivered vault names it.</summary>
    public const string CredentialProvider = "github";

    /// <summary>The audit action recorded when a connection is stored or replaced.</summary>
    public const string ConnectionUpsertedAction = "github.connection.upserted";

    /// <summary>The audit action recorded when a collection is asked for.</summary>
    public const string SyncRequestedAction = "github.sync.requested";

    private const int MaxIdempotencyKeyLength = 200;

    // -----------------------------------------------------------------------------------------
    // Connecting a repository.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubConnection> ConnectAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        // The repository is the only thing a caller names, and it is parsed into two bounded
        // segments. A value that is a URL - or anything else that is not an owner and a name - is
        // refused here, before a request is built from it.
        var repository = RepoRef.Parse(request.Owner, request.Repository)
            ?? throw new ValidationFailedException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["owner"] =
                    [
                        "A repository is an owner and a name, both as GitHub spells them. A URL is not accepted.",
                    ],
                    ["repository"] =
                    [
                        "A repository is an owner and a name, both as GitHub spells them. A URL is not accepted.",
                    ],
                });

        // The credential is leased and confirmed before it is stored, so a connection can only ever
        // hold a reference to a credential of this scope and this provider. The lease is returned
        // before this method returns and its bytes are zeroed by the lease.
        var lease = request.CredentialRef is { } credentialRef
            ? await LeaseAsync(owner, credentialRef, cancellationToken).ConfigureAwait(false)
            : null;

        var now = clock.GetUtcNow();
        GitHubConnection connection;

        try
        {
            connection = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                // The read is a real one: a connection this package stores is a repository GitHub
                // answered for. Nothing is written when GitHub refuses, and the refusal is the same
                // answer for a private repository and for one that does not exist.
                // The validation read is made with the credential the owner attached, because what it
                // establishes is that this repository is readable by this credential. The bytes are
                // handed straight to the transport and are in no other scope here.
                var described = await source
                    .DescribeRepositoryAsync(
                        repository,
                        lease is null ? null : Encoding.UTF8.GetString(lease.Secret),
                        token)
                    .ConfigureAwait(false);

                ThrowForRoute(described);

                var existing = await connections
                    .FindAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false);

                var stored = new GitHubConnection(
                    existing?.Id ?? Guid.NewGuid(),
                    owner.ScopeId,
                    projectId,
                    repository.Owner,
                    repository.Name,
                    request.CredentialRef,
                    described.DefaultBranch,
                    described.RepositoryId,
                    existing?.CreatedAt ?? now,
                    now);

                var saved = await connections.UpsertAsync(stored, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ConnectionUpsertedAction,
                    saved.Id,
                    now.ToUnixTimeMilliseconds(),
                    "github connection stored"));

                return saved;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }

        logger.LogInformation(
            "GitHub connection stored for project {ProjectId} as {Owner}/{Repository}.",
            projectId,
            connection.Owner,
            connection.Repo);

        return connection;
    }

    /// <inheritdoc />
    public async Task<GitHubConnection?> ReadConnectionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A project this owner may not reach is a 404 rather than an empty connection, so the read
        // cannot be used to discover another installation's project.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        return await connections.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------
    // Asking for a collection.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid milestoneId,
        GitHubSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var errors = GitHubSyncPolicy.Validate(
            request.HeadRef,
            request.BaseRef,
            request.ReleaseTag,
            request.ExpectedBundleRevision);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            errors["idempotencyKey"] = [$"An idempotency key must be 1 to {MaxIdempotencyKeyLength} characters."];
        }

        if (request.IssueNumber is { } issue && issue < 1)
        {
            errors["issueNumber"] = ["An issue number is one or greater."];
        }

        if (request.PullRequestNumber is { } pull && pull < 1)
        {
            errors["pullRequestNumber"] = ["A pull request number is one or greater."];
        }

        Throw(errors);

        // The milestone, its project and the connection are all read before a job exists, so a
        // request that could never run is refused here rather than as a failed job an owner has to
        // go and read. The milestone's revision leaves with them, because it is what the job binds to.
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

                var found = await connections
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new GitHubConnectionMissingException();

                return new PreparedSync(found, milestone.Revision);
            },
            cancellationToken).ConfigureAwait(false);

        // The job binds to the milestone's revision. That is what the delivered supersede check reads,
        // and binding to the evidence head instead would be wrong in a way no offset can fix: this
        // run's own append advances that head, so the head after a successful collection is the head
        // after a foreign one, and a check on it could not tell the two apart. The milestone's
        // revision is a signal this package's own work never moves, and the delivered conditional
        // commit turns a milestone edited mid-run into a `superseded` operation.
        //
        // The evidence head is protected separately and exactly, by the delivered append: it carries
        // the revision this run must extend, so a bundle that moved has nowhere to land.
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                SyncOperationKind,
                milestoneId,
                prepared.MilestoneRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The run row is written after the job exists, because it names the job. A replay under the
        // same idempotency key gets the original job back and its own insert changes no row, so the
        // run log cannot grow a second entry for one collection.
        var now = clock.GetUtcNow();
        var run = new SyncSource(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            milestoneId,
            request.ExpectedBundleRevision,
            prepared.Connection.Owner,
            prepared.Connection.Repo,
            request.HeadRef!.Trim(),
            request.BaseRef?.Trim(),
            request.ReleaseTag?.Trim(),
            request.IssueNumber,
            request.PullRequestNumber,
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
                        "github sync requested"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub sync requested for milestone {MilestoneId} as operation {OperationId} ({Created}).",
            milestoneId,
            accepted.Operation.Id,
            inserted);

        return new GitHubSyncAccepted(accepted.Operation, accepted.Created);
    }

    // -----------------------------------------------------------------------------------------
    // Performing the collection.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<GitHubCollected> CollectAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The job is handed an operation id and nothing else, so the words the owner typed are read
        // back from the run row. A job whose run row is missing cannot be collected honestly, and
        // says so as a permanent failure rather than guessing at refs.
        var run = await syncSources
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubJobError.RequestMissing);

        var connection = await connections
            .FindForMilestoneAsync(owner.ScopeId, run.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubJobError.ConnectionMissing);

        var repository = RepoRef.Parse(connection.Owner, connection.Repo)
            ?? throw new OperationPermanentException(GitHubJobError.RefNotFound);

        // The credential is leased for the duration of the one call that uses it and returned by the
        // finally below, whatever happens. The leased bytes become a string for the transport and
        // nothing else: no result, no log line and no exception ever carries it. An installation
        // connection mints its token through the App provider instead: the token is held in memory
        // until just before it expires, and a revoked installation fails here - before any fact is
        // read - rather than as invented evidence.
        string? installationBearer = null;
        var lease = connection.CredentialRef is { } reference && !IsInstallation(connection)
            ? await LeaseAsync(owner, reference, cancellationToken).ConfigureAwait(false)
            : null;

        if (IsInstallation(connection))
        {
            installationBearer = await InstallationBearerAsync(owner, connection, cancellationToken).ConfigureAwait(false);
        }

        GitEvidenceResult collected;
        try
        {
            collected = await source
                .FetchAsync(
                    repository,
                    new GitEvidenceRequest(
                        run.BaseRef ?? string.Empty,
                        run.HeadRef,
                        GitHubLimits.MaxCommits,
                        GitHubLimits.MaxChangedFiles,
                        GitHubLimits.MaxDiffBytes,
                        run.IssueNumber,
                        run.PullRequestNumber,
                        run.ReleaseTag),
                    installationBearer ?? (lease is null ? null : Encoding.UTF8.GetString(lease.Secret)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }

        ThrowFor(collected);

        if (collected.Outcome is not GitEvidenceOutcome.Collected)
        {
            throw new OperationTransientException(GitHubJobError.Unreachable);
        }

        var headSha = collected.ResolvedHeadSha
            ?? throw new OperationPermanentException(GitHubJobError.RefNotFound);

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
            // Somebody else's append reached this milestone first. Nothing was written, the stored
            // bundle is exactly as it was, and this run has no result of its own to attach. The job
            // ends as a permanent failure named `github_job_superseded`, which is the honest
            // terminal state for "the input this run was built on no longer exists" and is
            // deliberately not dressed up as a success.
            logger.LogInformation(
                "GitHub sync {OperationId} was superseded: the milestone's evidence moved to a revision this run did not extend.",
                operationId);

            throw new OperationPermanentException(GitHubJobError.Superseded);
        }

        // The run row records what was read, in the same unit of work as the audit row. It is
        // completed once: a second attempt on the same operation cannot overwrite the first run's
        // record of what it read.
        run.Complete(
            collected.ResolvedBaseSha,
            headSha,
            GitHubSyncPolicy.EndpointList(collected.Endpoints),
            collected.FetchedAt,
            collected.Commits.Count,
            collected.Compare?.Files.Count ?? 0,
            collected.Truncations.Count > 0,
            collected.Exclusions.Count);

        var completed = await unitOfWork.ExecuteAsync(
            async token => await syncSources.TryCompleteAsync(run, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub sync {OperationId} appended {ItemCount} items to milestone {MilestoneId} at revision {Revision} (completed={Completed}).",
            operationId,
            items.Count,
            run.MilestoneId,
            bundle.Revision,
            completed);

        return new GitHubCollected(
            bundle.Revision,
            items.Count,
            collected.Commits.Count,
            collected.Compare?.Files.Count ?? 0,
            collected.Truncations.Count > 0,
            collected.Exclusions.Count);
    }

    // -----------------------------------------------------------------------------------------
    // Turning a collection into evidence items.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The items one collection becomes. Every item says what it is about and whether it is whole:
    /// a file with no patch is an <c>unavailable</c> item carrying the reason, and a segment that
    /// stopped at a cap is a <c>partial</c> item that names the cap. Nothing is silently dropped and
    /// nothing is invented to fill a gap.
    /// </summary>
    private static IReadOnlyList<EvidenceAppendItem> Items(
        RepoRef repository,
        SyncSource run,
        GitEvidenceResult collected,
        string headSha)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(collected);

        var items = new List<EvidenceAppendItem>();
        var sourceRef = repository.ToSourceRef();
        var capturedAt = collected.FetchedAt;
        var head = Short(headSha) ?? "(unresolved)";

        // The repository's own identity, the two revisions the collection resolved to and the
        // endpoints it consulted. This is the item that makes the rest of them checkable.
        items.Add(Item(
            EvidenceKinds.ProjectMetadata,
            sourceRef,
            head,
            capturedAt,
            [
                $"Repository: {repository}",
                $"Repository id: {collected.RepositoryId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not reported"}",
                $"Default branch: {collected.DefaultBranch ?? "not reported"}",
                $"Base revision: {(string.IsNullOrWhiteSpace(collected.ResolvedBaseSha) ? "not compared" : collected.ResolvedBaseSha)}",
                $"Head revision: {head}",
                $"Fetched at: {capturedAt.UtcDateTime:O}",
                "Endpoints consulted:",
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

        foreach (var issue in collected.Issues)
        {
            items.Add(Item(
                EvidenceKinds.Issue,
                sourceRef,
                head,
                capturedAt,
                [
                    $"Issue #{issue.Number}: {issue.Title}",
                    $"State: {issue.State}",
                    $"Reference: {issue.Reference ?? "not reported"}",
                ],
                EvidenceAvailability.Complete,
                null));
        }

        foreach (var pull in collected.PullRequests)
        {
            items.Add(Item(
                EvidenceKinds.Pr,
                sourceRef,
                head,
                capturedAt,
                [
                    $"Pull request #{pull.Number}: {pull.Title}",
                    $"State: {pull.State}",
                    $"Merged: {(pull.Merged ? "yes" : "no")}",
                    $"Reference: {pull.Reference ?? "not reported"}",
                ],
                EvidenceAvailability.Complete,
                null));
        }

        if (collected.Release is { } release)
        {
            items.Add(Item(
                EvidenceKinds.Release,
                sourceRef,
                Short(release.Tag) ?? head,
                release.PublishedAt ?? capturedAt,
                [
                    $"Tag: {release.Tag}",
                    $"Name: {release.Name}",
                    $"Draft: {(release.Draft ? "yes" : "no")}",
                    $"Pre-release: {(release.PreRelease ? "yes" : "no")}",
                ],
                EvidenceAvailability.Complete,
                null));
        }

        // Every segment that stopped at a cap, named as a partial item that carries the cap in a
        // redaction label. A partial item with no exclusion would be indistinguishable from a whole
        // one, and the delivered evidence policy refuses that.
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

        // A file with no patch is unavailable evidence that says why. A binary file, a file past
        // GitHub's own patch limit and a file whose name says it holds a secret are three different
        // reasons and none of them is "nothing changed".
        var reason = file.ExclusionReason switch
        {
            null => $"GitHub returned no patch for this file. Reason: {GitUnavailableReasons.PatchAbsent}.",
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
        _ when segment.StartsWith("issue:", StringComparison.Ordinal) => EvidenceKinds.Issue,
        _ when segment.StartsWith("pr:", StringComparison.Ordinal) => EvidenceKinds.Pr,
        _ when segment.StartsWith("release:", StringComparison.Ordinal) => EvidenceKinds.Release,
        _ => EvidenceKinds.ProjectMetadata,
    };

    /// <summary>
    /// Builds one item, hashing the payload with the delivered policy so the same collected fact is
    /// the same item however it was assembled.
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
            // A single item that is too long is cut at the delivered bound and says so, rather than
            // being refused: the fact is real and the cut is declared in the item's own last line.
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

    // -----------------------------------------------------------------------------------------
    // Refusals.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Turns a transport refusal into the delivered classified job failure. The classification is
    /// made once, here, because only this method knows the difference between "will never work" and
    /// "worth another attempt", and guessing wrong either retries a refusal forever or abandons a
    /// throttling response.
    /// </summary>
    private static void ThrowFor(GitEvidenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case GitEvidenceOutcome.Collected:
                return;

            // A repository that is private and one that does not exist are the same answer from
            // GitHub, so they are the same answer here: permanent, and named as one thing.
            case GitEvidenceOutcome.RepositoryUnavailable:
                throw new OperationPermanentException(GitHubJobError.RepositoryUnavailable);

            // A refused credential will be refused again until the owner changes it.
            case GitEvidenceOutcome.CredentialRejected:
                throw new OperationPermanentException(GitHubJobError.CredentialRejected);

            // GitHub named its own delay. The worker clamps it with the delivered policy, so a
            // response body can never park this job for longer than the bound allows.
            case GitEvidenceOutcome.RateLimited:
                throw new OperationRateLimitedException(result.RetryAfter);

            // A ref that does not resolve will not resolve on a second attempt.
            case GitEvidenceOutcome.Rejected:
                throw new OperationPermanentException(GitHubJobError.RefNotFound);

            default:
                throw new OperationTransientException(GitHubJobError.Unreachable);
        }
    }

    /// <summary>
    /// Turns a transport refusal on the connection route into the route's own answer.
    /// </summary>
    /// <remarks>
    /// Deliberately a different mapping from <see cref="ThrowFor"/>, which is for a job. A request that
    /// cannot answer its caller has to produce a status and an envelope; a job has to produce a
    /// classified failure on its row. Reusing one mapping for both is how a 500 reaches a browser from
    /// a job failure class that means nothing to HTTP.
    /// </remarks>
    private static void ThrowForRoute(GitEvidenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case GitEvidenceOutcome.Collected:
                return;

            // One answer for a repository that is private and one that does not exist, because GitHub
            // gives one answer and this package must not add a second.
            case GitEvidenceOutcome.RepositoryUnavailable:
                throw new GitHubRepositoryUnavailableException();

            case GitEvidenceOutcome.CredentialRejected:
                throw new GitHubCredentialRejectedException();

            // A throttled or unreachable provider is answerable later, so it is a dependency answer
            // rather than a refusal of what the owner asked for.
            case GitEvidenceOutcome.RateLimited:
            case GitEvidenceOutcome.Rejected:
            case GitEvidenceOutcome.Unavailable:
                throw new GitHubUnavailableException();

            default:
                throw new GitHubUnavailableException();
        }
    }

    /// <summary>
    /// Whether a connection authenticates through an App installation rather than a vault token.
    /// </summary>
    private static bool IsInstallation(GitHubConnection connection) =>
        string.Equals(connection.AuthMode, GitHubAuthModes.Installation, StringComparison.Ordinal);

    /// <summary>
    /// Mints the installation token one collection reads with, classified into the delivered job
    /// failures. A revoked installation is permanent and publishes nothing; a GitHub that cannot
    /// mint right now is transient, so the delivered backoff applies.
    /// </summary>
    private async Task<string> InstallationBearerAsync(
        OwnerScope owner,
        GitHubConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await installationTokens
                .GetAsync(owner.ScopeId, connection.InstallationId ?? 0, connection.RepositoryId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubAppNotConfiguredException)
        {
            throw new OperationPermanentException(GitHubJobError.CredentialRejected);
        }
        catch (GitHubAppInstallationMismatchException)
        {
            throw new OperationPermanentException(GitHubJobError.ConnectionMissing);
        }
        catch (GitHubAppInstallationUnknownException)
        {
            throw new OperationPermanentException(GitHubJobError.ConnectionMissing);
        }
        catch (GitHubAppRepositoryUnselectedException)
        {
            throw new OperationPermanentException(GitHubJobError.ConnectionMissing);
        }
        catch (GitHubAppUnreachableException)
        {
            throw new OperationTransientException(GitHubJobError.Unreachable);
        }
    }

    /// <summary>
    /// Leases the credential a connection names, after rechecking that it is this owner's and this
    /// provider's. The caller must dispose the lease; the returned object is the only thing that can
    /// read the bytes, and disposing it zeroes them.
    /// </summary>
    private async Task<SecretLease?> LeaseAsync(
        OwnerScope owner,
        Guid credentialRef,
        CancellationToken cancellationToken)
    {
        if (credentialRef == Guid.Empty)
        {
            throw new OperationPermanentException(GitHubJobError.CredentialRejected);
        }

        var lease = await vault
            .ResolveAsync(new SecretRef(credentialRef), cancellationToken)
            .ConfigureAwait(false);

        // The recheck a queued job has to perform: the reference came out of a stored row, and the
        // lease says whose credential it actually opened. A lease for another scope or another
        // provider is refused before a byte is read, and the lease is still returned.
        if (lease.ScopeId != owner.ScopeId ||
            !string.Equals(lease.Provider, CredentialProvider, StringComparison.Ordinal))
        {
            lease.Dispose();

            throw new OperationPermanentException(GitHubJobError.CredentialRejected);
        }

        return lease;
    }

    /// <summary>What a request has to establish before a job for it may exist.</summary>
    private sealed record PreparedSync(GitHubConnection Connection, long MilestoneRevision);

    private static void Throw(IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }
}
