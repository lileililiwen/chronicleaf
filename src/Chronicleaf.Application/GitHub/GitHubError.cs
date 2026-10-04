namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The codes this package raises, and the codes a GitHub job can end with
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The two lists are separate on purpose. A <em>route</em> code becomes a status a browser sees
/// immediately, and this repository's status mapping is a contract with shipped clients. A
/// <em>job</em> code is stored on an operation row and read back through the delivered operation
/// route, and it exists so an owner can tell "the repository is not there" from "GitHub asked us to
/// wait" from "the credential was refused" without this package exposing anything about a response
/// body.
/// </para>
/// <para>
/// Every one of these is a code. No message, no URL, no repository name and no token is ever
/// stored on the row that carries one.
/// </para>
/// </remarks>
public static class GitHubError
{
    /// <summary>The repository is not public, or does not exist. The two are the same answer.</summary>
    public const string RepositoryUnavailable = "github_repository_unavailable";

    /// <summary>GitHub refused the credential, or required one and none was configured.</summary>
    public const string CredentialRejected = "github_credential_rejected";

    /// <summary>GitHub asked us to slow down; the delay is its own and is clamped by the job policy.</summary>
    public const string RateLimited = "github_rate_limited";

    /// <summary>GitHub could not answer, or answered with something this build cannot read.</summary>
    public const string Unreachable = "github_unreachable";

    /// <summary>GitHub refused the request itself: a ref that does not resolve, or a shape it rejects.</summary>
    public const string RequestRefused = "github_request_refused";

    /// <summary>The project has no repository connection, so there is nothing to collect from.</summary>
    public const string ConnectionMissing = "github_connection_missing";

    /// <summary>Every envelope code this package owns, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RepositoryUnavailable,
        CredentialRejected,
        RateLimited,
        Unreachable,
        RequestRefused,
        ConnectionMissing,
    ];
}

/// <summary>
/// The error codes a GitHub collection job writes onto its operation row.
/// </summary>
/// <remarks>
/// Distinct from <see cref="GitHubError"/> even where the words match, because the two are read by
/// different things: one by a browser through a status, the other by an owner through the delivered
/// operation route. A job that says <c>github_job_repository_unavailable</c> has failed for a reason
/// no later attempt will change.
/// </remarks>
public static class GitHubJobError
{
    /// <summary>The repository is absent or private. Permanent: a retry cannot make it appear.</summary>
    public const string RepositoryUnavailable = "github_job_repository_unavailable";

    /// <summary>The credential was refused. Permanent until the owner changes it.</summary>
    public const string CredentialRejected = "github_job_credential_rejected";

    /// <summary>GitHub asked us to wait. Transient, with GitHub's own delay bounded by the job policy.</summary>
    public const string RateLimited = "github_job_rate_limited";

    /// <summary>A ref the owner named does not resolve. Permanent: repeating the request changes nothing.</summary>
    public const string RefNotFound = "github_job_ref_not_found";

    /// <summary>GitHub could not answer. Transient, so the delivered backoff applies.</summary>
    public const string Unreachable = "github_job_unreachable";

    /// <summary>
    /// The project's repository connection is gone by the time the job runs. Permanent: a connection
    /// that was removed will not come back on a second attempt.
    /// </summary>
    public const string ConnectionMissing = "github_job_connection_missing";

    /// <summary>
    /// The run this job was created for has no row, so the words the owner typed cannot be read and
    /// this package will not guess at refs. Permanent and named, because the alternative - collecting
    /// from a default branch nobody asked for - is a collection of something else entirely.
    /// </summary>
    public const string RequestMissing = "github_job_request_missing";

    /// <summary>
    /// The milestone's evidence moved while this job was collecting, so nothing was appended. The
    /// delivered supersede check writes the terminal status for this case; the code exists so a test
    /// can name the situation rather than describe it.
    /// </summary>
    public const string Superseded = "github_job_superseded";

    /// <summary>Every code this build can write for a GitHub job, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RepositoryUnavailable,
        CredentialRejected,
        RefNotFound,
        Unreachable,
        ConnectionMissing,
        RequestMissing,
        Superseded,
    ];
}
