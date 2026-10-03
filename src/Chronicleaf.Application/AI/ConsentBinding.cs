using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronicleaf.Application.AI;

/// <summary>
/// The one value that binds an owner's authorization to a specific input, provider and configuration
/// (LLM-PROVIDER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// It is the SHA-256 of a canonical JSON document listing the scope, the purpose and aggregate, the
/// provider and model, the credential's identifier and metadata revision, the evidence and voice
/// revisions, the selected evidence ids and the digest of the snapshot bytes themselves. The owner
/// sees this value and the app sends it back with the generate call, so the server can answer one
/// question without a database round trip: is the thing the owner authorized still the thing that
/// would be sent?
/// </para>
/// <para>
/// <b>It is a binding, not a bearer token.</b> It carries no secret and grants nothing on its own: the
/// generate call also requires the owner's session, the preview is looked up inside the caller's own
/// scope, and the presented hash is compared against the stored row. A hash leaked from a log, a
/// screenshot or a referrer header identifies a preview to a holder who still has to be that
/// preview's owner in that scope, and the scope is inside the hashed document precisely so a hash
/// from one scope can never be presented into another. Presenting a valid hash for somebody else's
/// preview is a 404, not a 409 - the row is not visible from here at all.
/// </para>
/// <para>
/// Canonical means: no insignificant whitespace, object members in a fixed written order rather than
/// hash order, and integers written as integers. Two runs that agree on the facts produce the same
/// bytes, which is the only way a hash can mean "these exact inputs". The order is written out here
/// as literals instead of being sorted or reflected from a type, because a hash whose document is
/// whatever a class happens to serialise to is a hash that changes when a property is renamed.
/// </para>
/// </remarks>
public static class ConsentBinding
{
    /// <summary>
    /// Bumped when the binding document gains, drops or renames a member. A stored preview carries
    /// the version it was minted under, and a preview minted under an older one is treated as stale
    /// rather than compared against a document it was not part of.
    /// </summary>
    public const int Version = 1;

    /// <summary>The scope this binding authorizes, hashed in.</summary>
    /// <param name="scopeId">The owning scope.</param>
    /// <param name="purpose">The generation purpose.</param>
    /// <param name="aggregateRef">The id the purpose applies to.</param>
    /// <param name="providerId">The provider slot.</param>
    /// <param name="model">The model.</param>
    /// <param name="credentialRevision">The credential's metadata revision, or null when the provider needs none.</param>
    /// <param name="evidenceRevision">The evidence head revision.</param>
    /// <param name="voiceRevision">The project voice profile revision.</param>
    /// <param name="selectedEvidenceIds">The evidence ids in the snapshot, in snapshot order.</param>
    /// <param name="narrativeSelections">The narrative milestone ids the selection names, ordered.</param>
    /// <param name="snapshotSha256">The digest of the snapshot bytes themselves.</param>
    public static string Compute(
        Guid scopeId,
        string purpose,
        string aggregateRef,
        string providerId,
        string model,
        long? credentialRevision,
        long evidenceRevision,
        long voiceRevision,
        IReadOnlyList<Guid> selectedEvidenceIds,
        IReadOnlyList<Guid> narrativeSelections,
        string snapshotSha256)
    {
        var document = new JsonObject
        {
            ["aggregateRef"] = aggregateRef,
            ["credentialRevision"] = credentialRevision.HasValue
                ? JsonValue.Create(credentialRevision.Value)
                : null,
            ["evidenceIds"] = ToIdArray(selectedEvidenceIds),
            ["evidenceRevision"] = evidenceRevision,
            ["model"] = model,
            ["narrativeSelections"] = ToIdArray(narrativeSelections),
            ["purpose"] = purpose,
            ["scopeId"] = scopeId.ToString("d"),
            ["snapshotSha256"] = snapshotSha256,
            ["version"] = Version,
            ["voiceRevision"] = voiceRevision,
        };

        return Sha256Hex(Encoding.UTF8.GetBytes(document.ToJsonString(CanonicalJsonOptions)));
    }

    /// <summary>Lower-case hex SHA-256 over the UTF-8 bytes of a string.</summary>
    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    /// <summary>Lower-case hex SHA-256 over a byte sequence.</summary>
    public static string Sha256Hex(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>
    /// Rewrites a JSON document into one canonical form: compact, and with every object's members
    /// ordered by name at every depth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because the stored column is <c>jsonb</c>, and PostgreSQL does not preserve a
    /// <c>jsonb</c> document's original text - it re-serialises with its own member order and its own
    /// spacing. A digest taken over the bytes as written would therefore be unverifiable against the
    /// bytes as read, which is worse than no digest at all: it would make a row look altered when it
    /// was not, and a genuinely altered row would be indistinguishable from a formatting change.
    /// </para>
    /// <para>
    /// Canonicalising both sides fixes that. The value does not change - key order and whitespace
    /// carry no meaning in JSON, and the server itself discards them - and what the digest then
    /// covers is the content of the authorized input, which is what a consent is about. It also means
    /// the stored row can be re-hashed and compared by anyone, in any language, without this
    /// assembly.
    /// </para>
    /// </remarks>
    public static string CanonicalJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The document is not valid JSON.", nameof(json), exception);
        }

        return (Sort(root) ?? new JsonObject()).ToJsonString(CanonicalJsonOptions);
    }

    /// <summary>
    /// Recursively rebuilds a node with every object's members in ordinal key order, at every depth
    /// and including inside arrays.
    /// </summary>
    /// <remarks>
    /// The array case is the one that matters and the one that is easy to miss. The evidence payload
    /// is an array of objects, so a canonicaliser that stopped at the first array would leave those
    /// members in whatever order they happened to be serialised in - the order this process wrote them
    /// on the way in and PostgreSQL's own order on the way out - and the digest would then differ
    /// between the two reads of one unchanged row. A canonical form that is canonical only at the top
    /// is not canonical.
    /// </remarks>
    private static JsonNode? Sort(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject source:
            {
                var sorted = new JsonObject();
                foreach (var member in source.OrderBy(member => member.Key, StringComparer.Ordinal))
                {
                    sorted[member.Key] = Sort(member.Value);
                }

                return sorted;
            }

            case JsonArray source:
            {
                // Array order is content, not formatting: two documents whose arrays list the same
                // values in a different order are different documents, and reordering them would
                // hide a real change. Only each element is canonicalised.
                var sorted = new JsonArray();
                foreach (var element in source)
                {
                    sorted.Add(Sort(element));
                }

                return sorted;
            }

            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Compares a presented consent against the stored one without leaking how much of it matched.
    /// </summary>
    /// <remarks>
    /// A fixed-time comparison rather than <c>==</c>. The value is not a secret, so this is not about
    /// hiding it from a network attacker; it is so that the time this call takes says nothing at all
    /// about how close a wrong guess was, which keeps the comparison from becoming an oracle for
    /// anyone who can measure the host rather than the request.
    /// </remarks>
    public static bool Matches(string? presented, string stored) =>
        !string.IsNullOrEmpty(presented) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(presented),
            Encoding.ASCII.GetBytes(stored));

    private static JsonArray ToIdArray(IReadOnlyList<Guid> ids)
    {
        var array = new JsonArray();
        foreach (var id in ids)
        {
            array.Add(id.ToString("d"));
        }

        return array;
    }

    /// <summary>Compact, escaping-free JSON. The document is hashed, not displayed.</summary>
    internal static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
    };
}
