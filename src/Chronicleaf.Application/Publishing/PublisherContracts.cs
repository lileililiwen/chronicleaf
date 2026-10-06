using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Publishing;

/// <summary>
/// What one publisher target can do (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// Capabilities are declared by the adapter and read by the contract; they are never inferred
/// from a target id. An export-only target - Medium, LinkedIn - declares neither capability,
/// so asking it to draft or to publish is refused before any adapter is resolved. The six
/// transport adapters declare theirs in their own packages, and this contract serves exactly
/// what each declaration names.
/// </para>
/// </remarks>
/// <param name="TargetId">The registry id this description is for.</param>
/// <param name="SupportsDraft">Whether the target can hold the material as a remote draft.</param>
/// <param name="SupportsPublish">Whether the target can publish the material.</param>
/// <param name="SupportsReconcile">Whether an ambiguous write can be resolved by remote id.</param>
public sealed record PublisherCapabilities(
    string TargetId,
    bool SupportsDraft,
    bool SupportsPublish,
    bool SupportsReconcile);

/// <summary>
/// One target the registry classifies (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// The registry is classification, not transport: it says what a target id means, while the
/// adapter - where one exists - says how the bytes move. The two export-only descriptors are
/// registered by this package; every transport target is registered beside its adapter by the
/// package that owns the transport.
/// </remarks>
/// <param name="TargetId">The id publishes name.</param>
/// <param name="SupportsDraft">Whether the target can hold the material as a remote draft.</param>
/// <param name="SupportsPublish">Whether the target can publish the material.</param>
/// <param name="SupportsReconcile">Whether an ambiguous write can be resolved by remote id.</param>
/// <param name="ExportOnly">
/// True when material leaves through the target's export package and is carried by hand. An
/// export-only target supports neither mode, so the two flags above are always false here.
/// </param>
public sealed record PublisherTargetDescriptor(
    string TargetId,
    bool SupportsDraft,
    bool SupportsPublish,
    bool SupportsReconcile,
    bool ExportOnly)
{
    /// <summary>The Medium descriptor: export-only, with no transport behind it.</summary>
    public static PublisherTargetDescriptor Medium { get; } =
        new(PublisherTargetIds.Medium, false, false, false, true);

    /// <summary>The LinkedIn descriptor: export-only, with no transport behind it.</summary>
    public static PublisherTargetDescriptor LinkedIn { get; } =
        new(PublisherTargetIds.LinkedIn, false, false, false, true);

    /// <summary>Whether the requested mode is one this target serves.</summary>
    public bool Serves(string mode) =>
        (string.Equals(mode, PublishModes.Draft, StringComparison.Ordinal) && SupportsDraft)
        || (string.Equals(mode, PublishModes.Publish, StringComparison.Ordinal) && SupportsPublish);
}

/// <summary>
/// The registry every publisher adapter is classified in (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// A singleton of descriptors, and deliberately nothing else: it holds no adapter, no
/// credential and no store, so reading it can never send anything. The Medium and LinkedIn
/// export-only descriptors arrive with this package; transport packages contribute their own
/// descriptor beside their adapter registration, or let the adapter's own description speak
/// for its slot - the service prefers a contributed descriptor and otherwise reads the
/// adapter, so the two can never disagree unnoticed.
/// </remarks>
public interface IPublisherRegistry
{
    /// <summary>Every classified target, in registration order.</summary>
    IReadOnlyList<PublisherTargetDescriptor> Targets { get; }

    /// <summary>The descriptor for one target id, or null when no package classified it.</summary>
    PublisherTargetDescriptor? Find(string targetId);
}

/// <summary>
/// The default registry: the two export-only descriptors plus whatever each transport
/// package contributed (PUBLISHER-CONTRACT-001).
/// </summary>
public sealed class PublisherRegistry(IEnumerable<PublisherTargetDescriptor> descriptors) : IPublisherRegistry
{
    private readonly IReadOnlyList<PublisherTargetDescriptor> _targets =
        descriptors.ToList().AsReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<PublisherTargetDescriptor> Targets => _targets;

    /// <inheritdoc />
    public PublisherTargetDescriptor? Find(string targetId) =>
        _targets.FirstOrDefault(
            descriptor => string.Equals(descriptor.TargetId, targetId, StringComparison.Ordinal));
}

/// <summary>
/// Where a preview is aimed (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// Carries the target id only: anything a transport needs beyond "which target" arrives with
/// the adapter registration in the transport's own package, not through this contract. Kept as
/// a record rather than a bare string so that extension does not reshape the adapter contract.
/// </remarks>
/// <param name="TargetId">The registry id the preview is for.</param>
public sealed record PublisherTargetConfig(string TargetId);

/// <summary>
/// What an adapter says a send would do, before anything is sent (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// A preview is a pure function of the snapshot: it writes nothing remotely, leases no
/// credential and creates no row. The owner confirms its hash, and the publish re-computes
/// it from the still-stored rows - so a preview confirmed before an edit cannot authorize
/// the edited bytes.
/// </remarks>
/// <param name="TargetId">The registry id this preview is for.</param>
/// <param name="BodyHash">The digest of exactly the bytes the preview describes.</param>
/// <param name="MediaPlan">The pictures the send would carry, by media id, ordered.</param>
/// <param name="Warnings">Human-readable cautions (truncation, dropped pictures), never content.</param>
public sealed record PublishPreview(
    string TargetId,
    string BodyHash,
    IReadOnlyList<Guid> MediaPlan,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The digest the owner confirms: lowercase SHA-256 hex over the canonical
    /// <c>target|mode|asset|revision|bodyHash|mediaIds|warnings</c> document.
    /// </summary>
    public string Hash(string mode, Guid assetId, long contentRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);

        var canonical = string.Join(
            "\n",
            TargetId,
            mode,
            assetId.ToString("D"),
            contentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            BodyHash,
            string.Join(",", MediaPlan.Select(id => id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal)),
            string.Join("\n", Warnings.OrderBy(warning => warning, StringComparer.Ordinal)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// The authorized decision a run dispatches (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// Names the decision, never the content: the operation id the run belongs to, the approved
/// revision, the confirmed preview's hash, the mode and the idempotency key. The bytes travel
/// in the snapshot beside it, so a command read out of a log authorizes nothing by itself.
/// </remarks>
/// <param name="OperationId">The durable operation this send runs as.</param>
/// <param name="ApprovedRevision">The approved content revision to send.</param>
/// <param name="PreviewHash">The confirmed preview's digest.</param>
/// <param name="Mode">One of <see cref="PublishModes"/>.</param>
/// <param name="IdempotencyKey">The caller's key, unique within the scope for this contract.</param>
public sealed record PublishCommand(
    Guid OperationId,
    long ApprovedRevision,
    string PreviewHash,
    string Mode,
    string IdempotencyKey);

/// <summary>
/// What one adapter invocation answered (PUBLISHER-CONTRACT-002).
/// </summary>
/// <remarks>
/// The adapter reports; the service records. <c>unknown</c> here means the adapter itself
/// knows the write was ambiguous (a timeout after bytes left, a receipt it cannot parse):
/// the service stores it without a retry, exactly as it stores an ambiguous exception.
/// </remarks>
/// <param name="State">One of published, remote-draft, unknown or failed.</param>
/// <param name="RemoteId">The remote side's identifier, when it named one.</param>
/// <param name="RemoteUrl">The remote side's address, when it named one.</param>
/// <param name="ErrorCode">The redacted failure code, when the state is failed.</param>
public sealed record PublishResult(
    string State,
    string? RemoteId = null,
    string? RemoteUrl = null,
    string? ErrorCode = null);

/// <summary>
/// The stored receipt for one publication attempt, as owners read it (PUBLISHER-CONTRACT-002).
/// </summary>
/// <remarks>
/// Carries no body, no evidence and no credential: a receipt says what was sent where and
/// what the remote side answered, and a reader fetches the content through the review
/// package's own export, which re-checks every binding before serving a byte.
/// </remarks>
/// <param name="AttemptId">This attempt's identifier.</param>
/// <param name="OperationId">The durable operation that ran it.</param>
/// <param name="TargetId">The publisher target it was sent to.</param>
/// <param name="AssetId">The asset the sent revision belongs to.</param>
/// <param name="ContentRevision">The sent revision number.</param>
/// <param name="BodyHash">The digest of exactly the bytes that were sent.</param>
/// <param name="Mode">The mode that was authorized.</param>
/// <param name="State">One of <see cref="PublishStates"/>.</param>
/// <param name="RemoteId">The remote side's identifier, when it named one.</param>
/// <param name="RemoteUrl">The remote side's address, when it named one.</param>
/// <param name="ErrorCode">The redacted failure code, when the attempt ended without publishing.</param>
/// <param name="ReconcileHint">
/// Present and <c>manual</c> when the attempt is unknown and the target cannot reconcile:
/// the owner inspects the remote side by hand rather than reissuing the write.
/// </param>
public sealed record PublicationReceipt(
    Guid AttemptId,
    Guid OperationId,
    string TargetId,
    Guid AssetId,
    long ContentRevision,
    string BodyHash,
    string Mode,
    string State,
    string? RemoteId,
    string? RemoteUrl,
    string? ErrorCode,
    string? ReconcileHint);

/// <summary>
/// The transport every publish runs through, and the only one (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This package ships no implementation.</b> The interface is the contract the six future
/// transport packages implement; the only adapter-shaped object in this package's tests is a
/// test-local fake on a target id production never takes, so no test can shadow a shipped
/// transport the way the drafts suite guards against.
/// </para>
/// <para>
/// <b>An adapter converts generic content; it updates nothing.</b> It holds no store and
/// receives no store: the snapshot carries everything it may send, the lease carries the one
/// credential it may use, and neither outlives the call. It cannot update a milestone, a
/// bundle, an approval or an attempt, because it cannot reach any of them.
/// </para>
/// </remarks>
public interface IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    string TargetId { get; }

    /// <summary>What this target can do. Read by the contract, never inferred.</summary>
    PublisherCapabilities Describe();

    /// <summary>
    /// Says what a send would do, without sending anything: no remote write, no credential,
    /// no row.
    /// </summary>
    Task<PublishPreview> PrepareAsync(
        ApprovedAssetSnapshot snapshot,
        PublisherTargetConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the snapshot under the authorized command, using the leased credential for this
    /// call only. The lease is owned by the caller and is returned when the call returns.
    /// </summary>
    /// <exception cref="PublisherTimeoutException">
    /// The write may have happened before the answer was lost. The service stores unknown and
    /// never reissues.
    /// </exception>
    Task<PublishResult> PublishAsync(
        PublishCommand command,
        ApprovedAssetSnapshot snapshot,
        SecretLease credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an ambiguous receipt by remote id where the target permits it. Returns the
    /// receipt's current known state; a target that cannot reconcile reports unknown.
    /// </summary>
    Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The write may have reached the remote side before the answer was lost (PUBLISHER-CONTRACT-002).
/// </summary>
/// <remarks>
/// Raised by an adapter that cannot prove its bytes stayed home: a timeout after the request
/// left, a connection that broke while the response was arriving. The service records the
/// attempt unknown and fails the operation with <c>publication_unknown</c> without scheduling
/// another attempt, because a second dispatch would be a second public post.
/// </remarks>
public sealed class PublisherTimeoutException(string? remoteId = null)
    : Exception("The publish may have reached the remote side before the answer was lost.")
{
    /// <summary>The remote side's identifier, when the ambiguous answer named one.</summary>
    public string? RemoteId { get; } = remoteId;
}

/// <summary>
/// Leases the scope's publisher credential for one call (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// The narrow seam that keeps the vault out of a transport, on exactly the terms the LLM
/// adapters' key sources use: the adapter declares what it needs ("a credential, for a
/// scope, briefly") and the composition root supplies it, and only the composition root
/// knows the answer comes from a leased, sealed, scope-bound credential.
/// </para>
/// <para>
/// There is no "list the keys", no "peek" and no "verify": the only operation is acquire,
/// and the thing it returns must be returned. A publish leases once per run, immediately
/// around the adapter call, so a credential is never held across a queue wait.
/// </para>
/// </remarks>
public interface IPublisherCredentialSource
{
    /// <summary>
    /// Leases the scope's publisher credential.
    /// </summary>
    /// <param name="scopeId">The scope the send runs in, and the scope the credential must belong to.</param>
    /// <param name="cancellationToken">The call's own budget, honoured while the vault is opened.</param>
    /// <returns>A lease the caller must dispose before it returns.</returns>
    /// <exception cref="PublisherCredentialMissingException">No publisher credential is configured.</exception>
    Task<SecretLease> AcquireAsync(Guid scopeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the attempts table (PUBLISHER-CONTRACT-002).
/// </summary>
public interface IPublicationAttemptStore
{
    /// <summary>
    /// Inserts an attempt in state <c>prepared</c>. Returns false when the scope's key is
    /// already bound; nothing is written in that case.
    /// </summary>
    Task<bool> TryInsertAsync(PublicationAttempt attempt, CancellationToken cancellationToken = default);

    /// <summary>Reads one attempt inside a scope, or null when it is absent or owned elsewhere.</summary>
    Task<PublicationAttempt?> FindAsync(
        Guid scopeId,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the attempt a key is already bound to inside a scope, or null when unbound.
    /// </summary>
    Task<PublicationAttempt?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the attempt one operation runs, or null when the operation runs none.</summary>
    Task<PublicationAttempt?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims an attempt for sending: <c>prepared</c> becomes <c>sending</c> with the dispatch
    /// stamp. Returns false when this caller did not win the claim - the attempt already moved,
    /// and the caller re-reads rather than dispatching twice.
    /// </summary>
    Task<bool> TryMarkSendingAsync(
        Guid scopeId,
        Guid attemptId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an open attempt to one terminal state with its remote answer. Returns false when
    /// the attempt already reached a terminal state: a receipt that was read once reads the
    /// same the next time, and no completion overwrites one.
    /// </summary>
    Task<bool> TryCompleteAsync(
        Guid scopeId,
        Guid attemptId,
        string state,
        string? remoteId,
        string? remoteUrl,
        string? errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an unknown attempt to the resolved state a reconcile reported - published or
    /// remote-draft - with its remote answer. Returns false when the attempt is not unknown:
    /// only an ambiguous receipt can be resolved, and a resolved receipt is history that no
    /// reconcile rewrites.
    /// </summary>
    Task<bool> TryReconcileAsync(
        Guid scopeId,
        Guid attemptId,
        string state,
        string? remoteId,
        string? remoteUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What an owner asks for when they read a target preview (PUBLISHER-CONTRACT-001).
/// </summary>
/// <param name="TargetId">The registry id the preview is for.</param>
/// <param name="Mode">One of <see cref="PublishModes"/>.</param>
public sealed record PublishPreviewRequest(string TargetId, string Mode);

/// <summary>
/// One target preview, as the preview route answers it (PUBLISHER-CONTRACT-001).
/// </summary>
/// <param name="TargetId">The registry id this preview is for.</param>
/// <param name="Mode">The mode the preview was computed for.</param>
/// <param name="AssetId">The asset the preview describes.</param>
/// <param name="ContentRevision">The head revision the preview describes.</param>
/// <param name="BodyHash">The digest of exactly the bytes the preview describes.</param>
/// <param name="MediaPlan">The pictures a send would carry, by media id, ordered.</param>
/// <param name="Warnings">Human-readable cautions, never content.</param>
/// <param name="PreviewHash">The digest the publish request must confirm.</param>
public sealed record PublishPreviewResponse(
    string TargetId,
    string Mode,
    Guid AssetId,
    long ContentRevision,
    string BodyHash,
    IReadOnlyList<Guid> MediaPlan,
    IReadOnlyList<string> Warnings,
    string PreviewHash);

/// <summary>
/// What an owner asks for when they publish approved content (PUBLISHER-CONTRACT-001).
/// </summary>
/// <remarks>
/// Approval and confirmation travel separately on purpose: <see cref="ApprovalId"/> names the
/// review package's decision that the content may leave, while <see cref="Confirm"/> is this
/// contract's decision that it may leave toward one target in one mode. A publish that
/// treated the first as the second would send content the owner never agreed to send anywhere.
/// </remarks>
/// <param name="TargetId">The registry id to send to.</param>
/// <param name="Mode">One of <see cref="PublishModes"/>.</param>
/// <param name="PreviewHash">The confirmed preview's digest.</param>
/// <param name="ApprovalId">The live approval the request was read against.</param>
/// <param name="IdempotencyKey">The caller's key, unique within the scope for this contract.</param>
/// <param name="Confirm">Must be true: the separate, explicit publication consent.</param>
public sealed record PublishRequest(
    string TargetId,
    string Mode,
    string PreviewHash,
    Guid ApprovalId,
    string IdempotencyKey,
    bool Confirm);

/// <summary>
/// One accepted publish, as the publish route answers it (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <param name="OperationId">The durable operation the owner watches.</param>
/// <param name="AttemptId">The attempt that carries the receipt.</param>
/// <param name="Created">False when an identical request had already created this publish.</param>
public sealed record PublishAccepted(Guid OperationId, Guid AttemptId, bool Created);

/// <summary>
/// The reviewed-publication use cases the API layer calls (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A preview authorizes nothing; a publish sends only what the stored rows still
/// describe.</b> The request validates the live approval, the confirmed preview and the
/// credential metadata before a job exists, so a request that could never send is refused
/// here rather than as a failed job. The run re-checks every binding - the approval, the
/// body, the evidence head, the credential - leases the credential for the one call, and
/// records the receipt; a binding that moved ends the run without a dispatch.
/// </para>
/// <para>
/// <b>Nothing here logs prose or secrets.</b> The logger receives identifiers, codes and
/// counts. Approved bodies travel from a stored revision to an adapter; they never appear in
/// a log line, in an exception message or in an audit detail, and a leased credential never
/// reaches any of those either.
/// </para>
/// </remarks>
public interface IPublisherService
{
    /// <summary>
    /// Computes what a send would do, without sending anything.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="PublisherUnknownTargetException">No target with that id is registered (422).</exception>
    /// <exception cref="PublisherTargetExportOnlyException">The target is export-only (422).</exception>
    /// <exception cref="PublisherModeUnsupportedException">The mode is not one the target serves (422).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing live authorizes a send (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    Task<PublishPreviewResponse> PreviewAsync(
        OwnerScope owner,
        Guid assetId,
        PublishPreviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes one reviewed send and queues its durable run. An identical resubmission
    /// returns the original receipt; the same key over different work is a conflict.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="PublisherUnknownTargetException">No target with that id is registered (422).</exception>
    /// <exception cref="PublisherTargetExportOnlyException">The target is export-only (422).</exception>
    /// <exception cref="PublisherModeUnsupportedException">The mode is not one the target serves (422).</exception>
    /// <exception cref="PublisherPreviewStaleException">The confirmed preview moved (409).</exception>
    /// <exception cref="PublisherConfirmationRequiredException">No explicit confirmation travelled (422).</exception>
    /// <exception cref="PublisherCredentialMissingException">No publisher credential is configured (422).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing live authorizes a send (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="Operations.OperationIdempotencyConflictException">
    /// The key is already bound to different work (409).
    /// </exception>
    Task<PublishAccepted> PublishAsync(
        OwnerScope owner,
        Guid assetId,
        PublishRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one stored receipt inside a scope.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The attempt is absent or in another scope (404).</exception>
    Task<PublicationReceipt> ReadReceiptAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the target to resolve an ambiguous receipt where it permits it.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The attempt is absent or in another scope (404).</exception>
    /// <exception cref="PublisherReconcileUnavailableException">The target cannot reconcile (422).</exception>
    Task<PublicationReceipt> ReconcileAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one queued publish to its receipt: re-checks every binding, leases the credential
    /// for the one call, dispatches once, and records the receipt. Never dispatches twice for
    /// one attempt and never reissues an ambiguous write.
    /// </summary>
    /// <exception cref="Operations.OperationPermanentException">
    /// The run ended without publishing: stale bindings, a missing credential, a refused or an
    /// ambiguous write. The attempt row carries the redacted code; the operation carries it too.
    /// </exception>
    Task<Operations.OperationResult> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the project an asset belongs to inside a scope, for the Hosted per-project
    /// authorization boundary. A read, not a decision: it answers which project, never whether
    /// the caller may act on it.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    Task<Guid> ResolveProjectAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the project an attempt's asset belongs to inside a scope, for the Hosted
    /// per-project authorization boundary. A read, not a decision.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The attempt is absent or in another scope (404).</exception>
    Task<Guid> ResolveReceiptProjectAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the asset an attempt was accepted against inside a scope, for the Hosted
    /// per-project authorization boundary. A read, not a decision.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The attempt is absent or in another scope (404).</exception>
    Task<Guid> ResolveReceiptAssetAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the asset's head carries a live approval that still describes the stored state.
    /// Answered, never thrown, for the Hosted publish gate: a head without one is refused by
    /// the contract itself with the same 409 the gate would raise.
    /// </summary>
    Task<bool> IsApprovedHeadAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);
}
