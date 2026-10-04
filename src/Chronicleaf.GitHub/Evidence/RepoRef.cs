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

    private RepoRef(string owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    /// <summary>The account or organisation that owns the repository.</summary>
    public string Owner { get; }

    /// <summary>The repository's own name, without the owner.</summary>
    public string Name { get; }

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

        return new RepoRef(parsedOwner, parsedName);
    }

    /// <summary>The single path segment form used to build a request URL, escaped.</summary>
    public string ToPathSegment() => $"{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}";

    /// <summary>
    /// The external reference recorded on collected evidence. It names the repository the way a
    /// person would, and it is derived from the two validated segments rather than from anything a
    /// caller supplied.
    /// </summary>
    public string ToSourceRef() => $"github:{Owner}/{Name}";

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
}
