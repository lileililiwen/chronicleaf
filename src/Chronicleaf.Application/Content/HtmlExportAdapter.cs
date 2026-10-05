using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Content;

/// <summary>
/// Renders an approved snapshot to a sanitized portable HTML artifact (HTML-CONTENT-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here executes and nothing here fetches.</b> The source Markdown is unapproved-shaped
/// text the owner wrote: every run of it becomes escaped characters, and the only elements ever
/// emitted are the fixed set below with no attributes except a link <c>href</c> that passed the
/// protocol check, an image <c>src</c> naming a packaged relative path, and the receipt
/// <c>meta</c> tags. There is no tag parsing at all - a raw <c>&lt;script&gt;</c> in the source
/// is visible as those characters - which is what makes a tag impossible to smuggle through.
/// </para>
/// <para>
/// <b>Source image markup never becomes an image.</b> A <c>![alt](url)</c> run in the source is
/// rendered as its own literal characters: turning it into an <c>&lt;img&gt;</c> would either
/// fetch a remote URL at render time - a leak - or resolve an owner-written path - a traversal.
/// The only pictures in the artifact are the approval-covered ones in the snapshot, under paths
/// this class generates, so no entry can escape the artifact and no render can reach the network.
/// </para>
/// <para>
/// <b>Links navigate; they do not fetch.</b> An <c>http</c>/<c>https</c> destination stays a link
/// with <c>rel="noopener noreferrer"</c>, exactly as the review preview shows it. Anything else -
/// <c>javascript:</c>, <c>data:</c>, or any scheme this template does not know - is shown as the
/// characters the author wrote, with no anchor. A relative reference names no host and no scheme,
/// so it cannot reach anywhere from an offline file and stays a link.
/// </para>
/// </remarks>
public sealed class HtmlExportAdapter : IContentExportAdapter
{
    /// <summary>
    /// The template version the manifest and the receipt metas carry. Bumped only when the fixed
    /// template - the CSS, the skeleton or the renderer subset - changes, so a reader of an old
    /// file knows which template produced it.
    /// </summary>
    public const string TemplateVersion = "html-export/1";

    /// <summary>100 MiB: the design's bound on one export archive.</summary>
    public const long MaxArchiveBytes = 100L * 1024 * 1024;

    /// <summary>What a picture-less export is served as.</summary>
    public const string HtmlMediaType = "text/html; charset=utf-8";

    /// <summary>What an export carrying pictures is served as.</summary>
    public const string ZipMediaType = "application/zip";

    private const string IndexPath = "index.html";
    private const string ManifestPath = "manifest.json";
    private const string ImageDirectory = "images";

    /// <summary>The whole stylesheet, fixed: no author value ever reaches a style context.</summary>
    private const string EmbeddedCss =
        "body{font-family:system-ui,-apple-system,\"Segoe UI\",sans-serif;max-width:44rem;"
        + "margin:2rem auto;padding:0 1rem;line-height:1.6;color:#1a1a1a;background:#ffffff}"
        + "pre{background:#f4f4f4;padding:1rem;overflow:auto;border-radius:4px}"
        + "code{font-family:ui-monospace,monospace;font-size:0.9em}"
        + "figure{margin:1.5rem 0}img{max-width:100%;height:auto}"
        + "figcaption{font-size:0.9em;color:#555}sup{font-size:0.8em}";

    /// <inheritdoc />
    public Task<ExportArtifact> ExportAsync(
        ApprovedAssetSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (snapshot.AssetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        if (snapshot.ContentRevision < 1)
        {
            throw new ValidationFailedException(
                "contentRevision", "An exported revision follows a stored one, so name one.");
        }

        foreach (var picture in snapshot.Pictures)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(picture.ContentType, MediaTypes.Png, StringComparison.Ordinal))
            {
                throw new UnsupportedMediaTypeException();
            }
        }

        var imagePaths = snapshot.Pictures
            .Select((picture, index) => string.Create(
                CultureInfo.InvariantCulture, $"{ImageDirectory}/image-{index}.png"))
            .ToList();

        var document = RenderDocument(snapshot, imagePaths);
        var documentBytes = Encoding.UTF8.GetBytes(document);
        cancellationToken.ThrowIfCancellationRequested();

        ExportArtifact artifact;
        if (snapshot.Pictures.Count == 0)
        {
            artifact = SingleFile(snapshot, documentBytes);
        }
        else
        {
            artifact = PackArchive(snapshot, documentBytes, imagePaths, cancellationToken);
        }

        return Task.FromResult(artifact);
    }

    private static ExportArtifact SingleFile(ApprovedAssetSnapshot snapshot, byte[] documentBytes)
    {
        if (documentBytes.LongLength > MaxArchiveBytes)
        {
            throw new PayloadTooLargeException(MaxArchiveBytes);
        }

        var manifest = JsonSerializer.Serialize(new
        {
            templateVersion = TemplateVersion,
            assetId = snapshot.AssetId.ToString("D"),
            contentRevision = snapshot.ContentRevision,
            bodyHash = snapshot.BodyHash,
            evidenceRevision = snapshot.EvidenceRevision,
            files = new[]
            {
                new
                {
                    path = FileNameFor(snapshot, ".html"),
                    sha256 = Hash(documentBytes),
                    bytes = documentBytes.LongLength,
                },
            },
        });

        return new ExportArtifact(
            HtmlMediaType,
            FileNameFor(snapshot, ".html"),
            Hash(documentBytes),
            documentBytes,
            manifest);
    }

    private static ExportArtifact PackArchive(
        ApprovedAssetSnapshot snapshot,
        byte[] documentBytes,
        IReadOnlyList<string> imagePaths,
        CancellationToken cancellationToken)
    {
        long planned = documentBytes.LongLength;
        foreach (var picture in snapshot.Pictures)
        {
            planned += picture.Bytes.LongLength;
        }

        // The manifest is small beside pictures, but it is still counted: the bound is on the
        // archive, not on the pictures, so a manifest that itself overflowed would otherwise pass.
        // A fixed over-estimate keeps the check before any byte is packed rather than after.
        planned += 4096;

        if (planned > MaxArchiveBytes)
        {
            throw new PayloadTooLargeException(MaxArchiveBytes);
        }

        using var packed = new MemoryStream();

        using (var archive = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, IndexPath, documentBytes);

            for (var index = 0; index < snapshot.Pictures.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(archive, imagePaths[index], snapshot.Pictures[index].Bytes);
            }

            // The manifest is written last so its checksums describe the exact bytes packed above:
            // the document and picture bytes are already fixed by the time it is serialized.
            var manifest = ManifestJson(snapshot, documentBytes, imagePaths);
            WriteEntry(archive, ManifestPath, Encoding.UTF8.GetBytes(manifest));

            cancellationToken.ThrowIfCancellationRequested();
        }

        var bytes = packed.ToArray();

        if (bytes.LongLength > MaxArchiveBytes)
        {
            throw new PayloadTooLargeException(MaxArchiveBytes);
        }

        return new ExportArtifact(
            ZipMediaType,
            FileNameFor(snapshot, ".zip"),
            Hash(bytes),
            bytes,
            ManifestJson(snapshot, documentBytes, imagePaths));
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ManifestJson(
        ApprovedAssetSnapshot snapshot,
        byte[] documentBytes,
        IReadOnlyList<string> imagePaths)
    {
        var files = new List<object>(imagePaths.Count + 2)
        {
            new { path = IndexPath, sha256 = Hash(documentBytes), bytes = documentBytes.LongLength },
        };

        for (var index = 0; index < snapshot.Pictures.Count; index++)
        {
            var picture = snapshot.Pictures[index];
            files.Add(new
            {
                path = imagePaths[index],
                sha256 = Hash(picture.Bytes),
                bytes = picture.Bytes.LongLength,
            });
        }

        return JsonSerializer.Serialize(new
        {
            templateVersion = TemplateVersion,
            assetId = snapshot.AssetId.ToString("D"),
            contentRevision = snapshot.ContentRevision,
            bodyHash = snapshot.BodyHash,
            evidenceRevision = snapshot.EvidenceRevision,
            files,
        });
    }

    /// <summary>The attachment name: generated from the asset id and the revision, never from
    /// owner text, so no traversal or hostile character can reach a filename.</summary>
    private static string FileNameFor(ApprovedAssetSnapshot snapshot, string extension) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"chronicleaf-{snapshot.AssetId:N}-r{snapshot.ContentRevision}{extension}");

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ==============================================================================================
    // Rendering. The same subset the review preview shows - headings, lists, fenced code, inline
    // code, bold, links and footnote references - so what an owner approved on screen is what the
    // file carries. Anything else is a paragraph of escaped text.
    // ==============================================================================================

    private static string RenderDocument(ApprovedAssetSnapshot snapshot, IReadOnlyList<string> imagePaths)
    {
        var body = new StringBuilder();
        body.Append("<h1>");
        body.Append(Escape(snapshot.Title));
        body.Append("</h1>\n");

        foreach (var block in RenderBlocks(snapshot.BodyMarkdown))
        {
            body.Append(block);
            body.Append('\n');
        }

        if (imagePaths.Count > 0)
        {
            body.Append("<section aria-label=\"Approved pictures\">\n");
            for (var index = 0; index < snapshot.Pictures.Count; index++)
            {
                var caption = Escape(snapshot.Pictures[index].Caption);
                body.Append("<figure><img src=\"");
                body.Append(imagePaths[index]);
                body.Append("\" alt=\"");
                body.Append(caption);
                body.Append("\"><figcaption>");
                body.Append(caption);
                body.Append("</figcaption></figure>\n");
            }

            body.Append("</section>\n");
        }

        return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + $"<title>{Escape(snapshot.Title)}</title>\n"
            + $"<meta name=\"chronicleaf-template-version\" content=\"{TemplateVersion}\">\n"
            + $"<meta name=\"chronicleaf-body-hash\" content=\"{Escape(snapshot.BodyHash)}\">\n"
            + $"<meta name=\"chronicleaf-content-revision\" content=\"{snapshot.ContentRevision.ToString(CultureInfo.InvariantCulture)}\">\n"
            + $"<meta name=\"chronicleaf-evidence-revision\" content=\"{snapshot.EvidenceRevision.ToString(CultureInfo.InvariantCulture)}\">\n"
            + $"<style>{EmbeddedCss}</style>\n</head>\n<body>\n<article>\n{body}</article>\n</body>\n</html>\n";
    }

    private static IEnumerable<string> RenderBlocks(string source)
    {
        var lines = source.Split('\n');
        var current = new List<string>();
        string[]? code = null;
        var codeLines = new List<string>();

        // Local iterators cannot yield from inside a local function, so the blocks are collected
        // first and rendered after: the same split the preview performs, in the same order.
        var blocks = new List<(bool IsCode, List<string> Lines)>();

        foreach (var line in lines)
        {
            if (code is not null)
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add((true, codeLines));
                    codeLines = [];
                    code = null;
                }
                else
                {
                    codeLines.Add(line);
                }

                continue;
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (current.Count > 0)
                {
                    blocks.Add((false, current));
                    current = [];
                }

                code = [];
                continue;
            }

            if (line.Trim().Length == 0)
            {
                if (current.Count > 0)
                {
                    blocks.Add((false, current));
                    current = [];
                }

                continue;
            }

            current.Add(line);
        }

        if (code is not null)
        {
            blocks.Add((true, codeLines));
        }

        if (current.Count > 0)
        {
            blocks.Add((false, current));
        }

        foreach (var (isCode, blockLines) in blocks)
        {
            if (isCode)
            {
                yield return "<pre><code>" + Escape(string.Join("\n", blockLines)) + "</code></pre>";
                continue;
            }

            yield return RenderTextBlock(blockLines);
        }
    }

    private static string RenderTextBlock(List<string> lines)
    {
        var first = lines[0] ?? string.Empty;

        if (first.StartsWith("# ", StringComparison.Ordinal))
        {
            return "<h1>" + RenderInline(first[2..]) + "</h1>";
        }

        if (first.StartsWith("## ", StringComparison.Ordinal))
        {
            return "<h2>" + RenderInline(first[3..]) + "</h2>";
        }

        if (first.StartsWith("### ", StringComparison.Ordinal))
        {
            return "<h3>" + RenderInline(first[4..]) + "</h3>";
        }

        var isList = true;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                isList = false;
                break;
            }
        }

        if (isList)
        {
            var items = new StringBuilder("<ul>");
            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();
                items.Append("<li>");
                items.Append(RenderInline(trimmed[2..]));
                items.Append("</li>");
            }

            items.Append("</ul>");
            return items.ToString();
        }

        var paragraph = new StringBuilder("<p>");
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                paragraph.Append("<br>");
            }

            paragraph.Append(RenderInline(lines[index]));
        }

        paragraph.Append("</p>");
        return paragraph.ToString();
    }

    /// <summary>
    /// One line's inline runs. Code spans are atomic - no bold or link is recognized inside them -
    /// and a source image run is literal characters, never an element, for the reason the class
    /// remarks give.
    /// </summary>
    private static string RenderInline(string text)
    {
        var output = new StringBuilder();
        var rest = text.AsSpan();

        while (rest.Length > 0)
        {
            if (TryTakeCode(rest, out var code, out var codeLength))
            {
                output.Append("<code>");
                output.Append(Escape(code));
                output.Append("</code>");
                rest = rest[codeLength..];
                continue;
            }

            if (TryTakeBold(rest, out var bold, out var boldLength))
            {
                output.Append("<strong>");
                output.Append(Escape(bold));
                output.Append("</strong>");
                rest = rest[boldLength..];
                continue;
            }

            if (TryTakeImage(rest, out var imageLength))
            {
                // Inert by construction: the whole run, brackets and destination included, as
                // characters. A remote picture stays text and an owner-written path stays text.
                output.Append(Escape(rest[..imageLength].ToString()));
                rest = rest[imageLength..];
                continue;
            }

            if (TryTakeLink(rest, out var label, out var destination, out var linkLength))
            {
                if (IsSafeDestination(destination))
                {
                    output.Append("<a href=\"");
                    output.Append(EscapeAttribute(destination));
                    output.Append("\" rel=\"noopener noreferrer\">");
                    output.Append(Escape(label));
                    output.Append("</a>");
                }
                else
                {
                    // An unsafe destination is shown as the characters the author wrote, with no
                    // anchor: a reader sees the run as text and cannot reach it by clicking.
                    output.Append(Escape(rest[..linkLength].ToString()));
                }

                rest = rest[linkLength..];
                continue;
            }

            if (TryTakeFootnote(rest, out var footnote, out var footnoteLength))
            {
                output.Append("<sup>");
                output.Append(Escape(footnote));
                output.Append("</sup>");
                rest = rest[footnoteLength..];
                continue;
            }

            var next = NextSpecial(rest);
            if (next == 0)
            {
                // A special character that starts nothing - a lone `*`, an unclosed `**`, a
                // `[` with no partner - is one literal character. Without this the loop would
                // spin on the same position forever: slicing zero characters advances nothing.
                output.Append(Escape(rest[..1].ToString()));
                rest = rest[1..];
                continue;
            }

            output.Append(Escape(rest[..next].ToString()));
            rest = rest[next..];
        }

        return output.ToString();
    }

    private static bool TryTakeCode(ReadOnlySpan<char> rest, out string code, out int length)
    {
        code = string.Empty;
        length = 0;

        if (rest.Length < 2 || rest[0] != '`')
        {
            return false;
        }

        var end = rest[1..].IndexOf('`');
        if (end < 0)
        {
            return false;
        }

        var inner = rest.Slice(1, end).ToString();
        if (inner.Length == 0 || inner.Contains('\n'))
        {
            return false;
        }

        code = inner;
        length = end + 2;
        return true;
    }

    private static bool TryTakeBold(ReadOnlySpan<char> rest, out string bold, out int length)
    {
        bold = string.Empty;
        length = 0;

        if (rest.Length < 5 || rest[0] != '*' || rest[1] != '*')
        {
            return false;
        }

        var end = rest[2..].IndexOf('*');
        if (end < 0 || end + 1 >= rest[2..].Length || rest[2 + end + 1] != '*')
        {
            return false;
        }

        var inner = rest.Slice(2, end).ToString();
        if (inner.Length == 0 || inner.Contains('*'))
        {
            return false;
        }

        bold = inner;
        length = end + 4;
        return true;
    }

    private static bool TryTakeImage(ReadOnlySpan<char> rest, out int length)
    {
        length = 0;

        if (rest.Length < 5 || rest[0] != '!' || rest[1] != '[')
        {
            return false;
        }

        if (!TryTakeLinkInner(rest[1..], out var inner))
        {
            return false;
        }

        length = inner + 1;
        return true;
    }

    private static bool TryTakeLink(
        ReadOnlySpan<char> rest,
        out string label,
        out string destination,
        out int length)
    {
        label = string.Empty;
        destination = string.Empty;
        length = 0;

        if (!TryTakeLinkInner(rest, out length))
        {
            return false;
        }

        var inner = rest[..length].ToString();
        var middle = inner.IndexOf("](", StringComparison.Ordinal);
        label = inner[1..middle];
        destination = inner[(middle + 2)..^1];
        return true;
    }

    private static bool TryTakeLinkInner(ReadOnlySpan<char> rest, out int length)
    {
        length = 0;

        if (rest.Length == 0 || rest[0] != '[')
        {
            return false;
        }

        var labelEnd = rest.IndexOf(']');
        if (labelEnd < 1)
        {
            return false;
        }

        if (labelEnd + 1 >= rest.Length || rest[labelEnd + 1] != '(')
        {
            return false;
        }

        var destinationEnd = rest[(labelEnd + 2)..].IndexOf(')');
        if (destinationEnd < 0)
        {
            return false;
        }

        var destination = rest.Slice(labelEnd + 2, destinationEnd).ToString();
        if (destination.Length == 0 || destination.Contains(' ') || destination.Contains('\t'))
        {
            return false;
        }

        length = labelEnd + 2 + destinationEnd + 1;
        return true;
    }

    private static bool TryTakeFootnote(ReadOnlySpan<char> rest, out string footnote, out int length)
    {
        footnote = string.Empty;
        length = 0;

        if (rest.Length < 4 || rest[0] != '[' || rest[1] != '^')
        {
            return false;
        }

        var end = rest.IndexOf(']');
        if (end < 3)
        {
            return false;
        }

        var inner = rest.Slice(2, end - 2).ToString();
        foreach (var character in inner)
        {
            var allowed = char.IsLetterOrDigit(character) || character is '_' or '.' or '-';
            if (!allowed)
            {
                return false;
            }
        }

        footnote = inner;
        length = end + 1;
        return true;
    }

    private static int NextSpecial(ReadOnlySpan<char> rest)
    {
        var candidates = new List<int>(3);
        var tick = rest.IndexOf('`');
        if (tick >= 0)
        {
            candidates.Add(tick);
        }

        var bold = rest.IndexOf('*');
        if (bold >= 0)
        {
            candidates.Add(bold);
        }

        var bracket = rest.IndexOfAny('[', '!');
        if (bracket >= 0)
        {
            candidates.Add(bracket);
        }

        if (candidates.Count == 0)
        {
            return rest.Length;
        }

        var minimum = rest.Length;
        foreach (var candidate in candidates)
        {
            if (candidate < minimum)
            {
                minimum = candidate;
            }
        }

        return minimum;
    }

    /// <summary>Only <c>http</c> and <c>https</c> destinations are reachable, and relative
    /// references, which name no host and no scheme. Everything else is text.</summary>
    private static bool IsSafeDestination(string destination)
    {
        var trimmed = destination.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var schemeEnd = -1;
        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (index == 0)
            {
                if (!char.IsAsciiLetter(character))
                {
                    break;
                }

                continue;
            }

            if (character == ':')
            {
                schemeEnd = index;
                break;
            }

            if (!(char.IsAsciiLetterOrDigit(character) || character is '+' or '.' or '-'))
            {
                break;
            }
        }

        // No scheme: a relative reference. It names no host, no credentials and no scheme, so it
        // cannot reach anywhere from an offline file.
        if (schemeEnd < 0)
        {
            return true;
        }

        var scheme = trimmed[..schemeEnd];
        return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static string EscapeAttribute(string value) => WebUtility.HtmlEncode(value);
}
