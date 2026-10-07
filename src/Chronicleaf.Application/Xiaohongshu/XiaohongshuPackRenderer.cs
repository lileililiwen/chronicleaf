using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.ContentVariants;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Xiaohongshu;

/// <summary>
/// The deterministic Xiaohongshu pack template (XIAOHONGSHU-CONTENT-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure: no IO, no clock, no provider, no network.</b> The inputs are the source revision's
/// stored summary, its claim map, the already-resolved effective voice, the owner's chosen title,
/// tags, cover line, call to action and image order. The output is a note-shaped plain text that
/// cites only the footnote ids the claim map assigned, plus the ordered title candidates the
/// preview lets the owner choose from.
/// </para>
/// <para>
/// <b>Voice is applied, not consulted.</b> Every candidate title is cut to the effective
/// <c>TitleMaxChars</c> and to this package's own title ceiling, so a project override that
/// shortens the preset's limit shortens the pack's titles. The framing sentences carry no
/// emoji, no pronouns, no superlatives and no forbidden term, so they pass under either emoji
/// setting, either first-person setting and any marketing level - the project restriction wins
/// structurally, because a template that never emits what the voice forbids cannot be
/// overruled by a preset that allows it. The Xiaohongshu preset asks for emoji and medium
/// marketing; neither reaches the text.
/// </para>
/// <para>
/// <b>Nothing here is a platform claim.</b> The title and body bounds below are this package's
/// own internal ceilings, stated in characters of stored text. No Xiaohongshu limit is named,
/// because this product performs no Xiaohongshu call against which such a number could be
/// verified. The candidates are suggestions the owner chooses from, not promised platform
/// acceptance or growth.
/// </para>
/// </remarks>
public static class XiaohongshuPackRenderer
{
    /// <summary>
    /// The template version recorded as the revision's prompt version, so a stored pack names
    /// the template that rendered it.
    /// </summary>
    public const string TemplateVersion = "xiaohongshu-pack/1";

    /// <summary>
    /// This package's own internal ceiling for one title candidate, in characters. A longer
    /// choice is refused before anything is stored; the ceiling is a product bound, not a
    /// claim about any platform.
    /// </summary>
    public const int MaxTitleChars = 80;

    /// <summary>
    /// This package's own internal ceiling for the rendered pack text, in characters. Over it
    /// the adaptation is refused before anything is stored; approaching it carries a warning.
    /// </summary>
    public const int MaxPackChars = 10000;

    /// <summary>
    /// At or above this length the adaptation still stores, but the answer says so plainly.
    /// </summary>
    public const int LengthWarningAtChars = 9000;

    /// <summary>How many title candidates one pack offers. A bound, not a platform rule.</summary>
    public const int MaxTitleCandidates = 5;

    /// <summary>How many tags one pack may carry. A bound, not a platform rule.</summary>
    public const int MaxTags = 10;

    /// <summary>How many characters of the owner's own cover line one pack may carry.</summary>
    public const int MaxCoverChars = 200;

    /// <summary>How many characters of the owner's own call to action one pack may carry.</summary>
    public const int MaxCtaChars = 280;

    /// <summary>The verdict for one rendering: the candidates, the text, its length, and whether it is long.</summary>
    /// <param name="TitleCandidates">The derived titles, one to five, in order.</param>
    /// <param name="SelectedTitle">The owner's choice, exactly one of the candidates.</param>
    /// <param name="Text">The copy-ready plain text.</param>
    /// <param name="CharCount">The text's length in characters.</param>
    /// <param name="LengthWarning">True when the text reached the warning threshold.</param>
    public sealed record RenderedPack(
        IReadOnlyList<string> TitleCandidates,
        string SelectedTitle,
        string Text,
        int CharCount,
        bool LengthWarning);

    /// <summary>
    /// Renders one Xiaohongshu pack. A choice that names no candidate, or a text over
    /// <see cref="MaxPackChars"/>, is refused before it exists, because a limit that stored
    /// anyway would not be one.
    /// </summary>
    /// <exception cref="UnknownTitleException">The choice names no derived candidate.</exception>
    /// <exception cref="TooLongException">The rendered text is over the internal ceiling.</exception>
    public static RenderedPack Render(
        string milestoneTitle,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        VoiceValues voice,
        string selectedTitle,
        IReadOnlyList<string> tags,
        string? coverCopy,
        string? callToAction)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(selectedTitle);
        ArgumentNullException.ThrowIfNull(tags);

        var candidates = DeriveTitles(milestoneTitle, summary, voice);

        var choice = (selectedTitle ?? string.Empty).Trim();

        if (!candidates.Contains(choice, StringComparer.Ordinal))
        {
            throw new UnknownTitleException();
        }

        var body = new StringBuilder();
        body.Append(choice).Append("\n\n");

        // The cover line is the owner's own words, verbatim, or the summary when they supplied
        // none: a note without a cover line still opens on what the evidence states.
        var cover = string.IsNullOrWhiteSpace(coverCopy)
            ? summary.Summary + CiteFirst(summary, claims)
            : coverCopy.Trim();
        body.Append(cover).Append("\n\n");

        body.Append(summary.Summary).Append(CiteFirst(summary, claims)).Append("\n\n");

        body.Append("What changed:\n");

        var changes = summary.ClaimsIn(GroundedSummarySection.Changes);
        if (changes.Count == 0)
        {
            body.Append(ContentVariantRenderer.UnknownLabel).Append('\n');
        }
        else
        {
            for (var index = 0; index < changes.Count; index++)
            {
                var mapped = claims.SingleOrDefault(candidate =>
                    candidate.Section == GroundedSummarySection.Changes && candidate.Index == index);
                var citation = mapped is null ? string.Empty : $"[^{mapped.Id}]";
                body.Append("- ").Append(changes[index].Text).Append(citation).Append('\n');
            }
        }

        body.Append("\nKnown limitations:\n");

        var limitations = summary.ClaimsIn(GroundedSummarySection.KnownLimitations);
        if (limitations.Count == 0)
        {
            body.Append(ContentVariantRenderer.UnknownLabel).Append('\n');
        }
        else
        {
            for (var index = 0; index < limitations.Count; index++)
            {
                var mapped = claims.SingleOrDefault(candidate =>
                    candidate.Section == GroundedSummarySection.KnownLimitations && candidate.Index == index);
                var citation = mapped is null ? string.Empty : $"[^{mapped.Id}]";
                body.Append("- ").Append(limitations[index].Text).Append(citation).Append('\n');
            }
        }

        // The owner's own words, verbatim. Absent stays absent: a note with no call to action is
        // informational, and no follower, star or engagement promise is written on the owner's
        // behalf.
        if (!string.IsNullOrWhiteSpace(callToAction))
        {
            body.Append('\n').Append(callToAction.Trim()).Append('\n');
        }

        // The owner's own tags, verbatim, on their own line. None invented: a pack with no tags
        // carries no tag line at all.
        if (tags.Count > 0)
        {
            body.Append('\n').Append(string.Join(" ", tags)).Append('\n');
        }

        body.Append('\n');
        foreach (var claim in claims)
        {
            body.Append("[^").Append(claim.Id).Append("]: ").Append(claim.Text).Append('\n');
        }

        var text = body.ToString();

        if (text.Length > MaxPackChars)
        {
            throw new TooLongException(text.Length);
        }

        return new RenderedPack(candidates, choice, text, text.Length, text.Length >= LengthWarningAtChars);
    }

    /// <summary>
    /// Derives one to five title candidates from the summary's own words, each cut to the
    /// effective voice bound and this package's title ceiling. Candidates come from stored
    /// evidence-grounded text only, so choosing one can never introduce an ungrounded figure.
    /// </summary>
    public static IReadOnlyList<string> DeriveTitles(
        string milestoneTitle,
        GroundedSummary summary,
        VoiceValues voice)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(voice);

        var bound = Math.Min(voice.TitleMaxChars, MaxTitleChars);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>(MaxTitleCandidates);

        void Offer(string? text)
        {
            if (candidates.Count >= MaxTitleCandidates)
            {
                return;
            }

            var cut = ContentVariantRenderer.ApplyTitleBound(text ?? string.Empty, bound);
            if (cut.Length == 0 || !seen.Add(cut))
            {
                return;
            }

            candidates.Add(cut);
        }

        Offer(string.IsNullOrWhiteSpace(summary.MilestoneTitle) ? milestoneTitle : summary.MilestoneTitle);
        Offer(summary.Summary);

        foreach (var section in new[]
                 {
                     GroundedSummarySection.Changes,
                     GroundedSummarySection.ProblemsSolved,
                     GroundedSummarySection.NextSteps,
                 })
        {
            foreach (var claim in summary.ClaimsIn(section))
            {
                Offer(claim.Text);
                if (candidates.Count >= MaxTitleCandidates)
                {
                    break;
                }
            }
        }

        // The milestone title always offers at least one candidate: an empty summary still
        // names what the milestone is about.
        return candidates.Count == 0 ? ["Untitled note"] : candidates;
    }

    /// <summary>The choice names no derived candidate. Nothing was stored.</summary>
    /// <remarks>
    /// The choice itself is not carried: it may hold a figure the evidence never stated, and an
    /// exception message is not a place for ungrounded text.
    /// </remarks>
    public sealed class UnknownTitleException()
        : InvalidOperationException(
            "The title must be one of the pack's derived candidates. "
            + "Choose a title from the preview, or edit the pack after adapting it.");

    /// <summary>The rendered text is over the package's internal ceiling. Nothing was stored.</summary>
    public sealed class TooLongException(int charCount)
        : InvalidOperationException(
            $"The Xiaohongshu pack is {charCount} characters, over this product's own "
            + $"{MaxPackChars}-character ceiling. Shorten the title, cover, call to action or "
            + "tags, or adapt a story with less text.")
    {
        public int CharCount { get; } = charCount;
    }

    private static string CiteFirst(GroundedSummary summary, IReadOnlyList<DraftClaim> claims)
    {
        var first = claims.Count > 0 ? claims[0] : null;
        return first is null ? string.Empty : $"[^{first.Id}]";
    }
}
