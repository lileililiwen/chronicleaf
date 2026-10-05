using System.Text.RegularExpressions;

namespace Chronicleaf.GitHub.Evidence;

/// <summary>
/// A public GitHub repository named the way GitHub names one: an owner and a repository, and
/// nothing else (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no URL here, and that is the security property rather than a simplification.</b> A
/// request body that could carry <c>https://…</c> would let an owner name any host, and the server
/// would then send a request to it - with the vault's credential on it, if one were configured. Two
/// name segments, each checked against GitHub's own character rules and each capped in length,
/// cannot express a host, a port, a path, a query or a fragment, so the only place a URL could have
/// come from is nowhere.
/// </para>
/// <para>
/// The values are the ones GitHub itself accepts, so a name that passes here is one GitHub will
/// resolve. They are stored and compared ordinally: a repository name is case-insensitive to
/// GitHub, but two spellings of the same repository are the same fact and must not become two
/// evidence sources, so the comparison that matters happens on the resolved identity (the numeric
/// repository id) and never on the spelling.
/// </para>
/// </remarks>
public sealed partial record RepoRef
{
    /// <summary>The longest owner segment GitHub accepts.</summary>
    public const int OwnerMaxLength = 39;

    /// <summary>The longest repository segment GitHub accepts.</summary>
    public const int NameMaxLength = 100;

    /// <summary>
    /// The longest relative path a local reference may name, and the longest single segment of one.
    /// </summary>
    /// <remarks>
    /// Bounded because the value becomes an argument to a process and a component of a stored
    /// reference, and neither of those should be able to carry an unbounded string. It is generous
    /// enough for a deeply nested repository inside a monorepo and far too short to be a whole
    /// filesystem path.
    /// </remarks>
    public const int RelativePathMaxLength = 255;

    /// <summary>The longest one segment of a relative path may be.</summary>
    public const int RelativePathSegmentMaxLength = 100;

    /// <summary>The deepest relative path a reference may name.</summary>
    public const int RelativePathMaxSegments = 16;

    private RepoRef(string owner, string name, string sourcePrefix, bool isLocal)
    {
        Owner = owner;
        Name = name;
        SourcePrefix = sourcePrefix;
        IsLocal = isLocal;
    }

    /// <summary>The account or organisation that owns the repository.</summary>
    public string Owner { get; }

    /// <summary>The repository's own name, without the owner.</summary>
    public string Name { get; }

    /// <summary>
    /// The word that precedes this reference in collected evidence: <c>github</c> for a repository
    /// read over HTTP, <c>local-git</c> for one read off the operator's own disk.
    /// </summary>
    /// <remarks>
    /// A constant of this contract rather than something a caller supplies, so a source reference in
    /// stored evidence always says which transport read it. Two transports that produced the same
    /// word would make "where did this fact come from" unanswerable after the fact.
    /// </remarks>
    public string SourcePrefix { get; }

    /// <summary>
    /// True when this reference names a directory inside an operator-registered root rather than a
    /// repository on a host (LOCAL-GIT-EVIDENCE-002).
    /// </summary>
    /// <remarks>
    /// A local reference is a different kind of thing from a GitHub one and the difference is carried
    /// by the type rather than by a convention. <see cref="ToPathSegment"/> refuses to build a URL from
    /// one at all, so there is no code path by which a registered directory could become a request to
    /// <c>api.github.com</c>.
    /// </remarks>
    public bool IsLocal { get; }

    /// <summary>
    /// Parses an owner and a repository name, or returns <c>null</c> when either is not one.
    /// </summary>
    /// <remarks>
    /// Deliberately a null return rather than an exception: a repository named in a request is
    /// untrusted input, and the caller's honest answer to a bad name is a validation refusal, not
    /// a stack trace.
    /// </remarks>
    public static RepoRef? Parse(string? owner, string? name)
    {
        var parsedOwner = Clean(owner);
        var parsedName = Clean(name);

        if (parsedOwner is null || parsedName is null)
        {
            return null;
        }

        if (parsedOwner.Length is 0 or > OwnerMaxLength || !OwnerPattern().IsMatch(parsedOwner))
        {
            return null;
        }

        if (parsedName.Length is 0 or > NameMaxLength || !NamePattern().IsMatch(parsedName))
        {
            return null;
        }

        // "." and ".." are legal characters in a path segment and are not legal repository names;
        // refusing them here is what keeps a name from ever being read as traversal.
        if (parsedName is "." or "..")
        {
            return null;
        }

        return new RepoRef(parsedOwner, parsedName, "github", isLocal: false);
    }

    /// <summary>
    /// Parses a registered root alias and a path relative to it, or returns <c>null</c> when either is
    /// not one (LOCAL-GIT-EVIDENCE-002).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two segments are not the GitHub ones. The alias is the same shape a GitHub owner has,
    /// because it is an operator-chosen name and there is no reason to invent a second vocabulary for
    /// it; the path is the part that differs, because a repository inside a monorepo is not a single
    /// segment and a form that could not name one would be a form that could not do the job.
    /// </para>
    /// <para>
    /// <b>What this cannot be is a path that reaches outside.</b> No leading separator, no drive
    /// letter, no backslash, no empty or dot segment, no <c>..</c> at any depth, and a bounded number
    /// of bounded segments. Every one of those is a spelling that a filesystem would resolve to a
    /// directory the operator never registered, and this is a pure function of the text - the
    /// containment check against the registered root is made afterwards, by the filesystem, because
    /// only the filesystem can say what a symlink points at.
    /// </para>
    /// <para>
    /// <b>A backslash is refused rather than folded into a separator.</b> It is a legal character in a
    /// filename on the platform this product runs on, so rewriting <c>a\b</c> to <c>a/b</c> would read a
    /// different directory than the one that was named - a silent substitution is worse than a refusal,
    /// and the refusal is a 422 the owner can act on.
    /// </para>
    /// <para>
    /// <b>A single <c>.</c> is the root itself, and is the one dot segment accepted.</b> An operator who
    /// registers a directory and points a project at that directory has to be able to say so, and <c>.</c>
    /// is the only way to say it. Every other dot segment is refused, so this cannot become a way of
    /// spelling a traversal.
    /// </para>
    /// </remarks>
    public static RepoRef? Local(string? rootAlias, string? relativePath)
    {
        var alias = Clean(rootAlias);
        var path = Clean(relativePath);

        if (alias is null || path is null)
        {
            return null;
        }

        if (alias.Length is 0 or > OwnerMaxLength || !OwnerPattern().IsMatch(alias))
        {
            return null;
        }

        if (path.Length is 0
            || path.Length > RelativePathMaxLength
            || path[0] is '/'
            || path.Contains(':', StringComparison.Ordinal)
            || path.Contains('\\'))
        {
            return null;
        }

        if (path is RootItself)
        {
            return new RepoRef(alias, path, "local-git", isLocal: true);
        }

        var segments = path.Split('/');
        if (segments.Length > RelativePathMaxSegments)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > RelativePathSegmentMaxLength || segment is "." or "..")
            {
                return null;
            }

            if (!RelativePathSegmentPattern().IsMatch(segment))
            {
                return null;
            }
        }

        return new RepoRef(alias, path, "local-git", isLocal: true);
    }

    /// <summary>
    /// The one relative path that names the registered root rather than something inside it.
    /// </summary>
    /// <remarks>
    /// Written as a constant because both the reference parser and the database check constraint have to
    /// agree on this exact spelling, and a value that was spelled out in two places would eventually be
    /// spelled out differently in one of them.
    /// </remarks>
    public const string RootItself = ".";

    /// <summary>The single path segment form used to build a request URL, escaped.</summary>
    /// <exception cref="InvalidOperationException">
    /// This reference names a directory rather than a host repository. There is no URL to build, and
    /// building one anyway is the exact mistake the local variant exists to make impossible.
    /// </exception>
    public string ToPathSegment() => IsLocal
        ? throw new InvalidOperationException(
            "A local repository reference has no host path segment; it names a directory inside a registered root.")
        : $"{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}";

    /// <summary>
    /// The external reference recorded on collected evidence. It names the repository the way a
    /// person would, and it is derived from the two validated segments rather than from anything a
    /// caller supplied.
    /// </summary>
    /// <remarks>
    /// The prefix names the transport, so <c>local-git:workspace/services/api</c> and
    /// <c>github:owner/name</c> can never be mistaken for one another in a stored bundle, and neither
    /// of them can be read as a path to open.
    /// </remarks>
    public string ToSourceRef() => $"{SourcePrefix}:{Owner}/{Name}";

    public override string ToString() => $"{Owner}/{Name}";

    private static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    // A user or organisation name: alphanumerics and single hyphens, never leading or trailing.
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9]))*$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();

    // A repository name: the characters GitHub allows. Note the absence of '/', ':', '?' and '#',
    // which is also what stops a name from ever being read as a URL.
    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    // One segment of a relative path. Wider than a GitHub name - spaces and '+' are ordinary in a
    // directory - and still no separator, no dot segment and no leading dash, because a segment that
    // could be read as an option is a segment that could change what the command meant.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._ +()\\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RelativePathSegmentPattern();
}
