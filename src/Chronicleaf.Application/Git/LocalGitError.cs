namespace Chronicleaf.Application.Git;

/// <summary>
/// The codes this package raises on a route, and the codes a local collection job can end with
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two lists, for the same reason the delivered GitHub package has two: a route code becomes a status
/// a browser sees at once and is a contract with shipped clients, while a job code is stored on an
/// operation row and read back through the delivered operation route. Merging them would make one
/// stored word mean two statuses.
/// </para>
/// <para>
/// <b>Nothing here is a message.</b> No directory, no path, no Git output and no repository name ever
/// reaches an envelope, a log line or an operation row. The codes are the whole of what a caller is
/// told, and the reason a message is safe is that it is written here rather than composed from
/// something the request or the process produced.
/// </para>
/// </remarks>
public static class LocalGitError
{
    /// <summary>
    /// The alias is not one an operator registered. A 404, and the status is the design's own: an
    /// unregistered alias names something this server will never read, so "there is no such root" is the
    /// whole truth. A 422 would say the request was wrong, and the request is not - the operator's
    /// configuration is.
    /// </summary>
    public const string RootUnknown = "local_git_root_unknown";

    /// <summary>
    /// The path escapes its registered root, through traversal or through a symbolic link. This is
    /// the code the whole package exists to make reachable, so it is named rather than folded into a
    /// generic validation failure.
    /// </summary>
    public const string OutsideRoot = "local_git_outside_root";

    /// <summary>
    /// The path names something that is not a Git repository, or whose Git directory cannot be read.
    /// </summary>
    public const string NotARepository = "local_git_not_a_repository";

    /// <summary>
    /// The Git program is not installed, or not on the PATH this process was started with. A 503: the
    /// request was well formed and something this server depends on is missing.
    /// </summary>
    public const string GitUnavailable = "local_git_unavailable";

    /// <summary>
    /// A Git process outlived its own bound and was killed. A 503 with a bounded wait rather than a
    /// 504, because the answer a caller can act on is "this server cannot read that repository right
    /// now" and the per-process bound is this package's own number.
    /// </summary>
    public const string TimedOut = "local_git_timed_out";

    /// <summary>This project has no local repository registered, so there is nothing to collect from.</summary>
    public const string RepositoryMissing = "local_git_repository_missing";

    /// <summary>
    /// Local collection is refused in this deployment. Only a Hosted deployment ever raises it, and it
    /// is a 403 because the request is well formed and the subject - another tenant's disk - is not
    /// this caller's to read.
    /// </summary>
    public const string NotAvailableInHosted = "local_git_not_available_in_hosted";

    /// <summary>Every envelope code this package owns, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RootUnknown,
        OutsideRoot,
        NotARepository,
        GitUnavailable,
        TimedOut,
        RepositoryMissing,
        NotAvailableInHosted,
    ];
}

/// <summary>
/// The error codes a local collection job writes onto its operation row
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// Distinct from <see cref="LocalGitError"/> even where the words match, because one is read by a
/// browser through a status and the other by an owner through the delivered operation route.
/// </remarks>
public static class LocalGitJobError
{
    /// <summary>The run this job was created for has no row, so the words the owner typed cannot be read.</summary>
    public const string RequestMissing = "local_git_job_request_missing";

    /// <summary>
    /// The project's registered local repository is gone by the time the job runs. Permanent: a
    /// registration that was removed will not come back on a second attempt.
    /// </summary>
    public const string RepositoryMissing = "local_git_job_repository_missing";

    /// <summary>The registered root is no longer configured, or its directory is not readable.</summary>
    public const string RootUnknown = "local_git_job_root_unknown";

    /// <summary>The path no longer resolves inside its registered root.</summary>
    public const string OutsideRoot = "local_git_job_outside_root";

    /// <summary>
    /// The path is not a Git repository, or Git cannot read it. Permanent: a directory that is not a
    /// repository does not become one between two attempts.
    /// </summary>
    public const string NotARepository = "local_git_job_not_a_repository";

    /// <summary>A ref the owner named does not resolve to a commit. Permanent.</summary>
    public const string RefNotFound = "local_git_job_ref_not_found";

    /// <summary>
    /// The Git program is missing or could not be started. Transient in principle - an operator can
    /// install it - and bounded by the delivered attempt budget rather than retried forever.
    /// </summary>
    public const string GitUnavailable = "local_git_job_git_unavailable";

    /// <summary>A Git process outlived its bound and was killed. Transient, so the delivered backoff applies.</summary>
    public const string TimedOut = "local_git_job_timed_out";

    /// <summary>
    /// The head moved while this job was reading, so nothing was appended. The delivered supersede
    /// check writes the terminal status; this code exists so a test can name the situation rather
    /// than describe it.
    /// </summary>
    public const string Superseded = "local_git_job_superseded";

    /// <summary>
    /// The milestone's evidence moved while this job was collecting, so nothing was appended. The
    /// local twin of the delivered GitHub code, because a job row is read by this package's own panel.
    /// </summary>
    public const string BundleSuperseded = "local_git_job_bundle_superseded";

    /// <summary>Every code this build can write for a local collection job, so a test can assert it is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        RequestMissing,
        RepositoryMissing,
        RootUnknown,
        OutsideRoot,
        NotARepository,
        RefNotFound,
        GitUnavailable,
        TimedOut,
        Superseded,
        BundleSuperseded,
    ];
}
