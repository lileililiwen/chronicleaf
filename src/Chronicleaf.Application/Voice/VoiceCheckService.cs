using System.Globalization;
using System.Text;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Voice;

/// <summary>A span a finding points at, already expressed as an offset and a length.</summary>
internal readonly record struct VoiceFindingSpan(int Start, int Length);

/// <summary>
/// The deterministic voice checker (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// <para>
/// This is rule evaluation, not a model. It reads the resolved values of a voice and a piece of
/// text, and it returns findings. It calls nothing, opens nothing, and reaches no network, so the
/// same profile and the same text always produce the same findings - which is what makes "no
/// violation was reported" a statement a test can make and a reviewer can rely on.
/// </para>
/// <para>
/// The hard boundary of this type: it <em>reports</em>, it never <em>rewrites</em>. The text it is
/// given is not mutated, trimmed, normalized or re-encoded, and no finding carries a replacement
/// value. An owner whose draft breaks a rule is told which rule, which run of their own characters
/// and which configured term matched, and then the text is left exactly as they wrote it, because
/// silently editing somebody's words to satisfy a style rule is a different product with a
/// different risk profile.
/// </para>
/// <para>
/// Every rule the design lists is evaluated here, each producing a finding that names what it
/// flagged: forbidden terms, emoji against a profile that disallows them, the title's character
/// budget, and the declared language. Findings are ordered by field, then by rule in the fixed
/// order of <see cref="VoiceFindingRules.All"/>, then by offset, so the same input always yields
/// the same list in the same order.
/// </para>
/// </remarks>
public static class VoiceCheckService
{
    private static readonly string[] FieldOrder = [VoiceFindingRules.TitleField, VoiceFindingRules.BodyField];

    /// <summary>
    /// Judges a title and a body against a resolved voice, and returns every finding.
    /// </summary>
    /// <param name="values">The effective voice, already resolved from preset and overrides.</param>
    /// <param name="title">The title, examined as written.</param>
    /// <param name="body">The body, examined as written.</param>
    public static IReadOnlyList<VoiceFinding> Check(VoiceValues values, string? title, string? body)
    {
        ArgumentNullException.ThrowIfNull(values);

        var fields = new (string Name, string Text)[]
        {
            (VoiceFindingRules.TitleField, title ?? string.Empty),
            (VoiceFindingRules.BodyField, body ?? string.Empty),
        };

        var findings = new List<VoiceFinding>();

        foreach (var field in FieldOrder.Select(name => fields.First(entry => entry.Name == name)))
        {
            CheckForbiddenTerms(values, field.Name, field.Text, findings);
            CheckEmoji(values, field.Name, field.Text, findings);
            CheckTitleBudget(values, field.Name, field.Text, findings);
            CheckLanguage(values, field.Name, field.Text, findings);
        }

        return findings;
    }

    /// <summary>
    /// Wraps a value in double quotes for a message. Written as a helper rather than as a backslash
    /// escape because an escape does not exist inside an interpolated string - a reader looking for
    /// what a finding says about a term should find the term, not the quoting.
    /// </summary>
    private static string Quote(string value) => $"\"{value}\"";

    private static void CheckForbiddenTerms(
        VoiceValues values,
        string field,
        string text,
        List<VoiceFinding> findings)
    {
        foreach (var term in values.ForbiddenTerms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                // A blank term matches everywhere and would report the whole text. The write path
                // refuses it; skipping it here keeps a hand-edited row from producing nonsense.
                continue;
            }

            var spans = VoiceText.FindAll(text, term, out var normalized);

            foreach (var (start, length) in spans)
            {
                findings.Add(new VoiceFinding(
                    Rule: VoiceFindingRules.ForbiddenTerm,
                    Field: field,
                    Start: start,
                    Length: length,
                    Excerpt: VoiceText.Excerpt(text, start, length),
                    Term: term,
                    NormalizedTerm: normalized,
                    Message: string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {field} uses the forbidden term {Quote(term)}. Replace it with your own wording.")));
            }
        }
    }

    private static void CheckEmoji(
        VoiceValues values,
        string field,
        string text,
        List<VoiceFinding> findings)
    {
        if (values.Emoji)
        {
            // The profile allows emoji, so there is no rule to violate and nothing to report.
            return;
        }

        var span = VoiceText.FirstEmojiSpan(text);
        if (span is not { } match)
        {
            return;
        }

        var (start, length) = match;
        var excerpt = VoiceText.Excerpt(text, start, length);

        findings.Add(new VoiceFinding(
            Rule: VoiceFindingRules.EmojiNotAllowed,
            Field: field,
            Start: start,
            Length: length,
            Excerpt: excerpt,
            Term: excerpt,
            NormalizedTerm: excerpt.Normalize(NormalizationForm.FormC),
            Message: string.Create(
                CultureInfo.InvariantCulture,
                $"The {field} contains the emoji {Quote(excerpt)} and this voice has emoji turned off.")));
    }

    private static void CheckTitleBudget(
        VoiceValues values,
        string field,
        string text,
        List<VoiceFinding> findings)
    {
        if (field != VoiceFindingRules.TitleField)
        {
            return;
        }

        var overflow = VoiceText.OverflowSpan(text, values.TitleMaxChars);
        if (overflow is not { } match)
        {
            return;
        }

        var (start, length) = match;
        var budget = values.TitleMaxChars.ToString(CultureInfo.InvariantCulture);

        findings.Add(new VoiceFinding(
            Rule: VoiceFindingRules.TitleTooLong,
            Field: field,
            Start: start,
            Length: length,
            Excerpt: VoiceText.Excerpt(text, start, length),
            Term: budget,
            NormalizedTerm: budget,
            Message: string.Create(
                CultureInfo.InvariantCulture,
                $"The title runs past the {budget} characters this voice allows, starting at character {values.TitleMaxChars + 1}.")));
    }

    private static void CheckLanguage(
        VoiceValues values,
        string field,
        string text,
        List<VoiceFinding> findings)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var hanCount = VoiceText.CountCharacters(text, VoiceText.IsHan);
        var latinCount = VoiceText.CountCharacters(text, VoiceText.IsLatinLetter);

        switch (values.Language)
        {
            // A profile that declares Chinese and reads with no Han character in it at all is the
            // one mismatch that is unambiguous, so the span is the whole field: there is no smaller
            // offending run to point at.
            case "zh" when hanCount == 0:
                AddLanguageFinding(
                    values.Language,
                    field,
                    text,
                    new VoiceFindingSpan(0, text.Length),
                    "zh",
                    "no Han characters",
                    latinCount,
                    findings);
                break;

            // An English profile that reads with Han in it points at the first run of Han rather
            // than at the whole field, because that run is what an editor has to look at.
            case "en" when hanCount > 0:
                AddLanguageFinding(
                    values.Language,
                    field,
                    text,
                    ToSpan(VoiceText.FirstRunSpan(text, VoiceText.IsHan), text.Length),
                    "zh",
                    string.Create(CultureInfo.InvariantCulture, $"{hanCount} Han characters"),
                    latinCount,
                    findings);
                break;

            case "en" when latinCount == 0:
                AddLanguageFinding(
                    values.Language,
                    field,
                    text,
                    new VoiceFindingSpan(0, text.Length),
                    "en",
                    "no Latin letters",
                    latinCount,
                    findings);
                break;
        }
    }

    private static VoiceFindingSpan ToSpan((int Start, int Length)? span, int textLength) =>
        span is { } match ? new VoiceFindingSpan(match.Start, match.Length) : new VoiceFindingSpan(0, textLength);

    private static void AddLanguageFinding(
        string declared,
        string field,
        string text,
        VoiceFindingSpan span,
        string term,
        string observed,
        int latinCount,
        List<VoiceFinding> findings)
    {
        var excerpt = VoiceText.Excerpt(text, span.Start, span.Length);

        findings.Add(new VoiceFinding(
            Rule: VoiceFindingRules.LanguageMismatch,
            Field: field,
            Start: span.Start,
            Length: span.Length,
            Excerpt: excerpt,
            Term: term,
            NormalizedTerm: term,
            Message: string.Create(
                CultureInfo.InvariantCulture,
                $"This voice declares {declared} and the {field} has {observed} in it ({latinCount} Latin characters). This is a script check, not a judgement about meaning.")));
    }
}
