using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chronicleaf.GitHub.Evidence;

/// <summary>
/// The read-only GitHub client (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport is injected, and this class has no idea how it is built.</b> It takes an
/// <see cref="HttpClient"/> and a <see cref="TimeProvider"/>; the composition root decides the base
/// address, the timeout and the redirect behaviour. That is what lets a test supply a fake handler
/// and see this class's real decisions - the classification of a status, the resolution of a ref,
/// the enforcement of a cap - without a socket, and what lets production pin the host and refuse to
/// follow a redirect without any of that logic living here.
/// </para>
/// <para>
/// <b>No write is possible from here.</b> Every call below is a GET. There is no method that takes a
/// body, and a repository this product can see is one it only reads.
/// </para>
/// <para>
/// <b>A redirect is never followed.</b> If GitHub - or anything between here and it - answers 3xx,
/// the response is reported as unavailable and no second request is made. An <c>Authorization</c>
/// header is attached only when a credential was leased for this call, and a redirect is the one
/// construct that could carry it to a host that does not deserve it, so the safe answer is to treat
/// any redirect as a failure and let a later attempt try again.
/// </para>
/// <para>
/// <b>The response body is untrusted input.</b> It is parsed with <see cref="JsonDocument"/> and
/// every value taken from it is bounded before it leaves this class: a message, a title and an
/// author are truncated, a patch is a slice of the byte budget, and anything that is not the JSON
/// this client understands is a failure rather than a fact. A payload that claims to be a
/// repository with a 40-megabyte description gets its description cut, and nothing it says is ever
/// treated as an instruction.
/// </para>
/// </remarks>
public sealed class GitHubEvidenceClient : IGitEvidenceSource
{
    /// <summary>The host this client is pinned to. Requests are built as relative paths.</summary>
    public const string Host = "api.github.com";

    /// <summary>The API version this client speaks, sent as a header rather than assumed.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The user agent GitHub requires, which is never a secret and never a token.</summary>
    public const string UserAgent = "Chronicleaf-Evidence/1.0";

    private const int MaxTextFieldLength = 400;

    private static readonly IReadOnlyList<GitIssue> NoIssueList = [];
    private static readonly IReadOnlyList<GitPullRequest> NoPullRequestList = [];
    private static readonly IReadOnlyList<GitUnavailable> NoUnavailableList = [];

    private readonly HttpClient http;
    private readonly TimeProvider clock;

    public GitHubEvidenceClient(HttpClient http, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        this.http = http;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<GitEvidenceResult> DescribeRepositoryAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return DescribeAsync(repository, credential, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GitEvidenceResult> FetchAsync(
        RepoRef repository,
        GitEvidenceRequest request,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        var fetchedAt = clock.GetUtcNow();
        var endpoints = new List<GitEndpoint>();
        var exclusions = new List<GitExclusion>();
        var truncations = new List<GitTruncation>();

        // The repository metadata is read first because it carries GitHub's own id and default
        // branch. A repository that is private answers 404 here, before any ref is resolved, so a
        // private repository is never probed further than the single request that reveals it.
        var described = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}",
            endpoints,
            cancellationToken).ConfigureAwait(false);

        if (described.Value is not { } root)
        {
            return described.Fallback(fetchedAt);
        }

        var repositoryId = ReadLong(root, "id");
        var defaultBranch = ReadString(root, "default_branch", MaxTextFieldLength);
        var isPrivate = root.TryGetProperty("private", out var privateFlag) &&
                        privateFlag.ValueKind is JsonValueKind.True;

        if (isPrivate)
        {
            // A repository that reports itself private is reported the same way as one that is
            // absent, because the two must not be distinguishable from the outside.
            return GitEvidenceResult.RepositoryUnavailable(fetchedAt);
        }

        // Every ref is resolved to an immutable revision before a single fact is read. A ref that
        // moves during collection would otherwise produce a set of facts that never coexisted.
        string? baseSha = null;
        if (!string.IsNullOrWhiteSpace(request.BaseRef))
        {
            var resolvedBase = await ResolveAsync(
                repository,
                credential,
                request.BaseRef,
                endpoints,
                cancellationToken).ConfigureAwait(false);

            if (resolvedBase is null)
            {
                return GitEvidenceResult.Rejected(fetchedAt);
            }

            baseSha = resolvedBase;
        }

        var resolvedHead = await ResolveAsync(
            repository,
            credential,
            request.HeadRef,
            endpoints,
            cancellationToken).ConfigureAwait(false);

        if (resolvedHead is null)
        {
            return GitEvidenceResult.Rejected(fetchedAt);
        }

        var result = new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, fetchedAt)
        {
            RepositoryId = repositoryId,
            DefaultBranch = defaultBranch,
            ResolvedBaseSha = baseSha,
            ResolvedHeadSha = resolvedHead,
            Endpoints = endpoints,
        };

        if (baseSha is null)
        {
            // With no base there is no comparison, so the head commit itself is the fact.
            var head = await GetAsync(
                repository,
                credential,
                $"repos/{repository.ToPathSegment()}/commits/{Escape(resolvedHead)}",
                endpoints,
                cancellationToken).ConfigureAwait(false);

            if (head.Value is not { } commit)
            {
                // The ref resolved, so the repository is readable, but the commit itself could not
                // be read. That is a dependency failure, not a fact and not a refusal.
                return GitEvidenceResult.CouldNotBeRead(fetchedAt);
            }

            return With(
                result,
                commits: [ReadCommit(commit)],
                compare: null,
                exclusions: exclusions,
                truncations: truncations);
        }

        var compared = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/compare/{Escape(baseSha)}...{Escape(resolvedHead)}",
            endpoints,
            cancellationToken).ConfigureAwait(false);

        if (compared.Value is not { } comparison)
        {
            return GitEvidenceResult.CouldNotBeRead(fetchedAt);
        }

        var commits = new List<GitCommit>();
        if (comparison.TryGetProperty("commits", out var commitArray) &&
            commitArray.ValueKind is JsonValueKind.Array)
        {
            foreach (var entry in commitArray.EnumerateArray())
            {
                if (commits.Count >= request.MaxCommits)
                {
                    truncations.Add(new GitTruncation(GitSegments.Commits, commits.Count, request.MaxCommits));
                    break;
                }

                commits.Add(ReadCommit(entry));
            }
        }

        var files = ReadFiles(comparison, request, exclusions, truncations);

        // The three optional reads happen after the revisions are resolved, and each one that
        // cannot be read is recorded as missing rather than quietly left out. A comparison that
        // silently dropped the issue the owner asked about would claim to be a fuller story than
        // it is.
        var issues = new List<GitIssue>();
        var pullRequests = new List<GitPullRequest>();
        GitRelease? releaseFact = null;
        var unavailable = new List<GitUnavailable>();

        if (request.IssueNumber is { } issueNumber)
        {
            var issue = await GetAsync(
                repository,
                credential,
                $"repos/{repository.ToPathSegment()}/issues/{issueNumber}",
                endpoints,
                cancellationToken).ConfigureAwait(false);

            if (issue.Value is { } issueNode)
            {
                issues.Add(ReadIssue(issueNode, issueNumber));
            }
            else
            {
                unavailable.Add(
                    new GitUnavailable($"issue:{issueNumber}", ReasonOf(issue.Fallback(fetchedAt))));
            }
        }

        if (request.PullRequestNumber is { } pullRequestNumber)
        {
            var pull = await GetAsync(
                repository,
                credential,
                $"repos/{repository.ToPathSegment()}/pulls/{pullRequestNumber}",
                endpoints,
                cancellationToken).ConfigureAwait(false);

            if (pull.Value is { } pullNode)
            {
                pullRequests.Add(ReadPullRequest(pullNode, pullRequestNumber));
            }
            else
            {
                unavailable.Add(
                    new GitUnavailable($"pr:{pullRequestNumber}", ReasonOf(pull.Fallback(fetchedAt))));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ReleaseTag))
        {
            var tag = request.ReleaseTag;
            var release = await GetAsync(
                repository,
                credential,
                $"repos/{repository.ToPathSegment()}/releases/tags/{Escape(tag)}",
                endpoints,
                cancellationToken).ConfigureAwait(false);

            if (release.Value is { } releaseNode)
            {
                releaseFact = ReadRelease(releaseNode, tag);
            }
            else
            {
                unavailable.Add(new GitUnavailable($"release:{tag}", ReasonOf(release.Fallback(fetchedAt))));
            }
        }

        return With(
            result,
            commits: commits,
            compare: new GitCompare(ReadString(comparison, "status", 32) ?? "unknown", commits.Count, files),
            issues: issues,
            pullRequests: pullRequests,
            release: releaseFact,
            exclusions: exclusions,
            truncations: truncations,
            unavailable: unavailable);
    }

    private static GitEvidenceResult With(
        GitEvidenceResult result,
        IReadOnlyList<GitCommit> commits,
        GitCompare? compare,
        IReadOnlyList<GitExclusion> exclusions,
        IReadOnlyList<GitTruncation> truncations,
        IReadOnlyList<GitIssue>? issues = null,
        IReadOnlyList<GitPullRequest>? pullRequests = null,
        GitRelease? release = null,
        IReadOnlyList<GitUnavailable>? unavailable = null) =>
        new GitEvidenceResult(
            GitEvidenceOutcome.Collected,
            result.Repository,
            result.FetchedAt,
            retryAfter: null)
        {
            RepositoryId = result.RepositoryId,
            DefaultBranch = result.DefaultBranch,
            ResolvedBaseSha = result.ResolvedBaseSha,
            ResolvedHeadSha = result.ResolvedHeadSha,
            Commits = commits,
            Compare = compare,
            Issues = issues ?? NoIssueList,
            PullRequests = pullRequests ?? NoPullRequestList,
            Release = release,
            Unavailable = unavailable ?? NoUnavailableList,
            Endpoints = result.Endpoints,
            Exclusions = exclusions,
            Truncations = truncations,
        };

    private async Task<GitEvidenceResult> DescribeAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken)
    {
        var fetchedAt = clock.GetUtcNow();
        var endpoints = new List<GitEndpoint>();
        var described = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}",
            endpoints,
            cancellationToken).ConfigureAwait(false);

        if (described.Value is not { } root)
        {
            return described.Fallback(fetchedAt);
        }

        var isPrivate = root.TryGetProperty("private", out var flag) && flag.ValueKind is JsonValueKind.True;
        if (isPrivate)
        {
            return GitEvidenceResult.RepositoryUnavailable(fetchedAt);
        }

        return new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, fetchedAt)
        {
            RepositoryId = ReadLong(root, "id"),
            DefaultBranch = ReadString(root, "default_branch", MaxTextFieldLength),
            Endpoints = endpoints,
        };
    }

    /// <summary>
    /// Resolves a ref to the commit it names. A ref that GitHub does not know is a refusal, not a
    /// guess: the client never falls back to a branch name or a default branch.
    /// </summary>
    private async Task<string?> ResolveAsync(
        RepoRef repository,
        string? credential,
        string reference,
        List<GitEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        var response = await GetAsync(
            repository,
            credential,
            $"repos/{repository.ToPathSegment()}/commits/{Escape(reference)}",
            endpoints,
            cancellationToken).ConfigureAwait(false);

        if (response.Value is not { } commit)
        {
            return null;
        }

        return ReadString(commit, "sha", 64);
    }

    private async Task<Answer> GetAsync(
        RepoRef repository,
        string? credential,
        string path,
        List<GitEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/{path}");

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

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
            // The host is unreachable, the connection was reset, or the attempt timed out. None of
            // that is a fact about the repository, so none of it is recorded as one.
            endpoints.Add(new GitEndpoint("GET", path, 0));
            return Answer.Transport;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout is the transport's own deadline, not the caller's cancellation, so it is
            // reported the same way as a refused connection.
            endpoints.Add(new GitEndpoint("GET", path, 0));
            return Answer.Transport;
        }
        finally
        {
            request.Dispose();
        }

        using (response)
        {
            endpoints.Add(new GitEndpoint("GET", path, (int)response.StatusCode));

            if (response.StatusCode is HttpStatusCode.OK)
            {
                try
                {
                    await using var stream = await response.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var document = await JsonDocument
                        .ParseAsync(stream, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    // The element is cloned before the document is disposed. A JsonElement does not own
                    // the document it came from, so handing one out of a using-block would return a
                    // reference to something already freed - and the failure would surface a long way
                    // from here, as a disposed object rather than as a bad response.
                    return new Answer(document.RootElement.Clone(), response);
                }
                catch (JsonException)
                {
                    // A body that is not the JSON this client understands is a failure, never a fact.
                    return Answer.Unusable;
                }
            }

            return new Answer(null, response);
        }
    }

    private IReadOnlyList<GitFile> ReadFiles(
        JsonElement comparison,
        GitEvidenceRequest request,
        List<GitExclusion> exclusions,
        List<GitTruncation> truncations)
    {
        var files = new List<GitFile>();
        var budget = request.MaxDiffBytes;

        if (!comparison.TryGetProperty("files", out var fileArray) ||
            fileArray.ValueKind is not JsonValueKind.Array)
        {
            return files;
        }

        foreach (var entry in fileArray.EnumerateArray())
        {
            // The cap is checked before anything is recorded, including an exclusion, so the number of
            // files this collection reports is never above the number it promised. Checking it after an
            // exclusion is appended would let one more file than the cap be reported, which is exactly
            // the kind of off-by-one a reader would have no way to notice.
            if (files.Count >= request.MaxChangedFiles)
            {
                truncations.Add(new GitTruncation(GitSegments.Files, files.Count, request.MaxChangedFiles));
                break;
            }

            var path = ReadString(entry, "filename", 400) ?? "(unnamed)";
            var status = ReadString(entry, "status", 32) ?? "modified";
            var additions = ReadInt(entry, "additions") ?? 0;
            var deletions = ReadInt(entry, "deletions") ?? 0;

            // A file whose name says it holds a secret is never read, whatever else is true of it.
            // The exclusion is recorded as a fact about the exclusion; the content is not.
            if (SecretFilenames.IsExcluded(path))
            {
                exclusions.Add(new GitExclusion(path, SecretFilenames.Reason));
                files.Add(new GitFile(path, status, additions, deletions, null, PatchMissing: true, SecretFilenames.Reason));
                continue;
            }

            var patch = ReadString(entry, "patch", int.MaxValue);

            if (patch is null)
            {
                // GitHub returns no patch for a binary file and for one past its own limit. That is
                // recorded as missing evidence, never as a file that did not change.
                files.Add(new GitFile(path, status, additions, deletions, null, PatchMissing: true, null));
                continue;
            }

            var patchBytes = Encoding.UTF8.GetByteCount(patch);

            if (patchBytes > budget)
            {
                // The diff text cap is a promise about how much becomes evidence, so what did not
                // fit is marked rather than trimmed: a truncated patch would read as a whole one.
                files.Add(new GitFile(path, status, additions, deletions, null, PatchMissing: true, GitSegments.DiffText));
                truncations.Add(new GitTruncation(GitSegments.DiffText, budget, request.MaxDiffBytes));
                continue;
            }

            budget -= patchBytes;
            files.Add(new GitFile(path, status, additions, deletions, patch, PatchMissing: false, null));
        }

        return files;
    }

    private static GitIssue ReadIssue(JsonElement issue, int requested)
    {
        var number = ReadInt(issue, "number") ?? requested;
        var title = ReadString(issue, "title", MaxTextFieldLength) ?? string.Empty;
        var state = ReadString(issue, "state", 32) ?? "unknown";
        var reference = ReadString(issue, "html_url", 200);

        return new GitIssue(number, title, state, reference);
    }

    private static GitPullRequest ReadPullRequest(JsonElement pull, int requested)
    {
        var number = ReadInt(pull, "number") ?? requested;
        var title = ReadString(pull, "title", MaxTextFieldLength) ?? string.Empty;
        var state = ReadString(pull, "state", 32) ?? "unknown";
        var merged = pull.TryGetProperty("merged", out var flag) && flag.ValueKind is JsonValueKind.True;
        var reference = ReadString(pull, "html_url", 200);

        return new GitPullRequest(number, title, state, merged, reference);
    }

    private static GitRelease ReadRelease(JsonElement release, string requestedTag)
    {
        var tag = ReadString(release, "tag_name", 200) ?? requestedTag;
        var name = ReadString(release, "name", MaxTextFieldLength) ?? tag;
        var draft = release.TryGetProperty("draft", out var draftFlag) && draftFlag.ValueKind is JsonValueKind.True;
        var preRelease = release.TryGetProperty("prerelease", out var preFlag) && preFlag.ValueKind is JsonValueKind.True;
        var publishedAt = ReadTimestamp(release, "published_at");

        return new GitRelease(tag, name, draft, preRelease, publishedAt);
    }

    /// <summary>
    /// The fixed vocabulary an absent part is recorded in. It is a small closed set on purpose: the
    /// reason an issue could not be read is never a sentence copied out of a response body.
    /// </summary>
    private static string ReasonOf(GitEvidenceResult result) => result.Outcome switch
    {
        GitEvidenceOutcome.RepositoryUnavailable => GitUnavailableReasons.NotFound,
        GitEvidenceOutcome.CredentialRejected => GitUnavailableReasons.CredentialRejected,
        GitEvidenceOutcome.RateLimited => GitUnavailableReasons.RateLimited,
        GitEvidenceOutcome.Rejected => GitUnavailableReasons.Rejected,
        _ => GitUnavailableReasons.Unreachable,
    };

    private static GitCommit ReadCommit(JsonElement commit)    {
        var sha = ReadString(commit, "sha", 64) ?? string.Empty;
        var message = commit.TryGetProperty("commit", out var inner) ? inner : commit;
        var summary = ReadString(message, "message", MaxTextFieldLength) ?? string.Empty;
        var author = ReadString(message, "author", 0);

        if (author is null && message.TryGetProperty("author", out var authorNode))
        {
            author = ReadString(authorNode, "name", MaxTextFieldLength);
        }

        var authoredAt = ReadTimestamp(message, "date");

        return new GitCommit(sha, Bounded(Summary(summary)), author, authoredAt);
    }

    private static string Summary(string message)
    {
        var newline = message.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = newline < 0 ? message : message[..newline];

        return firstLine.Trim();
    }

    private static string Bounded(string value) =>
        value.Length <= MaxTextFieldLength ? value : value[..MaxTextFieldLength];

    private static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>
    /// Reads one string property, bounded by <paramref name="maxLength"/>.
    /// </summary>
    /// <remarks>
    /// The bound is the caller's, not a constant, and that matters most for a patch: a diff is bounded by
    /// the diff-text budget it was asked for, while a message or a title is bounded by what a title may
    /// be. Applying one bound to both would silently clip every patch to a title's length and the
    /// diff-text cap would then never be the thing that stops a collection.
    /// </remarks>
    private static string? ReadString(JsonElement element, string property, int maxLength)
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
    private readonly struct Answer
    {
        private readonly HttpResponseMessage? message;

        public Answer(JsonElement? body, HttpResponseMessage? response)
        {
            Value = body;
            message = response;
        }

        /// <summary>The parsed body, or <c>null</c> when the request produced no readable answer.</summary>
        public JsonElement? Value { get; }

        /// <summary>The request never reached an answer: no connection, a reset or a timeout.</summary>
        public static Answer Transport { get; } = new(null, null);

        /// <summary>GitHub answered 200 with a body this client cannot read as JSON.</summary>
        public static Answer Unusable { get; } = new(null, null);

        /// <summary>Turns a non-answer into the result it means, with the provider's own delay.</summary>
        public GitEvidenceResult Fallback(DateTimeOffset fetchedAt) =>
            message is null
                ? GitEvidenceResult.CouldNotBeRead(fetchedAt)
                : Classify(message, fetchedAt);

        private static GitEvidenceResult Classify(HttpResponseMessage response, DateTimeOffset fetchedAt) =>
            response.StatusCode switch
            {
                HttpStatusCode.NotFound => GitEvidenceResult.RepositoryUnavailable(fetchedAt),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GitEvidenceResult.CredentialRejected(fetchedAt),
                HttpStatusCode.TooManyRequests => GitEvidenceResult.RateLimited(fetchedAt, RetryAfterOf(response)),
                // A redirect is not an answer, and following one is the one way an Authorization
                // header could leave the host it was issued for. There is no second request here.
                HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect =>
                    GitEvidenceResult.CouldNotBeRead(fetchedAt),
                (HttpStatusCode)422 => GitEvidenceResult.Rejected(fetchedAt),
                _ when (int)response.StatusCode is >= 500 => GitEvidenceResult.CouldNotBeRead(fetchedAt),
                _ when (int)response.StatusCode is >= 400 => GitEvidenceResult.Rejected(fetchedAt),
                _ => GitEvidenceResult.CouldNotBeRead(fetchedAt),
            };

        /// <summary>
        /// The delay GitHub asked for, from either header it uses, bounded to what a caller can
        /// clamp. A value this cannot read is <c>null</c>, which sends the caller to its own backoff
        /// rather than to a number a response body chose.
        /// </summary>
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

            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets))
            {
                foreach (var value in resets)
                {
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) &&
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
}

/// <summary>The names this package uses for the parts of a collection that can stop early.</summary>
public static class GitSegments
{
    /// <summary>The commit list.</summary>
    public const string Commits = "commits";

    /// <summary>The changed-file list.</summary>
    public const string Files = "files";

    /// <summary>The diff text budget.</summary>
    public const string DiffText = "diff-text";
}

/// <summary>
/// The file names this package never reads, and the single reason it records for each
/// (GITHUB-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// A repository's <c>.env</c> and its private keys are not evidence. Copying one into a bundle
/// would move a secret into a table that is exported, summarised and eventually shown to a reader,
/// so the content of a file whose name says what it holds is never read at all - not truncated, not
/// hashed, not stored. What is recorded is that a file was excluded and why, which is a fact about
/// the collection rather than about the secret.
/// </para>
/// <para>
/// The list is by name, never by content, because content is only visible after it has been read.
/// It is therefore a floor rather than a guarantee: a secret committed under an ordinary filename
/// is not caught here, which is why the diff text cap and the owner's own review remain.
/// </para>
/// </remarks>
public static class SecretFilenames
{
    /// <summary>The one reason recorded for every exclusion. There is no per-file vocabulary.</summary>
    public const string Reason = "secret-or-private-key-filename";

    private static readonly string[] ExactNames =
    [
        ".env",
        ".netrc",
        "_netrc",
        ".npmrc",
        ".pypirc",
        ".git-credentials",
        "credentials",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
    ];

    private static readonly string[] Prefixes =
    [
        ".env.",
        "secrets.",
        "secret.",
    ];

    private static readonly string[] Suffixes =
    [
        ".pem",
        ".key",
        ".p12",
        ".pfx",
        ".keystore",
        ".jks",
        ".asc",
        ".gpg",
    ];

    /// <summary>True when a path names a file this package refuses to read.</summary>
    public static bool IsExcluded(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalised = path.Replace('\\', '/');
        var leaf = normalised[(normalised.LastIndexOf('/') + 1)..];

        // A key file nested under a directory is caught by its own name as well as by the
        // directory, so `.ssh/id_rsa` and `id_rsa` are the same answer.
        if (leaf.Equals(".ssh", StringComparison.OrdinalIgnoreCase) ||
            leaf.StartsWith(".ssh/", StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals(".gnupg", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var name in ExactNames)
        {
            if (leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in Prefixes)
        {
            if (leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var suffix in Suffixes)
        {
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
