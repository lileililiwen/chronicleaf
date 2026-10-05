using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.GitHub.GraphQL;

/// <summary>
/// What one relationship-enrichment attempt produced, or why it produced nothing
/// (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A refusal is a value here, not an exception. The ways GitHub can answer "no" mean different
/// things to a job: a repository that is absent or private is permanent, a refused credential is
/// permanent, a missing permission is permanent, a 429 is worth another attempt after the
/// provider's own delay, and anything else is a dependency that may come back.
/// </para>
/// <para>
/// A successful result carries only relationships <em>between things that already exist</em>: every
/// relation names the commit it was observed from and the provider's own node id, title, state and
/// URL. Nothing here invents a fact; a field GraphQL refused is recorded as unavailable and the
/// caller keeps whatever the REST collection already stored.
/// </para>
/// </remarks>
public sealed class GitRelationshipResult
{
    private static readonly IReadOnlyList<Evidence.GitEndpoint> NoEndpoints = [];
    private static readonly IReadOnlyList<GitRelation> NoRelations = [];
    private static readonly IReadOnlyList<GitRelationshipUnavailable> NoUnavailable = [];

    /// <summary>
    /// Builds a result. Public because this contract may have more than one implementation, and a
    /// private constructor would tie this value shape to the client that happens to ship first.
    /// </summary>
    public GitRelationshipResult(
        GitRelationshipOutcome outcome,
        Evidence.RepoRef? repository,
        DateTimeOffset fetchedAt,
        TimeSpan? retryAfter = null)
    {
        Outcome = outcome;
        Repository = repository;
        FetchedAt = fetchedAt;
        RetryAfter = retryAfter;
    }

    /// <summary>How the enrichment read ended.</summary>
    public GitRelationshipOutcome Outcome { get; }

    /// <summary>The repository that was read, when the attempt reached one.</summary>
    public Evidence.RepoRef? Repository { get; }

    /// <summary>At most <see cref="GitHubRelationshipReader.MaxRelations"/> attributed relations.</summary>
    public IReadOnlyList<GitRelation> Relations { get; init; } = NoRelations;

    /// <summary>
    /// Every commit whose relations were asked for and could not be read, each with the reason. A
    /// part that is missing is recorded as missing here rather than being dropped, so an owner is
    /// never told an enrichment covers a commit it does not mention.
    /// </summary>
    public IReadOnlyList<GitRelationshipUnavailable> Unavailable { get; init; } = NoUnavailable;

    /// <summary>True when the read stopped at <see cref="GitHubRelationshipReader.MaxRelations"/>.</summary>
    public bool Truncated { get; init; }

    /// <summary>Every request that was actually made, in order.</summary>
    public IReadOnlyList<Evidence.GitEndpoint> Endpoints { get; init; } = NoEndpoints;

    /// <summary>When the answer was produced.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>
    /// The delay the provider asked for on a throttled answer, clamped by the caller. Never
    /// trusted as-is: a response body must not be able to park an operation for a year.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Relationships were read for the commits that had any to report.</summary>
    public static GitRelationshipResult Collected(Evidence.RepoRef repository, DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.Collected, repository, fetchedAt, retryAfter: null);

    /// <summary>
    /// GitHub answered 404. A private repository and a repository that does not exist are the same
    /// answer on purpose, so an owner cannot use this to discover which private repositories exist.
    /// </summary>
    public static GitRelationshipResult RepositoryUnavailable(DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.RepositoryUnavailable, repository: null, fetchedAt, retryAfter: null);

    /// <summary>GitHub answered 401: the credential was refused, or one was required and none was configured.</summary>
    public static GitRelationshipResult CredentialRejected(DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.CredentialRejected, repository: null, fetchedAt, retryAfter: null);

    /// <summary>
    /// The credential is valid but may not read this repository's relationships: a GraphQL
    /// <c>FORBIDDEN</c> or an HTTP 403. Permanent, and reported without inventing a single relation.
    /// </summary>
    public static GitRelationshipResult PermissionDenied(DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.PermissionDenied, repository: null, fetchedAt, retryAfter: null);

    /// <summary>GitHub answered 429 and said when it may be asked again.</summary>
    public static GitRelationshipResult RateLimited(DateTimeOffset fetchedAt, TimeSpan? retryAfter) =>
        new(GitRelationshipOutcome.RateLimited, repository: null, fetchedAt, retryAfter);

    /// <summary>GitHub refused the request itself: malformed variables, or a shape it rejects.</summary>
    public static GitRelationshipResult Rejected(DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.Rejected, repository: null, fetchedAt, retryAfter: null);

    /// <summary>
    /// GitHub could not answer: a 5xx, a transport failure, a timeout, or a response that was not
    /// the JSON this client can read. Worth another attempt, never reported as a fact.
    /// </summary>
    public static GitRelationshipResult CouldNotBeRead(DateTimeOffset fetchedAt) =>
        new(GitRelationshipOutcome.Unavailable, repository: null, fetchedAt, retryAfter: null);
}

/// <summary>The answers a relationship read can give, and nothing else.</summary>
public enum GitRelationshipOutcome
{
    /// <summary>What was asked for was read, in whole or in honest part.</summary>
    Collected,

    /// <summary>The repository is absent or private. GitHub does not distinguish them.</summary>
    RepositoryUnavailable,

    /// <summary>The credential was refused, or one was required and none was configured.</summary>
    CredentialRejected,

    /// <summary>The credential may not read these relationships. Permanent and fact-free.</summary>
    PermissionDenied,

    /// <summary>GitHub asked us to slow down and said when.</summary>
    RateLimited,

    /// <summary>The request was refused as malformed or as pointing at something that is not public.</summary>
    Rejected,

    /// <summary>GitHub could not answer right now.</summary>
    Unavailable,
}

/// <summary>
/// One attributed relationship between an already-collected commit and an issue or pull request
/// (GITHUB-GRAPHQL-ENRICHMENT-001).
/// </summary>
/// <param name="NodeId">The provider's own identity for the related record. Deduplicated on this.</param>
/// <param name="Kind">Either <c>pr</c> or <c>issue</c>.</param>
/// <param name="Number">The issue or pull request number within the repository.</param>
/// <param name="Title">The title, bounded in length.</param>
/// <param name="State">GitHub's own state, bounded in length.</param>
/// <param name="Merged">Whether GitHub reports the pull request as merged, or <c>null</c> for an issue.</param>
/// <param name="Reference">The record's own web address, when the source gave one.</param>
/// <param name="BaseRef">The pull request's base branch, when the source gave one.</param>
/// <param name="HeadRef">The pull request's head branch, when the source gave one.</param>
/// <param name="FromSha">The collected commit this relation was observed from.</param>
/// <param name="FromPullNumber">For an issue reached through a pull request, that pull request's number.</param>
public sealed record GitRelation(
    string NodeId,
    string Kind,
    int Number,
    string Title,
    string State,
    bool? Merged,
    string? Reference,
    string? BaseRef,
    string? HeadRef,
    string FromSha,
    int? FromPullNumber = null);

/// <summary>
/// One commit whose relations were asked for and could not be read. The segment names the commit
/// and the reason names why, from the same fixed vocabulary the REST transport uses, so a reader
/// can tell an absent relation from a collected one.
/// </summary>
/// <param name="Segment">Which part was missing: <c>relation:&lt;sha&gt;</c>.</param>
/// <param name="Reason">Why it is missing, from the transport's own fixed vocabulary.</param>
public sealed record GitRelationshipUnavailable(string Segment, string Reason);

/// <summary>
/// One read-only source of GitHub relationship facts (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here is a read.</b> The interface has no write, no create, no comment and no
/// merge. That is a property of the shape rather than of a policy that could be forgotten: there is
/// no verb to call.
/// </para>
/// <para>
/// An implementation must be safe to call concurrently, must answer with a
/// <see cref="GitRelationshipResult"/> rather than an exception for a refusal the caller has to
/// classify, and must never log or return the credential it was given.
/// </para>
/// </remarks>
public interface IGitHubRelationshipReader
{
    /// <summary>
    /// Reads the issue and pull request relationships attributed to already-collected commits. Every
    /// SHA is validated before any request is made; a value that is not an immutable revision is
    /// never queried.
    /// </summary>
    Task<GitRelationshipResult> ReadAsync(
        Evidence.RepoRef repository,
        IReadOnlyList<string> commitShas,
        string? credential,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The read-only GitHub GraphQL client (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is injected, and this class has no idea how it is built.</b> It takes an
/// <see cref="HttpClient"/> and a <see cref="TimeProvider"/>; the composition root decides the base
/// address, the timeout and the redirect behaviour. That is what lets a test supply a fake handler
/// and see this class's real decisions without a socket.
/// </para>
/// <para>
/// <b>The query is fixed text.</b> There is exactly one named operation and the only values that
/// change between requests are the repository owner and name (validated by
/// <see cref="Evidence.RepoRef"/>), one commit SHA (validated as 40 hexadecimal characters before
/// any request) and the server's own page cursor echoed back bounded. No query is built from user
/// source text and no dynamic code is ever assembled: a caller cannot widen what is asked for.
/// </para>
/// <para>
/// <b>A redirect is never followed.</b> The shared handler refuses automatic redirects, and any 3xx
/// is reported as unavailable with no second request. An <c>Authorization</c> header is attached
/// only when a credential was leased for this call.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> It is parsed with <see cref="JsonDocument"/> and
/// every value taken from it is bounded before it leaves this class. Partial GraphQL errors mark
/// the fields they touch as unavailable; data that arrived beside errors is never treated as
/// complete for the fields the errors name.
/// </para>
/// </remarks>
public sealed class GitHubRelationshipReader : IGitHubRelationshipReader
{
    /// <summary>The path this client posts to, relative to the pinned API host.</summary>
    public const string GraphQlPath = "graphql";

    /// <summary>The one named operation this client ever sends.</summary>
    public const string OperationName = "ChronicleafRelationshipEnrichment";

    /// <summary>At most this many commits are enriched in one read.</summary>
    public const int MaxCommitShas = 20;

    /// <summary>At most this many relations are returned per commit page.</summary>
    public const int PageSize = 50;

    /// <summary>At most this many pages are followed for one commit.</summary>
    public const int MaxPagesPerCommit = 4;

    /// <summary>At most this many relationship records are kept in one read.</summary>
    public const int MaxRelations = 200;

    /// <summary>The longest page cursor echoed back to the server.</summary>
    public const int CursorMaxLength = 1000;

    private const int MaxTextFieldLength = 400;
    private const int MaxUrlLength = 200;
    private const int MaxRefLength = 255;
    private const int MaxNodeIdLength = 200;
    private const int MaxIssuesPerPullRequest = 20;

    /// <summary>
    /// The one query this client sends. Page sizes are literals, so a caller cannot move them; the
    /// variables are the repository, one commit SHA and the server's own cursor.
    /// </summary>
    public const string Query = """
        query ChronicleafRelationshipEnrichment($owner: String!, $name: String!, $oid: GitObjectID!, $after: String) {
          repository(owner: $owner, name: $name) {
            object(oid: $oid) {
              __typename
              ... on Commit {
                oid
                associatedPullRequests(first: 50, after: $after, orderBy: {field: CREATED_AT, direction: ASC}) {
                  nodes {
                    id
                    number
                    title
                    state
                    merged
                    url
                    baseRefName
                    headRefName
                    closingIssuesReferences(first: 20) {
                      nodes {
                        id
                        number
                        title
                        state
                        url
                      }
                    }
                  }
                  pageInfo {
                    hasNextPage
                    endCursor
                  }
                }
              }
            }
          }
        }
        """;

    private readonly HttpClient http;
    private readonly TimeProvider clock;

    public GitHubRelationshipReader(HttpClient http, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        this.http = http;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GitRelationshipResult> ReadAsync(
        Evidence.RepoRef repository,
        IReadOnlyList<string> commitShas,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(commitShas);

        var fetchedAt = clock.GetUtcNow();

        // Every ref is an immutable revision before a single fact is read. A value that is not 40
        // hexadecimal characters is not a commit this product collected, so it is never queried:
        // sending it would ask GitHub about something the caller invented rather than observed.
        var shas = commitShas
            .Where(IsCommitSha)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCommitShas)
            .ToArray();

        var relations = new List<GitRelation>();
        var unavailable = new List<GitRelationshipUnavailable>();
        var endpoints = new List<Evidence.GitEndpoint>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var sha in shas)
        {
            var cursor = (string?)null;

            for (var page = 0; page < MaxPagesPerCommit; page++)
            {
                if (relations.Count >= MaxRelations)
                {
                    truncated = true;
                    break;
                }

                var answer = await PostAsync(
                    repository,
                    sha,
                    cursor,
                    credential,
                    endpoints,
                    cancellationToken).ConfigureAwait(false);

                if (answer.Page is null)
                {
                    // The page did not arrive as readable data. A whole-run refusal ends the read;
                    // anything else is recorded against this commit and the next commit is still read,
                    // because one commit GitHub cannot describe must not cost the relations of the rest.
                    if (answer.Outcome is { } refusal)
                    {
                        return Refusal(repository, fetchedAt, refusal, answer.RetryAfter, endpoints);
                    }

                    unavailable.Add(new GitRelationshipUnavailable(
                        $"relation:{sha}",
                        answer.Reason ?? Evidence.GitUnavailableReasons.Unreachable));
                    break;
                }

                foreach (var relation in answer.Page.Relations)
                {
                    if (relations.Count >= MaxRelations)
                    {
                        truncated = true;
                        break;
                    }

                    // Pagination repeats a provider node rather than reporting an error for it, so
                    // the node id is the deduplication key: only one observed entry is ever kept.
                    if (seen.Add(relation.NodeId))
                    {
                        relations.Add(relation);
                    }
                }

                if (truncated)
                {
                    break;
                }

                if (answer.Page.Missing)
                {
                    unavailable.Add(new GitRelationshipUnavailable(
                        $"relation:{sha}",
                        Evidence.GitUnavailableReasons.NotFound));
                    break;
                }

                if (!answer.Page.HasNextPage || answer.Page.EndCursor is null)
                {
                    break;
                }

                if (page == MaxPagesPerCommit - 1)
                {
                    truncated = true;
                    break;
                }

                cursor = answer.Page.EndCursor;
            }

            if (truncated)
            {
                break;
            }
        }

        return new GitRelationshipResult(GitRelationshipOutcome.Collected, repository, fetchedAt)
        {
            Relations = relations,
            Unavailable = unavailable,
            Truncated = truncated,
            Endpoints = endpoints,
        };
    }

    private static GitRelationshipResult Refusal(
        Evidence.RepoRef repository,
        DateTimeOffset fetchedAt,
        GitRelationshipOutcome outcome,
        TimeSpan? retryAfter,
        List<Evidence.GitEndpoint> endpoints)
    {
        var empty = outcome switch
        {
            GitRelationshipOutcome.RepositoryUnavailable =>
                GitRelationshipResult.RepositoryUnavailable(fetchedAt),
            GitRelationshipOutcome.CredentialRejected =>
                GitRelationshipResult.CredentialRejected(fetchedAt),
            GitRelationshipOutcome.PermissionDenied =>
                GitRelationshipResult.PermissionDenied(fetchedAt),
            GitRelationshipOutcome.RateLimited =>
                GitRelationshipResult.RateLimited(fetchedAt, retryAfter),
            GitRelationshipOutcome.Rejected =>
                GitRelationshipResult.Rejected(fetchedAt),
            _ => GitRelationshipResult.CouldNotBeRead(fetchedAt),
        };

        return new GitRelationshipResult(empty.Outcome, repository, fetchedAt, empty.RetryAfter)
        {
            Endpoints = endpoints,
        };
    }

    /// <summary>True when a value is an immutable commit revision: 40 hexadecimal characters.</summary>
    public static bool IsCommitSha(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 40)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<PageAnswer> PostAsync(
        Evidence.RepoRef repository,
        string sha,
        string? cursor,
        string? credential,
        List<Evidence.GitEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        using var body = new StringContent(
            JsonSerializer.Serialize(new
            {
                operationName = OperationName,
                query = Query,
                variables = new
                {
                    owner = repository.Owner,
                    name = repository.Name,
                    oid = sha,
                    after = cursor,
                },
            }),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlPath)
        {
            Content = body,
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", Evidence.GitHubEvidenceClient.UserAgent);

        // The credential is attached here and nowhere else. It is a lease the caller already
        // returned by the time this call ends, and it is never logged, never put in a result and
        // never put in an exception.
        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            endpoints.Add(new Evidence.GitEndpoint("POST", GraphQlPath, 0));
            return PageAnswer.Transport;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            endpoints.Add(new Evidence.GitEndpoint("POST", GraphQlPath, 0));
            return PageAnswer.Transport;
        }

        using (response)
        {
            endpoints.Add(new Evidence.GitEndpoint("POST", GraphQlPath, (int)response.StatusCode));

            if (response.StatusCode is HttpStatusCode.OK)
            {
                JsonDocument document;
                try
                {
                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    document = await JsonDocument
                        .ParseAsync(stream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return PageAnswer.Transport;
                }

                using (document)
                {
                    return ReadPage(document.RootElement, sha, response);
                }
            }

            return PageAnswer.FromStatus(response.StatusCode, RetryAfterOf(response));
        }
    }

    /// <summary>
    /// One answered page: either readable data or the classification of the refusal. A whole-run
    /// refusal is an outcome; anything else is a reason recorded against one commit.
    /// </summary>
    private readonly struct PageAnswer
    {
        public PageAnswer(CommitPage? page, GitRelationshipOutcome? outcome, string? reason, TimeSpan? retryAfter = null)
        {
            Page = page;
            Outcome = outcome;
            Reason = reason;
            RetryAfter = retryAfter;
        }

        public CommitPage? Page { get; }

        public GitRelationshipOutcome? Outcome { get; }

        public string? Reason { get; }

        public TimeSpan? RetryAfter { get; }

        /// <summary>The request never reached an answer: no connection, a reset or a timeout.</summary>
        public static PageAnswer Transport { get; } = new(null, null, Evidence.GitUnavailableReasons.Unreachable);

        public static PageAnswer FromStatus(HttpStatusCode status, TimeSpan? retryAfter) =>
            status switch
            {
                HttpStatusCode.NotFound =>
                    new PageAnswer(null, GitRelationshipOutcome.RepositoryUnavailable, null),
                HttpStatusCode.Unauthorized =>
                    new PageAnswer(null, GitRelationshipOutcome.CredentialRejected, null),
                HttpStatusCode.Forbidden =>
                    new PageAnswer(null, GitRelationshipOutcome.PermissionDenied, null),
                HttpStatusCode.TooManyRequests =>
                    new PageAnswer(null, GitRelationshipOutcome.RateLimited, null, retryAfter),
                // A redirect is not an answer, and following one is the one way an Authorization
                // header could leave the host it was issued for. There is no second request here.
                HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect =>
                    new PageAnswer(null, GitRelationshipOutcome.Unavailable, null),
                (HttpStatusCode)422 =>
                    new PageAnswer(null, GitRelationshipOutcome.Rejected, null),
                _ when (int)status is >= 500 =>
                    new PageAnswer(null, GitRelationshipOutcome.Unavailable, null),
                _ when (int)status is >= 400 =>
                    new PageAnswer(null, GitRelationshipOutcome.Rejected, null),
                _ => new PageAnswer(null, GitRelationshipOutcome.Unavailable, null),
            };
    }

    /// <summary>One commit's page of relations, as read from one GraphQL response.</summary>
    private sealed record CommitPage(
        IReadOnlyList<GitRelation> Relations,
        bool HasNextPage,
        string? EndCursor,
        bool Missing);

    /// <summary>
    /// Reads one page out of a GraphQL response. Errors beside data mark the fields they touch as
    /// unavailable; data that arrived is kept, and data that never arrived for this commit is
    /// reported as missing rather than as a fact.
    /// </summary>
    private static PageAnswer ReadPage(JsonElement root, string sha, HttpResponseMessage response)
    {
        var errors = root.TryGetProperty("errors", out var errorArray) &&
            errorArray.ValueKind is JsonValueKind.Array
            ? errorArray.EnumerateArray().ToArray()
            : [];

        // An error type is the provider's own classification, never a sentence copied into storage.
        var types = errors
            .Select(error => error.TryGetProperty("type", out var type) &&
                type.ValueKind is JsonValueKind.String
                ? type.GetString()
                : null)
            .Where(type => type is not null)
            .ToHashSet(StringComparer.Ordinal);

        if (types.Contains("RATE_LIMITED"))
        {
            return new PageAnswer(null, GitRelationshipOutcome.RateLimited, null, RetryAfterOf(response));
        }

        if (types.Contains("FORBIDDEN"))
        {
            return new PageAnswer(null, GitRelationshipOutcome.PermissionDenied, null);
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind is not JsonValueKind.Object)
        {
            // No data at all. A NOT_FOUND error names the missing thing; anything else is a
            // dependency that may come back.
            return types.Contains("NOT_FOUND")
                ? new PageAnswer(null, GitRelationshipOutcome.RepositoryUnavailable, null)
                : PageAnswer.Transport;
        }

        if (!data.TryGetProperty("repository", out var repository) ||
            repository.ValueKind is JsonValueKind.Null)
        {
            // The repository itself is absent or private. On the first page of the first read this
            // is the whole run's answer; otherwise it is recorded against this commit, because a
            // repository that vanished mid-read must not silently keep the relations read before it.
            return types.Contains("NOT_FOUND")
                ? new PageAnswer(new CommitPage([], HasNextPage: false, EndCursor: null, Missing: true), null, null)
                : new PageAnswer(null, null, Evidence.GitUnavailableReasons.Unreachable);
        }

        if (!repository.TryGetProperty("object", out var commit) ||
            commit.ValueKind is JsonValueKind.Null)
        {
            return new PageAnswer(new CommitPage([], HasNextPage: false, EndCursor: null, Missing: true), null, null);
        }

        if (!commit.TryGetProperty("associatedPullRequests", out var connection) ||
            connection.ValueKind is not JsonValueKind.Object)
        {
            // The field GraphQL refused is marked unavailable and whatever else arrived is kept:
            // nonnull data with errors is never treated as complete for the errored field.
            var reason = types.Contains("NOT_FOUND")
                ? Evidence.GitUnavailableReasons.NotFound
                : Evidence.GitUnavailableReasons.Unreachable;
            return new PageAnswer(null, null, reason);
        }

        var relations = new List<GitRelation>();
        var missing = false;

        if (connection.TryGetProperty("nodes", out var nodes) &&
            nodes.ValueKind is JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind is not JsonValueKind.Object)
                {
                    // A deleted or inaccessible pull request arrives as a null node: an explicit
                    // unavailable source, never a fabricated fact and never silently dropped.
                    missing = true;
                    continue;
                }

                var pull = ReadPullRequest(node, sha);
                if (pull is null)
                {
                    missing = true;
                    continue;
                }

                relations.Add(pull);

                if (node.TryGetProperty("closingIssuesReferences", out var issues) &&
                    issues.ValueKind is JsonValueKind.Object &&
                    issues.TryGetProperty("nodes", out var issueNodes) &&
                    issueNodes.ValueKind is JsonValueKind.Array)
                {
                    foreach (var issueNode in issueNodes.EnumerateArray())
                    {
                        if (issueNode.ValueKind is not JsonValueKind.Object)
                        {
                            missing = true;
                            continue;
                        }

                        var issue = ReadIssue(issueNode, sha, pull.Number);
                        if (issue is null)
                        {
                            missing = true;
                            continue;
                        }

                        if (relations.Count < MaxRelations + MaxIssuesPerPullRequest)
                        {
                            relations.Add(issue);
                        }
                    }
                }
            }
        }

        var hasNextPage = false;
        string? endCursor = null;

        if (connection.TryGetProperty("pageInfo", out var pageInfo) &&
            pageInfo.ValueKind is JsonValueKind.Object)
        {
            hasNextPage = pageInfo.TryGetProperty("hasNextPage", out var next) &&
                next.ValueKind is JsonValueKind.True;
            var rawCursor = ReadBounded(pageInfo, "endCursor", CursorMaxLength);
            endCursor = hasNextPage ? rawCursor : null;
        }

        return new PageAnswer(
            new CommitPage(relations, hasNextPage, endCursor, missing && relations.Count == 0),
            null,
            null);
    }

    private static GitRelation? ReadPullRequest(JsonElement node, string sha)
    {
        var nodeId = ReadBounded(node, "id", MaxNodeIdLength);
        var number = ReadInt(node, "number");

        if (nodeId is null || number is null)
        {
            return null;
        }

        var title = ReadBounded(node, "title", MaxTextFieldLength) ?? string.Empty;
        var state = ReadBounded(node, "state", 32) ?? "unknown";
        var merged = node.TryGetProperty("merged", out var flag)
            ? flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => (bool?)false,
                _ => null,
            }
            : null;

        return new GitRelation(
            nodeId,
            "pr",
            number.Value,
            title,
            state,
            merged,
            ReadBounded(node, "url", MaxUrlLength),
            ReadBounded(node, "baseRefName", MaxRefLength),
            ReadBounded(node, "headRefName", MaxRefLength),
            sha);
    }

    private static GitRelation? ReadIssue(JsonElement node, string sha, int parentPullNumber)
    {
        var nodeId = ReadBounded(node, "id", MaxNodeIdLength);
        var number = ReadInt(node, "number");

        if (nodeId is null || number is null)
        {
            return null;
        }

        var title = ReadBounded(node, "title", MaxTextFieldLength) ?? string.Empty;
        var state = ReadBounded(node, "state", 32) ?? "unknown";

        return new GitRelation(
            nodeId,
            "issue",
            number.Value,
            title,
            state,
            Merged: null,
            ReadBounded(node, "url", MaxUrlLength),
            BaseRef: null,
            HeadRef: null,
            sha,
            FromPullNumber: parentPullNumber);
    }

    private static string? ReadBounded(JsonElement element, string property, int maxLength)
    {
        if (maxLength <= 0 || !element.TryGetProperty(property, out var node))
        {
            return null;
        }

        if (node.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        var value = node.GetString();

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var node) || node.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return node.TryGetInt32(out var value) ? value : null;
    }

    /// <summary>
    /// The delay GitHub asked for, from either header it uses, bounded to what a caller can
    /// clamp. A value this cannot read is <c>null</c>, which sends the caller to its own backoff
    /// rather than to a number a response body chose.
    /// </summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        foreach (var header in new[] { "Retry-After", "X-RateLimit-Reset" })
        {
            if (!response.Headers.TryGetValues(header, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (header == "Retry-After" &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds > 0)
                {
                    return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
                }

                if (header == "X-RateLimit-Reset" &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) &&
                    epoch > 0)
                {
                    var reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
                    var delay = reset - DateTimeOffset.UtcNow;

                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }
        }

        return null;
    }
}
