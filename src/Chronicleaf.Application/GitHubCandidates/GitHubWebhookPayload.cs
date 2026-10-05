using System.Globalization;
using System.Text.Json;
using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// One thing a delivery's payload says happened, before this package has decided what it is called
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// This is deliberately not a candidate. It is the payload's own claim - an event name, an object id, a
/// stable revision and a human label - and it becomes a candidate only after the package has resolved
/// whatever it still needs and built a source key from the provider's own values. Keeping the two apart
/// is what lets a payload that names nothing usable produce an ignored receipt rather than a row with
/// empty fields in it.
/// </remarks>
/// <param name="Kind">Which kind of thing this claims to be, from the closed candidate vocabulary.</param>
/// <param name="ObjectId">
/// The provider's own identity for the object: a release id, a tag name, an issue or pull request id,
/// or a repository id for a threshold. It becomes part of the source key.
/// </param>
/// <param name="Revision">
/// The immutable commit or the stable revision the payload named, or <c>null</c> when the payload named
/// none and the package has to resolve it.
/// </param>
/// <param name="Title">The bounded label a human would read.</param>
/// <param name="OccurredAt">When the payload says the thing happened.</param>
public sealed record CandidateProposal(
    string Kind,
    string ObjectId,
    string? Revision,
    string Title,
    DateTimeOffset OccurredAt);

/// <summary>
/// Reads one signed delivery's body and says what it claims (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is data, never an instruction.</b> Every value taken out of it is a bounded string or a
/// number that becomes part of a source key or a label. Nothing a payload says is executed, nothing is
/// interpreted, and nothing reaches a model - this package has no call to one and no path to one. A
/// release named <c>ignore previous instructions</c> is stored as those eleven words and nothing else
/// does with them.
/// </para>
/// <para>
/// <b>An empty proposal list is an answer, not a failure.</b> A verified event whose action this build
/// does not act on, and a verified event whose payload names nothing usable, both produce zero
/// proposals; the receipt records why from the closed reason vocabulary. Nothing is invented to fill the
/// gap and no partial row is written, because a candidate with half its identity in it is a candidate
/// nobody can deduplicate.
/// </para>
/// </remarks>
public static class GitHubWebhookPayload
{
    /// <summary>
    /// The proposals one payload makes, or the reason it makes none.
    /// </summary>
    /// <param name="Proposals">What the payload claims, possibly empty.</param>
    /// <param name="IgnoredReason">Why there is nothing here, when there is nothing.</param>
    /// <param name="Action">
    /// The action this payload's own document carried, which the receipt stores so a reader of a receipt
    /// can see what the event was without the body. It is <c>null</c> for a reading made before the body
    /// was parsed - an event this build does not route at all - and for a payload that carried no action,
    /// which GitHub sends for some of the events here.
    /// </param>
    public sealed record Reading(IReadOnlyList<CandidateProposal> Proposals, string? IgnoredReason, string? Action = null)
    {
        private static readonly IReadOnlyList<CandidateProposal> None = [];

        /// <summary>A payload that named something this build can propose from.</summary>
        public static Reading Proposed(IReadOnlyList<CandidateProposal> proposals) => new(proposals, null);

        /// <summary>A payload that named nothing this build can propose from.</summary>
        public static Reading NothingNamed() => new(None, GitHubCandidateIgnoredReasons.NothingNamed);

        /// <summary>A payload this build does not route.</summary>
        public static Reading UnsupportedEvent() => new(None, GitHubCandidateIgnoredReasons.UnsupportedEvent);

        /// <summary>An event whose action this build does not act on.</summary>
        public static Reading UnsupportedAction() => new(None, GitHubCandidateIgnoredReasons.UnsupportedAction);

        /// <summary>A star count below the lowest declared threshold.</summary>
        public static Reading BelowThreshold() => new(None, GitHubCandidateIgnoredReasons.BelowThreshold);
    }

    /// <summary>
    /// Reads a delivery body for the proposals it makes.
    /// </summary>
    /// <param name="body">
    /// The raw bytes, parsed here and never earlier: the signature is checked over these same bytes. This
    /// is the only place the document is parsed, because the payload's <c>action</c> is read from the same
    /// parse - a second parse of the same bytes would be a second chance for the two to disagree.
    /// </param>
    /// <param name="eventName">The event name from the delivery's header.</param>
    /// <param name="receivedAt">The clock, used when the payload names no instant of its own.</param>
    /// <exception cref="GitHubCandidateDeliveryMalformedException">
    /// The body is not a JSON object, or carries an action this build cannot interpret.
    /// </exception>
    /// <remarks>
    /// <b>Malformed is decided before anything is routed.</b> A signed body that is not a JSON object is a
    /// document this build cannot read at all, and answering <c>unsupported-action</c> for it would be
    /// blame on the event name: the sender did sign a release event, and this server simply cannot see
    /// what was inside it. Deciding readability first is what makes the 422 mean "we could not read
    /// this" and the 202 mean "we read it and chose not to act on it", which is the distinction a sender
    /// needs in order to know which of its two problems to fix.
    /// </remarks>
    public static Reading Read(ReadOnlyMemory<byte> body, string eventName, DateTimeOffset receivedAt)
    {
        JsonDocument document;
        JsonElement root;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // A signed body that is not JSON is still authenticated, so this is a 422 and not a 401:
            // the sender proved who it was and sent something this build cannot read.
            throw new GitHubCandidateDeliveryMalformedException(
                "That delivery is signed but its body is not JSON.");
        }

        using (document)
        {
            root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw new GitHubCandidateDeliveryMalformedException(
                    "That delivery is signed but its body is not a JSON object.");
            }

            // The action is read out of this one document and the routing table is consulted with it,
            // here, in one place. A per-kind reader deciding for itself whether to act is how an opened
            // issue or a star below the first threshold ends up recorded as a candidate: the reader read
            // the payload and never asked the table. One decision, made once, on values from one parse.
            var action = Action(root);
            var bounded = GitHubCandidatePolicy.Bounded(action, GitHubCandidatePolicy.ActionNameMaxLength);

            if (!GitHubCandidatePolicy.IsRoutedEvent(eventName))
            {
                return Reading.UnsupportedEvent() with { Action = bounded };
            }

            if (!GitHubCandidatePolicy.IsRoutedAction(eventName, bounded))
            {
                return Reading.UnsupportedAction() with { Action = bounded };
            }

            return eventName switch
            {
                "release" => Release(root, receivedAt),
                "create" => Create(root, receivedAt),
                "issues" => Issue(root, receivedAt),
                "pull_request" => PullRequest(root, receivedAt),
                "star" => Star(root, receivedAt),
                _ => Reading.UnsupportedEvent() with { Action = bounded },
            } with
            {
                // The action travels on the reading so the receipt can store what the event was without
                // the body ever being stored at all.
                Action = bounded,
            };
        }
    }

    /// <summary>
    /// The action this payload's own document carries, or <c>null</c> when it carries none.
    /// </summary>
    /// <remarks>
    /// Read from the already-parsed root rather than from a second parse of the bytes. A body's
    /// <c>action</c> and its contents must be one reading of one document: two parses would be two
    /// chances for the action that decides routing to come from a different document than the one the
    /// candidates are read from.
    /// </remarks>
    public static string? Action(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object ||
            !root.TryGetProperty("action", out var node) ||
            node.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return node.GetString();
    }

    private static Reading Release(JsonElement root, DateTimeOffset receivedAt)
    {
        // The action has already been routed by the caller; this reader only decides what the payload
        // names. An earlier spelling asked the routing question again here, and a reader that had to
        // remember to ask was how a routed event reached a proposal on an action nobody routes.
        if (!root.TryGetProperty("release", out var release) || release.ValueKind is not JsonValueKind.Object)
        {
            return Reading.NothingNamed();
        }

        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind is JsonValueKind.True)
        {
            // A draft release is not published, so there is nothing to propose. It is named as
            // unsupported rather than as a malformed delivery: the sender did nothing wrong.
            return Reading.UnsupportedAction();
        }

        if (!TryLong(release, "id", out var id))
        {
            return Reading.NothingNamed();
        }

        var tag = Text(release, "tag_name", GitHubCandidatePolicy.EventNameMaxLength * 4);
        var name = Text(release, "name", GitHubCandidatePolicy.TitleMaxLength);
        var published = Timestamp(release, "published_at") ?? receivedAt;

        return Reading.Proposed(
        [
            new CandidateProposal(
                GitHubCandidateKinds.Release,
                id.ToString(CultureInfo.InvariantCulture),
                Text(release, "target_commitish", 64),
                GitHubCandidatePolicy.Title(name, tag ?? id.ToString(CultureInfo.InvariantCulture)),
                published),
        ]);
    }

    private static Reading Create(JsonElement root, DateTimeOffset receivedAt)
    {
        // A create event carries its own ref type, and a branch is not a release anybody shipped. The
        // ref_type check belongs to the content of the payload rather than to the action word, which is
        // why it is made here and not in the routing table.
        var refType = Text(root, "ref_type", GitHubCandidatePolicy.EventNameMaxLength);

        if (refType is not null && !refType.Equals("tag", StringComparison.Ordinal))
        {
            return Reading.UnsupportedAction();
        }

        var reference = Text(root, "ref", GitHubCandidatePolicy.TitleMaxLength * 2);

        if (reference is null || reference.StartsWith("refs/tags/", StringComparison.Ordinal) is false)
        {
            // Either no ref, or one that is not a tag. A branch name that reached this point would
            // propose something nobody shipped.
            return reference is null ? Reading.NothingNamed() : Reading.UnsupportedAction();
        }

        var tagName = reference["refs/tags/".Length..];

        return Reading.Proposed(
        [
            new CandidateProposal(
                GitHubCandidateKinds.Tag,
                tagName,
                // A create payload names the ref and not the commit it points at, so the revision is
                // left for the caller to resolve. It is resolved before the key is built rather than
                // guessed from the name, because `tag:{name}:{sha}` is only a fact once the sha is one.
                null,
                GitHubCandidatePolicy.Title(tagName, tagName),
                Timestamp(root, "repository", "created_at") ?? receivedAt),
        ]);
    }

    private static Reading Issue(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryLong(root, "issue", "id", out var id) || !TryLong(root, "issue", "number", out var number))
        {
            return Reading.NothingNamed();
        }

        var title = Text(root, "issue", "title", GitHubCandidatePolicy.TitleMaxLength) ?? $"Issue #{number}";
        var closedAt = Timestamp(root, "issue", "closed_at") ?? receivedAt;

        return Reading.Proposed(
        [
            new CandidateProposal(
                GitHubCandidateKinds.Issue,
                id.ToString(CultureInfo.InvariantCulture),
                // The closed_at instant is the stable revision an issue has: an issue closes once, and
                // that instant is what makes two deliveries of the same close one candidate.
                closedAt.ToString("O", CultureInfo.InvariantCulture),
                GitHubCandidatePolicy.Title(title, title),
                closedAt),
        ]);
    }

    private static Reading PullRequest(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryLong(root, "pull_request", "id", out var id) || !TryLong(root, "pull_request", "number", out var number))
        {
            return Reading.NothingNamed();
        }

        // The merge is the fact. A pull request that was closed without merging is work that was
        // abandoned, and proposing it would be proposing a reversal as an achievement - so this is
        // refused on the payload's own claim rather than on an absence.
        if (!root.TryGetProperty("pull_request", out var pull) ||
            pull.ValueKind is not JsonValueKind.Object ||
            !pull.TryGetProperty("merged", out var merged) ||
            merged.ValueKind is not JsonValueKind.True)
        {
            return Reading.UnsupportedAction();
        }

        var mergedAt = Timestamp(root, "pull_request", "merged_at") ?? receivedAt;
        var title = Text(root, "pull_request", "title", GitHubCandidatePolicy.TitleMaxLength)
            ?? $"Pull request #{number}";

        return Reading.Proposed(
        [
            new CandidateProposal(
                GitHubCandidateKinds.PullRequest,
                id.ToString(CultureInfo.InvariantCulture),
                mergedAt.ToString("O", CultureInfo.InvariantCulture),
                GitHubCandidatePolicy.Title(title, title),
                mergedAt),
        ]);
    }

    private static Reading Star(JsonElement root, DateTimeOffset receivedAt)
    {
        if (!TryLong(root, "repository", "id", out var repositoryId))
        {
            return Reading.NothingNamed();
        }

        if (!TryLong(root, "repository", "stargazers_count", out var starCount))
        {
            // A payload without a star count says nothing about a threshold. Guessing one would
            // propose a milestone for a repository nobody counted.
            return Reading.NothingNamed();
        }

        var thresholds = GitHubCandidatePolicy.ReachedStarThresholds(starCount);

        if (thresholds.Count == 0)
        {
            return Reading.BelowThreshold();
        }

        var starredAt = Timestamp(root, "starred_at") ?? receivedAt;
        var proposals = new List<CandidateProposal>(thresholds.Count);

        foreach (var threshold in thresholds)
        {
            proposals.Add(new CandidateProposal(
                GitHubCandidateKinds.Star,
                repositoryId.ToString(CultureInfo.InvariantCulture),
                // The threshold itself is the stable revision of this kind of fact: the first delivery
                // that reports at least 100 proposes it, and every later delivery is the same key.
                threshold.ToString(CultureInfo.InvariantCulture),
                $"{threshold.ToString(CultureInfo.InvariantCulture)} stars",
                starredAt));
        }

        return Reading.Proposed(proposals);
    }

    /// <summary>
    /// One bounded string held directly by <paramref name="element"/>.
    /// </summary>
    /// <remarks>
    /// <b>Read from the element itself, and not through a parent of the same name.</b> A previous
    /// spelling of this overload passed the property name as both the parent and the property, which
    /// made it look for an <em>object</em> at that property and return nothing for any ordinary scalar -
    /// so a release's name, tag and commitish and a create event's ref and ref type all read as absent.
    /// Nothing failed loudly: candidates were still written, with their titles and tag names falling
    /// back to an id, and a suite asserting only the source keys would have stayed green over a payload
    /// reader that could not read a release. The three-argument overload is the one that takes a real
    /// parent, and the two are now genuinely different shapes rather than one calling the other wrongly.
    /// </remarks>
    private static string? Text(JsonElement element, string property, int maxLength) =>
        GitHubCandidatePolicy.Bounded(ReadString(element, property), maxLength);

    private static string? Text(JsonElement element, string parent, string property, int maxLength)
    {
        if (!element.TryGetProperty(parent, out var container) || container.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return GitHubCandidatePolicy.Bounded(ReadString(container, property), maxLength);
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var node) || node.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return node.GetString();
    }

    // The out parameter is a plain long rather than a nullable one. A `long?` here would make every
    // call site write `.Value` on a value the compiler had already been told was present by the
    // returned bool, and a call site that has to say `.Value` is one line away from forgetting to ask.
    private static bool TryLong(JsonElement element, string parent, string property, out long value)
    {
        value = 0;

        if (!element.TryGetProperty(parent, out var container) || container.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        return TryLong(container, property, out value);
    }

    private static bool TryLong(JsonElement element, string property, out long value)
    {
        value = 0;

        if (!element.TryGetProperty(property, out var node) || node.ValueKind is not JsonValueKind.Number)
        {
            return false;
        }

        if (node.TryGetInt64(out var parsed))
        {
            value = parsed;

            return true;
        }

        return false;
    }

    private static DateTimeOffset? Timestamp(JsonElement element, string property) =>
        Parse(ReadString(element, property));

    private static DateTimeOffset? Timestamp(JsonElement element, string parent, string property) =>
        Parse(Text(element, parent, property, 64));

    private static DateTimeOffset? Parse(string? raw) =>
        raw is not null && DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
