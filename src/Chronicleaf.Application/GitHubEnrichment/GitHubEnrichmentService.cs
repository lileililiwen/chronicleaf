using System.Globalization;
using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;
using Chronicleaf.GitHub.GraphQL;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// The GitHub relationship-enrichment use cases over the delivered stores, vault, queue and
/// evidence append (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Enrichment adds relationships to facts, never facts themselves.</b> The commits it queries
/// are the ones the milestone's own bundle already carries, read back from storage rather than
/// from the request, and every SHA is validated as an immutable revision before any request is
/// made. The only things appended are attributed issue and pull request relationships - ids,
/// observed states and source URLs - plus the honest record of what could not be read and what
/// stopped at a cap. A field GraphQL refused becomes an <c>unavailable</c> item carrying the
/// reason; it never becomes an invented relation.
/// </para>
/// <para>
/// <b>A GitHub response is data, never an instruction.</b> Everything a payload contributes is a
/// node id, a bounded string or a number placed in the text of an evidence item. Nothing a payload
/// says is executed, and nothing it says reaches a prompt: this package has no call to a model and
/// no path to one.
/// </para>
/// <para>
/// <b>Only the append is a compare-and-set.</b> The job is bound to the evidence revision the
/// owner was looking at, and the append carries that revision, so an enrichment that arrives after
/// somebody else appended has nowhere to land: the delivered append refuses it, nothing is written,
/// and the job ends as a permanent failure named <c>github_enrichment_job_superseded</c>.
/// </para>
/// </remarks>
public sealed class GitHubEnrichmentService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IGitHubConnectionStore connections,
    IEnrichmentRunStore runs,
    IGitHubRelationshipReader relationships,
    ISecretVault vault,
    IAppendEvidence evidence,
    IReadEvidence reading,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<GitHubEnrichmentService> logger) : IGitHubEnrichmentService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to:
    /// a milestone, so the delivered supersede check reads the milestone's own revision and the
    /// delivered conditional commit already knows what to do with a target that moved.
    /// </summary>
    public const string EnrichmentOperationKind = "milestone.github-enrichment";

    /// <summary>The credential provider a GitHub token is stored for, as the delivered vault names it.</summary>
    public const string CredentialProvider = GitHubEvidenceService.CredentialProvider;

    /// <summary>The audit action recorded when an enrichment is asked for.</summary>
    public const string EnrichmentRequestedAction = "github.enrichment.requested";

    /// <inheritdoc />
    public async Task<GitHubEnrichmentAccepted> RequestEnrichmentAsync(
        OwnerScope owner,
        Guid milestoneId,
        GitHubEnrichmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var errors = GitHubEnrichmentPolicy.Validate(request.ExpectedBundleRevision, request.IdempotencyKey);
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

                return new PreparedEnrichment(found, milestone.Revision);
            },
            cancellationToken).ConfigureAwait(false);

        // The job binds to the milestone's revision for the delivered supersede check, and the run
        // row carries the evidence revision for the delivered append. The two revisions guard two
        // different moves: a milestone edited mid-run, and a bundle appended mid-run.
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                EnrichmentOperationKind,
                milestoneId,
                prepared.MilestoneRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The run row is written after the job exists, because it names the job. A replay under the
        // same idempotency key gets the original job back and its own insert changes no row, so the
        // run log cannot grow a second entry for one enrichment.
        var now = clock.GetUtcNow();
        var run = new EnrichmentRun(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            milestoneId,
            request.ExpectedBundleRevision,
            prepared.Connection.Owner,
            prepared.Connection.Repo,
            now);

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await runs.TryInsertAsync(run, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        EnrichmentRequestedAction,
                        run.Id,
                        request.ExpectedBundleRevision,
                        "github enrichment requested"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub enrichment requested for milestone {MilestoneId} as operation {OperationId} ({Created}).",
            milestoneId,
            accepted.Operation.Id,
            inserted);

        return new GitHubEnrichmentAccepted(accepted.Operation, accepted.Created);
    }

    /// <inheritdoc />
    public async Task<GitHubEnrichmentCollected> EnrichAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The job is handed an operation id and nothing else, so the revision the owner was looking
        // at is read back from the run row. A job whose run row is missing cannot be enriched
        // honestly, and says so as a permanent failure rather than guessing at a revision.
        var run = await runs
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubEnrichmentJobError.RequestMissing);

        var connection = await connections
            .FindForMilestoneAsync(owner.ScopeId, run.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(GitHubEnrichmentJobError.ConnectionMissing);

        var repository = RepoRef.Parse(connection.Owner, connection.Repo)
            ?? throw new OperationPermanentException(GitHubEnrichmentJobError.ConnectionInvalid);

        // The commits are the milestone's own stored evidence, read back rather than requested. An
        // enrichment therefore cannot be pointed at anything the owner has not already collected,
        // and a bundle that moved since the request has nowhere to land: the expected revision the
        // run row carries no longer describes the head, and the job ends here as superseded rather
        // than enriching a bundle the owner did not ask about.
        var head = await reading
            .ListAsync(owner, run.MilestoneId, new PageRequest(Limit: 1, Cursor: null), cancellationToken)
            .ConfigureAwait(false);
        var bundle = head.Bundles.FirstOrDefault()
            ?? throw new OperationPermanentException(GitHubEnrichmentJobError.NothingToEnrich);

        if (bundle.Revision != run.ExpectedBundleRevision)
        {
            logger.LogInformation(
                "GitHub enrichment {OperationId} was superseded: the milestone's evidence moved to revision {Revision} past the requested {Expected}.",
                operationId,
                bundle.Revision,
                run.ExpectedBundleRevision);

            throw new OperationPermanentException(GitHubEnrichmentJobError.Superseded);
        }

        var shas = bundle.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Commit, StringComparison.Ordinal))
            .Select(item => item.SourceRevision)
            .Where(GitHubRelationshipReader.IsCommitSha)
            .Distinct(StringComparer.Ordinal)
            .Take(GitHubEnrichmentLimits.MaxCommits)
            .ToArray();

        if (shas.Length == 0)
        {
            throw new OperationPermanentException(GitHubEnrichmentJobError.NothingToEnrich);
        }

        // The credential is leased for the duration of the one read that uses it and returned by
        // the finally below, whatever happens. The leased bytes become a string for the transport
        // and nothing else: no result, no log line and no exception ever carries it.
        var lease = connection.CredentialRef is { } reference
            ? await LeaseAsync(owner, reference, cancellationToken).ConfigureAwait(false)
            : null;

        GitRelationshipResult enriched;
        try
        {
            enriched = await relationships
                .ReadAsync(
                    repository,
                    shas,
                    lease is null ? null : Encoding.UTF8.GetString(lease.Secret),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }

        ThrowFor(enriched);

        if (enriched.Outcome is not GitRelationshipOutcome.Collected)
        {
            throw new OperationTransientException(GitHubEnrichmentJobError.Unreachable);
        }

        var items = Items(repository, run, bundle, shas, enriched);

        EvidenceBundle enrichedBundle;
        try
        {
            enrichedBundle = await evidence
                .AppendAsync(owner, run.MilestoneId, run.ExpectedBundleRevision, items, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RevisionConflictException)
        {
            // Somebody else's append reached this milestone while the relationships were being
            // read. Nothing was written, the stored bundle is exactly as it was, and this run has
            // no result of its own to attach. The job ends as a permanent failure named
            // `github_enrichment_job_superseded`, which is the honest terminal state for "the input
            // this run was built on no longer exists" and is deliberately not dressed up as success.
            logger.LogInformation(
                "GitHub enrichment {OperationId} was superseded: the milestone's evidence moved past the requested revision while relationships were being read.",
                operationId);

            throw new OperationPermanentException(GitHubEnrichmentJobError.Superseded);
        }

        logger.LogInformation(
            "GitHub enrichment {OperationId} appended {ItemCount} items to milestone {MilestoneId} at revision {Revision}.",
            operationId,
            items.Count,
            run.MilestoneId,
            enrichedBundle.Revision);

        return new GitHubEnrichmentCollected(
            enrichedBundle.Revision,
            items.Count,
            enriched.Relations.Count,
            shas.Length,
            enriched.Truncated);
    }

    // -----------------------------------------------------------------------------------------
    // Turning an enrichment into evidence items.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The items one enrichment becomes. Every item says what it is about and whether it is whole:
    /// a commit whose relations could not be read is an <c>unavailable</c> item carrying the
    /// reason, and a read that stopped at the cap is a <c>partial</c> item that names the cap.
    /// Nothing is silently dropped and nothing is invented to fill a gap. The REST evidence this
    /// enriches is untouched: these items only add attributed relationships to it.
    /// </summary>
    private static IReadOnlyList<EvidenceAppendItem> Items(
        RepoRef repository,
        EnrichmentRun run,
        EvidenceBundle bundle,
        IReadOnlyList<string> shas,
        GitRelationshipResult enriched)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(enriched);

        var items = new List<EvidenceAppendItem>();
        var sourceRef = repository.ToSourceRef();
        var capturedAt = enriched.FetchedAt;
        var expected = run.ExpectedBundleRevision.ToString(CultureInfo.InvariantCulture);

        // The observation itself, so a reader can see which commits were queried, what came back
        // and which requests were made. The provider node ids live on the relation items; this
        // item carries the counts, the revision and the endpoints.
        items.Add(Item(
            EvidenceKinds.ProjectMetadata,
            sourceRef,
            expected,
            capturedAt,
            [
                $"Repository: {repository}",
                $"Enrichment of evidence revision: {expected}",
                $"Commits queried: {shas.Count}",
                $"Relationships observed: {enriched.Relations.Count}",
                $"Truncated: {(enriched.Truncated ? "yes" : "no")}",
                $"Fetched at: {capturedAt.UtcDateTime:O}",
                "Endpoints consulted:",
                .. enriched.Endpoints.Select(endpoint => $"  {endpoint.Method} {endpoint.Path} -> {endpoint.Status}"),
            ],
            EvidenceAvailability.Complete,
            null));

        foreach (var relation in enriched.Relations)
        {
            items.Add(RelationItem(sourceRef, capturedAt, relation));
        }

        // Every commit whose relations could not be read, named as unavailable with its reason.
        foreach (var missing in enriched.Unavailable)
        {
            var sha = missing.Segment.StartsWith("relation:", StringComparison.Ordinal)
                ? missing.Segment["relation:".Length..]
                : missing.Segment;

            items.Add(Item(
                EvidenceKinds.Commit,
                sourceRef,
                GitHubRelationshipReader.IsCommitSha(sha) ? sha : expected,
                capturedAt,
                [
                    $"Relations for commit {sha} were requested and could not be read.",
                    $"Reason: {missing.Reason}.",
                ],
                EvidenceAvailability.Unavailable,
                null));
        }

        // A read that stopped at the cap, named as a partial item that carries the cap in a
        // redaction label. A partial item with no exclusion would be indistinguishable from a whole
        // one, and the delivered evidence policy refuses that.
        if (enriched.Truncated)
        {
            items.Add(Item(
                EvidenceKinds.ProjectMetadata,
                sourceRef,
                expected,
                capturedAt,
                [
                    "Relationship enrichment stopped at the limit.",
                    $"Limit: {GitHubRelationshipReader.MaxRelations}.",
                ],
                EvidenceAvailability.Partial,
                [$"relations truncated at {GitHubRelationshipReader.MaxRelations}"]));
        }

        return items;
    }

    private static EvidenceAppendItem RelationItem(
        string sourceRef,
        DateTimeOffset capturedAt,
        GitRelation relation)
    {
        if (string.Equals(relation.Kind, "issue", StringComparison.Ordinal))
        {
            return Item(
                EvidenceKinds.Issue,
                sourceRef,
                relation.FromSha,
                capturedAt,
                [
                    $"Issue #{relation.Number}: {relation.Title}",
                    $"State: {relation.State}",
                    $"Reference: {relation.Reference ?? "not reported"}",
                    relation.FromPullNumber is { } pull
                        ? $"Observed through pull request #{pull}."
                        : "Observed through the repository's issue references.",
                    $"Observed from commit: {relation.FromSha}",
                    $"Provider node id: {relation.NodeId}",
                ],
                EvidenceAvailability.Complete,
                null);
        }

        return Item(
            EvidenceKinds.Pr,
            sourceRef,
            relation.FromSha,
            capturedAt,
            [
                $"Pull request #{relation.Number}: {relation.Title}",
                $"State: {relation.State}",
                $"Merged: {(relation.Merged is true ? "yes" : "no")}",
                $"Reference: {relation.Reference ?? "not reported"}",
                $"Base: {relation.BaseRef ?? "not reported"}",
                $"Head: {relation.HeadRef ?? "not reported"}",
                $"Observed from commit: {relation.FromSha}",
                $"Provider node id: {relation.NodeId}",
            ],
            EvidenceAvailability.Complete,
            null);
    }

    /// <summary>
    /// Builds one item, hashing the payload with the delivered policy so the same observed fact is
    /// the same item however it was assembled. A retry of the same run therefore appends nothing
    /// new: the delivered idempotent append recognises every fact already in the head.
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

    // -----------------------------------------------------------------------------------------
    // Refusals.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Turns a transport refusal into the delivered classified job failure. The classification is
    /// made once, here, because only this method knows the difference between "will never work" and
    /// "worth another attempt", and guessing wrong either retries a refusal forever or abandons a
    /// throttling response. A missing permission ends here as a permanent failure that fabricates
    /// nothing: no relation fact is appended on any refusal path.
    /// </summary>
    private static void ThrowFor(GitRelationshipResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Outcome)
        {
            case GitRelationshipOutcome.Collected:
                return;

            // A repository that is private and one that does not exist are the same answer from
            // GitHub, so they are the same answer here: permanent, and named as one thing.
            case GitRelationshipOutcome.RepositoryUnavailable:
                throw new OperationPermanentException(GitHubEnrichmentJobError.RepositoryUnavailable);

            // A refused credential will be refused again until the owner changes it.
            case GitRelationshipOutcome.CredentialRejected:
                throw new OperationPermanentException(GitHubEnrichmentJobError.CredentialRejected);

            // The credential may not read these relationships. Permanent, and deliberately a
            // different code from a refused credential: the owner must widen access, not rotate a key.
            case GitRelationshipOutcome.PermissionDenied:
                throw new OperationPermanentException(GitHubEnrichmentJobError.PermissionDenied);

            // GitHub named its own delay. The worker clamps it with the delivered policy, so a
            // response body can never park this job for longer than the bound allows.
            case GitRelationshipOutcome.RateLimited:
                throw new OperationRateLimitedException(result.RetryAfter);

            // Malformed variables against a fixed query will not become well formed on retry.
            case GitRelationshipOutcome.Rejected:
                throw new OperationPermanentException(GitHubEnrichmentJobError.RequestRefused);

            default:
                throw new OperationTransientException(GitHubEnrichmentJobError.Unreachable);
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
            throw new OperationPermanentException(GitHubEnrichmentJobError.CredentialRejected);
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

            throw new OperationPermanentException(GitHubEnrichmentJobError.CredentialRejected);
        }

        return lease;
    }

    /// <summary>What a request has to establish before a job for it may exist.</summary>
    private sealed record PreparedEnrichment(GitHubConnection Connection, long MilestoneRevision);

    private static void Throw(IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }
}
