using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronicleaf.Application.Narrative;

/// <summary>
/// The delivered summary builder: deterministic arrangement of attributed sources, invention-free
/// by construction (NARRATIVE-MEMORY-002).
/// </summary>
/// <remarks>
/// <para>
/// This builder reaches no provider, resolves no credential and opens no socket: it arranges the
/// context lines and milestone sources it is handed into a JSON document whose every section
/// carries the reference of the source it came from. A summary built this way can never name a
/// user, a fact or a source its inputs did not contain, because there is no model in the path
/// that could introduce one - the "unknown feedback" line survives into the body verbatim rather
/// than being completed.
/// </para>
/// <para>
/// The body is canonical JSON: sections in a fixed order, no insignificant whitespace, lowercase
/// hex digests. The same sources therefore always produce the same bytes, which is what lets the
/// refresh compare a fingerprint instead of re-reading prose.
/// </para>
/// </remarks>
public sealed class DeterministicNarrativeSummaryBuilder : INarrativeSummaryBuilder
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc />
    public string PromptVersion => NarrativeMemoryPolicy.PromptVersion;

    /// <inheritdoc />
    public Task<string> BuildAsync(
        NarrativeContextView context,
        IReadOnlyList<MilestoneNarrativeSource> milestones,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(milestones);

        var history = new JsonArray();
        var decisions = new JsonArray();
        var assets = new JsonArray();
        var feedback = new JsonArray();
        var citations = new List<string>();

        foreach (var line in context.Lines)
        {
            var node = new JsonObject
            {
                ["ref"] = line.Ref,
                ["text"] = line.Text,
            };

            switch (line.Section)
            {
                case "decisions":
                    decisions.Add(node);
                    break;
                case "feedback":
                    feedback.Add(node);
                    break;
                default:
                    history.Add(node);
                    break;
            }

            if (!string.Equals(line.Ref, "feedback:unknown", StringComparison.Ordinal))
            {
                citations.Add(line.Ref);
            }
        }

        foreach (var milestone in milestones.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id))
        {
            if (milestone.ApprovedExcerpt is null)
            {
                continue;
            }

            assets.Add(new JsonObject
            {
                ["ref"] = $"milestone:{milestone.Id:D}",
                ["excerpt"] = milestone.ApprovedExcerpt,
            });
        }

        // Unknown history stays unknown in the body: when the only feedback line is the
        // unknown marker, the section is the labelled string rather than a list a reader could
        // mistake for people who said things.
        var knownFeedback = feedback
            .Where(node => !string.Equals(
                (node as JsonObject)?["ref"]?.GetValue<string>(),
                "feedback:unknown",
                StringComparison.Ordinal))
            .Select(node => node!.DeepClone())
            .ToArray();

        var document = new JsonObject
        {
            ["promptVersion"] = PromptVersion,
            ["projectId"] = context.ProjectId.ToString("D"),
            ["throughMilestoneId"] = context.ThroughMilestoneId?.ToString("D"),
            ["cutoff"] = context.Cutoff.ToUniversalTime().ToString("O"),
            ["history"] = history,
            ["decisions"] = decisions,
            ["assets"] = assets,
            ["feedback"] = knownFeedback.Length == 0
                ? JsonValue.Create("unknown: no user feedback has been recorded.")
                : new JsonArray([.. knownFeedback]),
            ["citations"] = new JsonArray([.. citations.Order(StringComparer.Ordinal).Select(value => (JsonNode)value)]),
        };

        return Task.FromResult(document.ToJsonString(Canonical));
    }
}
