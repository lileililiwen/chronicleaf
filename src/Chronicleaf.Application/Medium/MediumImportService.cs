using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Medium;

/// <summary>
/// The Medium manual-import use cases over the delivered review and asset contracts
/// (MEDIUM-IMPORT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An import package is derived, never generated, and never sent.</b> The source is an
/// approved revision of any content asset; the package re-serves that revision's exact bytes
/// through the review package's own Markdown and HTML exports, then packs them with the
/// approval-covered pictures and the manual import instructions. There is no provider behind
/// this service, so there is no consent to check and no key to lease - and no transport behind
/// it either, so there is nothing a caller could supply that would reach a Medium endpoint.
/// </para>
/// <para>
/// <b>Approval is reused, never redefined.</b> Both inner exports re-check the live approval
/// under exactly the bindings the review package's own export re-checks: never approved is
/// <c>approval_required</c>, once approved but moved is <c>approval_stale</c>, and a picture
/// whose bytes are gone is <c>media_unavailable</c>. A title or body edit appends a head the
/// approval no longer names, so the edited derivative exports nothing until it carries an
/// approval of its own.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The
/// article, the instructions' provenance values and the pictures travel from stored exports
/// into a stored nothing - this service stores nothing at all - and none appears in a log
/// line, in an exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class MediumImportService(
    IContentReviewService reviews,
    IContentAssetStore assets,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<MediumImportService> logger) : IMediumImportService
{
    /// <summary>The audit action recorded when an approved article is packaged for Medium.</summary>
    public const string ImportPackagedAction = "medium-import.exported";

    /// <summary>The template version recorded in the manifest, so a stored package names the template that packed it.</summary>
    public const string TemplateVersion = "medium-import/1";

    /// <summary>The fixed timestamp every ZIP entry carries, so the same approved revision packs identically.</summary>
    public static readonly DateTimeOffset ArchiveTimestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The article document inside the import package.</summary>
    public const string ArticleFileName = "article.html";

    /// <summary>The article Markdown beside the document, for Medium's own import flow.</summary>
    public const string MarkdownFileName = "article.md";

    /// <summary>The checksum manifest inside the import package.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>The manual import instructions inside the import package.</summary>
    public const string InstructionsFileName = "MEDIUM-IMPORT.txt";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<MediumImportExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        // Both shapes come from the review package's own exports, so the live approval, the
        // body hash, the evidence head and the media selection are re-checked twice by the
        // code that owns them rather than once by a copy of them. Either call refusing is the
        // package's answer: the two shapes cannot disagree about what is authorized, because
        // neither shape decides.
        var markdown = await reviews
            .ExportAsync(owner, assetId, ContentReviewService.MarkdownFormat, revision, cancellationToken)
            .ConfigureAwait(false);

        var html = await reviews
            .ExportHtmlAsync(owner, assetId, revision, cancellationToken)
            .ConfigureAwait(false);

        var markdownBytes = Encoding.UTF8.GetBytes(markdown.BodyMarkdown);

        var (documentBytes, images) = UnpackHtmlArtifact(html);

        var planned = (long)documentBytes.Length + markdownBytes.Length + images.Sum(image => (long)image.Bytes.Length) + 8192;

        if (planned > HtmlExportAdapter.MaxArchiveBytes)
        {
            throw new PayloadTooLargeException(HtmlExportAdapter.MaxArchiveBytes);
        }

        var instructions = InstructionsFor(assetId, markdown);

        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                templateVersion = TemplateVersion,
                assetId = assetId,
                contentRevision = markdown.ContentRevision,
                bodyHash = markdown.BodyHash,
                articleFile = ArticleFileName,
                markdownFile = MarkdownFileName,
                instructionsFile = InstructionsFileName,
                images = images.Select(image => new
                {
                    file = image.Path,
                    sha256 = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant(),
                    bytes = image.Bytes.Length,
                    mediaType = image.MediaType,
                }).ToArray(),
            },
            ManifestJson);

        var orderedImages = images.OrderBy(image => image.Path, StringComparer.Ordinal).ToList();
        var packed = PackArchive(documentBytes, markdownBytes, orderedImages, manifest, instructions, cancellationToken);

        await unitOfWork.ExecuteAsync(
            token =>
            {
                // The audit row names the revision and the shape, never the prose: an export is
                // a record that bytes left the building, not a copy of them.
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ImportPackagedAction,
                    assetId,
                    markdown.ContentRevision,
                    $"revision={markdown.ContentRevision.ToString(CultureInfo.InvariantCulture)};"
                    + "format=medium;"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Content asset {AssetId} revision {Revision} packaged for manual Medium import.",
            assetId,
            markdown.ContentRevision);

        return new MediumImportExport(
            $"chronicleaf-{assetId:N}-r{markdown.ContentRevision}-medium.zip",
            "application/zip",
            packed,
            Convert.ToHexString(SHA256.HashData(packed)).ToLowerInvariant(),
            assetId,
            markdown.ContentRevision);
    }

    /// <inheritdoc />
    public Task PublishAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Existence is the only state publication consults, so that a foreign id is still the
        // delivered 404 rather than a transport refusal that confirms nothing. Everything else
        // about the asset - approved, edited, moved - is irrelevant: there is no transport for
        // any of it to travel on.
        return PublishAfterResolveAsync(owner, assetId, cancellationToken);
    }

    private async Task PublishAfterResolveAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        _ = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // No credential is leased, no job is enqueued, no audit row is written and no socket is
        // opened: a refusal that changed anything would be an account action of its own.
        throw new MediumPublishUnsupportedException();
    }

    /// <summary>
    /// Splits the HTML export into the article document and its pictures. A single-file export
    /// is the document with no pictures; a ZIP export contributes its index as the document and
    /// its image entries verbatim, so their references resolve exactly as the delivered
    /// template wrote them. The inner manifest is superseded by this package's own, which is
    /// the one that attributes the material to its Chronicleaf origin.
    /// </summary>
    /// <exception cref="InternalFailureException">The HTML export answered in a shape this build does not pack.</exception>
    private static (byte[] Document, List<PackagedImage> Images) UnpackHtmlArtifact(ApprovedHtmlExport html)
    {
        if (string.Equals(html.ContentType, HtmlExportAdapter.HtmlMediaType, StringComparison.Ordinal))
        {
            return (html.Bytes, []);
        }

        if (!string.Equals(html.ContentType, HtmlExportAdapter.ZipMediaType, StringComparison.Ordinal))
        {
            throw new InternalFailureException(
                "The HTML export answered in a shape this build does not pack, so no Medium "
                + "import package can be built from it. Nothing was stored and nothing was sent.");
        }

        using var archive = new ZipArchive(new MemoryStream(html.Bytes, writable: false), ZipArchiveMode.Read);

        var index = archive.GetEntry("index.html")
            ?? throw new InternalFailureException(
                "The HTML export carries no article document, so no Medium import package can "
                + "be built from it. Nothing was stored and nothing was sent.");

        byte[] document;
        using (var stream = index.Open())
        {
            document = ReadFully(stream);
        }

        var images = new List<PackagedImage>();

        foreach (var entry in archive.Entries.OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
        {
            if (string.Equals(entry.FullName, "index.html", StringComparison.Ordinal) ||
                string.Equals(entry.FullName, "manifest.json", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = entry.Open();
            images.Add(new PackagedImage(entry.FullName, "image/png", ReadFully(stream)));
        }

        return (document, images);
    }

    private static byte[] PackArchive(
        byte[] documentBytes,
        byte[] markdownBytes,
        IReadOnlyList<PackagedImage> images,
        byte[] manifestBytes,
        byte[] instructionsBytes,
        CancellationToken cancellationToken)
    {
        using var packed = new MemoryStream();

        using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The fixed order and the fixed timestamp are what make two packages of the same
            // approved revision agree byte for byte: nothing here records when the export ran.
            WriteEntry(archive, ArticleFileName, documentBytes);
            WriteEntry(archive, MarkdownFileName, markdownBytes);

            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(archive, image.Path, image.Bytes);
            }

            // The manifest is written after the files it checksums, so its digests describe the
            // exact bytes packed above; the instructions name those same digests' revision.
            WriteEntry(archive, ManifestFileName, manifestBytes);
            WriteEntry(archive, InstructionsFileName, instructionsBytes);

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

    private static byte[] ReadFully(Stream content)
    {
        // Stored exports are already bounded by the review and HTML packages' own archive
        // limits, so reading them fully here holds no more than those packages were willing
        // to serve.
        using var buffered = new MemoryStream();
        content.CopyTo(buffered);
        return buffered.ToArray();
    }

    /// <summary>
    /// The manual import instructions: what the package holds, the export-only boundary, and
    /// the hand steps that carry it into Medium. Fixed words except for the provenance values
    /// that attribute the material to its origin: the asset, the revision and the body hash.
    /// No step names a Medium URL that was visited or a post that exists, because neither does.
    /// </summary>
    private static byte[] InstructionsFor(Guid assetId, ApprovedContentExport markdown)
    {
        var instructions = string.Create(
            CultureInfo.InvariantCulture,
            $"Chronicleaf manual Medium import package\n"
            + $"========================================\n"
            + $"\n"
            + $"This package holds one approved article for import into Medium BY HAND. This\n"
            + $"product performs no Medium call of any kind: there is no API integration, no\n"
            + $"token and no browser posting behind it, and no post was made. Medium does not\n"
            + $"accept new API integrations; carry the files below through Medium's own import\n"
            + $"or editor flow instead:\n"
            + $"https://help.medium.com/hc/en-us/articles/213480228-API-Importing\n"
            + $"\n"
            + $"Provenance (the origin this material is attributed to)\n"
            + $"-----------------------------------------------------\n"
            + $"asset: {assetId:D}\n"
            + $"content revision: {markdown.ContentRevision.ToString(CultureInfo.InvariantCulture)}\n"
            + $"body SHA-256: {markdown.BodyHash}\n"
            + $"\n"
            + $"Contents\n"
            + $"--------\n"
            + $"{ArticleFileName}: the approved article as a sanitized offline document.\n"
            + $"{MarkdownFileName}: the approved article's exact Markdown bytes.\n"
            + $"images/*: the approval-covered pictures, when the approval covers any.\n"
            + $"{ManifestFileName}: per-file SHA-256 checksums for everything above.\n"
            + $"\n"
            + $"Steps\n"
            + $"-----\n"
            + $"1. Unpack this archive on your own machine.\n"
            + $"2. In Medium, start a new story or open its import flow.\n"
            + $"3. Paste the article from {MarkdownFileName}, or from {ArticleFileName} where\n"
            + $"   Medium accepts HTML.\n"
            + $"4. Upload each file under images/ where its picture belongs, and check the\n"
            + $"   captions against the article text.\n"
            + $"5. Check the footnotes: [^..] markers cite the Chronicleaf evidence this article\n"
            + $"   was grounded in, not Medium content.\n"
            + $"6. Publish the story yourself in Medium. Only that action publishes anything.\n");

        return Encoding.UTF8.GetBytes(instructions);
    }

    private sealed record PackagedImage(string Path, string MediaType, byte[] Bytes);
}
