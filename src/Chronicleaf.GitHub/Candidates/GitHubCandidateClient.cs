using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

// The pinned host, the API version and the user agent are the delivered evidence transport's own
// constants rather than copies of them, and the repository reference is its type too. This package adds
// a second read over the same host on the same connection pool; it does not add a second host.
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.GitHub.Candidates;

/// <summary>
/// The read-only tag and release client (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is injected, and this class has no idea how it is built.</b> It takes the same
/// <see cref="HttpClient"/> the delivered evidence client takes, so a poll and a collection share one
/// connection pool and one pinned host and one set of handler settings - and a test that replaced the
/// handler replaced it for both, which is what makes a scenario able to assert the whole repository
/// read rather than one call in isolation.
/// </para>
/// <para>
/// <b>No write is possible from here.</b> Every call is a GET, and this class has no method that takes
/// a body. The delivered evidence client made the same promise about a different surface, and this one
/// keeps it: a repository this product can see is one it only reads.
/// </para>
/// <para>
/// <b>A redirect is never followed, and a response body is untrusted input.</b> A 3xx is reported as
/// unavailable and no second request is made, because a redirect is the one construct that could carry
/// an <c>Authorization</c> header to a host that does not deserve it. Every value taken from a body is
/// bounded before it leaves this class, and a body that is not the JSON this client understands is a
/// failure rather than a fact.
/// </para>
/// </remarks>
public sealed class GitHubCandidateClient : IGitHubCandidateSource
{
    private const int MaxTextFieldLength = 400;
    private const int MaxTagNameLength = 200;

    private readonly HttpClient http;
    private readonly TimeProvider clock;

    public GitHubCandidateClient(HttpClient http, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        this.http = http;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GitHubCandidatePoll> PollAsync(
        RepoRef repository,
        GitHubCandidateRequest request,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        var fetchedAt = clock.GetUtcNow();
        var releases = new List<GitHubRelease>();
        var tags = new List<GitHubTag>();
        var poll = new GitHubCandidatePoll(
            GitHubCandidateOutcome.Collected,
            repository.ToString(),
            request.Ref,
            fetchedAt);

        // The releases are read first and on their own resource, because a release and a tag are two
        // different things somebody published and one endpoint cannot answer for both. A failure here is
        // not a failure of the whole poll: a repository with releases disabled still has tags, and a
        // poll that refused both would say nothing about either.
        var releasePath =
            $"repos/{repository.ToPathSegment()}/releases?per_page={request.MaxReleases.ToString(CultureInfo.InvariantCulture)}";

        var releaseAnswer = await GetAsync(repository, credential, releasePath, cancellationToken)
            .ConfigureAwait(false);

        // Success is asked of the answer itself rather than of the refusal classifier. A 2xx with a
        // parsed body is not a refusal, and the classifier below only ever sees the ones that are - so
        // a successful read can never be classified by a table that has no arm for success.
        if (!releaseAnswer.IsCollected)
        {
            return releaseAnswer.Fallback(fetchedAt);
        }

        foreach (var entry in Enumerate(releaseAnswer.Value!.Value))
        {
            if (releases.Count >= request.MaxReleases)
            {
                break;
            }

            // A draft release is not a release: it is an unfinished thing the owner of the repository
            // has not published. Proposing a Milestone from it would propose something nobody else can
            // see, so it is not read as a candidate.
            if (entry.TryGetProperty("draft", out var draft) && draft.ValueKind is JsonValueKind.True)
            {
                continue;
            }

            var id = ReadLong(entry, "id");
            if (id is null)
            {
                // A release with no identity cannot be keyed, and a key is what makes a candidate
                // recordable exactly once. Skipping it is recorded by the count, not by a substitute.
                continue;
            }

            var tag = ReadString(entry, "tag_name", MaxTagNameLength) ?? string.Empty;
            var preRelease = entry.TryGetProperty("prerelease", out var pre) && pre.ValueKind is JsonValueKind.True;

            releases.Add(new GitHubRelease(
                id.Value,
                tag,
                ReadString(entry, "name", MaxTextFieldLength) ?? tag,
                ReadString(entry, "target_commitish", 64),
                preRelease,
                ReadTimestamp(entry, "published_at")));
        }

        var tagPath =
            $"repos/{repository.ToPathSegment()}/tags?per_page={request.MaxTags.ToString(CultureInfo.InvariantCulture)}";

        var tagAnswer = await GetAsync(repository, credential, tagPath, cancellationToken).ConfigureAwait(false);
        if (!tagAnswer.IsCollected)
        {
            // The releases were read and are still real, so the poll reports them rather than throwing
            // away a fact it has. A caller that needs to know one half failed can compare the counts it
            // asked for with the counts it got.
            return poll with { Releases = releases, Tags = [] };
        }

        foreach (var entry in Enumerate(tagAnswer.Value!.Value))
        {
            if (tags.Count >= request.MaxTags)
            {
                break;
            }

            var name = ReadString(entry, "name", MaxTagNameLength);
            if (name is null)
            {
                continue;
            }

            string? sha = null;
            if (request.ResolveTagShas)
            {
                // A tag listing carries the name and the commit's url but not the commit, so the sha is
                // resolved rather than read from a field that is not there. `object.sha` is the commit
                // the tag points at when the tag is lightweight and the tag object when it is annotated,
                // which is exactly the two cases this product can tell apart and does not guess between.
                sha = ReadNestedString(entry, "commit", "sha", 64);

                if (sha is null || sha.Length == 0)
                {
                    sha = null;
                }
            }

            tags.Add(new GitHubTag(name, sha));
        }

        return poll with { Releases = releases, Tags = tags };
    }

    /// <inheritdoc />
    public async Task<string?> ResolveRefAsync(
        RepoRef repository,
        string reference,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var answer = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/commits/{Uri.EscapeDataString(reference.Trim())}",
            cancellationToken).ConfigureAwait(false);

        if (!answer.IsCollected)
        {
            // A refusal here is not thrown and not logged with its code: a ref that does not resolve is
            // a fact about the repository, and the caller's honest answer to it is "no candidate".
            return null;
        }

        var sha = ReadString(answer.Value!.Value, "sha", 64);

        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement value) =>
        value.ValueKind is JsonValueKind.Array ? value.EnumerateArray() : [];

    private async Task<Answer> GetAsync(
        RepoRef repository,
        string? credential,
        string path,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{GitHubEvidenceClient.Host}/{path}");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubEvidenceClient.ApiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", GitHubEvidenceClient.UserAgent);

        if (!string.IsNullOrEmpty(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }

        HttpResponseMessage response;
        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return Answer.Transport;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Answer.Transport;
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return new Answer(null, response);
            }

            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                return new Answer(document.RootElement.Clone(), response);
            }
            catch (JsonException)
            {
                return Answer.Unusable;
            }
        }
    }

    private static string? ReadString(JsonElement element, string property, int maxLength)
    {
        if (maxLength <= 0 || !element.TryGetProperty(property, out var node) ||
            node.ValueKind is not JsonValueKind.String)
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

    private static string? ReadNestedString(
        JsonElement element,
        string parent,
        string property,
        int maxLength)
    {
        if (!element.TryGetProperty(parent, out var container) ||
            container.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return ReadString(container, property, maxLength);
    }

    private static long? ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var node) || node.ValueKind is not JsonValueKind.Number)
        {
            return null;
        }

        return node.TryGetInt64(out var value) ? value : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        var raw = ReadString(element, property, 64);

        return raw is not null && DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>One answered request: either a parsed body or the classification of the refusal.</summary>
    private readonly struct Answer(JsonElement? body, HttpResponseMessage? message)
    {
        public JsonElement? Value { get; } = body;

        /// <summary>True when this request succeeded and its body is the JSON this client understands.</summary>
        /// <remarks>
        /// <b>Success is a property of the answer, not a row in the refusal table.</b> A 2xx whose body
        /// could be parsed is a read; everything else is a refusal for
        /// <see cref="Fallback"/> to name. Deriving success here rather than by asking the classifier is
        /// what makes it impossible for a successful read to be reported as a failure, which is exactly
        /// what happened when this client asked a switch whose last arm was a catch-all.
        /// </remarks>
        public bool IsCollected => Value is not null && message is { IsSuccessStatusCode: true };

        /// <summary>A request that never reached a status line: a transport failure or a timeout.</summary>
        public static Answer Transport { get; } = new(null, null);

        /// <summary>
        /// A 2xx whose body is not the JSON this client understands.
        /// </summary>
        /// <remarks>
        /// Deliberately the same answer as <see cref="Transport"/>: both are "GitHub could not be read
        /// right now" to a caller, both are worth another attempt under the delivered backoff, and
        /// telling them apart would give a caller a distinction it cannot act on. A body that is not the
        /// documented shape is a dependency failure rather than a fact about the repository.
        /// </remarks>
        public static Answer Unusable { get; } = new(null, null);

        /// <summary>Turns a non-answer into the poll it means, with the provider's own delay.</summary>
        public GitHubCandidatePoll Fallback(DateTimeOffset fetchedAt) =>
            message is null
                ? GitHubCandidatePoll.Unavailable(fetchedAt)
                : Classify(message, fetchedAt);

        /// <summary>
        /// Names the refusal this status means. Only ever reached for a non-success answer, so there is
        /// deliberately no arm for a 2xx: a success that reached the catch-all below would be reported as
        /// unavailable, and a poll that read a repository perfectly would be recorded as a failed job.
        /// </summary>
        private static GitHubCandidatePoll Classify(HttpResponseMessage response, DateTimeOffset fetchedAt) =>
            response.StatusCode switch
            {
                HttpStatusCode.NotFound => GitHubCandidatePoll.RepositoryUnavailable(fetchedAt),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    GitHubCandidatePoll.CredentialRejected(fetchedAt),
                HttpStatusCode.TooManyRequests =>
                    GitHubCandidatePoll.RateLimited(fetchedAt, RetryAfterOf(response)),
                HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect =>
                    GitHubCandidatePoll.Unavailable(fetchedAt),
                (HttpStatusCode)422 => GitHubCandidatePoll.Rejected(fetchedAt),
                _ when (int)response.StatusCode is >= 500 => GitHubCandidatePoll.Unavailable(fetchedAt),
                _ when (int)response.StatusCode is >= 400 => GitHubCandidatePoll.Rejected(fetchedAt),
                _ => GitHubCandidatePoll.Unavailable(fetchedAt),
            };

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Retry-After", out var values))
            {
                foreach (var value in values)
                {
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                        seconds > 0)
                    {
                        return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
                    }
                }
            }

            return null;
        }
    }
}
