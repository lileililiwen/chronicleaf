using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The bounds this package keeps and the decisions it makes before anything is stored
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// Every rule here is a closed decision rather than a judgement call made at the point of use. The
/// routing table is a fixed set of event and action pairs, the star thresholds are two numbers, and a
/// payload can only ever name a kind this package has a builder for - which is what lets the "exactly
/// one candidate per source key" promise be checked by a unique index rather than by reading a loop
/// and hoping.
/// </remarks>
public static class GitHubCandidatePolicy
{
    /// <summary>
    /// The largest delivery this route accepts.
    /// </summary>
    /// <remarks>
    /// One mebibyte, and it is checked against the request's own declared length before the body is
    /// read, so an oversized delivery is a 413 rather than a large allocation. The largest payload this
    /// package actually reads is a release object, which is a few kilobytes; the ceiling exists for the
    /// events it does not route at all, so that an unroutable delivery is still bounded.
    /// </remarks>
    public const long DeliveryMaxBytes = 1024 * 1024;

    /// <summary>The widest a stored candidate title may be, and the point at which a provider's string is cut.</summary>
    public const int TitleMaxLength = 200;

    /// <summary>The widest a stored event or action name may be before it is refused as malformed.</summary>
    public const int EventNameMaxLength = 40;

    /// <summary>The widest a stored action name may be.</summary>
    public const int ActionNameMaxLength = 40;

    /// <summary>The widest a GitHub delivery id may be. GitHub's are UUIDs, and this leaves room.</summary>
    public const int DeliveryIdMaxLength = 64;

    /// <summary>The widest an idempotency key may be.</summary>
    public const int IdempotencyKeyMaxLength = 200;

    /// <summary>
    /// The star counts that are worth proposing at all.
    /// </summary>
    /// <remarks>
    /// Two numbers, chosen rather than derived, and the package says so out loud: a repository's star
    /// count is a popularity signal rather than a piece of work, so only the two round numbers a
    /// maintainer would actually mention in a release note are proposed, and everything between them
    /// produces nothing. A threshold is expressed as the count that crosses it, so the first delivery
    /// that reports at least the threshold proposes it and every later one is the same source key.
    /// </remarks>
    public static readonly IReadOnlyList<int> StarThresholds = [100, 1000];

    /// <summary>The events this package routes, in the order the closed vocabulary names them.</summary>
    public static readonly IReadOnlyList<string> SupportedEvents =
    [
        "release",
        "create",
        "issues",
        "pull_request",
        "star",
    ];

    /// <summary>
    /// The actions this package acts on, per event.
    /// </summary>
    /// <remarks>
    /// The pairs are the design's, and each exclusion is as load-bearing as each inclusion:
    /// <list type="bullet">
    /// <item><c>release</c> only on <c>published</c>. A draft is unpublished, and an edited or deleted
    /// release proposes nothing new.</item>
    /// <item><c>create</c> only on a tag. A branch is not a release anybody shipped.</item>
    /// <item><c>issues</c> only on <c>closed</c>. An opened issue is work in progress.</item>
    /// <item><c>pull_request</c> only on <c>closed</c> <em>and</em> only when the payload says it merged.
    /// A closed pull request that did not merge is work that was abandoned, and proposing it would be
    /// proposing a reversal as an achievement.</item>
    /// <item><c>star</c> only on <c>created</c>, and only at a declared threshold.</item>
    /// </list>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> RoutedActions =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["release"] = ["published"],
            ["create"] = [],
            ["issues"] = ["closed"],
            ["pull_request"] = ["closed"],
            ["star"] = ["created"],
        };

    /// <summary>True when this build routes the event at all.</summary>
    public static bool IsRoutedEvent(string? @event) =>
        @event is not null && RoutedActions.ContainsKey(@event);

    /// <summary>True when the event's own action is one this build acts on, whatever the payload says next.</summary>
    public static bool IsRoutedAction(string? @event, string? action)
    {
        if (@event is null || !RoutedActions.TryGetValue(@event, out var actions))
        {
            return false;
        }

        // An action is optional: GitHub sends one for most of these events and the header alone can name
        // the event. An event whose routed set is empty (a created tag) is identified by the payload's
        // own ref type instead, which is a decision about content rather than about the action word.
        if (action is null || action.Length == 0)
        {
            return actions.Count == 0;
        }

        return actions.Contains(action, StringComparer.Ordinal);
    }

    /// <summary>True when this event's action alone decides nothing and the payload's ref type must.</summary>
    public static bool NeedsRefTypeCheck(string? @event) =>
        @event is not null && RoutedActions.TryGetValue(@event, out var actions) && actions.Count == 0;

    /// <summary>
    /// The thresholds a star count has reached, lowest first.
    /// </summary>
    /// <remarks>
    /// A count of 1 500 reaches both thresholds and therefore produces two candidates - one per crossed
    /// threshold - because "first crosses 100 or 1000" is a statement about each threshold separately.
    /// A count of zero reaches none, and neither does a count that is not a number at all.
    /// </remarks>
    public static IReadOnlyList<int> ReachedStarThresholds(long starCount)
    {
        if (starCount <= 0)
        {
            return [];
        }

        return [.. StarThresholds.Where(threshold => starCount >= threshold).OrderBy(threshold => threshold)];
    }

    /// <summary>True when the header names a delivery this build can accept at all.</summary>
    public static bool IsUsableEventName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= EventNameMaxLength;

    /// <summary>True when the header names a delivery id this build can store.</summary>
    public static bool IsUsableDeliveryId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= DeliveryIdMaxLength;

    /// <summary>
    /// Whether a signed delivery's signature matches its own bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HMAC-SHA256 over the raw request body, compared in constant time against the
    /// <c>X-Hub-Signature-256</c> header's hex digest. Constant time matters here in a way it does not
    /// everywhere else: a byte-at-a-time comparison would let a sender learn the correct digest one
    /// character at a time by timing the answer, and a delivery route's whole authentication is that
    /// digest.
    /// </para>
    /// <para>
    /// <b>The raw body, not a re-serialised one.</b> The signature is computed by GitHub over the exact
    /// bytes it sent, and any re-encoding - reordered members, a different whitespace, a number written
    /// differently - produces different bytes and a different digest. This method therefore takes the
    /// buffer the server read and never a parsed model, which is also why nothing upstream of here
    /// deserialises the body before the comparison.
    /// </para>
    /// <para>
    /// A missing header, a header that is not <c>sha256=</c> followed by sixty-four hex characters, and
    /// a length that is not sixty-four are all the same answer: no. An <c>sha1=</c> header is refused
    /// rather than accepted alongside, because a sender that can produce a SHA-1 signature can also
    /// produce an HMAC-SHA256 one, and this route never had a weaker answer to offer.
    /// </para>
    /// </remarks>
    public static bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        const string Prefix = "sha256=";
        if (signatureHeader.Length != Prefix.Length + 64 ||
            !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = new byte[32];
        for (var index = 0; index < 32; index++)
        {
            var high = HexValue(signatureHeader[Prefix.Length + (index * 2)]);
            var low = HexValue(signatureHeader[Prefix.Length + (index * 2) + 1]);

            if (high < 0 || low < 0)
            {
                return false;
            }

            presented[index] = (byte)((high << 4) | low);
        }

        Span<byte> computed = stackalloc byte[32];
        var key = Encoding.UTF8.GetBytes(secret);

        try
        {
            // HashData returns the number of bytes it wrote, not a success flag: HMAC-SHA256 over any
            // input always writes exactly 32, so the length is checked and the comparison is then the
            // constant-time one. `&&` here would be a length check short-circuiting a byte comparison,
            // and a partial digest must never be compared.
            if (HMACSHA256.HashData(key, body, computed) != computed.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(computed, presented);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(computed);
            CryptographicOperations.ZeroMemory(presented);
        }
    }

    /// <summary>The signature a sender would produce for these bytes, for a test to build a real header.</summary>
    /// <remarks>
    /// Public because a scenario has to produce a genuine digest rather than assert that a digest is
    /// compared: a fixture that hard-coded the expected value would go stale the moment the algorithm
    /// this method uses changed, and would keep passing while the route stopped verifying anything.
    /// </remarks>
    public static string Sign(ReadOnlySpan<byte> body, string secret)
    {
        Span<byte> computed = stackalloc byte[32];
        var key = Encoding.UTF8.GetBytes(secret);

        try
        {
            HMACSHA256.HashData(key, body, computed);

            return "sha256=" + Convert.ToHexStringLower(computed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(computed);
        }
    }

    /// <summary>A candidate title cut at the declared bound, because a provider's string is not a length.</summary>
    public static string Title(string? value, string fallback) =>
        Bounded(value, TitleMaxLength) is { Length: > 0 } bounded ? bounded : fallback;

    /// <summary>A bounded copy of a provider's string, or <c>null</c> when it carried nothing.</summary>
    public static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>
    /// The parse of a poll's <c>since</c>, or the answer when it is not a usable instant.
    /// </summary>
    /// <remarks>
    /// Refused rather than defaulted. A default window would silently propose the wrong things - a
    /// repository's last ten thousand releases - after an owner asked for one afternoon's worth, and the
    /// result would look like a correct answer to a question nobody asked.
    /// </remarks>
    public static bool TryParseSince(string? value, out DateTimeOffset? since, out string? error)
    {
        since = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            error = "A since window is an ISO 8601 instant.";

            return false;
        }

        since = parsed;

        return true;
    }

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'a' and <= 'f' => character - 'a' + 10,
        // Uppercase is accepted because it is the same digest and a sender that wrote it in uppercase
        // has still signed the same bytes; refusing it would reject a correct delivery over casing.
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => -1,
    };
}
