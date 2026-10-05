using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Core;

namespace Chronicleaf.Application.ContentVariants;

/// <summary>
/// The deterministic templates that render an approved story's accepted summary into reusable
/// formats (CONTENT-ASSET-VARIANTS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure: no IO, no clock, no provider.</b> The inputs are the source revision's stored summary,
/// its claim map, the milestone title and the already-resolved effective voice. The outputs are a
/// title and a Markdown body that cite only the footnote ids the claim map assigned: a claim that
/// cannot be cited is a section that reads the unknown label instead.
/// </para>
/// <para>
/// <b>Every section the design names is present in every format that names it.</b> A section with
/// no source claims is not omitted silently and not filled inventively: it carries
/// <see cref="UnknownLabel"/>, which is the requirement's "missing fields omitted or labeled
/// unknown, never fabricated numbers" made visible. The footnote definitions close every body, so
/// a citation is never a reference to nothing.
/// </para>
/// <para>
/// <b>Voice is applied, not consulted.</b> The title is cut to the effective
/// <c>TitleMaxChars</c>, so a project override that shortens the preset's limit shortens the
/// variant's title: the project setting wins structurally, because the preset value never reaches
/// this code. The framing sentences carry no pronouns, so they pass under either first-person
/// setting rather than preferring one.
/// </para>
/// </remarks>
public static class ContentVariantRenderer
{
    /// <summary>
    /// The template version recorded as the revision's prompt version, so a stored variant names
    /// the templates that rendered it.
    /// </summary>
    public const string TemplateVersion = "content-variants/1";

    /// <summary>
    /// What a section without source claims reads. Digit-free on purpose: the numeral rule refuses
    /// a figure that appears in no evidence item, and a label with a number in it would trip the
    /// very check that proves nothing was invented.
    /// </summary>
    public const string UnknownLabel = "Unknown — no source evidence recorded.";

    /// <summary>
    /// The notice every readme-update body opens with. A proposal block only: this package holds no
    /// repository reference and performs no write, so the notice is structural rather than
    /// aspirational.
    /// </summary>
    public const string ReadmeNotice =
        "> README proposal — no repository was changed. Apply these words by hand.";

    /// <summary>The bound a social-short body keeps its prose within, footnote definitions aside.</summary>
    public const int SocialShortMaxChars = 500;

    /// <summary>Renders one variant format. Unknown types are refused before any text exists.</summary>
    public static (string Title, string Body) Render(
        string type,
        string milestoneTitle,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        VoiceValues voice,
        IReadOnlyList<(string Section, string Text)> history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(voice);

        if (!ContentVariantTypes.IsVariant(type))
        {
            throw new ArgumentException(
                $"The content format '{type}' is not one this product derives.",
                nameof(type));
        }

        var title = ApplyTitleBound(milestoneTitle, voice.TitleMaxChars);

        var body = type switch
        {
            ContentVariantTypes.Changelog => Changelog(title, summary, claims, history),
            ContentVariantTypes.TechnicalBlog => TechnicalBlog(title, summary, claims, history),
            ContentVariantTypes.SocialShort => SocialShort(title, summary, claims, history),
            ContentVariantTypes.GithubRelease => GithubRelease(title, summary, claims, history),
            ContentVariantTypes.ReadmeUpdate => ReadmeUpdate(title, summary, claims, history),
            ContentVariantTypes.Portfolio => Portfolio(title, summary, claims, history),
            ContentVariantTypes.WeeklyProgress => WeeklyProgress(title, summary, claims, history),
            _ => throw new ArgumentException(
                $"The content format '{type}' is not one this product derives.",
                nameof(type)),
        };

        return (title, body);
    }

    /// <summary>Cuts a title to the effective voice limit, so the project override always wins.</summary>
    public static string ApplyTitleBound(string title, int maxChars)
    {
        var trimmed = (title ?? string.Empty).Trim();
        var bound = Math.Max(4, maxChars);

        if (trimmed.Length <= bound)
        {
            return trimmed;
        }

        return trimmed[..(bound - 1)].TrimEnd() + "…";
    }

    private static string Changelog(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n");
        body.Append(Section("Added", summary, GroundedSummarySection.Changes, claims));
        body.Append(UnknownSection("Improved"));
        body.Append(Section("Fixed", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string TechnicalBlog(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n");
        body.Append("\n## Why\n").Append(SummaryLine(summary, claims)).Append('\n');
        body.Append(Section("Problem", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(Section("Solution", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Effect", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Limitations", summary, GroundedSummarySection.KnownLimitations, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string SocialShort(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        // Concise on purpose: the title, the accepted summary sentence and the first recorded
        // change, each with the citation the claim map assigned. When that does not fit the bound,
        // the change is dropped before the summary is, because the summary is what the story is
        // about and a truncated citation would be a reference to nothing.
        var firstChange = summary.Changes.Count > 0
            ? CiteChange(summary, claims) + "\n"
            : string.Empty;
        var candidate = $"# {title}\n{summary.Summary}{CiteFirst(summary, claims)}\n{firstChange}";

        if (candidate.Length > SocialShortMaxChars && firstChange.Length > 0)
        {
            candidate = $"# {title}\n{summary.Summary}{CiteFirst(summary, claims)}\n";
        }

        var body = new StringBuilder(candidate);
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string GithubRelease(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n");
        body.Append("\n## What's changed\n").Append(SummaryLine(summary, claims)).Append('\n');
        body.Append(Section("Added", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Fixed", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string ReadmeUpdate(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        // A fenced proposal block under an explicit notice. The fence keeps the suggested words
        // visibly separate from the variant's own sections, and the notice says what did not
        // happen: no repository reference is held here, so no write could have occurred.
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n\n").Append(ReadmeNotice).Append("\n");
        body.Append("\n```proposal\n");
        body.Append(title).Append('\n');
        body.Append(summary.Summary).Append(CiteFirst(summary, claims)).Append('\n');
        for (var changeIndex = 0; changeIndex < summary.Changes.Count; changeIndex++)
        {
            var mapped = claims.SingleOrDefault(candidate =>
                candidate.Section == GroundedSummarySection.Changes && candidate.Index == changeIndex);
            var citation = mapped is null ? string.Empty : $"[^{mapped.Id}]";
            body.Append("- ").Append(summary.Changes[changeIndex].Text).Append(citation).Append('\n');
        }

        body.Append("```\n");
        body.Append(Section("Source changes", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Problems addressed", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string Portfolio(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n");
        body.Append("\n## Context\n").Append(SummaryLine(summary, claims)).Append('\n');
        body.Append(Section("Contribution", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Result", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(Section("What is next", summary, GroundedSummarySection.NextSteps, claims));
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string WeeklyProgress(
        string title,
        GroundedSummary summary,
        IReadOnlyList<DraftClaim> claims,
        IReadOnlyList<(string Section, string Text)> history)
    {
        // Project-local by construction: the only inputs are this milestone's accepted summary and
        // this project's narrative lines. Client aggregation across projects belongs to the agency
        // report package and has no input here.
        var body = new StringBuilder();
        body.Append("# ").Append(title).Append("\n");
        body.Append(Section("Completed", summary, GroundedSummarySection.Changes, claims));
        body.Append(Section("Resolved", summary, GroundedSummarySection.ProblemsSolved, claims));
        body.Append(UnknownSection("In progress"));
        body.Append(Section("Next week", summary, GroundedSummarySection.NextSteps, claims));
        body.Append(Limitations(summary, claims));
        body.Append(History(history));
        body.Append(Footnotes(claims));
        return body.ToString();
    }

    private static string SummaryLine(GroundedSummary summary, IReadOnlyList<DraftClaim> claims) =>
        summary.Summary + CiteFirst(summary, claims);

    private static string CiteChange(GroundedSummary summary, IReadOnlyList<DraftClaim> claims)
    {
        var mapped = claims.SingleOrDefault(candidate =>
            candidate.Section == GroundedSummarySection.Changes && candidate.Index == 0);
        return mapped is null
            ? summary.Changes[0].Text
            : summary.Changes[0].Text + $"[^{mapped.Id}]";
    }

    private static string CiteFirst(GroundedSummary summary, IReadOnlyList<DraftClaim> claims)
    {
        var first = claims.Count > 0 ? claims[0] : null;
        return first is null ? string.Empty : $"[^{first.Id}]";
    }

    private static string Section(
        string heading,
        GroundedSummary summary,
        GroundedSummarySection section,
        IReadOnlyList<DraftClaim> claims)
    {
        var body = new StringBuilder();
        body.Append("\n## ").Append(heading).Append('\n');

        var items = summary.ClaimsIn(section);
        if (items.Count == 0)
        {
            body.Append(UnknownLabel).Append('\n');
            return body.ToString();
        }

        for (var index = 0; index < items.Count; index++)
        {
            var mapped = claims.SingleOrDefault(candidate =>
                candidate.Section == section && candidate.Index == index);
            var citation = mapped is null ? string.Empty : $"[^{mapped.Id}]";
            body.Append("- ").Append(items[index].Text).Append(citation).Append('\n');
        }

        return body.ToString();
    }

    private static string UnknownSection(string heading) =>
        $"\n## {heading}\n{UnknownLabel}\n";

    private static string Limitations(GroundedSummary summary, IReadOnlyList<DraftClaim> claims) =>
        Section("Known limitations", summary, GroundedSummarySection.KnownLimitations, claims);

    private static string History(IReadOnlyList<(string Section, string Text)> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        // Attributed lines from the project's narrative memory, copied verbatim. The source entry
        // ids travel in the audit row rather than here: an id is a run of characters the numeral
        // rule would read as a figure appearing in no evidence item.
        var body = new StringBuilder();
        body.Append("\n## Project history\n");

        foreach (var line in history)
        {
            body.Append("- ").Append(line.Text).Append(" (from project history)\n");
        }

        return body.ToString();
    }

    private static string Footnotes(IReadOnlyList<DraftClaim> claims)
    {
        var body = new StringBuilder();
        body.Append('\n');

        foreach (var claim in claims)
        {
            body.Append("[^").Append(claim.Id).Append("]: ").Append(claim.Text).Append('\n');
        }

        return body.ToString();
    }

    // Footnote definitions close every body, so a citation is never a reference to nothing.
}
