using Chronicleaf.GitHub.GraphQL;

namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// The promises this package makes about how much of a repository's relationships become evidence
/// (GITHUB-GRAPHQL-ENRICHMENT-002).
/// </summary>
/// <remarks>
/// These are constants rather than settings. They are the numbers a test asserts and the numbers a
/// reader is told, and a configuration key that could move them would make both of those statements
/// conditional. The page and record caps live on the transport; the commit cap lives here because
/// it is this product's promise about how many stored commits one enrichment may revisit.
/// </remarks>
public static class GitHubEnrichmentLimits
{
    /// <summary>At most this many stored commits are queried in one enrichment.</summary>
    public const int MaxCommits = GitHubRelationshipReader.MaxCommitShas;

    /// <summary>The longest idempotency key a request may carry.</summary>
    public const int MaxIdempotencyKeyLength = 200;
}

/// <summary>
/// What a caller may ask an enrichment for, and the checks those words have to pass
/// (GITHUB-GRAPHQL-ENRICHMENT-002).
/// </summary>
public static class GitHubEnrichmentPolicy
{
    /// <summary>
    /// The checks an enrichment request's own words have to pass, as field errors. An empty result
    /// means the request is well formed; a non-empty one means no job was created and nothing was
    /// called.
    /// </summary>
    public static Dictionary<string, string[]> Validate(long expectedBundleRevision, string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (expectedBundleRevision < 0)
        {
            errors["expectedBundleRevision"] = ["The expected evidence revision cannot be negative."];
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > GitHubEnrichmentLimits.MaxIdempotencyKeyLength)
        {
            errors["idempotencyKey"] =
            [
                $"An idempotency key must be 1 to {GitHubEnrichmentLimits.MaxIdempotencyKeyLength} characters.",
            ];
        }

        return errors;
    }
}
