using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Content;

/// <summary>
/// One approved picture a site post may carry (STATIC-BLOG-EXPORT-001).
/// </summary>
/// <remarks>
/// The only pictures a site may pack: the selected pictures of the evidence head the live
/// approval is bound to, with the bytes read back out of the delivered media store. A picture
/// whose bytes are gone is a refused export, never a silently picture-less archive.
/// </remarks>
/// <param name="MediaId">The stored media this picture was read from.</param>
/// <param name="Caption">The owner's caption.</param>
/// <param name="ContentType">The decoded media type of the stored bytes.</param>
/// <param name="Bytes">The exact stored bytes, packed once under a generated path.</param>
public sealed record SitePostImage(
    Guid MediaId,
    string Caption,
    string ContentType,
    byte[] Bytes);

/// <summary>
/// One approved post a site depicts (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <param name="AssetId">The asset the approved revision belongs to.</param>
/// <param name="MilestoneId">The confirmed milestone the asset belongs to.</param>
/// <param name="MilestoneTitle">The milestone title the revision carries.</param>
/// <param name="OccurredAt">When the milestone occurred; the timeline order.</param>
/// <param name="ContentRevision">The approved revision number.</param>
/// <param name="BodyHash">The digest of exactly the approved bytes.</param>
/// <param name="EvidenceRevision">The evidence head revision the approval was bound to.</param>
/// <param name="BodyMarkdown">The approved revision's exact stored bytes.</param>
/// <param name="Pictures">The approval-covered pictures, in any order; packed in media-id order.</param>
public sealed record SitePostInput(
    Guid AssetId,
    Guid MilestoneId,
    string MilestoneTitle,
    DateTimeOffset OccurredAt,
    long ContentRevision,
    string BodyHash,
    long EvidenceRevision,
    string BodyMarkdown,
    IReadOnlyList<SitePostImage> Pictures);

/// <summary>
/// One built site: the deterministic archive and the manifest describing it
/// (STATIC-BLOG-EXPORT-002).
/// </summary>
/// <param name="ZipBytes">The complete portable site: index, posts, pictures, manifest, .nojekyll.</param>
/// <param name="ManifestJson">The checksum manifest, in canonical order.</param>
/// <param name="ContentSha256">Lowercase SHA-256 hex over the canonical file listing.</param>
/// <param name="PostCount">How many posts the archive depicts.</param>
/// <param name="ImageCount">How many distinct pictures the archive packs.</param>
public sealed record StaticBlogSite(
    byte[] ZipBytes,
    string ManifestJson,
    string ContentSha256,
    int PostCount,
    int ImageCount);

/// <summary>
/// Renders approved snapshots into a portable static site (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here executes and nothing here fetches.</b> Each post body is rendered through the
/// HTML export package's own sanitizing adapter - the same fixed template the single-file export
/// uses - so a script, a frame or an unsafe protocol in approved text stays characters here for
/// exactly the reason it stays characters there. Source image markup never becomes an image for
/// the same reason: the only pictures in the archive are the approval-covered ones, packed once
/// under generated paths.
/// </para>
/// <para>
/// <b>The archive is deterministic.</b> Entries are packed in sorted order with a fixed entry
/// timestamp, the manifest lists every file with its digest, and the content digest covers the
/// canonical file listing only - no timestamp reaches it. The same immutable selection therefore
/// builds the same checksums twice, which is the boundary scenario.
/// </para>
/// <para>
/// <b>Links are relative.</b> Index, posts and pictures address each other relatively, so the
/// archive reads offline and under any base path it is deployed at. The base path itself travels
/// as deployment metadata in the manifest and the index receipt, never as a filesystem path and
/// never inside a packed entry name: every entry name is generated here from an id.
/// </para>
/// </remarks>
public static class StaticBlogSiteBuilder
{
    /// <summary>The template version the manifest and the index receipt carry.</summary>
    public const string TemplateVersion = "static-blog/1";

    /// <summary>At most 500 posts in one site, the design's bound.</summary>
    public const int MaxPosts = 500;

    /// <summary>200 MiB: the design's bound on one site archive.</summary>
    public const long MaxSiteBytes = 200L * 1024 * 1024;

    /// <summary>Bounded to readable deployment paths rather than filesystem depth.</summary>
    public const int MaxBasePathLength = 128;

    /// <summary>
    /// The fixed entry timestamp every packed entry carries. ZIP entries record a time, so two
    /// builds can only agree byte for byte when that time is not "now".
    /// </summary>
    public static readonly DateTimeOffset ArchiveTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string IndexPath = "index.html";
    private const string ManifestPath = "manifest.json";
    private const string PostsDirectory = "posts";
    private const string AssetsDirectory = "assets";
    private const string NoJekyllPath = ".nojekyll";

    /// <summary>
    /// Renders the approved posts into the portable site.
    /// </summary>
    /// <exception cref="ValidationFailedException">The selection or base path is not one this package accepts (422).</exception>
    /// <exception cref="Media.UnsupportedMediaTypeException">A picture's type is not one this site packs (415).</exception>
    /// <exception cref="PayloadTooLargeException">The site would exceed the archive bound (413).</exception>
    public static async Task<StaticBlogSite> BuildAsync(
        IReadOnlyList<SitePostInput> posts,
        string? basePath,
        string siteTitle,
        IContentExportAdapter htmlExport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(htmlExport);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteTitle);

        var normalizedBasePath = NormalizeBasePath(basePath);

        if (posts.Count == 0)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A static site depicts at least one approved post, so name one.");
        }

        if (posts.Count > MaxPosts)
        {
            throw new ValidationFailedException(
                "approvedAssetIds",
                $"A static site depicts at most {MaxPosts} posts.");
        }

        var seen = new HashSet<Guid>();
        foreach (var post in posts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (post.AssetId == Guid.Empty || !seen.Add(post.AssetId))
            {
                throw new ValidationFailedException(
                    "approvedAssetIds",
                    "A static site names each approved asset once, so name each once.");
            }

            if (post.ContentRevision < 1)
            {
                throw new ValidationFailedException(
                    "approvedAssetIds",
                    "An exported post follows a stored revision, so name one.");
            }
        }

        // The timeline order: oldest first, the same projection the milestone timeline serves.
        var ordered = posts
            .OrderBy(post => post.OccurredAt)
            .ThenBy(post => post.MilestoneId)
            .ThenBy(post => post.AssetId)
            .ToList();

        var documents = new Dictionary<Guid, byte[]>(ordered.Count);
        var siteImages = new SortedDictionary<Guid, byte[]>(Comparer<Guid>.Default);

        foreach (var post in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pictures = post.Pictures
                .OrderBy(picture => picture.MediaId)
                .Select(picture => new ApprovedExportPicture(
                    picture.MediaId,
                    picture.Caption,
                    picture.ContentType,
                    picture.Bytes))
                .ToList();

            var snapshot = new ApprovedAssetSnapshot(
                post.AssetId,
                post.ContentRevision,
                post.MilestoneTitle,
                post.BodyMarkdown,
                post.BodyHash,
                post.EvidenceRevision,
                pictures);

            var artifact = await htmlExport.ExportAsync(snapshot, cancellationToken).ConfigureAwait(false);

            byte[] document;
            if (string.Equals(artifact.MediaType, HtmlExportAdapter.ZipMediaType, StringComparison.Ordinal))
            {
                document = RelinkDocument(artifact.Bytes, pictures);
            }
            else
            {
                document = artifact.Bytes;
            }

            documents[post.AssetId] = document;

            foreach (var picture in pictures)
            {
                siteImages.TryAdd(picture.MediaId, picture.Bytes);
            }
        }

        var index = RenderIndex(siteTitle, normalizedBasePath, ordered);
        var indexBytes = Encoding.UTF8.GetBytes(index);

        var postPaths = ordered.ToDictionary(
            post => post.AssetId,
            post => string.Create(
                CultureInfo.InvariantCulture, $"{PostsDirectory}/{post.AssetId:N}.html"));

        var manifestPosts = ordered.Select(post => new
        {
            assetId = post.AssetId.ToString("D"),
            milestoneId = post.MilestoneId.ToString("D"),
            contentRevision = post.ContentRevision,
            bodyHash = post.BodyHash,
            evidenceRevision = post.EvidenceRevision,
            path = postPaths[post.AssetId],
        }).ToList();

        var fileEntries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [IndexPath] = indexBytes,
        };

        foreach (var post in ordered)
        {
            fileEntries[postPaths[post.AssetId]] = documents[post.AssetId];
        }

        foreach (var (mediaId, bytes) in siteImages)
        {
            fileEntries[string.Create(CultureInfo.InvariantCulture, $"{AssetsDirectory}/{mediaId:N}.png")] = bytes;
        }

        var files = fileEntries
            .Select(entry => new
            {
                path = entry.Key,
                sha256 = Hash(entry.Value),
                bytes = entry.Value.LongLength,
            })
            .ToList();

        var contentSha = Hash(Encoding.UTF8.GetBytes(string.Concat(
            files.Select(file => string.Create(
                CultureInfo.InvariantCulture, $"{file.path}\n{file.sha256}\n")))));

        var manifest = JsonSerializer.Serialize(new
        {
            templateVersion = TemplateVersion,
            basePath = normalizedBasePath,
            posts = manifestPosts,
            files,
            contentSha256 = contentSha,
        });

        var manifestBytes = Encoding.UTF8.GetBytes(manifest);

        long planned = manifestBytes.LongLength + 1024;
        foreach (var entry in fileEntries.Values)
        {
            planned += entry.LongLength;
        }

        if (planned > MaxSiteBytes)
        {
            throw new PayloadTooLargeException(MaxSiteBytes);
        }

        var packed = PackArchive(fileEntries, manifestBytes, cancellationToken);

        if (packed.LongLength > MaxSiteBytes)
        {
            throw new PayloadTooLargeException(MaxSiteBytes);
        }

        return new StaticBlogSite(
            packed,
            manifest,
            contentSha,
            ordered.Count,
            siteImages.Count);
    }

    /// <summary>
    /// The canonical selection hash: lowercase SHA-256 hex over the sorted asset ids.
    /// </summary>
    public static string SelectionHashFor(IEnumerable<Guid> assetIds)
    {
        ArgumentNullException.ThrowIfNull(assetIds);

        var canonical = string.Join(
            "\n",
            assetIds.OrderBy(id => id).Select(id => id.ToString("D")));

        return Hash(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>
    /// Normalizes a deployment base path to a relative URL segment, never a filesystem path.
    /// </summary>
    /// <exception cref="ValidationFailedException">The base path names a filesystem location (422).</exception>
    public static string NormalizeBasePath(string? basePath)
    {
        var trimmed = (basePath ?? "/").Trim();

        if (trimmed.Length == 0)
        {
            trimmed = "/";
        }

        if (trimmed.Length > MaxBasePathLength)
        {
            throw new ValidationFailedException(
                "basePath",
                $"A base path is at most {MaxBasePathLength} characters.");
        }

        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "basePath",
                "A base path is a relative URL segment starting with /, never a filesystem path.");
        }

        var normalized = trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;

        if (normalized.Length == 0)
        {
            normalized = "/";
        }

        if (normalized != "/")
        {
            // The leading slash leaves the first split segment empty; every other segment must
            // name a real URL part rather than the current or parent directory.
            var segments = normalized.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (segment.Length == 0 || segment is "." or "..")
                {
                    throw new ValidationFailedException(
                        "basePath",
                        "A base path names URL segments, never the current or parent directory.");
                }

                foreach (var character in segment)
                {
                    var allowed = char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '~';
                    if (!allowed)
                    {
                        throw new ValidationFailedException(
                            "basePath",
                            "A base path carries URL segments of letters, digits, dots, underscores, dashes and tildes.");
                    }
                }
            }
        }

        return normalized;
    }

    /// <summary>
    /// Rewrites a per-post archive's packaged image paths to the site's shared asset paths.
    /// </summary>
    /// <remarks>
    /// The HTML adapter packs a post's pictures under generated <c>images/image-{i}.png</c> paths
    /// aligned with the ordered picture list it was handed - the same order this builder passed.
    /// The site packs each distinct picture once under <c>assets/{mediaId}.png</c>, so the
    /// document's references move with them. Only the exact generated paths are rewritten: owner
    /// text that merely mentions such a path is escaped output and can never match a quoted
    /// <c>src</c>.
    /// </remarks>
    private static byte[] RelinkDocument(byte[] postArchive, IReadOnlyList<ApprovedExportPicture> pictures)
    {
        using var archive = new ZipArchive(new MemoryStream(postArchive), ZipArchiveMode.Read);

        var index = archive.GetEntry("index.html")
            ?? throw new InternalFailureException(
                "A post archive this build packed carries no index, so it cannot be published.");

        string document;
        using (var stream = index.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            document = reader.ReadToEnd();
        }

        for (var position = 0; position < pictures.Count; position++)
        {
            var source = string.Create(CultureInfo.InvariantCulture, $"src=\"images/image-{position}.png\"");
            var target = string.Create(CultureInfo.InvariantCulture, $"src=\"../assets/{pictures[position].MediaId:N}.png\"");

            document = document.Replace(source, target, StringComparison.Ordinal);
        }

        return Encoding.UTF8.GetBytes(document);
    }

    private static string RenderIndex(
        string siteTitle,
        string basePath,
        IReadOnlyList<SitePostInput> ordered)
    {
        var body = new StringBuilder();
        body.Append("<h1>");
        body.Append(Escape(siteTitle));
        body.Append("</h1>\n<ul>\n");

        foreach (var post in ordered)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"posts/{post.AssetId:N}.html");

            body.Append("<li><a href=\"");
            body.Append(path);
            body.Append("\">");
            body.Append(Escape(post.MilestoneTitle));
            body.Append("</a> <time datetime=\"");
            body.Append(post.OccurredAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            body.Append("\">");
            body.Append(post.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            body.Append("</time> <span>evidence revision ");
            body.Append(post.EvidenceRevision.ToString(CultureInfo.InvariantCulture));
            body.Append("</span></li>\n");
        }

        body.Append("</ul>\n");

        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + $"<title>{Escape(siteTitle)}</title>\n"
            + $"<meta name=\"chronicleaf-template-version\" content=\"{TemplateVersion}\">\n"
            + $"<meta name=\"chronicleaf-base-path\" content=\"{EscapeAttribute(basePath)}\">\n"
            + "</head>\n<body>\n"
            + body.ToString()
            + "</body>\n</html>\n";
    }

    private static byte[] PackArchive(
        SortedDictionary<string, byte[]> files,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        using var packed = new MemoryStream();

        using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The fixed order and the fixed timestamp are what make two builds of the same
            // selection agree byte for byte: nothing here records when the build ran.
            WriteEntry(archive, NoJekyllPath, []);
            WriteEntry(archive, IndexPath, files[IndexPath]);

            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(entry.Key, IndexPath, StringComparison.Ordinal))
                {
                    continue;
                }

                WriteEntry(archive, entry.Key, entry.Value);
            }

            WriteEntry(archive, ManifestPath, manifestBytes);

            cancellationToken.ThrowIfCancellationRequested();
        }

        return packed.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = ArchiveTimestamp;
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string EscapeAttribute(string value) => WebUtility.HtmlEncode(value);
}
