using System.Globalization;

namespace Chronicleaf.Application.Content;
/// <summary>
/// The closed set of grounding rules (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// <para>
/// Two of these end a generation and two of them annotate a stored draft, and the split is deliberate.
/// <b>A rule that ends generation is a rule about a fact that cannot be true.</b> An evidence id
/// outside the frozen snapshot, or a number that appears in no evidence item at all, are not opinions
/// about a draft - they are the draft asserting something the authorized material does not contain.
/// <b>A rule that annotates is a rule about something a machine cannot decide.</b> Whether a claim is
/// well supported by the evidence it cites is a judgement, and a judgement this system is not entitled
/// to make silently in either direction. Those are reported to a human and the draft is stored, because
/// discarding a draft for a missing footnote is as wrong as publishing one that invents a figure.
/// </para>
/// <para>
/// <b>No rule here claims to verify meaning.</b> The design says so and this file is where that
/// honesty lives: the checks are identifier membership and numeral provenance. A claim can be perfectly
/// cited and still be wrong about what the citation means, and the surface that shows these findings
/// has to say so rather than implying that a clean list is a verified draft.
/// </para>
/// </remarks>
public static class GroundingRule
{
    /// <summary>
    /// A factual item cited no evidence at all. The draft is stored; the item is reported.
    /// </summary>
    public const string ClaimUnreferenced = "claim_unreferenced";

    /// <summary>
    /// Every evidence item a factual item cited was one nothing was collected from. The draft is
    /// stored; the item is reported.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ClaimUnreferenced"/> on purpose. An item that cites an
    /// <c>unavailable</c> evidence record has cited something real - a record saying a source could not
    /// be read - and a reviewer needs to know that the claim rests on a gap rather than on a fact.
    /// </remarks>
    public const string ClaimCitedUnavailable = "claim_cited_unavailable";

    /// <summary>
    /// A factual item cited an evidence id that is not in the frozen bundle the revision names.
    /// </summary>
    /// <remarks>
    /// Only reachable when a stored row was edited outside this package, because generation refuses
    /// such a claim before it is written. It is reported rather than treated as fatal for that reason:
    /// the row is what an owner is about to read, and a finding they can see beats a read that fails.
    /// </remarks>
    public const string ClaimEvidenceMissing = "claim_evidence_missing";

    public static readonly IReadOnlyList<string> All =
        [ClaimUnreferenced, ClaimCitedUnavailable, ClaimEvidenceMissing];

    public static bool IsKnown(string? rule) => rule is not null && All.Contains(rule, StringComparer.Ordinal);
}

/// <summary>
/// One thing a human has to look at before trusting a stored draft
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A finding is a <em>report</em> and never a correction, on the same terms the delivered voice checker
/// established: nothing here edits the summary or the body, and no finding carries a replacement value.
/// A draft that cites nothing is stored with a finding, not silently repaired - rewriting a model's
/// citations is exactly as dishonest as inventing them, and the owner cannot tell the difference.
/// </para>
/// <para>
/// The shape is a span into the stored text, in the same spirit as the delivered
/// <c>VoiceFinding</c>. <paramref name="Section"/> and <paramref name="Index"/> locate the claim
/// exactly, and <paramref name="Excerpt"/> is the claim's own text copied out so a panel can show what
/// was flagged without the reader having to resolve the index themselves.
/// </para>
/// </remarks>
/// <param name="Rule">One of <see cref="GroundingRule"/>.</param>
/// <param name="Section">
/// Which summary section the claim came from, or <c>body</c> for a finding about the Markdown itself.
/// </param>
/// <param name="Index">The claim's position inside that section, from zero; -1 for a whole-field finding.</param>
/// <param name="EvidenceId">The evidence id the finding is about, when it is about one.</param>
/// <param name="Excerpt">The flagged claim's own text, or the matched numeral.</param>
/// <param name="Message">What a reader needs to decide, in fixed words.</param>
public sealed record GroundingFinding(
    string Rule,
    string Section,
    int Index,
    Guid? EvidenceId,
    string Excerpt,
    string Message);

/// <summary>
/// The outcome of checking a summary against the frozen snapshot: either a refusal that ends the
/// generation, or a draft plus the findings a human has to judge
/// (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
public sealed record GroundingVerdict(string? FailureCode, IReadOnlyList<GroundingFinding> Findings)
{
    /// <summary>True when generation must stop, and <see cref="FailureCode"/> names why.</summary>
    public bool Failed => FailureCode is not null;

    public static GroundingVerdict Refused(string code) => new(code, []);

    public static GroundingVerdict Accepted(IReadOnlyList<GroundingFinding> findings) => new(null, findings);
}

/// <summary>
/// The deterministic grounding rules, and the one refusal that ends a generation
/// (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here calls anything.</b> It reads a summary, a claim map and a snapshot, and returns a
/// verdict. The same input always produces the same findings, which is what makes a stored draft's
/// finding list a fact about the draft rather than a report about when it was read.
/// </para>
/// <para>
/// <b>The failure order is fixed</b> and it matters: an unsupported reference is reported before a
/// fabricated metric. A claim citing an id outside the snapshot has no evidence text to check a number
/// against at all, so checking the number first would mean checking it against material that is not
/// authorized for this call - which is the exact thing the membership check exists to prevent.
/// </para>
/// </remarks>
public static class GroundingValidator
{
    /// <summary>
    /// Checks every claim of an accepted summary against the frozen snapshot.
    /// </summary>
    /// <param name="claims">The claim map, which is the summary's factual content with chosen ids.</param>
    /// <param name="snapshotIds">The evidence ids the owner authorized, and no others.</param>
    /// <param name="evidenceTextById">
    /// Each authorized item's text, keyed by id. Used for the numeral rule: a number in a claim has to
    /// appear in the text of an item that claim cites, or in the whole authorized set when it cites
    /// nothing at all.
    /// </param>
    public static GroundingVerdict Validate(
        IReadOnlyList<DraftClaim> claims,
        IReadOnlySet<Guid> snapshotIds,
        IReadOnlyDictionary<Guid, string> evidenceTextById)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(snapshotIds);
        ArgumentNullException.ThrowIfNull(evidenceTextById);

        var findings = new List<GroundingFinding>();

        foreach (var claim in claims)
        {
            // 1. Membership first, and it is fatal. A claim that names an id outside the snapshot is
            //    asserting something about material the owner never authorized sending, so no further
            //    check on that claim is meaningful and none is attempted.
            foreach (var id in claim.EvidenceIds)
            {
                if (!snapshotIds.Contains(id))
                {
                    return GroundingVerdict.Refused(ContentError.UnsupportedReference);
                }
            }

            // 2. The numeral rule. This is the design's "no marketing statistics, star counts or user
            //    counts invented", and it is deliberately a numeral rule and not a claim checker: a
            //    figure with no origin anywhere in the authorized material is invented, and a figure
            //    that is in the material is at least traceable to something the owner has read.
            var cited = claim.EvidenceIds.Count == 0
                ? string.Join('\n', evidenceTextById.Values)
                : string.Join('\n', claim.EvidenceIds.Select(id => evidenceTextById.GetValueOrDefault(id) ?? string.Empty));

            foreach (var number in ClaimMap.NumbersIn(claim.Text))
            {
                if (!ClaimMap.ContainsNumber(cited, number))
                {
                    return GroundingVerdict.Refused(ContentError.FabricatedMetric);
                }
            }

            // 3. The two annotations, which do not end a generation.
            if (claim.EvidenceIds.Count == 0)
            {
                findings.Add(new GroundingFinding(
                    GroundingRule.ClaimUnreferenced,
                    GroundedSummary.SectionKey(claim.Section),
                    claim.Index,
                    null,
                    claim.Text,
                    "This claim cites no evidence item, so nothing about it can be checked against the "
                    + "milestone's own record. Read it and decide whether it is true."));
            }
        }

        return GroundingVerdict.Accepted(findings);
    }

    /// <summary>
    /// Produces the findings a stored draft carries, from the stored summary and the frozen bundle.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Validate"/>, and deliberately covering <em>all three</em>
    /// rules rather than only the two that need the bundle. Generation could produce its own list
    /// because it holds the snapshot; a read has to produce the same list from what is stored, and a rule
    /// only one of the two paths evaluated would mean a draft's findings changed depending on who asked.
    /// So the read path recomputes every rule, and the two that need the bundle read the bundle the
    /// revision names rather than the milestone's current head - because a finding is about this
    /// revision, not about what has happened to the milestone since.
    /// </remarks>
    /// <param name="claims">The claim map read back from the stored summary.</param>
    /// <param name="availabilityById">
    /// Each id the frozen bundle holds and its availability, or <c>null</c> for an id the bundle does not
    /// hold.
    /// </param>
    public static IReadOnlyList<GroundingFinding> ForStoredRevision(
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyDictionary<Guid, string?> availabilityById)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(availabilityById);

        var findings = new List<GroundingFinding>();

        foreach (var claim in claims)
        {
            var allUnavailable = claim.EvidenceIds.Count > 0;

            foreach (var id in claim.EvidenceIds)
            {
                if (!availabilityById.TryGetValue(id, out var availability) || availability is null)
                {
                    allUnavailable = false;
                    findings.Add(new GroundingFinding(
                        GroundingRule.ClaimEvidenceMissing,
                        GroundedSummary.SectionKey(claim.Section),
                        claim.Index,
                        id,
                        claim.Text,
                        "This claim cites an evidence item the frozen bundle does not contain."));
                    continue;
                }

                if (!string.Equals(availability, Core.EvidenceAvailability.Unavailable, StringComparison.Ordinal))
                {
                    allUnavailable = false;
                }
            }

            if (claim.EvidenceIds.Count == 0)
            {
                findings.Add(new GroundingFinding(
                    GroundingRule.ClaimUnreferenced,
                    GroundedSummary.SectionKey(claim.Section),
                    claim.Index,
                    null,
                    claim.Text,
                    "This claim cites no evidence item, so nothing about it can be checked against the "
                    + "milestone's own record. Read it and decide whether it is true."));
            }
            else if (allUnavailable)
            {
                findings.Add(new GroundingFinding(
                    GroundingRule.ClaimCitedUnavailable,
                    GroundedSummary.SectionKey(claim.Section),
                    claim.Index,
                    null,
                    claim.Text,
                    "Every evidence item this claim cites says it could not be read, so the claim rests on "
                    + "a gap rather than on a collected fact."));
            }
        }

        return findings;
    }

    /// <summary>
    /// A stable sort order for findings, so the same draft always lists them the same way.
    /// </summary>
    /// <remarks>
    /// Section order comes from <see cref="GroundedSummary.Sections"/> rather than from an ordinal sort,
    /// so a finding on <c>changes</c> stays above one on <c>nextSteps</c> however the sections are
    /// renamed, and the index breaks ties inside a section. Written as a comparison rather than as a
    /// <c>ThenBy</c> chain because a chain over a projection would be re-evaluated per comparison and
    /// the order is part of what a stored list means.
    /// </remarks>
    public static IReadOnlyList<GroundingFinding> Order(IEnumerable<GroundingFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return [.. findings
            .OrderBy(finding => SectionRank(finding.Section))
            .ThenBy(finding => finding.Index)
            .ThenBy(finding => finding.Rule, StringComparer.Ordinal)
            .ThenBy(finding => finding.Excerpt, StringComparer.Ordinal)];
    }

    private static int SectionRank(string section)
    {
        if (section == "body")
        {
            return GroundedSummary.Sections.Count;
        }

        for (var index = 0; index < GroundedSummary.Sections.Count; index++)
        {
            if (string.Equals(
                    GroundedSummary.SectionKey(GroundedSummary.Sections[index]),
                    section,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
