using System.Text.Json;
using Chronicleaf.Core;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// The two jsonb columns this package stores, serialized and read back through one small codec.
/// </summary>
/// <remarks>
/// <para>
/// The capture tables hold two things that are genuinely lists - the URLs a job was asked to capture
/// and the viewport names - and one that is a value object: the viewport a candidate was taken at. Each
/// rides in a jsonb column through a value converter, for the reason the delivered voice package gives:
/// a relational column per field could not express what is stored here, and the viewport in particular
/// is one value that means exactly one thing rather than three loose numbers whose combination might
/// not be a viewport this package offers.
/// </para>
/// <para>
/// <b>The viewport is stored as a whole object, not as width and height columns.</b> A reader that
/// finds 1440 and 900 in a row can be certain they are the design's desktop viewport and not two
/// numbers somebody typed; and the value the database compares in the uniqueness tuple is the same value
/// the record was constructed from, so a capture cannot be considered a duplicate of a differently
/// sized one. Storing the name alongside the numbers is what makes that check readable in a table dump.
/// </para>
/// <para>
/// Deserialization is total: a column that is null, empty or not the shape this codec writes reads as
/// the default rather than throwing. A row whose stored JSON is corrupt should surface as a candidate
/// with no usable viewport - which the job's own validation then refuses - rather than as a query that
/// takes down every read of a milestone's candidates.
/// </para>
/// </remarks>
public static class CaptureColumnCodec
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>The request's URLs, as a jsonb array of strings.</summary>
    public static string SerializeUrls(IReadOnlyList<string> urls) => JsonSerializer.Serialize(urls, Wire);

    /// <summary>The request's URLs, or an empty list for a column that holds nothing usable.</summary>
    public static IReadOnlyList<string> DeserializeUrls(string? json) =>
        TryDeserializeArray(json);

    /// <summary>The request's viewport names, as a jsonb array of strings.</summary>
    public static string SerializeViewports(IReadOnlyList<string> viewports) => JsonSerializer.Serialize(viewports, Wire);

    /// <summary>The request's viewport names, or an empty list for a column that holds nothing usable.</summary>
    public static IReadOnlyList<string> DeserializeViewports(string? json) =>
        TryDeserializeArray(json);

    /// <summary>
    /// The viewport a candidate was taken at, as a jsonb object.
    /// </summary>
    /// <remarks>
    /// Serialized as the three properties the record has, in the record's own order, so the stored text
    /// is stable across runs - which matters because this value is inside a unique index, and a
    /// serializer whose property order varied would produce two different keys for one viewport.
    /// </remarks>
    public static string SerializeViewport(CaptureViewport viewport) => JsonSerializer.Serialize(
        new ViewportDocument(viewport.Name, viewport.Width, viewport.Height),
        Wire);

    /// <summary>
    /// The viewport a candidate was taken at, or <c>null</c> when the column holds nothing usable.
    /// </summary>
    /// <remarks>
    /// A name this package does not recognise is read back as <c>null</c> rather than as a viewport
    /// with that name and whatever dimensions came with it. Constructing a viewport from stored text
    /// would let a row claim a size the product does not offer, and the read route would then publish a
    /// label and a dimension that contradict each other.
    /// </remarks>
    public static CaptureViewport? DeserializeViewport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ViewportDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<ViewportDocument>(json, Wire);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null)
        {
            return null;
        }

        var named = CaptureViewport.FromName(document.Name);

        // The dimensions are checked as well as the name, because a stored object could carry a valid
        // name beside numbers that are not that viewport's. The name is the contract; the numbers are a
        // copy of it.
        return named is not null && named.Width == document.Width && named.Height == document.Height
            ? named
            : null;
    }

    private static IReadOnlyList<string> TryDeserializeArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, Wire) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The stored shape of a viewport.
    /// </summary>
    /// <remarks>
    /// A private record rather than serializing <see cref="CaptureViewport"/> directly, so the column's
    /// shape is this package's to state rather than a by-product of a domain record somebody may later
    /// add a property to.
    /// </remarks>
    private sealed record ViewportDocument(string Name, int Width, int Height);
}

/// <summary>
/// Structural comparison for the two string-list columns, in the order the owner listed them.
/// </summary>
/// <remarks>
/// The mapper compares a loaded value against a current one to decide whether anything changed, and
/// without a comparator it falls back to reference equality for a collection - so re-reading an
/// unchanged request would look like an edit and be written back on every read. The comparison is
/// ordinal and ordered, because the order is the order the job walks and two lists that happen to hold
/// the same strings in a different order are not the same capture.
/// </remarks>
public sealed class StringListComparer : ValueComparer<IReadOnlyList<string>>
{
    private StringListComparer()
        : base((left, right) => AreEqual(left, right), value => HashOf(value), value => CopyOf(value))
    {
    }

    /// <summary>The single instance; the comparer holds no state.</summary>
    public static StringListComparer Instance { get; } = new();

    public override bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y) => AreEqual(x, y);

    public override int GetHashCode(IReadOnlyList<string> obj) => HashOf(obj);

    private static bool AreEqual(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Count != y.Count)
        {
            return false;
        }

        for (var index = 0; index < x.Count; index++)
        {
            if (!string.Equals(x[index], y[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int HashOf(IReadOnlyList<string> value)
    {
        var hash = new HashCode();

        foreach (var item in value)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// A detached copy, so the tracker's snapshot cannot be mutated by whoever holds the list.
    /// </summary>
    private static IReadOnlyList<string> CopyOf(IReadOnlyList<string>? value) =>
        value is null ? Array.Empty<string>() : value.ToArray();
}
