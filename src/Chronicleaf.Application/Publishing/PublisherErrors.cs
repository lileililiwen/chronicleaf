using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Publishing;

/// <summary>
/// The codes this package adds to the delivered error envelope (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status
/// mapping is unchanged; this file is vocabulary, not a second envelope. Every message is authored
/// here in fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope. No message names a claim, an evidence text, a URL, a token or a host path.
/// </para>
/// <para>
/// <c>publisher_unsupported</c> is deliberately absent here. That string belongs to the Medium
/// manual-import and LinkedIn copy packages, whose publish routes refuse unconditionally because
/// no transport exists behind them. This contract classifies those two targets as export-only in
/// its registry and refuses them with <see cref="TargetExportOnly"/> instead, so the two refusals
/// stay distinguishable: one says "this product never publishes there", the other says "this
/// contract holds no transport for a target it knows is export-only".
/// </para>
/// </remarks>
public static class PublisherError
{
    /// <summary>
    /// The target id names no registered publisher target (422). The remedy is a target the
    /// registry knows, not a retry.
    /// </summary>
    public const string UnknownTarget = "publisher_unknown_target";

    /// <summary>
    /// The target is export-only: material leaves through its export package and is carried by
    /// hand (422). The remedy is the export, not a publish request.
    /// </summary>
    public const string TargetExportOnly = "publisher_target_export_only";

    /// <summary>
    /// The mode is not one the target's adapter serves (422). The remedy is the other mode, not
    /// a retry.
    /// </summary>
    public const string ModeUnsupported = "publisher_mode_unsupported";

    /// <summary>
    /// The confirmed preview no longer describes the stored state (409). The remedy is a fresh
    /// preview and a new confirmation, not a retry of the stale one.
    /// </summary>
    public const string PreviewStale = "publisher_preview_stale";

    /// <summary>
    /// The publish request did not carry an explicit confirmation (422). Approving content is
    /// not consenting to send it: the remedy is a separate, explicit confirmation per
    /// target and mode.
    /// </summary>
    public const string ConfirmationRequired = "publisher_confirmation_required";

    /// <summary>
    /// No publisher credential is configured for this scope (422). The remedy is storing one,
    /// not a retry: a send without a leased credential cannot happen.
    /// </summary>
    public const string CredentialMissing = "publisher_credential_missing";

    /// <summary>
    /// The target's adapter cannot reconcile an ambiguous write (422). The receipt stays
    /// unknown and the remedy is manual inspection on the remote side, not a reissue.
    /// </summary>
    public const string ReconcileUnavailable = "publisher_reconcile_unavailable";

    /// <summary>
    /// A write may have reached the remote side before the answer was lost (operation failed).
    /// The receipt is retained for reconciliation and the write is never reissued
    /// automatically, because a second dispatch would be a second public post.
    /// </summary>
    public const string Unknown = "publication_unknown";
}

/// <summary>
/// The target id names no registered publisher target (422).
/// </summary>
public sealed class PublisherUnknownTargetException(string targetId)
    : ApiException(
        PublisherError.UnknownTarget,
        "No publisher target with that id is registered. Name a target the publisher registry "
        + "knows; nothing was queued and no credential was leased.")
{
    /// <summary>The unknown id, for the panel that lists what is registered. Never a secret.</summary>
    public string TargetId { get; } = targetId;
}

/// <summary>
/// The target is export-only: material leaves through its export, never through a publish (422).
/// </summary>
/// <remarks>
/// Unconditional for the two classified targets: there is no account, no token and no transport
/// behind them in this product, so no state of the content - approved or not - could make a
/// publish possible. The refusal carries no URL and triggers no outbound request, because there
/// is nothing to call. It deliberately does not reuse <c>publisher_unsupported</c>: that string
/// is the export packages' own refusal, and reusing it here would merge two different decisions
/// into one code.
/// </remarks>
public sealed class PublisherTargetExportOnlyException(string targetId)
    : ApiException(
        PublisherError.TargetExportOnly,
        "That target is export-only. Export the approved material and carry it there by hand; "
        + "no publish was queued, no credential was leased and no account action was taken.")
{
    /// <summary>The export-only target that was named. Never a secret.</summary>
    public string TargetId { get; } = targetId;
}

/// <summary>
/// The mode is not one the target's adapter serves (422).
/// </summary>
public sealed class PublisherModeUnsupportedException(string targetId, string mode)
    : ApiException(
        PublisherError.ModeUnsupported,
        "That target does not serve the requested mode. Ask for the mode its capabilities name; "
        + "nothing was queued and no credential was leased.")
{
    /// <summary>The target that was named. Never a secret.</summary>
    public string TargetId { get; } = targetId;

    /// <summary>The mode that was asked for. Never content.</summary>
    public string Mode { get; } = mode;
}

/// <summary>
/// The confirmed preview no longer describes the stored state (409).
/// </summary>
public sealed class PublisherPreviewStaleException()
    : ApiException(
        PublisherError.PreviewStale,
        "The confirmed preview no longer describes the current revision. Read a fresh preview "
        + "and confirm it again; nothing was queued and no credential was leased.");

/// <summary>
/// The publish request did not carry an explicit confirmation (422).
/// </summary>
/// <remarks>
/// Approval and publication consent are two decisions on purpose: an approval says the text may
/// leave as an export, while a confirmation says it may leave toward one target in one mode.
/// A publish that treated the first as the second would send content the owner never agreed to
/// send anywhere.
/// </remarks>
public sealed class PublisherConfirmationRequiredException()
    : ApiException(
        PublisherError.ConfirmationRequired,
        "Publication needs an explicit confirmation for this target and mode. Approving the "
        + "content is not consent to send it: confirm the current preview and ask again.");

/// <summary>
/// No publisher credential is configured for this scope (422).
/// </summary>
/// <remarks>
/// Answered at request time from the vault's own metadata, before a job exists: a send without
/// a leased credential cannot happen, so a request that could never send is refused here rather
/// than as a failed job an owner has to go and read. The message names no credential id and no
/// key version.
/// </remarks>
public sealed class PublisherCredentialMissingException()
    : ApiException(
        PublisherError.CredentialMissing,
        "No publisher credential is configured for this installation. Store one and ask again; "
        + "nothing was queued and nothing was sent.");

/// <summary>
/// The target's adapter cannot reconcile an ambiguous write (422).
/// </summary>
public sealed class PublisherReconcileUnavailableException(string targetId)
    : ApiException(
        PublisherError.ReconcileUnavailable,
        "That target cannot reconcile an ambiguous write. Inspect the remote side by hand; the "
        + "receipt stays unknown and the write is not reissued.")
{
    /// <summary>The target that was named. Never a secret.</summary>
    public string TargetId { get; } = targetId;
}
