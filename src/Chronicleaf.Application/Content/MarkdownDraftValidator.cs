using System.Text;
using System.Text.RegularExpressions;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The local checks a generated body must pass before it can be stored as a draft
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 has already decided what is true; this decides whether the prose says so.</b> The
/// summary was validated against the frozen evidence snapshot, so every claim in the claim map is
/// grounded. What can still go wrong is that the body over-reaches: it cites something that is not a
/// claim, it drops a known limitation, or it introduces a figure that appears in no evidence item at
/// all. Each of those is decided here, locally, from bytes this system is holding.
/// </para>
/// <para>
/// <b>No repair call is spent on any of it.</b> The design permits one repair, and it permits it for
/// invalid summary JSON - a formatting failure with an obvious cause. A body that cites an unknown
/// footnote, or that omits a caveat, is not a formatting failure: repairing it would be a second
/// provider call spent asking the same model to reconsider what it claimed, and the owner may be billed
/// for it. So these refusals are terminal for the run and the previous draft is left exactly as it was.
/// </para>
/// <para>
/// <b>What this cannot do, stated plainly.</b> It checks identifiers and numerals. It does not check
/// that a cited claim supports the sentence it is attached to, and no local rule ever could. A body
/// can pass every check here and still misrepresent its evidence, which is why the stored draft is a
/// draft and why <see cref="GroundingFinding"/> is what a human is shown rather than a verdict.
/// </para>
/// </remarks>
public static partial class MarkdownDraftValidator
{
    /// <summary>
    /// A footnote definition: <c>[^id]:</c> at the start of a line, with up to three spaces of
    /// indentation, which is what CommonMark allows.
    /// </summary>
    [GeneratedRegex(@"(?m)^ {0,3}\[\^(?<id>[A-Za-z0-9_.-]+)\]:", RegexOptions.CultureInvariant)]
    private static partial Regex DefinitionPattern { get; }

    /// <summary>
    /// A footnote reference, anywhere in the body including inside a definition line - the definition's
    /// own marker is subtracted afterwards so an unused definition is not counted as a citation.
    /// </summary>
    [GeneratedRegex(@"\[\^(?<id>[A-Za-z0-9_.-]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern { get; }

    /// <summary>
    /// The verdict for one body, or the code that says why it cannot be stored.
    /// </summary>
    /// <param name="FailureCode">A <see cref="ContentError"/> job code, or <c>null</c> when the body is storable.</param>
    /// <param name="CitationCount">How many footnote references the body actually used.</param>
    public sealed record Verdict(string? FailureCode, int CitationCount);

    /// <summary>
    /// Checks a generated body against the claim map and the summary it was written from.
    /// </summary>
    /// <param name="body">The Markdown exactly as the provider returned it.</param>
    /// <param name="claims">The claim map, which fixes the set of legal footnote ids.</param>
    /// <param name="knownLimitations">The limitation claims the summary accepted, which must survive.</param>
    /// <param name="evidenceText">Every authorized evidence item's text, joined, for the numeral rule.</param>
    public static Verdict Validate(
        string? body,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<GroundedClaim> knownLimitations,
        string evidenceText)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new Verdict(ContentError.UngroundedBody, 0);
        }

        var byteCount = Encoding.UTF8.GetByteCount(body);
        if (byteCount > Core.ContentAssetLimits.MaxBodyBytes)
        {
            return new Verdict(ContentError.BodyTooLarge, 0);
        }

        var legal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            legal.Add(claim.Id);
        }

        var defined = new List<string>();
        foreach (Match match in DefinitionPattern.Matches(body))
        {
            defined.Add(match.Groups["id"].Value);
        }

        // Every definition has to name a claim that exists. A body that invents a footnote is a body
        // asserting a source this system never checked, which is the one thing a grounded draft cannot
        // contain - and it is refused immediately rather than repaired, exactly as the design requires
        // for an unsupported reference.
        foreach (var id in defined)
        {
            if (!legal.Contains(id))
            {
                return new Verdict(ContentError.CitationUnknown, 0);
            }
        }

        var references = new List<string>();
        foreach (Match match in ReferencePattern.Matches(body))
        {
            references.Add(match.Groups["id"].Value);
        }

        // Each definition's own marker is a reference too, so one occurrence per definition is removed
        // before the reference list means anything. Removing rather than excluding by position keeps
        // this correct when a definition line contains another reference in its own text.
        foreach (var id in defined)
        {
            var at = references.IndexOf(id);
            if (at >= 0)
            {
                references.RemoveAt(at);
            }
        }

        var definitions = new HashSet<string>(defined, StringComparer.Ordinal);

        // Every reference has to resolve. A dangling marker is a citation to nothing, and storing it
        // would leave a reader with a superscript that goes nowhere - the visual promise of provenance
        // with none behind it.
        foreach (var id in references)
        {
            if (!definitions.Contains(id))
            {
                return new Verdict(ContentError.CitationUnknown, 0);
            }
        }

        // A body with no citation at all is not a grounded draft. This is checked after the citation
        // rules rather than before them so the specific answer wins when there is a specific fault.
        if (references.Count == 0)
        {
            return new Verdict(ContentError.UngroundedBody, 0);
        }

        // Every known limitation the summary accepted has to be in the body. The design's fixture names
        // an answer that ignored one, and the check is containment rather than equality so a model that
        // reflowed a sentence is not punished for it - a limitation that is quietly dropped is the one
        // outcome worse than no draft at all.
        var normalized = Normalize(body);

        foreach (var limitation in knownLimitations)
        {
            if (!normalized.Contains(Normalize(limitation.Text), StringComparison.Ordinal))
            {
                return new Verdict(ContentError.LimitationDropped, references.Count);
            }
        }

        // The same numeral rule the summary passed, applied to the prose. A figure in the body that
        // appears in no authorized evidence item is invented, and this is the rule the design states
        // for the finished text rather than for the intermediate one. Footnote labels are removed first,
        // because a citation this system minted is a provenance token and not a figure.
        foreach (var number in ClaimMap.NumbersInProse(body))
        {
            if (!ClaimMap.ContainsNumber(evidenceText, number))
            {
                return new Verdict(ContentError.FabricatedMetric, references.Count);
            }
        }

        return new Verdict(null, references.Count);
    }

    /// <summary>
    /// Folds text for a containment comparison: NFC, collapsed whitespace, case-insensitive.
    /// </summary>
    /// <remarks>
    /// NFC because the same characters can be spelled with different byte sequences and a limitation
    /// that is present but composed differently is present. Collapsed whitespace for the same reason a
    /// model reflows a paragraph. The comparison is deliberately loose in exactly these two ways and
    /// nowhere else: punctuation, word order and added words are all still compared, so a limitation
    /// that was rewritten into a different statement is still reported as dropped.
    /// </remarks>
    private static string Normalize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(folded.Length);
        var previousWasSpace = false;

        foreach (var character in folded)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
            previousWasSpace = false;
        }

        return builder.ToString();
    }
}
