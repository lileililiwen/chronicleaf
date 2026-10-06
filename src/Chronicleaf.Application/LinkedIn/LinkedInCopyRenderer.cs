using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.ContentVariants;
using Chronicleaf.Core;

namespace Chronicleaf.Application.LinkedIn;

/// <summary>
/// The deterministic LinkedIn copy template (LINKEDIN-COPY-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure: no IO, no clock, no provider, no network.</b> The inputs are the source revision's
/// stored summary, its claim map, the already-resolved effective voice, and the owner's own
/// call to action, hashtags and per-picture accessibility words. The output is plain post text
/// that cites only the footnote ids the claim map assigned.
/// </para>
/// <para>
/// <b>Voice is applied, not consulted.</b> The title is cut to the effective
/// <c>TitleMaxChars</c>, so a project override that shortens the preset's limit shortens the
/// copy's first line. The framing sentences carry no emoji, no pronouns, no superlatives and
/// no forbidden term, so they pass under either emoji setting, either first-person setting and
/// any marketing level - the project restriction wins structurally, because a template that
/// never emits what the voice forbids cannot be overruled by a preset that allows it.
/// </para>
/// <para>
/// <b>Nothing here is a platform claim.</b> The length bound below is this package's own
/// internal ceiling, stated in characters of stored text, and the response carries the exact
/// character count beside it. No LinkedIn limit is named, because this product performs no
/// LinkedIn call against which such a number could be verified.
/// </para>
/// </remarks>
public static class LinkedInCopyRenderer
{
    /// <summary>
    /// The template version recorded as the revision's prompt version, so a stored copy names
    /// the template that rendered it.
    /// </summary>
    public const string TemplateVersion = "linkedin-copy/1";

    /// <summary>
    /// This package's own internal ceiling for the rendered copy text, in characters. Over it
    /// the adaptation is refused before anything is stored; approaching it carries a warning.
    /// </summary>
    public const int MaxCopyChars = 10000;

    /// <summary>
    /// At or above this length the adaptation still stores, but the answer says so plainly.
    /// </summary>
    public const int LengthWarningAtChars = 9000;

    /// <summary>How many hashtags one copy may carry. A bound, not a platform rule.</summary>
    public const int MaxHashtags = 5;

    /// <summary>How many characters of the owner's own call to action one copy may carry.</summary>
    public const int MaxCtaChars = 280;

    /// <summary>The verdict for one rendering: the text, its length, and whether it is long.</summary>
    /// <param name="Text">The copy-ready plain text.</param>
    /// <param name="CharCount">The text's length in characters.</param>
    /// <param name="LengthWarning">True when the text reached the warning threshold.</param>
    public sealed record RenderedCopy(string Text, int CharCount, bool LengthWarning);

    /// <summary>
    /// Renders one LinkedIn copy. A text over <see cref="MaxCopyChars"/> is refused before it
    /// exists, because a limit that stored anyway would not be one.
    /// </summary>
    /// <exception cref="TooLongException">The rendered text is over the internal ceiling.</exception>
    public static RenderedCopy Render(
        string milestoneTitle,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        VoiceValues voice,
        string? callToAction,
        IReadOnlyList<string> hashtags)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(hashtags);

        var title = ContentVariantRenderer.ApplyTitleBound(
            string.IsNullOrWhiteSpace(summary.MilestoneTitle) ? milestoneTitle : summary.MilestoneTitle,
            voice.TitleMaxChars);

        var body = new StringBuilder();
        body.Append(title).Append("\n\n");
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

        // The owner's own words, verbatim. Absent stays absent: a post with no call to action is
        // informational, and no growth promise is written on the owner's behalf.
        if (!string.IsNullOrWhiteSpace(callToAction))
        {
            body.Append('\n').Append(callToAction.Trim()).Append('\n');
        }

        // The owner's own tags, verbatim, on their own line. None invented: a copy with no
        // hashtags carries no tag line at all.
        if (hashtags.Count > 0)
        {
            body.Append('\n').Append(string.Join(" ", hashtags)).Append('\n');
        }

        body.Append('\n');
        foreach (var claim in claims)
        {
            body.Append("[^").Append(claim.Id).Append("]: ").Append(claim.Text).Append('\n');
        }

        var text = body.ToString();

        if (text.Length > MaxCopyChars)
        {
            throw new TooLongException(text.Length);
        }

        return new RenderedCopy(text, text.Length, text.Length >= LengthWarningAtChars);
    }

    /// <summary>The rendered text is over the package's internal ceiling. Nothing was stored.</summary>
    public sealed class TooLongException(int charCount)
        : InvalidOperationException(
            $"The LinkedIn copy is {charCount} characters, over this product's own "
            + $"{MaxCopyChars}-character ceiling. Shorten the call to action, use fewer "
            + "hashtags, or adapt a story with less text.")
    {
        public int CharCount { get; } = charCount;
    }

    private static string CiteFirst(GroundedSummary summary, IReadOnlyList<DraftClaim> claims)
    {
        var first = claims.Count > 0 ? claims[0] : null;
        return first is null ? string.Empty : $"[^{first.Id}]";
    }
}
