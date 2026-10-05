namespace Chronicleaf.Core;

/// <summary>
/// One repository's webhook subscription: which connection accepts a signed delivery, and where the
/// secret that authenticates it is held (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="CredentialRef"/> holds a vault reference and never a secret.</b> The value is the
/// delivered vault's own credential id, so a read of this table - by a route, an export, an audit or
/// a test - learns that a webhook secret is configured for this connection and nothing whatsoever about
/// it. The bytes themselves are opened by the delivered vault for the length of one comparison and
/// returned, and they exist in no other scope: not in this table, not in a delivery, not in a log line
/// and not in an exception message.
/// </para>
/// <para>
/// This row is the only thing that makes a delivery addressable. The webhook route names a connection
/// id, and a connection id is not a secret: the design says so explicitly, and a row that treated an
/// identifier as authentication would accept anything anybody guessed. What authenticates a delivery
/// is the HMAC signature over its raw body, computed with the secret this row references.
/// </para>
/// <para>
/// <b><see cref="ArchivedAt"/> is how a connection goes quiet.</b> An archived subscription still
/// answers a delivery - 202 with an ignored receipt, because a sender that is told "gone" retries for
/// hours - but it produces no candidate. The same is true of an archived project and of a connection
/// that has no row at all, and all three are one answer rather than three: a delivery that arrives for
/// something this product is not collecting from is acknowledged, not refused.
/// </para>
/// </remarks>
public sealed class GitHubWebhookSubscription
{
    /// <param name="id">This subscription's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="connectionId">The repository connection that accepts a signed delivery.</param>
    /// <param name="projectId">The project the connection belongs to, denormalised for the scoped read.</param>
    /// <param name="credentialRef">The vault reference of the webhook secret, never the secret.</param>
    /// <param name="archivedAt">When the subscription stopped accepting candidates, or <c>null</c>.</param>
    /// <param name="createdAt">When the subscription was first stored.</param>
    /// <param name="updatedAt">When the subscription last changed.</param>
    public GitHubWebhookSubscription(
        Guid id,
        Guid scopeId,
        Guid connectionId,
        Guid projectId,
        Guid credentialRef,
        DateTimeOffset? archivedAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs a scope.", nameof(scopeId));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs a connection.", nameof(connectionId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs a project.", nameof(projectId));
        }

        if (credentialRef == Guid.Empty)
        {
            throw new ArgumentException("A subscription needs the reference to a stored secret.", nameof(credentialRef));
        }

        Id = id;
        ScopeId = scopeId;
        ConnectionId = connectionId;
        ProjectId = projectId;
        CredentialRef = credentialRef;
        ArchivedAt = archivedAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ConnectionId { get; }

    public Guid ProjectId { get; }

    /// <summary>The vault reference of the webhook secret. An id, never a value.</summary>
    public Guid CredentialRef { get; }

    public DateTimeOffset? ArchivedAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>True when this subscription accepts nothing. An archived project also makes it quiet.</summary>
    public bool IsArchived => ArchivedAt.HasValue;
}

/// <summary>
/// One signed delivery GitHub sent, and what became of it (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The receipt is the dedupe key, not the body.</b> <see cref="DeliveryId"/> is GitHub's own id for
/// the delivery and the unique index on <c>(connection_id, delivery_id)</c> is what makes a retry the
/// same delivery rather than a second one. A sender that redelivers - which GitHub does, and does not
/// tell you when - gets the original receipt back and no second candidate, so the receipt a reader sees
/// and the rows a reader counts cannot disagree.
/// </para>
/// <para>
/// <b>Nothing about the payload is stored.</b> There is no body column, no signature column and no
/// secret column: a delivery's own text may name a private issue title or a branch name, and a table
/// of receipts is a table a support export would carry. What is stored is a closed-vocabulary event
/// name, the action the event carried, when it arrived and what this build did with it.
/// </para>
/// </remarks>
public sealed class GitHubWebhookDelivery
{
    /// <param name="id">This receipt's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="connectionId">The connection the delivery arrived for.</param>
    /// <param name="projectId">
    /// The project that connection belongs to, denormalised from the subscription. A receipt names its
    /// project so the scoped candidate read that follows a delivery needs no join, and so a receipt
    /// stays legible after the connection has been reconnected to a different repository.
    /// </param>
    /// <param name="deliveryId">GitHub's own identifier for the delivery, which makes a retry the same row.</param>
    /// <param name="event">The event name from the delivery's header, bounded.</param>
    /// <param name="action">The action the event carried, bounded, when it carried one.</param>
    /// <param name="status">What this build did with the delivery, from the closed receipt vocabulary.</param>
    /// <param name="reason">Why a delivery was ignored, from the same closed vocabulary, or <c>null</c>.</param>
    /// <param name="candidateCount">How many candidates this delivery produced.</param>
    /// <param name="receivedAt">When the delivery was received.</param>
    /// <param name="proposalsJson">
    /// This package's own reading of the payload, as a canonical JSON document.
    /// </summary>
    /// <remarks>
    /// <b>This is a routing decision, not a stored payload.</b> The delivery's body is never written
    /// anywhere: it may name a private issue title, a branch name and an author's login, and a table of
    /// receipts is a table a support export would carry. What is stored instead is the closed-vocabulary
    /// list of proposals this build already decided to make from it - a kind, a provider id, a bounded
    /// label and an instant - which is the same information the candidate rows carry and nothing more.
    /// <para>
    /// It has to be stored because the work happens after the route has answered. The route
    /// authenticates, decides what the payload claims and returns 202; the outbox consumer later writes
    /// the candidates, and a consumer that had to re-read a body nobody kept would either propose
    /// nothing or guess. Storing the decision rather than the document is what lets the work be
    /// deferred without keeping a foreign repository's contents in a table.
    /// </para>
    /// </remarks>
    public GitHubWebhookDelivery(
        Guid id,
        Guid scopeId,
        Guid connectionId,
        Guid projectId,
        string deliveryId,
        string @event,
        string? action,
        string status,
        string? reason,
        int candidateCount,
        DateTimeOffset receivedAt,
        string proposalsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalsJson);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A delivery needs an id.", nameof(id));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A delivery needs a connection.", nameof(connectionId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A delivery needs the project its connection belongs to.", nameof(projectId));
        }

        if (candidateCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateCount),
                candidateCount,
                "A delivery cannot have produced a negative number of candidates.");
        }

        Id = id;
        ScopeId = scopeId;
        ConnectionId = connectionId;
        ProjectId = projectId;
        DeliveryId = deliveryId;
        Event = @event;
        Action = action;
        Status = status;
        Reason = reason;
        CandidateCount = candidateCount;
        ReceivedAt = receivedAt;
        ProposalsJson = proposalsJson;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ConnectionId { get; }

    /// <summary>The project this connection belonged to when the delivery arrived.</summary>
    public Guid ProjectId { get; }

    /// <summary>
    /// This package's reading of the payload, as canonical JSON. Never the payload itself.
    /// </summary>
    /// <remarks>
    /// Kept next to the receipt rather than in a column of its own because it has exactly one reader -
    /// the outbox consumer that turns a receipt into candidates - and a second table for it would be a
    /// second place a foreign repository's words could land.
    /// </remarks>
    public string ProposalsJson { get; }

    /// <summary>GitHub's identifier for the delivery. The unique index on it is the retry guard.</summary>
    public string DeliveryId { get; }

    public string Event { get; }

    public string? Action { get; }

    /// <summary>
    /// What this build did with the delivery, from the closed receipt vocabulary.
    /// </summary>
    /// <remarks>
    /// Written once, by <see cref="RecordCandidates"/>, and never rewritten afterwards. A receipt that
    /// could be re-decided would let a redelivered event change what the first one was recorded as
    /// having done, which is the one thing a receipt exists to prevent.
    /// </remarks>
    public string Status { get; private set; }

    /// <summary>Why a delivery produced no candidate, or <c>null</c> when it produced one.</summary>
    public string? Reason { get; private set; }

    public int CandidateCount { get; private set; }

    public DateTimeOffset ReceivedAt { get; }

    /// <summary>
    /// Records that a delivery was accepted and produced candidates, once.
    /// </summary>
    /// <remarks>
    /// A receipt that already names a status is never rewritten, so a redelivered event cannot change
    /// what the first one was recorded as having done.
    /// </remarks>
    /// <param name="count">How many candidates this delivery produced.</param>
    /// <param name="reason">
    /// Why it produced none, which is required when <paramref name="count"/> is zero and forbidden when
    /// it is not.
    /// </param>
    /// <exception cref="InvalidOperationException">The receipt already has a status.</exception>
    /// <exception cref="ArgumentException">
    /// The reason is not one of the closed ignored-reason vocabulary, or it is missing for a receipt that
    /// produced nothing.
    /// </exception>
    /// <remarks>
    /// <b>The vocabulary is checked here rather than only by the database.</b> A reason outside it is
    /// refused by the check constraint on the receipts table, so a caller that passed one used to learn
    /// about it as a failed UPDATE after the delivery had already been acknowledged - the receipt stayed
    /// pending and the failure surfaced as a dispatched-but-failed outbox event. Checking the value at
    /// the point of the decision means the mistake is a refusal the caller can read instead of a
    /// constraint violation it cannot explain.
    /// </remarks>
    public void RecordCandidates(int count, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(count));

        if (CandidateCount > 0 || Status != GitHubCandidateReceipt.Pending)
        {
            throw new InvalidOperationException("A recorded delivery receipt cannot be recorded again.");
        }

        if (count > 0)
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                // A recorded receipt has no reason: a reader shown "recorded" beside an explanation would
                // be reading a claim about why something succeeded.
                throw new ArgumentException(
                    "A recorded receipt carries no reason.",
                    nameof(reason));
            }

            CandidateCount = count;
            Status = GitHubCandidateReceipt.Recorded;
            Reason = null;

            return;
        }

        if (string.IsNullOrWhiteSpace(reason) ||
            !GitHubCandidateIgnoredReasons.All.Contains(reason, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "An ignored receipt must name one of this build's ignored reasons.",
                nameof(reason));
        }

        CandidateCount = 0;
        Status = GitHubCandidateReceipt.Ignored;
        Reason = reason;
    }
}

/// <summary>
/// One proposal for a Milestone that a tag, a release or an event produced
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A candidate is a proposal, and the type says so in every direction.</b> It names the repository it
/// came from, what it came from and when it was received, and it has no milestone, no bundle and no
/// revision - because confirming it is <see cref="Chronicleaf.Core.Milestone"/>'s decision and this
/// package does not make one. There is no field a later reader could mistake for a confirmed record,
/// and no route in this package that would turn one into one.
/// </para>
/// <para>
/// <b><see cref="SourceKey"/> is the identity, and it is the provider's own.</b> It is
/// <c>release:{id}</c>, <c>tag:{name}:{sha}</c> or <c>event:{event}:{objectId}:{revision}</c>, and the
/// unique index on <c>(scope_id, source_key)</c> is what makes "exactly one candidate per source key"
/// a fact about the database rather than a hope about a loop. A tag and a release that name the same
/// commit are two candidates on purpose - they are two different things somebody published - and
/// because their keys differ, each is recorded once and separately.
/// </para>
/// <para>
/// <b>The title is the owner's to act on, so it is bounded rather than trusted.</b> It comes from
/// somebody else's repository, and a release name is an arbitrary string. It is cut at a declared
/// length on the way in, and it is never executed, never interpreted and never sent anywhere.
/// </para>
/// </remarks>
public sealed class GitHubCandidate
{
    /// <param name="id">This candidate's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="connectionId">The connection the candidate came from.</param>
    /// <param name="projectId">The project the connection belongs to.</param>
    /// <param name="sourceKey">The provider's own identity for the thing this candidate proposes.</param>
    /// <param name="kind">Which kind of thing it proposes, from the closed candidate vocabulary.</param>
    /// <param name="title">What a human would read, bounded on the way in.</param>
    /// <param name="revision">The immutable revision or stable revision the source named, when it named one.</param>
    /// <param name="source">Whether a delivery or a poll proposed it.</param>
    /// <param name="deliveryId">The receipt that produced it, or <c>null</c> when a poll did.</param>
    /// <param name="receivedAt">When the source said the thing happened, or when it was received.</param>
    public GitHubCandidate(
        Guid id,
        Guid scopeId,
        Guid connectionId,
        Guid projectId,
        string sourceKey,
        string kind,
        string title,
        string? revision,
        string source,
        string? deliveryId,
        DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs a scope.", nameof(scopeId));
        }

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs a connection.", nameof(connectionId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A candidate needs a project.", nameof(projectId));
        }

        Id = id;
        ScopeId = scopeId;
        ConnectionId = connectionId;
        ProjectId = projectId;
        SourceKey = sourceKey;
        Kind = kind;
        Title = title;
        Revision = revision;
        Source = source;
        DeliveryId = deliveryId;
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ConnectionId { get; }

    public Guid ProjectId { get; }

    /// <summary>The provider's own identity for the thing proposed. The unique index on it is the dedupe.</summary>
    public string SourceKey { get; }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The commit sha a tag or release names, or the stable revision an event named.</summary>
    public string? Revision { get; }

    /// <summary>Whether a signed delivery or a poll proposed this.</summary>
    public string Source { get; }

    /// <summary>The receipt that produced this candidate, or <c>null</c> when a poll did.</summary>
    public string? DeliveryId { get; }

    /// <summary>When the source said it happened; the thing a reader sorts by.</summary>
    public DateTimeOffset ReceivedAt { get; }

    /// <summary>The commit a tag or release names, when it named one.</summary>
    public string? Sha => Revision;

    /// <summary>The tag or release name, when the key carries one.</summary>
    public string? TagName =>
        Kind is GitHubCandidateKinds.Tag or GitHubCandidateKinds.Release
            ? GitHubCandidateSourceKeys.DisplayName(SourceKey)
            : null;
}

/// <summary>
/// One record of one candidate poll (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written when the owner asks and completed when the run finishes.</b> That is what makes
/// the request itself durable: a job payload carries an operation id and nothing else, so the window the
/// owner typed has to live somewhere the worker can read without trusting a queue message. A row with a
/// null <see cref="HeadSha"/> or a null <see cref="CompletedAt"/> is a poll that was asked for and did
/// not finish, which is a true statement about it.
/// </para>
/// <para>
/// <b>It records what was read, never what was proposed.</b> The counts here are the provider's page and
/// this package's deduplication, so a run that read forty releases and proposed three is legible as such:
/// a reader can tell the difference between a quiet repository and a filter that swallowed everything.
/// </para>
/// </remarks>
public sealed class GitHubCandidateRun
{
    /// <param name="id">This record's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="operationId">The operation that performs the poll. One poll, one operation.</param>
    /// <param name="projectId">The project whose repository was polled.</param>
    /// <param name="expectedProjectRevision">The project revision the owner was looking at when they asked.</param>
    /// <param name="owner">The account or organisation of the connection that was used.</param>
    /// <param name="repo">The repository of the connection that was used.</param>
    /// <param name="since">The window the owner asked for, or <c>null</c> for the whole bounded page.</param>
    /// <param name="createdAt">When the poll was asked for.</param>
    public GitHubCandidateRun(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid projectId,
        long expectedProjectRevision,
        string owner,
        string repo,
        DateTimeOffset? since,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A candidate run needs an id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A candidate run needs the operation that polls for it.", nameof(operationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A candidate run needs a project.", nameof(projectId));
        }

        if (expectedProjectRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedProjectRevision),
                expectedProjectRevision,
                "A project revision is zero or greater.");
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        ProjectId = projectId;
        ExpectedProjectRevision = expectedProjectRevision;
        Owner = owner;
        Repo = repo;
        Since = since;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public Guid ProjectId { get; }

    /// <summary>The project revision this run was asked against, and therefore the one it must find unchanged.</summary>
    public long ExpectedProjectRevision { get; }

    public string Owner { get; }

    public string Repo { get; }

    public DateTimeOffset? Since { get; }

    /// <summary>The last commit any read resolved, when one did.</summary>
    public string? HeadSha { get; private set; }

    /// <summary>The paths consulted, comma separated, when they were.</summary>
    public string? Endpoints { get; private set; }

    /// <summary>When the last of those requests answered.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    public int ReleaseCount { get; private set; }

    public int TagCount { get; private set; }

    public int CandidateCount { get; private set; }

    public int RecordedCount { get; private set; }

    /// <summary>When the run finished and recorded what it read, or <c>null</c> while it has not.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Records what the poll read. The only state this row takes after it is created, and it can be
    /// taken once: a completed poll is a statement about one collection, and a second completion would
    /// be a second claim about the same operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run has already been completed.</exception>
    public void Complete(
        string? headSha,
        string endpoints,
        DateTimeOffset fetchedAt,
        int releaseCount,
        int tagCount,
        int candidateCount,
        int recordedCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoints);

        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("A completed candidate run cannot be completed again.");
        }

        HeadSha = headSha;
        Endpoints = endpoints;
        FetchedAt = fetchedAt;
        ReleaseCount = releaseCount;
        TagCount = tagCount;
        CandidateCount = candidateCount;
        RecordedCount = recordedCount;
        CompletedAt = fetchedAt;
    }
}

/// <summary>What a proposal is a proposal <em>of</em>. A closed set, never a string a payload chose.</summary>
public static class GitHubCandidateKinds
{
    /// <summary>A published release.</summary>
    public const string Release = "release";

    /// <summary>A created tag.</summary>
    public const string Tag = "tag";

    /// <summary>A merged pull request.</summary>
    public const string PullRequest = "pull-request";

    /// <summary>A closed issue.</summary>
    public const string Issue = "issue";

    /// <summary>A star count that crossed one of the declared thresholds.</summary>
    public const string Star = "star";

    /// <summary>Every kind this build records, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Release,
        Tag,
        PullRequest,
        Issue,
        Star,
    ];
}

/// <summary>What a candidate was proposed by.</summary>
public static class GitHubCandidateSources
{
    /// <summary>A signed delivery GitHub sent to the webhook route.</summary>
    public const string Webhook = "webhook";

    /// <summary>A poll of the repository's own releases and tags.</summary>
    public const string Poll = "poll";

    /// <summary>Every source this build records.</summary>
    public static readonly IReadOnlyList<string> All = [Webhook, Poll];
}

/// <summary>
/// What a delivery's receipt says happened to it.
/// </summary>
/// <remarks>
/// <see cref="Pending"/> is not a state a reader ever sees: a receipt is written when the delivery is
/// accepted and completed when the outbox consumer has decided, and a delivery whose consumer never
/// ran stays pending rather than being reported as something it was not.
/// </remarks>
public static class GitHubCandidateReceipt
{
    /// <summary>Accepted and stored, but not yet decided. Never presented as an outcome.</summary>
    public const string Pending = "pending";

    /// <summary>The delivery produced at least one candidate.</summary>
    public const string Recorded = "recorded";

    /// <summary>The delivery was verified and deliberately produced nothing. It still has a receipt.</summary>
    public const string Ignored = "ignored";

    /// <summary>Every status a receipt can reach, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All = [Pending, Recorded, Ignored];
}

/// <summary>Why a verified delivery produced no candidate. Closed on purpose: never provider prose.</summary>
public static class GitHubCandidateIgnoredReasons
{
    /// <summary>This build does not route this event at all.</summary>
    public const string UnsupportedEvent = "unsupported-event";

    /// <summary>This event arrived with an action this build does not act on.</summary>
    public const string UnsupportedAction = "unsupported-action";

    /// <summary>The star count is below the lowest declared threshold.</summary>
    public const string BelowThreshold = "below-star-threshold";

    /// <summary>The subscription, or its project, is archived: acknowledged and nothing more.</summary>
    public const string ConnectionInactive = "connection-inactive";

    /// <summary>The event named nothing this build can turn into a candidate.</summary>
    public const string NothingNamed = "nothing-named";

    /// <summary>
    /// Every reason a receipt may carry, so a writer can check a value before the database refuses it.
    /// </summary>
    /// <remarks>
    /// This list and the check constraint on the receipts table are the same five words, and the reason
    /// the record carries its own copy is that a caller learns about a bad value from an argument check
    /// rather than from a constraint violation on an UPDATE that has already been decided. A reason
    /// outside it is a value no reader would know how to interpret, which is what a reason is for.
    /// </remarks>
    public static readonly IReadOnlyList<string> All =
    [
        UnsupportedEvent,
        UnsupportedAction,
        BelowThreshold,
        ConnectionInactive,
        NothingNamed,
    ];
}

/// <summary>
/// The provider's own identity for a candidate, and the one rule about what it may contain
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A source key is built from parts the provider supplied, so it has to be a bounded string rather
/// than an arbitrary one. Every segment is trimmed, cut at a declared length and stripped of the
/// colons that would make the key ambiguous - <c>tag:a:b:c</c> must not be readable as the tag
/// <c>a:b</c> at sha <c>c</c>. That is why a tag whose name contains a colon still produces a key
/// that no other tag could produce, rather than a key two different tags could collide on.
/// </para>
/// <para>
/// The format is the design's, and it is the same shape for all three kinds so a reader can tell
/// where a key came from without a second column: <c>release:</c>, <c>tag:</c> and <c>event:</c>
/// are disjoint prefixes, so two kinds can never produce the same key.
/// </para>
/// </remarks>
public static class GitHubCandidateSourceKeys
{
    /// <summary>The widest stored source key, so the column and the builder are bounded by one number.</summary>
    public const int MaxLength = 320;

    /// <summary>The widest single segment of a key.</summary>
    public const int SegmentMaxLength = 120;

    /// <summary>The prefix a published release's key carries.</summary>
    public const string ReleasePrefix = "release:";

    /// <summary>The prefix a created tag's key carries.</summary>
    public const string TagPrefix = "tag:";

    /// <summary>The prefix an event's key carries.</summary>
    public const string EventPrefix = "event:";

    /// <summary>The key for a published release, from GitHub's own release id.</summary>
    public static string Release(long releaseId) => $"{ReleasePrefix}{releaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The key for a created tag, from its name and the commit it names.</summary>
    public static string Tag(string name, string sha) =>
        $"{TagPrefix}{Segment(name)}:{Segment(sha)}";

    /// <summary>The key for an event, from the event, the object it names and its stable revision.</summary>
    public static string Event(string @event, string objectId, string revision) =>
        $"{EventPrefix}{Segment(@event)}:{Segment(objectId)}:{Segment(revision)}";

    /// <summary>
    /// The human part of a tag or release key: the tag or release name, and nothing else.
    /// </summary>
    /// <remarks>
    /// Recovered from the key rather than stored twice, so the column a reader sorts by and the column
    /// a reader searches cannot disagree. A key this build did not write has no display name, which is
    /// why the callers all build keys with <see cref="Tag"/> or <see cref="Release"/>.
    /// </remarks>
    public static string? DisplayName(string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);

        if (sourceKey.StartsWith(ReleasePrefix, StringComparison.Ordinal))
        {
            // A release key is an id, and there is no name in it to recover.
            return null;
        }

        if (!sourceKey.StartsWith(TagPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = sourceKey[TagPrefix.Length..];

        // The last colon separates the name from the sha, and a name cannot contain one because the
        // builder stripped them - so this split is exact rather than a guess.
        var separator = name.LastIndexOf(':');

        return separator < 0 ? null : name[..separator];
    }

    /// <summary>
    /// One key segment: trimmed, colon-free and cut at a declared length.
    /// </summary>
    /// <remarks>
    /// Colons are removed rather than escaped. An escape would have to be reversible and would make the
    /// key longer for every segment, while a colon is not a character GitHub puts in a tag name, a
    /// release id or a revision - so removing it costs nothing real and removes the only way two
    /// different provider values could produce one key.
    /// </remarks>
    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(none)";
        }

        var trimmed = value.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);

        foreach (var character in trimmed)
        {
            if (character != ':' && character is not (>= '\u0000' and <= '\u001f'))
            {
                builder.Append(character);
            }

            if (builder.Length == SegmentMaxLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "(none)" : builder.ToString();
    }
}
