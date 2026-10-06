namespace Chronicleaf.Application.Common;

/// <summary>
/// Single source of truth for the status code of every envelope code owned by this package.
/// The mapping is contractual: changing a code here changes an HTTP status for shipped clients.
/// </summary>
public static class ApiErrorStatus
{
    public static int ForCode(string code) => code switch
    {
        ApiError.MalformedJson => 400,
        ApiError.ValidationFailed => 422,
        ApiError.IfMatchRequired => 428,
        ApiError.RevisionConflict => 409,
        ApiError.PayloadTooLarge => 413,
        ApiError.DatabaseUnavailable => 503,
        ApiError.SchemaMismatch => 503,
        ApiError.DependencyTimeout => 504,

        // The access package's codes. They extend this mapping without changing a single existing
        // status: a feature package adds a code, the envelope shape stays the same, and a status a
        // shipped client already relies on is never renumbered.
        Access.AccessError.Unauthenticated => 401,
        Access.AccessError.InvalidCredentials => 401,
        Access.AccessError.OriginMismatch => 403,
        Access.AccessError.AntiforgeryInvalid => 403,
        Access.AccessError.TooManyRequests => 429,
        Access.AccessError.InstallationMissing => 503,

        // The projects-and-milestones package's codes. Like the access codes above, they add
        // mappings and change no existing status: a 404 for an id this caller may not reach, and a
        // 409 for an edit of an archived project, which is a conflict with the record's own state
        // rather than with another writer.
        ApiError.NotFound => 404,
        Milestones.MilestoneError.ProjectArchived => 409,

        // The background-jobs package's codes. Again additive: a 404 for an operation this caller may
        // not reach, and two 409s for conditions that are conflicts with the operation's own state -
        // an idempotency key already bound to other work, and a terminal operation asked to stop.
        Operations.OperationError.NotFound => 404,
        Operations.OperationError.IdempotencyConflict => 409,
        Operations.OperationError.NotCancellable => 409,

        // The local-media-storage package's codes. Additive in the same way, and the 404 is
        // deliberately the already-delivered one: an absent media id, an id in another scope and a
        // key that resolves outside the MediaRoot are all one answer, so a caller cannot probe for
        // another installation's media by comparing codes. 415 for a type this package does not store,
        // 413 for a body over the limit, 422 for a decoded image over the pixel limit, 409 for media
        // something still references, 503 for a storage provider that cannot serve the request.
        // MediaError.NotFound and MediaError.PayloadTooLarge deliberately reuse the delivered
        // constants, so they are already mapped above - an absent id, an id in another scope and an
        // oversized body mean the same thing here as anywhere else. Only the codes that are genuinely
        // new get an arm.
        Media.MediaError.UnsupportedMediaType => 415,
        Media.MediaError.ImageTooLarge => 422,
        Media.MediaError.MediaInUse => 409,
        Media.MediaError.StorageUnavailable => 503,

        // The credential-vault package's codes. Additive in the same way, and both 503s for the same
        // reason: a save that cannot be sealed and a resolve that cannot be opened are the same class
        // of condition - the operator's key is not usable right now, or the stored bytes no longer
        // authenticate - and both are answerable once that is fixed. Neither is a 500, because a 500
        // would tell the caller the server is broken, and neither is a 404, because the credential
        // does exist. credential_missing deliberately has no arm: it is a job's error code, not an
        // envelope code, and no route resolves a secret.
        Secrets.CredentialVaultError.VaultUnavailable => 503,
        Secrets.CredentialVaultError.Undecryptable => 503,

        // The llm-provider-contract package's codes. Additive in the same way, and the choices here
        // are the package's failure policy rather than defaults. 409 for a consent that no longer
        // covers the input: nothing is wrong with the request, and a 422 would invite a client to
        // resend the same already-consented bytes while a 404 would send it looking for another
        // preview. 504 for an ambiguous provider timeout, which is deliberately not retried because
        // the provider may already be billing the call. 422 for a provider slot with no installed
        // adapter and for a model the installed adapter's provider does not accept, because both are
        // answerable before anything is spent. 429 for a provider's own throttling, which is the
        // provider's answer passed through rather than this server's rate limiter. 502 for a
        // refusal and for malformed provider output: the request was well formed and something
        // upstream produced something unusable. 503 for a pre-response outage with the bounded retry
        // spent, and 409 for a preview id collision. The two that reuse delivered codes -
        // ValidationFailed and NotFound - are already mapped above.
        AI.AiError.ConsentStale => 409,
        AI.AiError.PreviewConflict => 409,
        AI.AiError.GenerationTimeout => 504,
        AI.AiError.ProviderNotInstalled => 422,
        AI.AiError.ModelNotSupported => 422,
        AI.AiError.CredentialRejected => 422,
        AI.AiError.RateLimited => 429,
        AI.AiError.ProviderRefused => 502,
        AI.AiError.ProviderInvalidResponse => 502,
        AI.AiError.ProviderUnavailable => 503,

        // The github-evidence package's codes. Additive in the same way, and the choices are this
        // package's failure policy rather than defaults. 422 for a repository that cannot be read and
        // for a refused credential: both are answerable before anything is spent, and neither is a 404
        // because a private repository does exist - what it is not is readable by this owner, and
        // GitHub deliberately does not say which of the two cases this is. 422 for a project with no
        // connection, which is again something the owner supplies. 503 for GitHub being unreachable,
        // which is not a 500 because nothing here says this server is broken. RequestRefused
        // deliberately has no arm: it is a job's error code, not an envelope code, and no route
        // answers a call that GitHub refused.
        GitHub.GitHubError.RepositoryUnavailable => 422,
        GitHub.GitHubError.CredentialRejected => 422,
        GitHub.GitHubError.ConnectionMissing => 422,
        GitHub.GitHubError.Unreachable => 503,

        // The local-git-evidence package's codes. Additive in the same way, and the choices here are this
        // package's failure policy rather than defaults. 404 for an alias nobody registered, because the
        // request names a root that does not exist here rather than a malformed body, and a 422 would send
        // the caller to change a name that was fine. 422 for a directory that is not a Git repository and
        // for a project with no registration: both are facts the owner or the operator can change today,
        // and both are about a real, reachable project. 403 for a path that leaves its registered root, and
        // for a deployment that may not read local directories: the requests are well formed and what is
        // refused is the destination, so a 422 would invite a client to resend an identical request. 503 for
        // a missing Git and for a read that outlived this package's own bound - operator-repairable
        // conditions, not a broken product, and deliberately not 504s, because the bound is a constant of
        // this package rather than somebody else's latency promise. The ten job codes deliberately have no
        // arm: they are written to the operation row, and no route answers a collection that already failed.
        Git.LocalGitError.RootUnknown => 404,
        Git.LocalGitError.NotARepository => 422,
        Git.LocalGitError.RepositoryMissing => 422,
        Git.LocalGitError.OutsideRoot => 403,
        Git.LocalGitError.NotAvailableInHosted => 403,
        Git.LocalGitError.GitUnavailable => 503,
        Git.LocalGitError.TimedOut => 503,

        // The screenshot-capture package's codes. Additive in the same way, and the choices are this
        // package's failure policy rather than defaults. 403 for a destination the URL policy will not
        // photograph: the milestone is real and the request is well formed, and it is the destination
        // that is refused, so 404 would be a lie and 422 would invite a client to resend an identical
        // request. 503 for a host that cannot start the capture sandbox, which is a missing kernel
        // capability rather than a broken product - and deliberately not a path that captures without
        // it. TooLarge and Timeout deliberately reuse the delivered 413 and 504 constants, so an
        // oversized ask and a page that never finished read the same as anywhere else. The four
        // page_* and job_* codes deliberately have no arm: they are written to the operation row, and no
        // route answers a capture that already failed.
        Capture.CaptureError.TargetBlocked => 403,
        Capture.CaptureError.SandboxUnavailable => 503,

        // The grounded-markdown-drafts package's codes. Additive in the same way, and the choices are this
        // package's failure policy rather than defaults. 422 for a milestone that is not confirmed and for
        // a selection in which nothing was actually collected: both are facts the owner can change today
        // with one click, and both are refused before a redacted snapshot is on screen that looks like it
        // is about to be sent. The 409 and the two that reuse delivered codes - ConsentStale, NotFound,
        // ValidationFailed and TooLarge - are already mapped above. The thirteen job codes deliberately
        // have no arm: they are written to the operation row, and no route answers a draft that already
        // failed.
        Content.ContentError.MilestoneNotConfirmed => 422,
        Content.ContentError.NoGrounding => 422,

        // The markdown-review-export package's codes. Additive in the same way. 409 for an export
        // with no live approval and for an approval that no longer describes the stored revision,
        // the evidence head or the pictures: nothing is wrong with the request, and a 422 would
        // invite a client to resend bytes that can no longer be authorized. 422 for a revision
        // that carries deterministic findings, because the remedy is to correct the text - the same
        // shape as every other semantic refusal in this file.
        Content.ReviewError.ApprovalRequired => 409,
        Content.ReviewError.ApprovalStale => 409,
        Content.ReviewError.ApprovalBlocked => 422,

        // The html-content-export package's code. Additive in the same way: 409 for an approved
        // picture whose bytes are gone, because nothing is wrong with the request and a 422 would
        // invite a client to resend bytes that cannot be completed - the remedy is to reselect or
        // re-capture the picture and approve again, never a picture-less artifact.
        Content.HtmlExportError.MediaUnavailable => 409,

        // The public-project-journey package's codes. Additive in the same way: 409 for a slug
        // already taken by another project's journey, because the remedy is another slug rather
        // than a corrected body - and 409 for a preview hash that no longer matches the stored
        // selection, because nothing is wrong with the request and the remedy is a fresh preview
        // rather than a retry of the publication.
        Journey.JourneyError.SlugConflict => 409,
        Journey.JourneyError.SelectionStale => 409,

        // The agency-client-projects package's code. Additive in the same way: 409 for an
        // assignment naming an archived client, because the remedy is another client (or
        // clearing back to personal) rather than a corrected body - the same shape as the
        // project-archived conflict above. The foreign-client refusal deliberately reuses the
        // delivered NotFound, so a caller cannot probe for another scope's clients.
        Agency.AgencyError.ClientArchived => 409,

        // The cloud-tenant-boundary package's codes. Additive in the same way: 401 for an OIDC
        // exchange this deployment does not accept, 403 for a tenant the caller holds no live
        // membership in, 409 for a session with no selected tenant and for a Community volume
        // with no explicit import, and 422 for an account past its tenant quota. The
        // foreign-tenant refusal deliberately reuses the delivered NotFound above, so a caller
        // cannot probe for another tenant by comparing codes.
        Tenancy.TenancyError.OidcLoginFailed => 401,
        Tenancy.TenancyError.TenantAccessDenied => 403,
        Tenancy.TenancyError.TenantSelectionRequired => 409,
        Tenancy.TenancyError.CommunityImportRequired => 409,
        Tenancy.TenancyError.TenantLimitReached => 422,

        // The team-project-permissions package's codes. Additive in the same way: 403 for a
        // project action the caller holds no grant for, 410 for an invitation whose 7-day life
        // ran out before it was accepted, and 409 for a token that is already used, a duplicate
        // pending invitation and a removal or demotion that would leave no live owner. The
        // unknown-token and foreign-project refusals deliberately reuse the delivered NotFound
        // above, so a caller cannot probe for another tenant's invitations or projects.
        Teams.TeamPermissionError.AccessDenied => 403,
        Teams.TeamPermissionError.InvitationExpired => 410,
        Teams.TeamPermissionError.InvitationReused => 409,
        Teams.TeamPermissionError.InvitationExists => 409,
        Teams.TeamPermissionError.LastOwner => 409,

        // The narrative-memory package's codes. Additive in the same way. 422 for a context that
        // does not fit the byte budget without losing a pinned fact, because the remedy is to
        // narrow the cutoff or shorten entries - the same shape as every other semantic refusal
        // in this file. The foreign-evidence refusal deliberately reuses the delivered NotFound
        // above, and the three job codes deliberately have no arm: they are written to the
        // operation row, and no route answers a refresh that already failed.
        Narrative.NarrativeError.ContextTooLarge => 422,

        // The screenshot-evolution-images package's codes. Additive in the same way, and the
        // choices are this package's failure policy rather than defaults. 422 for depicted
        // pictures that span more than one viewport outside the gallery, and for fewer than two
        // milestones depicting a selected screenshot: both are facts the owner can change today
        // with one selection, and both are refused before any bytes are stored. The 409s and the
        // 413 deliberately reuse delivered codes - RevisionConflict for a selection that moved
        // since the review, the review package's approval_required and approval_stale, the HTML
        // export's media_unavailable and the delivered payload-too-large - because the condition
        // and the remedy are the same and a second spelling would pretend otherwise.
        Evolution.EvolutionError.ComparisonViewportMismatch => 422,
        Evolution.EvolutionError.InsufficientSelected => 422,

        // The github-release-candidates package's codes. Additive in the same way, and the choices are
        // this package's failure policy rather than defaults. 401 for a delivery that did not
        // authenticate - and deliberately the same 401 for an unknown connection id, a connection with
        // no subscription, a missing signature and a wrong one, because a route that answered those
        // four differently would let anybody discover which connection ids exist. It is not a 404 and
        // not a 403 for the same reason. 422 for a delivery that authenticated but could not be read,
        // which is a different problem only the sender can fix and must be distinguishable from a
        // wrong signature. 422 for a project with no repository connection, which is something the
        // owner supplies. 413 for a body over this package's own one-mebibyte ceiling, and 503 for
        // GitHub being unreachable rather than a 500. The six job codes deliberately have no arm:
        // they are written to the operation row, and no route answers a poll that already failed.
        GitHubCandidates.GitHubCandidateError.SignatureInvalid => 401,
        GitHubCandidates.GitHubCandidateError.DeliveryMalformed => 422,
        GitHubCandidates.GitHubCandidateError.ConnectionMissing => 422,
        GitHubCandidates.GitHubCandidateError.Unreachable => 503,
        GitHubCandidates.GitHubCandidateError.DeliveryTooLarge => 413,

        // The scheduled-project-sync package's codes. Additive in the same way, and the choices are
        // this package's failure policy rather than defaults. 429 for an activation past the
        // scope's enabled-schedule quota, because the remedy is to disable another schedule rather
        // than to retry or correct this one. 409 for a project with no readable repository
        // connection, because nothing is wrong with the request and the remedy is to connect a
        // repository rather than to resend the schedule.
        Schedules.ScheduleError.QuotaExceeded => 429,
        Schedules.ScheduleError.NoConnection => 409,

        // The hosted-archive-operations package's codes. Additive in the same way, and the
        // choices are this package's failure policy rather than defaults. 409 for a backup whose
        // schema, key version or content no longer matches the live scope: nothing is wrong with
        // the request, and the remedy is a fresh backup rather than a retry of the rehearsal.
        // 409 for a deletion that raced another deletion, because the remedy is to read the
        // stored tombstone rather than to delete again. 410 for a restore past the recovery
        // window, because the remedy is the operator purge rather than a retry - the same shape
        // as the teams package's expired invitation. The foreign-tombstone refusal deliberately
        // reuses the delivered NotFound above, so a caller cannot probe for another scope's
        // deletions.
        Archive.ArchiveError.KeyMismatch => 409,
        Archive.ArchiveError.MediaMismatch => 409,
        Archive.ArchiveError.SchemaMismatch => 409,
        Archive.ArchiveError.AlreadyDeleted => 409,
        Archive.ArchiveError.RestoreExpired => 410,

        _ => 500,
    };
}

/// <summary>
/// Base class for failures that a request may translate into a <see cref="ApiError"/>.
/// </summary>
/// <remarks>
/// Messages carried by these exceptions are authored here, never taken from a provider
/// response body, a token or a filesystem path. The middleware writes
/// <see cref="Message"/> verbatim, so a caller that interpolates untrusted text here
/// would leak it.
/// </remarks>
public abstract class ApiException : Exception
{
    protected ApiException(string code, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
        : base(message) => (Code, FieldErrors) = (code, fieldErrors);

    public string Code { get; }

    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }
}

/// <summary>The request body was not valid JSON (400).</summary>
public sealed class MalformedJsonException(string message = "The request body is not valid JSON.")
    : ApiException(ApiError.MalformedJson, message);

/// <summary>The request parsed but failed semantic validation (422).</summary>
public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> fieldErrors)
    : ApiException(ApiError.ValidationFailed, "The request failed validation.", fieldErrors)
{
    public ValidationFailedException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}

/// <summary>A mutation that requires <c>If-Match</c> arrived without it (428).</summary>
public sealed class IfMatchRequiredException(string resource = "resource")
    : ApiException(ApiError.IfMatchRequired, $"An If-Match revision is required for this {resource}.");

/// <summary>The supplied revision no longer matches the stored revision (409).</summary>
public sealed class RevisionConflictException(long actualRevision)
    : ApiException(ApiError.RevisionConflict, "The supplied revision is stale.")
{
    /// <summary>The revision actually stored. The caller's stale value is never echoed.</summary>
    public long ActualRevision { get; } = actualRevision;
}

/// <summary>The request body exceeded the accepted size (413).</summary>
public sealed class PayloadTooLargeException(long limitBytes)
    : ApiException(ApiError.PayloadTooLarge, $"The request body exceeds the {limitBytes} byte limit.");

/// <summary>A dependency could not serve the request right now (503).</summary>
public sealed class DependencyUnavailableException(string dependency, string? reason = null)
    : ApiException(ApiError.DatabaseUnavailable, $"The {dependency} dependency is unavailable. {reason}".TrimEnd());

/// <summary>A dependency did not answer in time (504).</summary>
public sealed class DependencyTimeoutException(string dependency, TimeSpan timeout)
    : ApiException(ApiError.DependencyTimeout, $"The {dependency} dependency did not answer within {timeout.TotalSeconds:0.##}s.");

/// <summary>
/// An unexpected server-side failure (500). The message stays authored here; the middleware adds
/// only the trace id, never the exception text of a provider.
/// </summary>
public sealed class InternalFailureException(string message)
    : ApiException(ApiError.InternalError, message);
