namespace Chronicleaf.Core;

/// <summary>
/// The well-known publisher target ids this product classifies (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Medium"/> and <see cref="LinkedIn"/> are the two export-only targets: the
/// manual-import and copy packages behind them carry no account, no token and no transport,
/// so their publish routes refuse unconditionally with their own <c>publisher_unsupported</c>.
/// This contract classifies them as export-only in its registry rather than colliding with
/// that code: a publish asked of this contract for either target is refused with this
/// package's own <c>publisher_target_export_only</c> before any adapter is resolved, any
/// credential is leased or any job exists.
/// </para>
/// <para>
/// The six transport adapters (GitHub Pages, Ghost, Hashnode, Dev.to, WordPress, custom
/// blog) register their own target ids beside their adapters in their own packages. This
/// file names no id for them, because an id this package named but no adapter served would
/// be a target that answers "known" and then cannot publish - the same lie in reverse.
/// </para>
/// </remarks>
public static class PublisherTargetIds
{
    /// <summary>The Medium manual-import target: export-only, never published by this product.</summary>
    public const string Medium = "medium";

    /// <summary>The LinkedIn copy target: export-only, never published by this product.</summary>
    public const string LinkedIn = "linkedin";

    /// <summary>Bounded to the width of the <c>publication_attempts.target_id</c> column.</summary>
    public const int MaxLength = 64;

    /// <summary>True for a target id this build classifies as export-only.</summary>
    public static bool IsExportOnly(string? targetId) =>
        string.Equals(targetId, Medium, StringComparison.Ordinal)
        || string.Equals(targetId, LinkedIn, StringComparison.Ordinal);
}

/// <summary>
/// The modes a publication may run in (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// A mode is chosen per request, never stored as a capability of the content: the same
/// approved revision may go out as a remote draft for review on the remote side, or as a
/// publication, and the attempt row records which one was authorized because the receipt
/// of one is not the receipt of the other.
/// </remarks>
public static class PublishModes
{
    /// <summary>Ask the remote side to hold the material as a draft, where the target offers one.</summary>
    public const string Draft = "draft";

    /// <summary>Ask the remote side to publish the material.</summary>
    public const string Publish = "publish";

    public static IReadOnlyList<string> All { get; } = [Draft, Publish];

    /// <summary>Bounded to the width of the <c>publication_attempts.mode</c> column.</summary>
    public const int MaxLength = 16;

    /// <summary>
    /// True for a mode this build sends. Compared ordinally: a mode is an identifier a stored
    /// row is read back by, so a folded spelling would authorize a mode nobody named.
    /// </summary>
    public static bool IsKnown(string? mode) => mode is not null && All.Contains(mode, StringComparer.Ordinal);
}

/// <summary>
/// The states a publication attempt may hold (PUBLISHER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Prepared, sending, then one terminal state, and a terminal state never moves.</b> The
/// row is created <c>prepared</c> beside the operation that will run it; the run claims it
/// to <c>sending</c> before any adapter is invoked; and the adapter's answer - or the
/// absence of a trustworthy answer - moves it to exactly one of <c>published</c>,
/// <c>remote-draft</c>, <c>unknown</c> or <c>failed</c>. There is no UPDATE out of a terminal
/// state anywhere in this repository, so a receipt that was read once reads the same the
/// next time.
/// </para>
/// <para>
/// <c>unknown</c> is the honest answer to "the bytes may have left": an ambiguous timeout
/// and a crash after the upstream commit are indistinguishable from inside this process, so
/// both become <c>unknown</c> with no automatic retry. A second dispatch would be a second
/// public post, which is the failure the lack of a retry exists to prevent.
/// </para>
/// </remarks>
public static class PublishStates
{
    /// <summary>Authorized and queued, but no run has claimed the attempt yet.</summary>
    public const string Prepared = "prepared";

    /// <summary>One run holds the claim. A second claimant does not dispatch again.</summary>
    public const string Sending = "sending";

    /// <summary>The remote side holds the material as published.</summary>
    public const string Published = "published";

    /// <summary>The remote side holds the material as a draft.</summary>
    public const string RemoteDraft = "remote-draft";

    /// <summary>The write may have happened; it is not reissued and it is reconciled by hand or not at all.</summary>
    public const string Unknown = "unknown";

    /// <summary>The write did not happen, or the run never reached an adapter.</summary>
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } =
        [Prepared, Sending, Published, RemoteDraft, Unknown, Failed];

    /// <summary>Bounded to the width of the <c>publication_attempts.state</c> column.</summary>
    public const int MaxLength = 16;

    /// <summary>True while no run has finished with this attempt.</summary>
    public static bool IsOpen(string? state) =>
        string.Equals(state, Prepared, StringComparison.Ordinal)
        || string.Equals(state, Sending, StringComparison.Ordinal);

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);
}

/// <summary>
/// One reviewed publication decision and its outcome (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// An attempt is the publisher's analogue of the review package's approval: a row about a
/// specific revision and a specific body, never about an asset in general.
/// <see cref="ContentRevision"/> names the revision, <see cref="BodyHash"/> is the digest of
/// exactly the bytes that were authorized, <see cref="EvidenceRevision"/> names the evidence
/// head the approval the run re-checked was bound to, and <see cref="PreviewHash"/> names the
/// target preview the owner confirmed. A run whose re-check finds any of those moved does
/// not dispatch: the receipt records what was authorized and the failure records why it was
/// not sent.
/// </para>
/// <para>
/// A row is never deleted and never rewritten: the state moves forward by compare-and-set
/// from <c>prepared</c> through <c>sending</c> to one terminal state, and a second dispatch
/// of the same attempt is refused by the claim rather than recorded twice. The credential
/// that authorized the send is named nowhere here - not by id, not by version - because a
/// receipt outlives the credential and must not become a way to reach it.
/// </para>
/// </remarks>
/// <param name="id">This attempt's identifier.</param>
/// <param name="scopeId">The owning scope, carried on the row so a scoped read never trusts a join.</param>
/// <param name="operationId">The durable operation that runs this attempt, exactly one attempt per operation.</param>
/// <param name="targetId">The publisher target this attempt sends to.</param>
/// <param name="assetId">The asset the published revision belongs to.</param>
/// <param name="contentRevision">The authorized revision number, from 1.</param>
/// <param name="bodyHash">Lowercase SHA-256 hex over the UTF-8 bytes of the authorized body.</param>
/// <param name="evidenceRevision">The evidence head revision the authorizing approval was bound to.</param>
/// <param name="previewHash">The digest of the target preview the owner confirmed.</param>
/// <param name="mode">One of <see cref="PublishModes"/>: the ask the owner authorized.</param>
/// <param name="approvalId">The live approval the request was accepted against.</param>
/// <param name="idempotencyKey">The caller's key, unique within the scope for this contract.</param>
/// <param name="state">One of <see cref="PublishStates"/>.</param>
/// <param name="remoteId">The remote side's identifier for the material, when it named one.</param>
/// <param name="remoteUrl">The remote side's address for the material, when it named one.</param>
/// <param name="errorCode">The redacted failure code, when the attempt ended without publishing.</param>
/// <param name="dispatchedAt">When a run claimed the attempt for sending, or null while unclaimed.</param>
/// <param name="completedAt">When the attempt reached its terminal state, or null while open.</param>
/// <param name="createdAt">When the attempt was authorized; UTC.</param>
/// <param name="updatedAt">When the attempt last moved; UTC.</param>
public sealed class PublicationAttempt
{
    public PublicationAttempt(
        Guid id,
        Guid scopeId,
        Guid operationId,
        string targetId,
        Guid assetId,
        long contentRevision,
        string bodyHash,
        long evidenceRevision,
        string previewHash,
        string mode,
        Guid approvalId,
        string idempotencyKey,
        string state,
        string? remoteId,
        string? remoteUrl,
        string? errorCode,
        DateTimeOffset? dispatchedAt,
        DateTimeOffset? completedAt,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A publication attempt needs a non-empty id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A publication attempt needs its operation.", nameof(operationId));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("A publication attempt needs an asset.", nameof(assetId));
        }

        if (approvalId == Guid.Empty)
        {
            throw new ArgumentException("A publication attempt needs the approval it was accepted against.", nameof(approvalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        if (targetId.Length > PublisherTargetIds.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetId),
                "A publisher target id is bounded by the column width.");
        }

        if (!PublishModes.IsKnown(mode))
        {
            throw new ArgumentException("The mode is not one this product sends.", nameof(mode));
        }

        if (!PublishStates.IsKnown(state))
        {
            throw new ArgumentException("The state is not one an attempt holds.", nameof(state));
        }

        if (contentRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentRevision), contentRevision, "A published revision is 1 or greater.");
        }

        if (evidenceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceRevision), evidenceRevision, "An evidence revision is 1 or greater.");
        }

        if (!IsSha256Hex(bodyHash))
        {
            throw new ArgumentException("The body hash must be lowercase SHA-256 hex.", nameof(bodyHash));
        }

        if (!IsSha256Hex(previewHash))
        {
            throw new ArgumentException("The preview hash must be lowercase SHA-256 hex.", nameof(previewHash));
        }

        if (remoteId is not null && (remoteId.Length == 0 || remoteId.Length > Limits.MaxRemoteIdLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(remoteId), "A remote id is bounded by the column width when present.");
        }

        if (remoteUrl is not null && (remoteUrl.Length == 0 || remoteUrl.Length > Limits.MaxRemoteUrlLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(remoteUrl), "A remote URL is bounded by the column width when present.");
        }

        if (errorCode is not null && (errorCode.Length == 0 || errorCode.Length > Limits.MaxErrorCodeLength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(errorCode), "An error code is bounded by the column width when present.");
        }

        if (idempotencyKey.Length > Limits.MaxIdempotencyKeyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotencyKey),
                $"An idempotency key is at most {Limits.MaxIdempotencyKeyLength} characters.");
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        TargetId = targetId;
        AssetId = assetId;
        ContentRevision = contentRevision;
        BodyHash = bodyHash;
        EvidenceRevision = evidenceRevision;
        PreviewHash = previewHash;
        Mode = mode;
        ApprovalId = approvalId;
        IdempotencyKey = idempotencyKey;
        State = state;
        RemoteId = remoteId;
        RemoteUrl = remoteUrl;
        ErrorCode = errorCode;
        DispatchedAt = dispatchedAt;
        CompletedAt = completedAt;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public string TargetId { get; }

    public Guid AssetId { get; }

    public long ContentRevision { get; }

    public string BodyHash { get; }

    public long EvidenceRevision { get; }

    public string PreviewHash { get; }

    public string Mode { get; }

    public Guid ApprovalId { get; }

    public string IdempotencyKey { get; }

    public string State { get; }

    public string? RemoteId { get; }

    public string? RemoteUrl { get; }

    public string? ErrorCode { get; }

    public DateTimeOffset? DispatchedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>A stable diagnostic label for an attempt row. It names the attempt, never the content.</summary>
    public static string Label(Guid attemptId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"publication-attempts/{attemptId:N}");

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHex = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The bounds this package's own storage imposes, beside the record that satisfies them.</summary>
    public static class Limits
    {
        /// <summary>How long a remote side's identifier may be as a stored label.</summary>
        public const int MaxRemoteIdLength = 200;

        /// <summary>How long a remote side's address may be as a stored label.</summary>
        public const int MaxRemoteUrlLength = 2000;

        /// <summary>How long a redacted failure code may be as a stored label.</summary>
        public const int MaxErrorCodeLength = 64;

        /// <summary>How long a caller's idempotency key may be.</summary>
        public const int MaxIdempotencyKeyLength = 200;
    }
}
