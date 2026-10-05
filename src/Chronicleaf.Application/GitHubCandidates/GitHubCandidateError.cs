namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The codes this package raises, and the codes a candidate job can end with
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The two lists are separate on purpose, exactly as they are in the delivered github-evidence package:
/// a <em>route</em> code becomes a status a browser sees immediately, while a <em>job</em> code is
/// stored on an operation row and read back through the delivered operation route. Reusing one list for
/// both is how a job failure class ends up meaning something to HTTP that it never meant.
/// </para>
/// <para>
/// Every one of these is a code. No message, no repository name, no payload text and no secret is ever
/// stored on the row that carries one.
/// </para>
/// </remarks>
public static class GitHubCandidateError
{
    /// <summary>
    /// The signed delivery did not authenticate: the signature was absent, malformed or computed over
    /// different bytes. 401, and deliberately the same answer for an unknown connection, an archived one
    /// and a wrong secret.
    /// </summary>
    /// <remarks>
    /// One answer for four different situations is the point. A delivery route that said "no such
    /// connection" for an unknown id and "wrong signature" for a bad one would let anybody discover
    /// which connection ids exist, and a sender that got different answers would know which of its two
    /// problems to fix. It is also what makes "a random connection id is not authentication" true by
    /// construction rather than by policy.
    /// </remarks>
    public const string SignatureInvalid = "github_candidate_signature_invalid";

    /// <summary>
    /// The delivery arrived intact but this build cannot read it: no event name, no delivery id, or a
    /// body that is not the JSON the webhook contract describes.
    /// </summary>
    /// <remarks>
    /// 422 rather than 400, and deliberately not 401. The signature was checked before any of this, so
    /// a 422 here says the sender is authenticated and has sent something malformed - which is a
    /// problem only the sender can fix, and a 400 would imply this server could not parse a request it
    /// never tried to parse.
    /// </remarks>
    public const string DeliveryMalformed = "github_candidate_delivery_malformed";

    /// <summary>The project has no repository connection, so there is nothing to propose anything about.</summary>
    public const string ConnectionMissing = "github_candidate_connection_missing";

    /// <summary>GitHub could not answer, or answered with something this build cannot read.</summary>
    public const string Unreachable = "github_candidate_unreachable";

    /// <summary>The delivery body is over the one-mebibyte ceiling this package accepts.</summary>
    public const string DeliveryTooLarge = "github_candidate_delivery_too_large";

    /// <summary>Every envelope code this package owns, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SignatureInvalid,
        DeliveryMalformed,
        ConnectionMissing,
        Unreachable,
        DeliveryTooLarge,
    ];
}

/// <summary>
/// The error codes a candidate poll writes onto its operation row.
/// </summary>
/// <remarks>
/// Distinct from <see cref="GitHubCandidateError"/> for the same reason the delivered GitHub job codes
/// are distinct from its envelope codes: the two are read by different things, and a code that means
/// something to an HTTP client means nothing to an owner reading a queued operation.
/// </remarks>
public static class GitHubCandidateJobError
{
    /// <summary>The repository is absent or private. Permanent: a retry cannot make it appear.</summary>
    public const string RepositoryUnavailable = "github_candidate_job_repository_unavailable";

    /// <summary>The credential was refused. Permanent until the owner changes it.</summary>
    public const string CredentialRejected = "github_candidate_job_credential_rejected";

    /// <summary>GitHub asked us to wait, with its own delay bounded by the delivered job policy.</summary>
    public const string RateLimited = "github_candidate_job_rate_limited";

    /// <summary>GitHub could not answer. Transient, so the delivered backoff applies.</summary>
    public const string Unreachable = "github_candidate_job_unreachable";

    /// <summary>
    /// The project's repository connection is gone by the time the job runs. Permanent: a connection
    /// that was removed will not come back on a second attempt.
    /// </summary>
    public const string ConnectionMissing = "github_candidate_job_connection_missing";

    /// <summary>
    /// The request this job was created for has no row, so the words the owner typed cannot be read.
    /// Permanent and named, because the alternative is polling with a window nobody asked for.
    /// </summary>
    public const string RequestMissing = "github_candidate_job_request_missing";

    /// <summary>The project's revision moved while the poll ran, so nothing was recorded.</summary>
    public const string Superseded = "github_candidate_job_superseded";

    /// <summary>Every code this build can write for a candidate job, so a test can assert the set is closed.</summary>
    /// <remarks>
    /// <see cref="RateLimited"/> is absent from this list on purpose and the reason is worth stating:
    /// a throttled answer is not a code this job records, because the delivered worker translates it
    /// into the operation row's own <c>rate_limited</c> status and its own bounded wait. A code written
    /// here as well would be a second, unreadable spelling of a decision the delivered queue already
    /// made and already reports.
    /// </remarks>
    public static readonly IReadOnlyList<string> All =
    [
        RepositoryUnavailable,
        CredentialRejected,
        Unreachable,
        ConnectionMissing,
        RequestMissing,
        Superseded,
    ];
}
