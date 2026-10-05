namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// The error codes a GitHub relationship-enrichment job writes onto its operation row
/// (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the route codes even where the words match, because the two are read by
/// different things: a route code becomes a status a browser sees immediately, while a job code is
/// stored on an operation row and read back through the delivered operation route.
/// </para>
/// <para>
/// Every one of these is a code. No message, no URL, no repository name and no token is ever
/// stored on the row that carries one.
/// </para>
/// </remarks>
public static class GitHubEnrichmentJobError
{
    /// <summary>The repository is absent or private. Permanent: a retry cannot make it appear.</summary>
    public const string RepositoryUnavailable = "github_enrichment_job_repository_unavailable";

    /// <summary>The credential was refused. Permanent until the owner changes it.</summary>
    public const string CredentialRejected = "github_enrichment_job_credential_rejected";

    /// <summary>
    /// The credential may not read this repository's relationships. Permanent, and no relation
    /// fact is fabricated to fill the gap.
    /// </summary>
    public const string PermissionDenied = "github_enrichment_job_permission_denied";

    /// <summary>GitHub asked us to wait. Transient, with GitHub's own delay bounded by the job policy.</summary>
    public const string RateLimited = "github_enrichment_job_rate_limited";

    /// <summary>GitHub refused the request itself. Permanent: repeating the request changes nothing.</summary>
    public const string RequestRefused = "github_enrichment_job_request_refused";

    /// <summary>GitHub could not answer. Transient, so the delivered backoff applies.</summary>
    public const string Unreachable = "github_enrichment_job_unreachable";

    /// <summary>
    /// The project's repository connection is gone by the time the job runs. Permanent: a connection
    /// that was removed will not come back on a second attempt.
    /// </summary>
    public const string ConnectionMissing = "github_enrichment_job_connection_missing";

    /// <summary>
    /// The stored connection does not name a repository this transport can address. Permanent:
    /// a second attempt reads the same row.
    /// </summary>
    public const string ConnectionInvalid = "github_enrichment_job_connection_invalid";

    /// <summary>
    /// The run this job was created for has no row, so the revision the owner was looking at cannot
    /// be read and this package will not guess at one. Permanent and named, because the alternative -
    /// enriching whatever the head happens to be - is an enrichment of something else entirely.
    /// </summary>
    public const string RequestMissing = "github_enrichment_job_request_missing";

    /// <summary>
    /// The milestone holds no collected commits to enrich. Permanent: there is nothing to query.
    /// </summary>
    public const string NothingToEnrich = "github_enrichment_job_nothing_to_enrich";

    /// <summary>
    /// The milestone's evidence moved while this job was reading, so nothing was appended. The
    /// delivered supersede check writes the terminal status for a moved milestone; this code is the
    /// honest terminal state for a moved bundle, and is deliberately not dressed up as a success.
    /// </summary>
    public const string Superseded = "github_enrichment_job_superseded";

    /// <summary>Every code this build can write for an enrichment job, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RepositoryUnavailable,
        CredentialRejected,
        PermissionDenied,
        RateLimited,
        RequestRefused,
        Unreachable,
        ConnectionMissing,
        ConnectionInvalid,
        RequestMissing,
        NothingToEnrich,
        Superseded,
    ];
}
