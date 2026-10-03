using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Evidence;

/// <summary>
/// Everything this package refuses before it reaches the database (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// Validation lives here rather than in the endpoint or the store so that a collector calling
/// <see cref="IAppendEvidence.AppendAsync"/> directly gets exactly the same refusals a route does. A
/// rule that only the HTTP layer enforced would be a rule a later evidence package could quietly
/// skip.
/// </remarks>
public static class EvidencePolicy
{
    /// <summary>
    /// The canonical SHA-256 of a payload: the same payload always hashes to the same value, and a
    /// re-ordered or re-spelled one does not.
    /// </summary>
    /// <remarks>
    /// The canonical form is what makes idempotence a real property rather than a coincidence. A
    /// collector that assembles the same facts in a different order, or spells a field the same way
    /// with different whitespace, produces the same hash - so the second append is recognised as the
    /// same fact and returns the bundle that already holds it, instead of recording a duplicate
    /// revision that says the same thing twice.
    /// <para>
    /// The canonical form is a JSON object with its keys sorted by ordinal comparison, no insignificant
    /// whitespace, and the values escaped canonically; the hash is taken over its UTF-8 bytes. Nothing
    /// is trimmed from the payload here: trimming would make two genuinely different payloads hash the
    /// same, which is precisely the confusion this is meant to remove.
    /// </para>
    /// </remarks>
    public static string CanonicalSha256(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var canonical = new StringBuilder();
        canonical.Append('{');

        var ordered = fields
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            if (index > 0)
            {
                canonical.Append(',');
            }

            AppendJsonString(canonical, ordered[index].Key);
            canonical.Append(':');
            AppendJsonString(canonical, ordered[index].Value);
        }

        canonical.Append('}');

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// The canonical hash of one item's payload, over exactly the fields that make it a distinct
    /// fact: its kind, source, source revision, collected text and the exclusions applied to it.
    /// </summary>
    /// <remarks>
    /// Deliberately excluded: the id, the bundle, the milestone, <c>capturedAt</c> and
    /// <c>availability</c>. Two collections of the same commit at the same revision, minutes apart,
    /// are the same fact and must not become two revisions - and a re-read that failed where the
    /// first succeeded is a different claim about the same fact, which the availability field
    /// carries on its own row rather than through the hash.
    /// </remarks>
    public static string ItemPayloadSha256(EvidenceAppendItem item)
    {
        var redactions = new StringBuilder();
        foreach (var redaction in item.Redactions.Order(StringComparer.Ordinal))
        {
            if (redactions.Length > 0)
            {
                redactions.Append('\n');
            }

            redactions.Append(redaction);
        }

        return CanonicalSha256(
        [
            new KeyValuePair<string, string>("kind", item.Kind),
            new KeyValuePair<string, string>("sourceRef", item.SourceRef),
            new KeyValuePair<string, string>("sourceRevision", item.SourceRevision),
            new KeyValuePair<string, string>("text", item.Text),
            new KeyValuePair<string, string>("redactions", redactions.ToString()),
        ]);
    }

    /// <summary>
    /// Checks every field of an append and returns the field errors it found. An empty result means
    /// the append is well formed; a non-empty one means nothing was written.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Validate(
        IReadOnlyList<EvidenceAppendItem> items,
        bool ownerWritableOnly)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (items.Count == 0)
        {
            errors["items"] = ["At least one evidence item is required."];
            return errors;
        }

        if (items.Count > EvidenceLimits.MaxItemsPerBundle)
        {
            throw new PayloadTooLargeException(EvidenceLimits.MaxItemsPerBundle);
        }

        for (var index = 0; index < items.Count; index++)
        {
            ValidateItem(items[index], ownerWritableOnly, $"items[{index}]", errors);
        }

        return errors;
    }

    /// <summary>
    /// Checks the combined text of the whole resulting bundle. The bound is on the bundle, not on the
    /// append, because a bundle is a snapshot: a caller that fits today can still be refused when the
    /// snapshot it extends is already full.
    /// </summary>
    public static void ValidateBundleBounds(IReadOnlyList<EvidenceItem> items)
    {
        if (items.Count > EvidenceLimits.MaxItemsPerBundle)
        {
            throw new PayloadTooLargeException(EvidenceLimits.MaxItemsPerBundle);
        }

        var total = 0;
        foreach (var item in items)
        {
            total += Encoding.UTF8.GetByteCount(item.Text);
        }

        if (total > EvidenceLimits.MaxTextBytesPerBundle)
        {
            throw new PayloadTooLargeException(EvidenceLimits.MaxTextBytesPerBundle);
        }
    }

    private static void ValidateItem(
        EvidenceAppendItem item,
        bool ownerWritableOnly,
        string path,
        Dictionary<string, string[]> errors)
    {
        void Fail(string message) => Add(errors, path, message);

        if (!EvidenceKinds.All.Contains(item.Kind, StringComparer.Ordinal))
        {
            Fail("The evidence kind is not one this system records.");
        }
        else if (ownerWritableOnly && !EvidenceKinds.IsOwnerWritable(item.Kind))
        {
            // A request may only add what the owner actually authored. Letting one claim to be a
            // commit or a release would be a collected fact nobody collected.
            Fail("A manual append may only add manual, limitations or next-steps evidence.");
        }

        if (!EvidenceAvailability.All.Contains(item.Availability, StringComparer.Ordinal))
        {
            Fail("The availability is not one this system records.");
        }

        if (item.SourceRef is null || item.SourceRef.Length is 0 or > EvidenceLimits.SourceRefMaxLength)
        {
            Fail($"The source reference must be 1 to {EvidenceLimits.SourceRefMaxLength} characters.");
        }

        if (item.SourceRevision is null || item.SourceRevision.Length is 0 or > EvidenceLimits.SourceRevisionMaxLength)
        {
            Fail($"The source revision must be 1 to {EvidenceLimits.SourceRevisionMaxLength} characters.");
        }

        if (!EvidenceValues.IsSha256Hex(item.PayloadSha256))
        {
            Fail("The payload hash must be a lowercase 64-character SHA-256.");
        }

        if (item.Text is null)
        {
            Fail("The text is required.");
        }
        else if (item.Text.Length > EvidenceLimits.ItemTextMaxLength)
        {
            Fail($"The text must be at most {EvidenceLimits.ItemTextMaxLength} characters.");
        }

        var availability = item.Availability;

        if (string.Equals(availability, EvidenceAvailability.Unavailable, StringComparison.Ordinal))
        {
            // The honest part of this package. An unavailable source has no payload, no media and no
            // reason-free silence: it carries the reason it could not be read, and it never points at
            // media, because a source that produced nothing produced no bytes to point at.
            if (string.IsNullOrWhiteSpace(item.Text))
            {
                Fail("An unavailable source must record why it could not be collected.");
            }

            if (item.MediaId is not null)
            {
                Fail("An unavailable source cannot reference media.");
            }
        }
        else if (item.Availability is not null
                 && !string.Equals(availability, EvidenceAvailability.Partial, StringComparison.Ordinal)
                 && string.IsNullOrEmpty(item.Text))
        {
            Fail("Collected evidence must carry the text that was collected.");
        }

        if (item.Redactions.Count > EvidenceLimits.MaxRedactions)
        {
            Fail($"At most {EvidenceLimits.MaxRedactions} redactions may be named.");
        }

        foreach (var redaction in item.Redactions)
        {
            if (redaction is null || redaction.Length is 0 or > EvidenceLimits.RedactionMaxLength)
            {
                Fail($"Each redaction must be 1 to {EvidenceLimits.RedactionMaxLength} characters.");
                break;
            }
        }

        if (string.Equals(availability, EvidenceAvailability.Partial, StringComparison.Ordinal)
            && item.Redactions.Count == 0)
        {
            // A partial source that names no exclusion is indistinguishable from a complete one, and
            // a reader would be told the story is whole when it is not.
            Fail("A partial source must name what was excluded.");
        }
    }

    private static void Add(Dictionary<string, string[]> errors, string field, string message)
    {
        errors[field] = errors.TryGetValue(field, out var existing)
            ? [.. existing, message]
            : [message];
    }

    private static void AppendJsonString(StringBuilder target, string value)
    {
        target.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    target.Append("\\\"");
                    break;
                case '\\':
                    target.Append("\\\\");
                    break;
                case '\n':
                    target.Append("\\n");
                    break;
                case '\r':
                    target.Append("\\r");
                    break;
                case '\t':
                    target.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        target.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    }
                    else
                    {
                        target.Append(character);
                    }

                    break;
            }
        }

        target.Append('"');
    }
}
