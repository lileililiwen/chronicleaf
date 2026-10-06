using System.Globalization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Publishing;

/// <summary>
/// The reviewed-publication use cases, over the delivered asset, approval, evidence, vault and
/// queue contracts (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The request authorizes; the run re-checks; the receipt remembers.</b> The request validates
/// the live approval, the confirmed preview and the credential metadata before a job exists, and
/// freezes every binding it was accepted against into the attempt row. The run claims the attempt
/// before any adapter is invoked, re-validates the frozen bindings against the still-stored rows,
/// leases the credential for the one call, and records the receipt. A binding that moved ends the
/// run without a dispatch, and an ambiguous answer ends it unknown without a retry.
/// </para>
/// <para>
/// <b>Nothing here logs prose or secrets.</b> The logger receives identifiers, codes and counts.
/// Approved bodies travel from a stored revision to an adapter; they never appear in a log line,
/// in an exception message or in an audit detail, and a leased credential never reaches any of
/// those either - the lease is disposed before the call returns.
/// </para>
/// </remarks>
public sealed class PublisherService(
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IEvidenceStore evidence,
    IMediaStore media,
    IEnumerable<IPublisherAdapter> adapters,
    IPublisherRegistry registry,
    IPublicationAttemptStore attempts,
    IPublisherCredentialSource credentials,
    ICredentialVaultService vault,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<PublisherService> logger) : IPublisherService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to:
    /// an asset, so the delivered supersede check reads the asset's own revision and turns an
    /// asset edited mid-run into a superseded operation rather than a commit against bytes
    /// somebody else has since changed.
    /// </summary>
    public const string PublishOperationKind = "publication.publish";

    /// <summary>The credential slot a publish leases, per call, through the vault.</summary>
    public const string CredentialProvider = "publisher";

    /// <summary>The audit action recorded when a reviewed publish is authorized.</summary>
    public const string PublishRequestedAction = "publisher.publish.requested";

    /// <summary>The audit action recorded when a publish run committed a receipt.</summary>
    public const string PublishCommittedAction = "publisher.publish.committed";

    /// <summary>The audit action recorded when a publish run ended without sending.</summary>
    public const string PublishRefusedAction = "publisher.publish.refused";

    /// <inheritdoc />
    public async Task<PublishPreviewResponse> PreviewAsync(
        OwnerScope owner,
        Guid assetId,
        PublishPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "A preview names its asset, so name one.");
        }

        var descriptor = RequireTarget(request.TargetId);
        RequireMode(descriptor, request.Mode);

        var resolved = await ResolveLiveAsync(owner, assetId, cancellationToken).ConfigureAwait(false);
        var adapter = RequireAdapter(descriptor);
        var snapshot = await SnapshotForAsync(owner, resolved, cancellationToken).ConfigureAwait(false);
        var preview = await adapter
            .PrepareAsync(snapshot, new PublisherTargetConfig(descriptor.TargetId), cancellationToken)
            .ConfigureAwait(false);

        // The preview is bound to the exact bytes it describes: a preview that answered for other
        // bytes is not a preview of this revision, and confirming its hash must not authorize them.
        if (!string.Equals(preview.TargetId, descriptor.TargetId, StringComparison.Ordinal) ||
            !string.Equals(preview.BodyHash, snapshot.BodyHash, StringComparison.Ordinal))
        {
            throw new PublisherPreviewStaleException();
        }

        return new PublishPreviewResponse(
            descriptor.TargetId,
            request.Mode,
            resolved.Asset.Id,
            resolved.Revision.Revision,
            resolved.Revision.BodyHash,
            preview.MediaPlan,
            preview.Warnings,
            preview.Hash(request.Mode, resolved.Asset.Id, resolved.Revision.Revision));
    }

    /// <inheritdoc />
    public async Task<PublishAccepted> PublishAsync(
        OwnerScope owner,
        Guid assetId,
        PublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "A publish names its asset, so name one.");
        }

        if (!request.Confirm)
        {
            // Approval is not publication consent: the owner approved that the content may leave
            // as an export, and this flag is the separate decision that it may leave toward one
            // target in one mode. Refused before anything is read beyond the flag, because a
            // request without consent authorizes no lookup.
            throw new PublisherConfirmationRequiredException();
        }

        if (request.ApprovalId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "approvalId",
                "A publish names the approval it was read against, so name one.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > PublicationAttempt.Limits.MaxIdempotencyKeyLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key must be 1 to {PublicationAttempt.Limits.MaxIdempotencyKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.PreviewHash))
        {
            throw new ValidationFailedException(
                "previewHash",
                "A publish confirms the preview it was read against, so name its hash.");
        }

        var descriptor = RequireTarget(request.TargetId);
        RequireMode(descriptor, request.Mode);

        var resolved = await ResolveLiveAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        // The approval the owner read must be the live one: confirming a preview against a
        // decision that has since been withdrawn or replaced authorizes nothing.
        if (resolved.Approval.Id != request.ApprovalId)
        {
            throw new ApprovalStaleException("approval");
        }

        var adapter = RequireAdapter(descriptor);
        var snapshot = await SnapshotForAsync(owner, resolved, cancellationToken).ConfigureAwait(false);
        var preview = await adapter
            .PrepareAsync(snapshot, new PublisherTargetConfig(descriptor.TargetId), cancellationToken)
            .ConfigureAwait(false);

        // The confirmed hash must be the preview the stored rows still describe. An edit, a
        // reselect or a re-approval after the owner read the preview changes what a send would
        // do, so the stale confirmation is refused before a job exists rather than as a failed
        // run the owner has to go and read.
        var freshHash = preview.Hash(request.Mode, resolved.Asset.Id, resolved.Revision.Revision);
        if (!string.Equals(preview.BodyHash, snapshot.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(freshHash, request.PreviewHash, StringComparison.Ordinal))
        {
            throw new PublisherPreviewStaleException();
        }

        // The credential metadata is read before a job exists, for the reason the message gives:
        // a request that could never send is refused here. The bytes are leased later, once per
        // run, immediately around the adapter call - never here, and never across a queue wait.
        var status = await vault
            .DescribeAsync(owner.ScopeId, CredentialProvider, cancellationToken)
            .ConfigureAwait(false);

        if (!status.Configured)
        {
            throw new PublisherCredentialMissingException();
        }

        var key = request.IdempotencyKey.Trim();
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                PublishOperationKind,
                resolved.Asset.Id,
                resolved.Revision.Revision,
                key),
            cancellationToken).ConfigureAwait(false);

        // The attempt row is written after the job exists, because it names the job. A replay
        // under the same key gets the original job back and its own insert changes no row, so
        // one press of the button can never grow two attempts.
        var now = clock.GetUtcNow();
        var attempt = new PublicationAttempt(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            descriptor.TargetId,
            resolved.Asset.Id,
            resolved.Revision.Revision,
            resolved.Revision.BodyHash,
            resolved.Revision.EvidenceRevision,
            request.PreviewHash,
            request.Mode,
            resolved.Approval.Id,
            key,
            PublishStates.Prepared,
            remoteId: null,
            remoteUrl: null,
            errorCode: null,
            dispatchedAt: null,
            completedAt: null,
            now,
            now);

        var created = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var inserted = await attempts.TryInsertAsync(attempt, token).ConfigureAwait(false);

                if (inserted)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        PublishRequestedAction,
                        resolved.Asset.Id,
                        resolved.Revision.Revision,
                        $"target={descriptor.TargetId};"
                        + $"mode={request.Mode};"
                        + $"revision={resolved.Revision.Revision.ToString(CultureInfo.InvariantCulture)};"));
                }

                return inserted;
            },
            cancellationToken).ConfigureAwait(false);

        if (!created)
        {
            // The key is already bound. An identical request gets the original receipt back and
            // no second row; a different one is a conflict and still creates nothing. The
            // comparison is over the full frozen binding - not just the operation fingerprint,
            // which only covers the asset and the revision - so the same key over another mode,
            // another preview or another approval is a 409 rather than a second send.
            var existing = await attempts
                .FindByIdempotencyKeyAsync(owner.ScopeId, key, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The idempotency key was not taken, but no attempt for it can be read.");

            if (!SameBinding(existing, attempt))
            {
                throw new OperationIdempotencyConflictException(key, existing.IdempotencyKey);
            }

            logger.LogInformation(
                "Publish replay for asset {AssetId} returned attempt {AttemptId}.",
                assetId,
                existing.Id);

            return new PublishAccepted(existing.OperationId, existing.Id, Created: false);
        }

        logger.LogInformation(
            "Publish requested for asset {AssetId} to target {TargetId} as operation {OperationId}.",
            assetId,
            descriptor.TargetId,
            accepted.Operation.Id);

        return new PublishAccepted(accepted.Operation.Id, attempt.Id, accepted.Created);
    }

    /// <inheritdoc />
    public async Task<PublicationReceipt> ReadReceiptAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (attemptId == Guid.Empty)
        {
            throw new ValidationFailedException("attemptId", "A receipt names its attempt, so name one.");
        }

        var attempt = await attempts
            .FindAsync(owner.ScopeId, attemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("publication attempt");

        return ToReceipt(attempt);
    }

    /// <inheritdoc />
    public async Task<PublicationReceipt> ReconcileAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (attemptId == Guid.Empty)
        {
            throw new ValidationFailedException("attemptId", "A reconciliation names its attempt, so name one.");
        }

        var attempt = await attempts
            .FindAsync(owner.ScopeId, attemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("publication attempt");

        // Only an ambiguous receipt needs resolving. A resolved receipt is history, and asking
        // the remote side about it again would be a read the owner did not need, so it is
        // answered from the row.
        if (!string.Equals(attempt.State, PublishStates.Unknown, StringComparison.Ordinal))
        {
            return ToReceipt(attempt);
        }

        var descriptor = TryResolveDescriptor(attempt.TargetId)
            ?? throw new PublisherUnknownTargetException(attempt.TargetId);

        if (!descriptor.SupportsReconcile)
        {
            // No reliable reconcile keeps unknown: the receipt stays, the write is not
            // reissued, and the owner inspects the remote side by hand.
            throw new PublisherReconcileUnavailableException(attempt.TargetId);
        }

        var adapter = RequireAdapter(descriptor);
        var answer = await adapter
            .ReconcileAsync(ToReceipt(attempt), cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(answer.State, PublishStates.Published, StringComparison.Ordinal) ||
            string.Equals(answer.State, PublishStates.RemoteDraft, StringComparison.Ordinal))
        {
            // The receipt moves; the operation row keeps its failure. Reconciling rewrites no
            // history: the operation records that the run ended ambiguously, and the attempt
            // records what the remote side is now known to hold.
            var now = clock.GetUtcNow();
            _ = await attempts.TryReconcileAsync(
                owner.ScopeId,
                attempt.Id,
                answer.State,
                answer.RemoteId,
                answer.RemoteUrl,
                now,
                cancellationToken).ConfigureAwait(false);

            var reread = await attempts
                .FindAsync(owner.ScopeId, attempt.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? attempt;

            logger.LogInformation(
                "Publication attempt {AttemptId} reconciled to {State}.",
                attempt.Id,
                reread.State);

            return ToReceipt(reread);
        }

        return ToReceipt(attempt);
    }

    /// <inheritdoc />
    public async Task<OperationResult> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var attempt = await attempts
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException();

        // A rerun of a finished attempt returns its receipt without touching an adapter: the
        // queue may deliver the same operation twice, but only one result is ever committed.
        if (!PublishStates.IsOpen(attempt.State))
        {
            return ResultFor(attempt);
        }

        var now = clock.GetUtcNow();
        var claimed = await attempts
            .TryMarkSendingAsync(owner.ScopeId, attempt.Id, now, cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            // Another run holds or finished the claim. A finished attempt is answered from its
            // row; a run that died between the claim and the receipt left an ambiguous write,
            // which becomes unknown rather than a second dispatch: from inside this process a
            // crash after the upstream commit is indistinguishable from a timeout, and both
            // must not be reissued.
            var current = await attempts
                .FindAsync(owner.ScopeId, attempt.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? attempt;

            if (!PublishStates.IsOpen(current.State))
            {
                return ResultFor(current);
            }

            await FailAsync(
                owner,
                current,
                PublisherError.Unknown,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            throw new OperationPermanentException(PublisherError.Unknown);
        }

        var descriptor = TryResolveDescriptor(attempt.TargetId);

        if (descriptor is null)
        {
            await FailAsync(
                owner,
                attempt,
                PublisherError.UnknownTarget,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            throw new OperationPermanentException(PublisherError.UnknownTarget);
        }

        // The frozen bindings are re-checked against the still-stored rows before any adapter
        // is invoked: an edit, a withdrawal, a re-approval or an evidence append after the
        // request ends the run without a dispatch, and the receipt keeps the frozen record of
        // what was authorized rather than being overwritten by the moved state.
        var live = await TryResolveLiveAsync(owner, attempt.AssetId, cancellationToken).ConfigureAwait(false);

        if (live is null ||
            live.Approval.Id != attempt.ApprovalId ||
            live.Revision.Revision != attempt.ContentRevision ||
            !string.Equals(live.Revision.BodyHash, attempt.BodyHash, StringComparison.Ordinal) ||
            live.Revision.EvidenceRevision != attempt.EvidenceRevision)
        {
            throw await FailWithApprovalStaleAsync(owner, attempt, cancellationToken).ConfigureAwait(false);
        }

        var adapter = adapters.FirstOrDefault(
            candidate => string.Equals(candidate.TargetId, descriptor.TargetId, StringComparison.Ordinal));

        if (adapter is null)
        {
            await FailAsync(
                owner,
                attempt,
                PublisherError.UnknownTarget,
                clock.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            throw new OperationPermanentException(PublisherError.UnknownTarget);
        }

        var snapshot = await SnapshotForAsync(owner, live, cancellationToken).ConfigureAwait(false);

        // The preview is recomputed from the still-stored rows for the same reason the approval
        // is: a confirmation for other bytes authorizes nothing, and the run - not the request -
        // is the last point at which that can be known.
        var preview = await adapter
            .PrepareAsync(snapshot, new PublisherTargetConfig(descriptor.TargetId), cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(preview.BodyHash, attempt.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(
                preview.Hash(attempt.Mode, attempt.AssetId, attempt.ContentRevision),
                attempt.PreviewHash,
                StringComparison.Ordinal))
        {
            throw await FailWithPreviewStaleAsync(owner, attempt, cancellationToken).ConfigureAwait(false);
        }

        // The credential is leased for this call only, immediately around the adapter call, and
        // returned before the run returns. A reference handed to the run through the attempt row
        // would outlive the call; instead the run resolves the scope's credential fresh, checks
        // it belongs to this scope, and holds the bytes for no longer than the send.
        SecretLease lease;
        try
        {
            lease = await credentials.AcquireAsync(owner.ScopeId, cancellationToken).ConfigureAwait(false);
        }
        catch (PublisherCredentialMissingException)
        {
            throw await FailWithCredentialMissingAsync(owner, attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (Secrets.CredentialMissingException)
        {
            throw await FailWithCredentialMissingAsync(owner, attempt, cancellationToken).ConfigureAwait(false);
        }

        using (lease)
        {
            if (lease.ScopeId != owner.ScopeId)
            {
                throw await FailWithCredentialMissingAsync(owner, attempt, cancellationToken).ConfigureAwait(false);
            }

            PublishResult result;
            try
            {
                result = await adapter.PublishAsync(
                    new PublishCommand(
                        attempt.OperationId,
                        attempt.ContentRevision,
                        attempt.PreviewHash,
                        attempt.Mode,
                        attempt.IdempotencyKey),
                    snapshot,
                    lease,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PublisherTimeoutException timedOut)
            {
                // The bytes may have left: unknown, retained, never reissued.
                await CompleteAsync(
                    owner,
                    attempt,
                    PublishStates.Unknown,
                    timedOut.RemoteId,
                    remoteUrl: null,
                    PublisherError.Unknown,
                    cancellationToken).ConfigureAwait(false);

                throw new OperationPermanentException(PublisherError.Unknown, timedOut);
            }
            catch (OperationFailedException)
            {
                throw;
            }
            catch (Exception unclassified) when (unclassified is not OperationCanceledException)
            {
                // A crash after the upstream commit is indistinguishable from an ambiguous
                // timeout from inside this process, so it becomes unknown rather than a retry:
                // retrying a possibly-completed remote write would duplicate a public post,
                // which is the failure the lack of a retry exists to prevent.
                await CompleteAsync(
                    owner,
                    attempt,
                    PublishStates.Unknown,
                    remoteId: null,
                    remoteUrl: null,
                    PublisherError.Unknown,
                    cancellationToken).ConfigureAwait(false);

                throw new OperationPermanentException(PublisherError.Unknown, unclassified);
            }

            if (string.Equals(result.State, PublishStates.Published, StringComparison.Ordinal) ||
                string.Equals(result.State, PublishStates.RemoteDraft, StringComparison.Ordinal))
            {
                await CompleteAsync(
                    owner,
                    attempt,
                    result.State,
                    result.RemoteId,
                    result.RemoteUrl,
                    errorCode: null,
                    cancellationToken).ConfigureAwait(false);

                await AuditTerminalAsync(
                    owner, attempt, PublishCommittedAction, result.State, cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    "Publication attempt {AttemptId} committed as {State}.",
                    attempt.Id,
                    result.State);

                var reread = await attempts
                    .FindAsync(owner.ScopeId, attempt.Id, cancellationToken)
                    .ConfigureAwait(false)
                    ?? attempt;

                return ResultFor(reread);
            }

            if (string.Equals(result.State, PublishStates.Unknown, StringComparison.Ordinal))
            {
                await CompleteAsync(
                    owner,
                    attempt,
                    PublishStates.Unknown,
                    result.RemoteId,
                    result.RemoteUrl,
                    PublisherError.Unknown,
                    cancellationToken).ConfigureAwait(false);

                throw new OperationPermanentException(PublisherError.Unknown);
            }

            var code = string.IsNullOrWhiteSpace(result.ErrorCode)
                ? OperationErrorCodes.Permanent
                : result.ErrorCode;

            await CompleteAsync(
                owner,
                attempt,
                PublishStates.Failed,
                result.RemoteId,
                result.RemoteUrl,
                code,
                cancellationToken).ConfigureAwait(false);

            await AuditTerminalAsync(
                owner, attempt, PublishRefusedAction, code, cancellationToken).ConfigureAwait(false);

            throw new OperationPermanentException(code);
        }
    }

    /// <inheritdoc />
    public async Task<Guid> ResolveProjectAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var asset = await assets
            .FindAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        return asset.ProjectId;
    }

    /// <inheritdoc />
    public async Task<Guid> ResolveReceiptProjectAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var attempt = await attempts
            .FindAsync(owner.ScopeId, attemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("publication attempt");

        var asset = await assets
            .FindAsync(owner.ScopeId, attempt.AssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("publication attempt");

        return asset.ProjectId;
    }

    /// <inheritdoc />
    public async Task<Guid> ResolveReceiptAssetAsync(
        OwnerScope owner,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var attempt = await attempts
            .FindAsync(owner.ScopeId, attemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("publication attempt");

        return attempt.AssetId;
    }

    /// <inheritdoc />
    public async Task<bool> IsApprovedHeadAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var live = await TryResolveLiveAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        return live is not null;
    }

    // ==============================================================================================
    // Helpers.
    // ==============================================================================================

    /// <summary>
    /// The live approval and the stored rows it still describes, or the refusal the request and
    /// the preview share.
    /// </summary>
    private sealed record LivePublication(
        ContentAsset Asset,
        ContentRevision Revision,
        ContentApproval Approval,
        EvidenceBundle Head);

    private PublisherTargetDescriptor RequireTarget(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ValidationFailedException(
                "targetId",
                "A publish names the publisher target it sends to, so name one.");
        }

        var trimmed = targetId.Trim();
        var registered = registry.Find(trimmed);

        if ((registered is not null && registered.ExportOnly) || PublisherTargetIds.IsExportOnly(trimmed))
        {
            // The export-only classification lives in the registry rather than in the two
            // export packages' refusal: this is the contract's own decision, with its own
            // code, answered before any adapter is resolved and before any job exists. It
            // wins over any adapter declaration, so a transport claiming an export-only id
            // is still refused rather than served.
            throw new PublisherTargetExportOnlyException(trimmed);
        }

        return TryResolveDescriptor(trimmed)
            ?? throw new PublisherUnknownTargetException(trimmed);
    }

    /// <summary>
    /// The classification for one target id, or null when nothing registered it.
    /// </summary>
    /// <remarks>
    /// The registry's descriptors come first; a registered adapter's own description covers
    /// its slot when no descriptor was contributed beside it, so the contract serves exactly
    /// what each declaration names without a second registration to drift from it.
    /// </remarks>
    private PublisherTargetDescriptor? TryResolveDescriptor(string targetId)
    {
        var registered = registry.Find(targetId);

        if (registered is not null)
        {
            return registered;
        }

        var adapter = adapters.FirstOrDefault(
            candidate => string.Equals(candidate.TargetId, targetId, StringComparison.Ordinal));

        if (adapter is null)
        {
            return null;
        }

        var capabilities = adapter.Describe();

        if (!string.Equals(capabilities.TargetId, adapter.TargetId, StringComparison.Ordinal))
        {
            // An adapter must describe its own slot: a description for another target is a
            // wiring mistake, and serving it would send one target's material under another
            // target's capabilities.
            return null;
        }

        return new PublisherTargetDescriptor(
            adapter.TargetId,
            capabilities.SupportsDraft,
            capabilities.SupportsPublish,
            capabilities.SupportsReconcile,
            ExportOnly: false);
    }

    private static void RequireMode(PublisherTargetDescriptor descriptor, string? mode)
    {
        if (mode is null || !PublishModes.IsKnown(mode))
        {
            throw new ValidationFailedException(
                "mode",
                "A publish names its mode, draft or publish, so name one.");
        }

        if (!descriptor.Serves(mode))
        {
            throw new PublisherModeUnsupportedException(descriptor.TargetId, mode);
        }
    }

    private IPublisherAdapter RequireAdapter(PublisherTargetDescriptor descriptor) =>
        adapters.FirstOrDefault(
            candidate => string.Equals(candidate.TargetId, descriptor.TargetId, StringComparison.Ordinal))
        ?? throw new PublisherUnknownTargetException(descriptor.TargetId);

    private async Task<LivePublication> ResolveLiveAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await assets
            .FindAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        var approvals = await reviews
            .ListForAssetAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false);

        var live = approvals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            if (approvals.Count == 0)
            {
                // Merely generating a draft authorizes no send: there is no decision behind
                // this request at all.
                throw new ApprovalRequiredException();
            }

            throw new ApprovalStaleException("invalidated");
        }

        if (live.ContentRevision != asset.CurrentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        var stored = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, live.ContentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ApprovalStaleException("body");

        if (!string.Equals(ContentRevision.Hash(stored.BodyMarkdown), live.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(stored.BodyHash, live.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("body");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null || head.Id != stored.EvidenceBundleId || head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        if (!string.Equals(
                ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("media");
        }

        return new LivePublication(asset, stored, live, head);
    }

    /// <summary>
    /// The run-time re-check: the same bindings, but a moved binding is an answer rather than
    /// an exception, because the run records the failure on the attempt it already owns.
    /// </summary>
    private async Task<LivePublication?> TryResolveLiveAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ResolveLiveAsync(owner, assetId, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            return null;
        }
    }

    /// <summary>
    /// The immutable bytes the adapter may send: the stored revision's exact text with the
    /// approval-covered pictures it names, carrying the digest the run re-verified rather than
    /// trusted (PUBLISHER-CONTRACT-001).
    /// </summary>
    /// <remarks>
    /// The snapshot is the content package's own <see cref="ApprovedAssetSnapshot"/>, not a
    /// second shape: the publisher converts generic content and must not redefine what approved
    /// bytes are. A picture whose bytes are gone ends the read with <c>media_unavailable</c>
    /// rather than a silently picture-less send, on exactly the terms the HTML export holds its
    /// own artifact to - and unlike that export, no media type is refused here, because what a
    /// remote side accepts is its adapter's decision rather than this contract's.
    /// </remarks>
    private async Task<ApprovedAssetSnapshot> SnapshotForAsync(
        OwnerScope owner,
        LivePublication live,
        CancellationToken cancellationToken)
    {
        var summary = GroundedSummary.FromCanonicalJson(live.Revision.SummaryJson);

        var pictures = new List<ApprovedExportPicture>();
        foreach (var (mediaId, caption) in SelectedPictures(live.Head))
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaContent content;
            try
            {
                content = await media.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaNotFoundException)
            {
                throw new MediaUnavailableException();
            }

            await using (content.ConfigureAwait(false))
            {
                var bytes = await ReadBoundedAsync(
                    content.Content, HtmlExportAdapter.MaxArchiveBytes, cancellationToken).ConfigureAwait(false);

                pictures.Add(new ApprovedExportPicture(mediaId, caption, content.Ref.MediaType, bytes));
            }
        }

        return new ApprovedAssetSnapshot(
            live.Asset.Id,
            live.Revision.Revision,
            summary?.MilestoneTitle ?? ContentAsset.Label(live.Asset.Id),
            live.Revision.BodyMarkdown,
            live.Revision.BodyHash,
            live.Revision.EvidenceRevision,
            pictures.OrderBy(picture => picture.MediaId).ToList());
    }

    /// <summary>
    /// Reads a media stream up to the portable-artifact bound. A picture larger than any portable
    /// artifact could ever be is refused before its bytes are all held, rather than after.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        Stream content,
        long limit,
        CancellationToken cancellationToken)
    {
        using var buffered = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new PayloadTooLargeException(limit);
            }

            buffered.Write(chunk, 0, read);
        }

        return buffered.ToArray();
    }

    private static IReadOnlyList<(Guid MediaId, string Caption)> SelectedPictures(EvidenceBundle head)
    {
        ArgumentNullException.ThrowIfNull(head);

        return [.. head.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                && item.MediaId is not null
                && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
            .Select(item => (item.MediaId!.Value, item.Text))];
    }

    /// <summary>
    /// Whether a stored attempt already authorizes exactly the work a replay asks for: the same
    /// target, asset, revision, body, preview, mode and approval. Anything else under the same
    /// key is a conflict, never a second send.
    /// </summary>
    private static bool SameBinding(PublicationAttempt existing, PublicationAttempt asked) =>
        string.Equals(existing.TargetId, asked.TargetId, StringComparison.Ordinal) &&
        existing.AssetId == asked.AssetId &&
        existing.ContentRevision == asked.ContentRevision &&
        string.Equals(existing.BodyHash, asked.BodyHash, StringComparison.Ordinal) &&
        existing.EvidenceRevision == asked.EvidenceRevision &&
        string.Equals(existing.PreviewHash, asked.PreviewHash, StringComparison.Ordinal) &&
        string.Equals(existing.Mode, asked.Mode, StringComparison.Ordinal) &&
        existing.ApprovalId == asked.ApprovalId;

    private PublicationReceipt ToReceipt(PublicationAttempt attempt)
    {
        var descriptor = TryResolveDescriptor(attempt.TargetId);
        var hint = string.Equals(attempt.State, PublishStates.Unknown, StringComparison.Ordinal) &&
            (descriptor is null || !descriptor.SupportsReconcile)
            ? "manual"
            : null;

        return new PublicationReceipt(
            attempt.Id,
            attempt.OperationId,
            attempt.TargetId,
            attempt.AssetId,
            attempt.ContentRevision,
            attempt.BodyHash,
            attempt.Mode,
            attempt.State,
            attempt.RemoteId,
            attempt.RemoteUrl,
            attempt.ErrorCode,
            hint);
    }

    private static OperationResult ResultFor(PublicationAttempt attempt) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"publication:{attempt.Id:D}:{attempt.State}"));

    private async Task CompleteAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        string state,
        string? remoteId,
        string? remoteUrl,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        _ = await attempts.TryCompleteAsync(
            owner.ScopeId,
            attempt.Id,
            state,
            remoteId,
            remoteUrl,
            errorCode,
            now,
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Publication attempt {AttemptId} recorded as {State}.",
            attempt.Id,
            state);
    }

    private async Task AuditTerminalAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        string action,
        string outcome,
        CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteAsync(
            token =>
            {
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    action,
                    attempt.AssetId,
                    attempt.ContentRevision,
                    $"target={attempt.TargetId};"
                    + $"mode={attempt.Mode};"
                    + $"state={outcome};"));

                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FailAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = await attempts.TryCompleteAsync(
            owner.ScopeId,
            attempt.Id,
            PublishStates.Failed,
            remoteId: null,
            remoteUrl: null,
            errorCode,
            now,
            cancellationToken).ConfigureAwait(false);

        await AuditTerminalAsync(
            owner, attempt, PublishRefusedAction, errorCode, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Publication attempt {AttemptId} refused with {ErrorCode} before any dispatch.",
            attempt.Id,
            errorCode);
    }

    /// <summary>
    /// Records the failure on an open attempt and answers the operation's own permanent failure.
    /// The throw is the method's answer: call sites <c>throw await Fail...</c> so the failure
    /// code reaches the operation row through the worker's own commit.
    /// </summary>
    private async Task<OperationPermanentException> FailWithApprovalStaleAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        CancellationToken cancellationToken)
    {
        await FailAsync(owner, attempt, Content.ReviewError.ApprovalStale, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        return new OperationPermanentException(Content.ReviewError.ApprovalStale);
    }

    private async Task<OperationPermanentException> FailWithPreviewStaleAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        CancellationToken cancellationToken)
    {
        await FailAsync(owner, attempt, PublisherError.PreviewStale, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        return new OperationPermanentException(PublisherError.PreviewStale);
    }

    private async Task<OperationPermanentException> FailWithCredentialMissingAsync(
        OwnerScope owner,
        PublicationAttempt attempt,
        CancellationToken cancellationToken)
    {
        // The run-time code is the vault's own job code, not the request-time 422: the owner
        // deleted the credential between the request and the run, and the operation records what
        // the queue already knows how to name.
        await FailAsync(
                owner,
                attempt,
                Secrets.CredentialVaultError.Missing,
                clock.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        return new OperationPermanentException(Secrets.CredentialVaultError.Missing);
    }
}
