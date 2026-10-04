using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The promises this package makes about how much of a repository becomes evidence
/// (GITHUB-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// These are constants rather than settings. They are the numbers a test asserts and the numbers a
/// reader is told, and a configuration key that could move them would make both of those statements
/// conditional.
/// </para>
/// <para>
/// They are passed to the transport on the request rather than buried inside it, so a caller can read
/// the cap off the request it wrote and the transport cannot quietly apply a different one.
/// </para>
/// </remarks>
public static class GitHubLimits
{
    /// <summary>At most this many commits are read for one collection.</summary>
    public const int MaxCommits = 100;

    /// <summary>At most this many changed files are read for one collection.</summary>
    public const int MaxChangedFiles = 100;

    /// <summary>At most this many bytes of diff text become evidence for one collection.</summary>
    public const int MaxDiffBytes = 200 * 1024;

    /// <summary>The longest ref a request may name.</summary>
    public const int RefMaxLength = 255;

    /// <summary>The longest release tag a request may name.</summary>
    public const int TagMaxLength = 100;
}

/// <summary>
/// What a caller may ask a collection for, and the checks those words have to pass
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// A ref is validated here rather than being escaped and sent, because a ref is a path segment in
/// the URL the client builds: anything outside the character set GitHub itself accepts is refused
/// with a field error instead of being percent-encoded into a request whose meaning nobody can
/// predict. The repository is validated the same way, in the transport's own value type, and there
/// is no ref shape anywhere in this package that can carry a host.
/// </remarks>
public static class GitHubSyncPolicy
{
    /// <summary>
    /// The checks a sync request's own words have to pass, as field errors. An empty result means
    /// the request is well formed; a non-empty one means no job was created and nothing was called.
    /// </summary>
    /// <param name="headRef">The ref to collect.</param>
    /// <param name="baseRef">The ref to compare from, or <c>null</c>.</param>
    /// <param name="releaseTag">A release tag, or <c>null</c>.</param>
    /// <param name="expectedBundleRevision">The evidence revision the owner was looking at.</param>
    public static Dictionary<string, string[]> Validate(
        string? headRef,
        string? baseRef,
        string? releaseTag,
        long expectedBundleRevision)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        Ref(headRef, "headRef", required: true, errors);
        Ref(baseRef, "baseRef", required: false, errors);
        Tag(releaseTag, "releaseTag", errors);

        if (expectedBundleRevision < 0)
        {
            errors["expectedBundleRevision"] = ["The expected evidence revision cannot be negative."];
        }

        return errors;
    }

    /// <summary>
    /// A ref is a branch, a tag or a revision: the characters GitHub accepts in one, bounded in
    /// length, with no scheme, no host and no path separator.
    /// </summary>
    public static bool IsRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > GitHubLimits.RefMaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var acceptable = char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or '/' or '+' or '~';

            if (!acceptable)
            {
                return false;
            }
        }

        // A leading or trailing separator, a doubled separator and a lone dot segment are all ways of
        // making a path mean something other than the ref it was written to be.
        return value[0] is not ('/' or '.') &&
            value[^1] is not ('/' or '.') &&
            !value.Contains("//", StringComparison.Ordinal) &&
            !value.Contains("/./", StringComparison.Ordinal) &&
            !value.Contains("/../", StringComparison.Ordinal) &&
            !value.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>A release tag is a ref's simpler sibling: no slashes, no scheme, bounded.</summary>
    public static bool IsTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > GitHubLimits.TagMaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return value[0] is not '.' && value[^1] is not '.' && !value.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// The endpoint paths a run consulted, as the stored comma-separated list. A path is what the
    /// transport asked for and never a full URL, so a stored row cannot contain a host.
    /// </summary>
    public static string EndpointList(IReadOnlyList<GitEndpoint> endpoints)
    {
        var seen = new List<string>(endpoints.Count);

        foreach (var endpoint in endpoints)
        {
            var entry = $"{endpoint.Method} {endpoint.Path}";

            if (!seen.Contains(entry, StringComparer.Ordinal))
            {
                seen.Add(entry);
            }
        }

        return string.Join(',', seen);
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

        if (value.Length > GitHubLimits.RefMaxLength)
        {
            errors[field] = [$"A ref must be at most {GitHubLimits.RefMaxLength} characters."];
        }
        else if (!IsRef(value))
        {
            errors[field] =
            [
                "A ref may hold letters, digits and . _ - / + ~ only, and cannot start or end with a separator.",
            ];
        }
    }

    private static void Tag(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > GitHubLimits.TagMaxLength)
        {
            errors[field] = [$"A release tag must be at most {GitHubLimits.TagMaxLength} characters."];
        }
        else if (!IsTag(value))
        {
            errors[field] = ["A release tag may hold letters, digits and . _ - only."];
        }
    }
}
