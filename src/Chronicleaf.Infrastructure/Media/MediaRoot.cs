using Chronicleaf.Application.Media;
using Microsoft.Extensions.Logging;
using Platform.Storage.Keys;

namespace Chronicleaf.Infrastructure.Media;

/// <summary>
/// The one directory images may exist in, and the only way an object key becomes a path
/// (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// Two independent checks stand between a key and the filesystem, and both are needed because they
/// catch different things.
/// </para>
/// <para>
/// The first is the adopted platform's own <see cref="StorageObjectKey"/>, which refuses a key that is
/// empty, contains a control character, begins or ends with a separator, contains an empty segment
/// or contains a <c>.</c> or <c>..</c> segment, and refuses one over 1024 characters. It is a
/// value-type constructor, so a key that was not built by this package cannot exist in the first
/// place: a hostile string fails to become a key rather than becoming a bad one. That closes
/// <c>..</c> traversal and absolute paths completely, and it is the shared platform's rule rather than
/// a second one written here.
/// </para>
/// <para>
/// The second is this class, and it exists because a valid key is not the same as a safe path.
/// <see cref="StorageObjectKey"/> reasons about a key's own text; it cannot know that one of the key's
/// segments is a symbolic link. So every path handed to the storage provider is built here, walked
/// one segment at a time, and checked twice: lexically, that the composed path is still under the
/// root, and physically, that no segment on the way is a link whose real target leaves the root. A
/// lexical check alone would pass <c>media/escape/secret.png</c> when <c>escape</c> is a symlink to
/// <c>/etc</c>, because the composed string genuinely does begin with the root; only resolving the
/// link shows where the read or write really lands.
/// </para>
/// <para>
/// The root itself is resolved the same way, once, at construction. An operator who points
/// <c>Chronicleaf:MediaRoot</c> at a symlinked mount is a normal deployment, so the root is accepted
/// and its <em>real</em> target becomes the boundary every check is made against - the bytes still
/// live where the operator said they would, and nothing above them is reachable. What is refused is a
/// link <em>inside</em> the root that points out of it.
/// </para>
/// </remarks>
public sealed class MediaRoot
{
    private readonly string _webRootPath;
    private readonly ILogger<MediaRoot> _logger;

    private MediaRoot(string realRoot, string configuredPath, string webRootPath, ILogger<MediaRoot> logger)
    {
        Root = realRoot;
        ConfiguredPath = configuredPath;
        _webRootPath = webRootPath;
        _logger = logger;
    }

    /// <summary>
    /// The absolute, symlink-resolved root. Every containment decision is made against this value
    /// and never against the configured string.
    /// </summary>
    public string Root { get; }

    /// <summary>The path exactly as configured, for diagnostics that name the operator's own value.</summary>
    public string ConfiguredPath { get; }

    /// <summary>
    /// Resolves the configured root, refuses a root inside <c>wwwroot</c>, and creates the directory.
    /// </summary>
    /// <remarks>
    /// Failing here rather than at first use is deliberate. A root inside <c>wwwroot</c> would be
    /// served by the static file middleware to any unauthenticated caller, so a start-up failure is
    /// the honest outcome for a misconfiguration that would otherwise leak silently. The check is
    /// performed on the real root, after symlink resolution, because a symlink into <c>wwwroot</c> is
    /// exactly the case a purely textual check would miss.
    /// </remarks>
    public static MediaRoot Resolve(MediaSettings settings, string contentRootPath, string webRootPath, ILogger<MediaRoot> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var configured = settings.MediaRootPath;
        var absolute = Path.GetFullPath(
            Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured));

        var real = ResolveExistingLinks(absolute);
        var webRoot = string.IsNullOrWhiteSpace(webRootPath)
            ? Path.Combine(contentRootPath, MediaSettings.WebRootDirectoryName)
            : Path.GetFullPath(webRootPath);

        if (IsUnder(real, webRoot) || IsUnder(webRoot, real))
        {
            throw new InvalidOperationException(
                "Chronicleaf:MediaRoot must not be the published wwwroot or inside it. Stored images are "
                + "private and the static file middleware serves that directory without an owner session.");
        }

        Directory.CreateDirectory(real);

        return new MediaRoot(real, configured, webRoot, logger);
    }

    /// <summary>
    /// Turns a validated object key into an absolute path inside this root, or refuses it.
    /// </summary>
    /// <remarks>
    /// The key has already been through <see cref="StorageObjectKey"/> by the time it arrives, so the
    /// walk below is about the filesystem rather than about the key's text: it composes the path one
    /// segment at a time and checks each intermediate against the root both lexically and after
    /// resolving links. A key whose first segment is not a link is therefore safe by construction, and
    /// a later segment that is one is caught at the segment where the link actually is rather than
    /// after the damage.
    /// </remarks>
    /// <exception cref="MediaOutsideRootException">A segment leaves the root (404).</exception>
    public string ResolveInside(StorageObjectKey key)
    {
        var current = Root;

        foreach (var segment in key.Value.Split('/'))
        {
            current = Path.Combine(current, segment);

            if (!IsUnder(current, Root))
            {
                Refuse(key, "the composed path leaves the media root");
            }

            var link = ResolveLink(current);
            if (link is not null && !IsUnder(link, Root))
            {
                Refuse(key, "a path segment is a link whose target leaves the media root");
            }
        }

        // The final path may itself be a link even when no directory above it is, so it is checked
        // once more as a whole.
        var finalLink = ResolveLink(current);
        if (finalLink is not null && !IsUnder(finalLink, Root))
        {
            Refuse(key, "the object is a link whose target leaves the media root");
        }

        return current;
    }

    /// <summary>
    /// The same check as <see cref="ResolveInside"/>, reported rather than thrown.
    /// </summary>
    /// <remarks>
    /// Used where a refusal is data rather than a request outcome - the retirement path's decision to
    /// release bytes, and the purge's enumeration - so those paths do not have to wrap a guard in a
    /// try block to learn whether a stored key is usable.
    /// </remarks>
    public bool TryResolveInside(string key, out string resolvedPath)
    {
        try
        {
            resolvedPath = ResolveInside(new StorageObjectKey(key));

            return true;
        }
        catch (Exception exception) when (exception is MediaOutsideRootException or ArgumentException or ArgumentOutOfRangeException)
        {
            resolvedPath = string.Empty;

            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the root itself or sits under it. A prefix comparison
    /// on the full string, with a separator appended to the root, so <c>/media-other</c> is not
    /// mistaken for a child of <c>/media</c>.
    /// </summary>
    private static bool IsUnder(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.Ordinal)
        || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Follows a chain of symbolic links to its final target, or returns <c>null</c> when the path is
    /// not a link. A path that does not exist, or that this process may not inspect, is treated as
    /// not-a-link: it cannot redirect a read or a write anywhere, and refusing to inspect the root's
    /// own children would make the store unusable on a root with restricted permissions.
    /// </summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The real path of the deepest existing ancestor of <paramref name="path"/>, with everything
    /// below it re-appended. This is what resolves a root that does not exist yet, and what makes a
    /// link anywhere on the way to it visible.
    /// </summary>
    private static string ResolveExistingLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var remainder = new Stack<string>();
        var current = full;

        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            remainder.Push(Path.GetFileName(current));
            current = parent;
        }

        var resolved = ResolveLink(current) ?? current;

        foreach (var segment in remainder)
        {
            resolved = Path.Combine(resolved, segment);
        }

        return Path.GetFullPath(resolved);
    }

    private void Refuse(StorageObjectKey key, string reason)
    {
        // The reason names the rule, never the resolved path: this message reaches a caller's
        // response body, and a resolved path is a disclosure of the host's filesystem layout.
        _logger.LogWarning(
            "Refused a media object key that leaves the media root ({Reason}).",
            reason);

        throw new MediaOutsideRootException();
    }
}
