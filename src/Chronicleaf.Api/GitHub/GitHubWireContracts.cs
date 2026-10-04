using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Core;

namespace Chronicleaf.Api.GitHub;

/// <summary>
/// The wire shape of a repository connection (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// Field names are pinned explicitly, as the access and milestone packages' are, so the contract
/// cannot drift with serializer policy.
/// </para>
/// <para>
/// <b>What a client is told:</b> the two names it asked for, the default branch GitHub reported, and
/// whether a credential is configured. <b>What it is not told:</b> the credential's value - it is not
/// here to be had, because this package never holds one - and no URL. A connection is two names, and
/// a response that could carry a host would be a response that could be pointed somewhere else.
/// </para>
/// <para>
/// <c>hasCredential</c> is a boolean rather than the reference itself. A client needs to know whether
/// a token is configured; it has no use for the vault's identifier, and a stored id in a browser's
/// memory is one more copy of a thing that identifies a secret.
/// </para>
/// </remarks>
public sealed record GitHubConnectionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch,
    [property: JsonPropertyName("repositoryId")] long? RepositoryId,
    [property: JsonPropertyName("hasCredential")] bool HasCredential,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    public static GitHubConnectionResponse From(GitHubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new GitHubConnectionResponse(
            connection.Id.ToString("D"),
            connection.ProjectId.ToString("D"),
            connection.Owner,
            connection.Repo,
            connection.DefaultBranch,
            connection.RepositoryId,
            connection.CredentialRef is not null,
            Stamp(connection.CreatedAt),
            Stamp(connection.UpdatedAt));
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The body a connection is stored from (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// Three fields, and the absence of a fourth is the security property. There is no URL, no endpoint
/// and no host: a repository is an owner and a name, and a body that could name a host would let a
/// request choose where the server's next request goes.
/// </para>
/// <para>
/// The credential arrives as a reference to one the owner already stored through the delivered vault
/// route. This route never accepts a token and never returns one.
/// </para>
/// </remarks>
public sealed record GitHubConnectionRequest(
    [property: JsonPropertyName("owner")] string? Owner,
    [property: JsonPropertyName("repository")] string? Repository,
    [property: JsonPropertyName("credentialRef")] string? CredentialRef);

/// <summary>
/// The body a collection is asked for (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// The evidence revision travels in the body and as <c>If-Match</c>, and the server refuses a body
/// that disagrees with the header rather than picking one. The idempotency key is the caller's: a
/// repeat of the same key returns the job the first request created instead of starting a second
/// collection of the same repository.
/// </remarks>
public sealed record GitHubSyncWireRequest(
    [property: JsonPropertyName("headRef")] string? HeadRef,
    [property: JsonPropertyName("baseRef")] string? BaseRef,
    [property: JsonPropertyName("releaseTag")] string? ReleaseTag,
    [property: JsonPropertyName("issueNumber")] int? IssueNumber,
    [property: JsonPropertyName("pullRequestNumber")] int? PullRequestNumber,
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey)
{
    /// <summary>
    /// The request a body that did not arrive produces: every field empty and the revision absent, so
    /// an empty body is refused by name rather than being read as "revision zero, no refs" and
    /// matching a milestone that happens to have no evidence yet.
    /// </summary>
    public static GitHubSyncWireRequest Empty { get; } = new(
        HeadRef: null,
        BaseRef: null,
        ReleaseTag: null,
        IssueNumber: null,
        PullRequestNumber: null,
        ExpectedBundleRevision: null,
        IdempotencyKey: null);
}

/// <summary>
/// The answer to a collection request: the job that will perform it, and where to read its progress
/// (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// A 202 and an operation id, never a bundle. The collection is a bounded set of network reads with
/// a rate limit attached, so the work happens in the delivered queue and this response points at the
/// delivered operation route. <c>operationUrl</c> is included so a client does not have to build the
/// path itself, and <c>created</c> says whether this request created the job or replayed one.
/// </remarks>
public sealed record GitHubSyncAcceptedResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationUrl")] string OperationUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created")] bool Created);
