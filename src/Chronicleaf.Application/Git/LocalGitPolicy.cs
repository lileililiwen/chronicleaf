using System.Text;
using Chronicleaf.Application.GitHub;
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The promises this package makes about how much of a local repository becomes evidence, and about
/// how long a Git process may run (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The commit and file caps are the github-evidence caps.</b> A local repository is the same kind of
/// thing as a hosted one from the reader's point of view - commits, files, diff text - and an owner
/// who is used to one cap should not find the other package quietly offering five times as much. They
/// are referenced rather than restated so the two numbers cannot drift apart.
/// </para>
/// <para>
/// <b>Two diff numbers, and the difference between them is the point.</b> The output cap is the design's
/// 256 KiB: it bounds what one <c>git diff</c> process is allowed to write, so a repository with one enormous
/// file cannot make Git write unboundedly into a worker's memory. The evidence budget is half the delivered
/// bundle's own text limit, because a bundle holds the patch text <em>and</em> the repository metadata, the
/// commits, the exclusions and the truncation records - and a patch budget equal to the whole bundle limit
/// would leave no room for any of them, so a repository with a large diff would fail the append rather than
/// produce the honest truncated bundle this package promises. Anything past either cap is recorded as a
/// truncation with the cap named, never silently shortened.
/// </para>
/// <para>
/// <b>Thirty seconds is per Git process, not per collection.</b> A collection may run several of them,
/// and the total is therefore bounded by the count of commands this package issues rather than by one
/// generous number. A process that outlives its own bound is killed with its tree and reported as a
/// dependency that could not answer, because a hung <c>git diff</c> on a large repository is a real
/// failure and not something to wait out.
/// </para>
/// </remarks>
public static class LocalGitLimits
{
    /// <summary>At most this many commits are read for one collection. The delivered GitHub cap.</summary>
    public const int MaxCommits = GitHubLimits.MaxCommits;

    /// <summary>At most this many changed files are read. The delivered GitHub cap.</summary>
    public const int MaxChangedFiles = GitHubLimits.MaxChangedFiles;

    /// <summary>At most this many bytes of diff text become evidence for one local collection.</summary>
    public const int MaxDiffBytes = 128 * 1024;

    /// <summary>
    /// At most this many bytes one <c>git diff</c> process is allowed to write.
    /// </summary>
    /// <remarks>
    /// The design's 256 KiB, and it bounds the pipe rather than the evidence. A result the pipe truncated
    /// is reported as a dependency failure rather than published, because a prefix of a diff with nothing in
    /// it saying what is missing is exactly the kind of fact this product's evidence contract exists to
    /// prevent - while a <em>complete</em> diff larger than the evidence budget is a different thing, and that
    /// one becomes an honest truncated bundle.
    /// </remarks>
    public const int MaxDiffOutputBytes = 256 * 1024;

    /// <summary>How long one Git process may run before it is killed and the dependency refused.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest ref a request may name. The delivered GitHub bound, on the same words.</summary>
    public const int RefMaxLength = GitHubLimits.RefMaxLength;

    /// <summary>
    /// The longest idempotency key a request may carry, and the same bound the GitHub route uses.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// The most uncommitted changes this package will count in a working tree, so a repository with
    /// a million untracked files cannot turn a notice into an unbounded read.
    /// </summary>
    public const int MaxUncommittedCounted = 10_000;

    /// <summary>The most bytes of Git's own output this package will read from one process.</summary>
    public const int MaxOutputBytes = 2 * 1024 * 1024;
}

/// <summary>
/// Which deployment the local collection policy is being evaluated for (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// A real input to the decision, and the same shape as the delivered capture deployment mode for one
/// reason: the difference between these two modes is a property of <em>whose disk</em> the operator's
/// disk is, and that cannot be decided by a comment or inferred from a hostname.
/// </para>
/// <para>
/// In <see cref="Community"/> the process runs on the owner's own machine and a registered root is
/// their own directory, so reading a committed revision out of it is what the product is for. In
/// <see cref="Hosted"/> the same process would be reading a shared machine's disk on behalf of a
/// tenant who never registered it, and no operator configuration can make that somebody else's
/// directory into evidence. So <see cref="Hosted"/> refuses local collection outright rather than
/// reducing the cap or hiding the option.
/// </para>
/// </remarks>
public enum LocalGitDeploymentMode
{
    /// <summary>
    /// A single-tenant Community install. Registered roots are the operator's own directories and may
    /// be read.
    /// </summary>
    Community = 0,

    /// <summary>
    /// The shared Hosted deployment. Local filesystem discovery is refused, whatever an operator has
    /// configured.
    /// </summary>
    Hosted = 1,
}

/// <summary>
/// The operator's registered roots and the deployment they may be read in
/// (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>One directory may hold several aliases, and the same directory may be registered twice.</b>
/// Git's own worktrees and submodules are ordinary reasons for two names to point at one place, and a
/// scheme that refused the second registration would make an operator's choice look like an attack.
/// What matters is only that a <em>request</em> may name an alias and never a path.
/// </para>
/// <para>
/// <b>Defaults are restrictive, not permissive.</b> With no configuration there are no roots, so every
/// connection is refused; a local collection that worked because nothing was configured would be a
/// collection of whatever directory the process happened to start in.
/// </para>
/// <para>
/// <b>Configuration is the operator's and cannot be widened by a request.</b> The alias is the only
/// half of the address a caller supplies, and it is resolved against this table rather than
/// concatenated into a path.
/// </para>
/// </remarks>
/// <param name="Alias">The name a request may use, compared case-insensitively.</param>
/// <param name="Path">The absolute directory this alias names. Never leaves the server.</param>
public sealed record LocalRepositoryRoot(string Alias, string Path);

/// <summary>
/// The three answers a path inside a registered root can have (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// A closed vocabulary rather than a boolean, so a caller is forced to say which of the three it got. The
/// distinction is the difference between "you asked for something that is not here" and "you asked for
/// something outside the place you are allowed to look", and the two have nothing in common from the
/// caller's point of view: the first is a fact about a directory, the second is a refusal.
/// </remarks>
public enum LocalPathResolution
{
    /// <summary>The path is a real directory inside the registered root, and may be read.</summary>
    Resolved = 0,

    /// <summary>
    /// A link in the path left the registered root. Refused, and never read from.
    /// </summary>
    Outside = 1,

    /// <summary>
    /// The path is not there, or its root is not readable. A fact about a directory rather than a refusal.
    /// </summary>
    Absent = 2,
}

/// <summary>
/// The read-only view of the registered roots that both the routes and the transport resolve against
/// (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// An interface rather than the settings object itself, because the two consumers must not be able to
/// hold the same mutable list: the service captures a snapshot at startup and the transport resolves
/// against it, and a list that could be appended to while a collection was running would be a
/// collection whose allowed roots changed underneath it.
/// </remarks>
public interface ILocalRepositoryCatalog
{
    /// <summary>Which deployment this is. Read first, because it can refuse everything.</summary>
    LocalGitDeploymentMode DeploymentMode { get; }

    /// <summary>Every registered root, in configuration order. Names only, never a usable path.</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>Resolves an alias to its directory, or <c>null</c> when the alias is not registered.</summary>
    LocalRepositoryRoot? Find(string? alias);
}

/// <summary>
/// The policy half of a local connection: what a request may say, and how a path is checked for
/// containment (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two checks, and neither is sufficient alone.</b> <see cref="ValidateConnection"/> is a pure
/// function of the text an owner typed: no leading separator, no drive letter, no <c>..</c> at any
/// depth, bounded segments. <see cref="ResolveWithin"/> asks the filesystem whether the path that text
/// names is really inside the registered root, following symlinks as it goes. A pure check cannot see
/// a symlink and a filesystem check cannot tell whether a string was ever meant as a path, so a
/// request is only accepted when both agree.
/// </para>
/// <para>
/// <b>The filesystem check resolves every segment, not just the last one.</b> <c>root/link/notes.md</c>
/// where <c>link</c> is a symlink to <c>/etc</c> is a path whose <em>final</em> segment is not itself a
/// link, so a check that resolved only the last component would call it contained. Each segment is
/// resolved in turn and the containment is re-tested after every step, which is what makes a link out
/// of the root fail at the step it is followed rather than at the end.
/// </para>
/// </remarks>
public static class LocalGitPolicy
{
    /// <summary>
    /// The checks a connection request's own two words have to pass, as field errors. An empty result
    /// means the request is well formed; a non-empty one means nothing was written and no directory
    /// was even named.
    /// </summary>
    /// <param name="rootAlias">The operator-registered root's alias.</param>
    /// <param name="relativePath">The repository's path inside that root.</param>
    public static Dictionary<string, string[]> ValidateConnection(string? rootAlias, string? relativePath)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(rootAlias))
        {
            errors["rootAlias"] = ["A registered root alias is required."];
        }
        else if (RepoRef.Local(rootAlias, "probe") is null)
        {
            // Checked by the same parser that reads the alias back, so a request cannot name an alias
            // the server would later refuse to resolve. The probe path is a single safe segment
            // because only the alias is under test here.
            errors["rootAlias"] =
            [
                $"An alias is at most {RepoRef.OwnerMaxLength} characters of letters, digits and single hyphens.",
            ];
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            errors["relativePath"] = ["A path inside the root is required."];
        }
        else if (RepoRef.Local("probe", relativePath) is null)
        {
            errors["relativePath"] =
            [
                "A path is relative to the root and holds at most "
                + $"{RepoRef.RelativePathMaxSegments} segments of letters, digits and . _ + ( ) - , "
                + $"each at most {RepoRef.RelativePathSegmentMaxLength} characters. "
                + "No leading separator, no drive letter, and no . or .. segment.",
            ];
        }

        return errors;
    }

    /// <summary>
    /// The checks a collection request's own words have to pass, as field errors.
    /// </summary>
    /// <param name="headRef">The ref to collect.</param>
    /// <param name="baseRef">The ref to compare from, or <c>null</c>.</param>
    /// <param name="expectedBundleRevision">The evidence revision the owner was looking at.</param>
    public static Dictionary<string, string[]> ValidateSync(
        string? headRef,
        string? baseRef,
        long expectedBundleRevision)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        Ref(headRef, "headRef", required: true, errors);
        Ref(baseRef, "baseRef", required: false, errors);

        if (expectedBundleRevision < 0)
        {
            errors["expectedBundleRevision"] = ["The expected evidence revision cannot be negative."];
        }

        return errors;
    }

    /// <summary>
    /// A ref is a branch, a tag or a revision, and the rules are the delivered GitHub ones plus the
    /// one that matters most for a command line: it may not begin with a dash.
    /// </summary>
    /// <remarks>
    /// Git would read a leading <c>-</c> as an option, and this package builds an argument array
    /// rather than a shell string - so the argument would still be an argument, but it would be the
    /// wrong one. Refusing the shape here means the question never arises, and
    /// <c>--end-of-options</c> is passed to Git as well so the two do not have to be the same
    /// defence.
    /// </remarks>
    public static bool IsRef(string? value)
    {
        if (!GitHubSyncPolicy.IsRef(value))
        {
            return false;
        }

        return value is not null && value[0] is not ('/' or '.' or '-');
    }

    /// <summary>
    /// Resolves a validated relative path inside a registered root, and says which of the three things
    /// happened (LOCAL-GIT-EVIDENCE-002).
    /// </summary>
    /// <remarks>
    /// <b>Three answers, not one, because the three have different owners and different repairs.</b> A
    /// path that leaves the root is a refusal the owner must not be able to work around; a path that is
    /// simply not there is a fact about a directory; and a path that is there is the only one that may be
    /// read. Collapsing them into a single <c>null</c> would make all three look like one bug.
    /// </remarks>
    /// <param name="Directory">The resolved absolute directory, when the path resolved.</param>
    /// <param name="Resolution">
    /// <see cref="LocalPathResolution.Outside"/> when a link left the root, and
    /// <see cref="LocalPathResolution.Absent"/> when the path is not there or its root is unreadable.
    /// </param>
    public static (string? Directory, LocalPathResolution Resolution) ResolveWithin(
        LocalRepositoryRoot root,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (string.IsNullOrWhiteSpace(root.Path) || !Path.IsPathRooted(root.Path))
        {
            // A registered root that is not an absolute directory cannot contain anything, and a relative
            // root is the exact shape of the bug this control exists to catch.
            return (null, LocalPathResolution.Absent);
        }

        var boundary = ResolveExisting(root.Path);
        if (boundary is null)
        {
            return (null, LocalPathResolution.Absent);
        }

        var current = boundary;

        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var next = Path.Combine(current, segment);

            if (!IsInside(boundary, next))
            {
                return (null, LocalPathResolution.Outside);
            }

            // A segment that is a link is resolved before the next segment is appended, so a link to /etc is
            // followed exactly where it appears in the path rather than at the end of it. A check that
            // resolved only the final component would call `root/link/notes.md` contained, because its last
            // segment is an ordinary file.
            var resolved = ResolveExisting(next);
            if (resolved is null)
            {
                return (null, LocalPathResolution.Absent);
            }

            if (!IsInside(boundary, resolved))
            {
                return (null, LocalPathResolution.Outside);
            }

            current = resolved;
        }

        return IsInside(boundary, current) ? (current, LocalPathResolution.Resolved) : (null, LocalPathResolution.Outside);
    }

    /// <summary>
    /// True when a path is the boundary directory itself or lies beneath it, compared on the resolved
    /// absolute form so a trailing separator and a <c>..</c> in the middle cannot both look
    /// contained.
    /// </summary>
    public static bool IsInside(string boundary, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(boundary);

        return string.Equals(candidate, prefix, StringComparison.Ordinal) ||
            candidate.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The subcommand names a run issued, comma separated, with no operand. The run-log column
    /// records these and nothing else.
    /// </summary>
    public static string CommandList(IEnumerable<string> subcommands)
    {
        ArgumentNullException.ThrowIfNull(subcommands);

        var seen = new List<string>();

        foreach (var subcommand in subcommands)
        {
            if (!seen.Contains(subcommand, StringComparer.Ordinal))
            {
                seen.Add(subcommand);
            }
        }

        return string.Join(',', seen);
    }

    /// <summary>
    /// The sanitised environment every Git process is given, so a repository-local or global
    /// configuration cannot turn a read into a pager, an editor or a credential prompt
    /// (LOCAL-GIT-EVIDENCE-002).
    /// </summary>
    /// <remarks>
    /// Three values, each for a specific reason. <c>GIT_TERMINAL_PROMPT</c> off means a repository that
    /// wants a credential answers with a refusal instead of blocking a worker forever - and local
    /// collection needs no credential at all, so a prompt could only ever be something unexpected.
    /// <c>GIT_PAGER</c> and <c>GIT_EDITOR</c> off mean no process this package starts can become an
    /// interactive terminal, which is what keeps its output bounded and its exit code meaningful. The
    /// inherited environment is otherwise left alone: <c>safe.directory</c> in an operator's global Git
    /// configuration is a real and common need, and nulling the global config to be tidy would refuse
    /// to read repositories the operator legitimately owns.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ProcessEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_PAGER"] = "cat",
        ["GIT_EDITOR"] = "true",
    };

    /// <summary>
    /// The leading arguments every Git process in this package starts with, after the executable and
    /// before the subcommand.
    /// </summary>
    /// <remarks>
    /// <b>Each one is a belt to the braces of a policy that already refuses the dangerous shapes.</b>
    /// <c>--no-pager</c> so nothing this starts can wait for a terminal; <c>core.hooksPath</c> pointed
    /// at the null device so a repository's hooks are never even looked for, let alone run;
    /// <c>diff.external</c> and <c>diff.noprefix</c> set so an external diff driver and a
    /// <c>.gitattributes</c> textconv filter cannot be invoked - both are programs the repository
    /// chooses, and this package reads a repository, it does not run one. <c>credential.helper</c>
    /// empty so a repository cannot ask Git to fetch a credential from a helper on this machine,
    /// which local collection has no use for and no business triggering. <c>safe.directory=*</c>
    /// because this product reads a directory an operator registered, and the operator is the only
    /// party who can say whether it is theirs to read.
    /// </remarks>
    public static IReadOnlyList<string> BaseArguments() =>
    [
        "--no-pager",
        "-c", "core.hooksPath=/dev/null",
        "-c", "core.pager=cat",
        "-c", "diff.external=",
        "-c", "diff.noprefix=false",
        "-c", "credential.helper=",
        "-c", "core.symlinks=true",
        "-c", "safe.directory=*",
    ];

    /// <summary>
    /// Truncates read output to a byte bound and says that it did, so a segment that stopped at a cap
    /// is never presented as one that ended.
    /// </summary>
    internal static (string Text, bool Truncated) Cap(string text, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= maxBytes)
        {
            return (text, false);
        }

        // Cut on a character boundary so the result is still valid text, and cut to at most the cap
        // rather than to a nearby character that could exceed it.
        var budget = maxBytes;
        while (budget > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, budget)) > maxBytes)
        {
            budget--;
        }

        return (text[..budget], true);
    }

    private static string? ResolveExisting(string path)
    {
        try
        {
            // `GetFullPath` normalises a trailing `.` away, so the one dot segment the reference parser
            // accepts - the one that names the registered root itself - arrives here as the boundary.
            var full = Path.GetFullPath(path);

            // Both existence checks follow a link, so a link to a directory answers as one and a link to a
            // file as one. A link that points at nothing is neither, and is refused: a path that resolves
            // nowhere cannot be inside a root.
            FileSystemInfo? info = Directory.Exists(full)
                ? new DirectoryInfo(full)
                : File.Exists(full)
                    ? new FileInfo(full)
                    : null;

            if (info is null)
            {
                return null;
            }

            // `true` follows a chain of links rather than stopping at the first, so a link that points at
            // another link is resolved to whatever that points at.
            var target = info.ResolveLinkTarget(returnFinalTarget: true);

            return Path.GetFullPath(target?.FullName ?? full);
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable or impossible path is not contained; it is simply refused, and the caller
            // is told so as a refusal rather than as an exception with a path in its message.
            return null;
        }
    }

    private static void Ref(
        string? value,
        string field,
        bool required,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors[field] = ["A branch, tag or revision is required."];
            }

            return;
        }

        if (value.Length > LocalGitLimits.RefMaxLength)
        {
            errors[field] = [$"A ref must be at most {LocalGitLimits.RefMaxLength} characters."];
        }
        else if (!IsRef(value))
        {
            errors[field] =
            [
                "A ref may hold letters, digits and . _ - / + ~ only, cannot start with a separator, and cannot start with a dash.",
            ];
        }
    }
}
