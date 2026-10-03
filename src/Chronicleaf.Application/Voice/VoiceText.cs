using System.Globalization;
using System.Text;

namespace Chronicleaf.Application.Voice;

/// <summary>
/// One run of the text, carrying where it actually sits in the original string
/// (VOICE-PROFILES-002).
/// </summary>
/// <param name="Start">Index of the run's first UTF-16 code unit in the original text.</param>
/// <param name="Length">The run's length in the original text, before any folding.</param>
/// <param name="Folded">The run in NFC, which is the form matching compares.</param>
internal sealed record VoiceTextRun(int Start, int Length, string Folded);

/// <summary>
/// Normalization and span arithmetic for the checker (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// <para>
/// The one problem this type exists to solve: normalizing a string can change its length, so a
/// naive <c>haystack.Normalize().IndexOf(needle)</c> reports an offset into a string the caller
/// never had. A composed <c>e</c> plus a combining acute is one code point where the stored text
/// had two code units, and every span after that point would be short by one.
/// </para>
/// <para>
/// So the text is split into grapheme clusters, each is folded to NFC on its own, and the original
/// start and length of every cluster are carried alongside. A match is then a range of clusters,
/// and the span is the range's first start through the last cluster's end - an offset into the
/// text the owner actually typed, in the text they actually typed it in.
/// </para>
/// <para>
/// Folding per cluster is sufficient rather than approximate because NFC never composes across a
/// cluster boundary, so the concatenation of a cluster range's folded forms is the folded form of
/// that range.
/// </para>
/// </remarks>
internal static class VoiceText
{
    /// <summary>How much of a span an excerpt may copy out, so one finding cannot return a whole body.</summary>
    public const int ExcerptMaxLength = 120;

    /// <summary>
    /// Splits text into grapheme clusters, each folded to NFC and paired with its original span.
    /// </summary>
    public static IReadOnlyList<VoiceTextRun> Runs(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var runs = new List<VoiceTextRun>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var start = 0;

        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current!;
            runs.Add(new VoiceTextRun(start, element.Length, element.Normalize(NormalizationForm.FormC)));
            start += element.Length;
        }

        return runs;
    }

    /// <summary>
    /// The number of characters in a text, counted the way an author counts them: one per grapheme
    /// cluster, so a title budget of 60 is 60 characters and not 60 code units.
    /// </summary>
    public static int CountCharacters(string? text) => Runs(text).Count;

    /// <summary>
    /// Every non-overlapping occurrence of <paramref name="needle"/> in <paramref name="text"/>,
    /// as spans into the original text.
    /// </summary>
    /// <remarks>
    /// Case-insensitive by ordinal comparison, which is what makes a forbidden term found in a
    /// different case a finding rather than a miss, while leaving Han characters - which have no
    /// case - compared exactly. The needle is folded first so a term stored with decomposed accents
    /// still matches text composed the other way round.
    /// <para>
    /// Occurrences are leftmost and non-overlapping: after a match the scan resumes past it, so a
    /// term that overlaps itself is reported once per occurrence rather than once per suffix.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(int Start, int Length)> FindAll(
        string? text,
        string? needle,
        out string normalizedNeedle)
    {
        normalizedNeedle = (needle ?? string.Empty).Normalize(NormalizationForm.FormC);

        if (string.IsNullOrEmpty(text) || normalizedNeedle.Length == 0)
        {
            return [];
        }

        var runs = Runs(text);
        var spans = new List<(int, int)>();

        for (var from = 0; from < runs.Count; from++)
        {
            var matched = TryMatchAt(runs, from, normalizedNeedle, out var length);
            if (matched)
            {
                spans.Add((runs[from].Start, length));
                from = ClusterIndexAt(runs, runs[from].Start + length) - 1;
            }
        }

        return spans;
    }

    /// <summary>
    /// The first maximal run of clusters that each contain an emoji rune, as a span into the
    /// original text; or <c>null</c> when the text holds none. Grouping consecutive emoji into one
    /// span is what makes a ZWJ sequence or a flag report as the single character it is drawn as.
    /// </summary>
    public static (int Start, int Length)? FirstEmojiSpan(string? text)
    {
        var runs = Runs(text);
        var open = -1;
        var lastEnd = -1;

        foreach (var run in runs)
        {
            if (ContainsEmoji(run))
            {
                if (open < 0)
                {
                    open = run.Start;
                }

                lastEnd = run.Start + run.Length;
            }
            else if (open >= 0)
            {
                return (open, lastEnd - open);
            }
        }

        return open < 0 ? null : (open, lastEnd - open);
    }

    /// <summary>
    /// The first maximal run of clusters containing a rune that satisfies
    /// <paramref name="predicate"/>, as a span into the original text; or <c>null</c>.
    /// </summary>
    public static (int Start, int Length)? FirstRunSpan(string? text, Func<int, bool> predicate)
    {
        var runs = Runs(text);
        var open = -1;
        var lastEnd = -1;

        foreach (var run in runs)
        {
            if (AnyRune(run, predicate))
            {
                if (open < 0)
                {
                    open = run.Start;
                }

                lastEnd = run.Start + run.Length;
            }
            else if (open >= 0)
            {
                return (open, lastEnd - open);
            }
        }

        return open < 0 ? null : (open, lastEnd - open);
    }

    /// <summary>How many characters of a text satisfy <paramref name="predicate"/>.</summary>
    public static int CountCharacters(string? text, Func<int, bool> predicate)
    {
        var total = 0;

        foreach (var run in Runs(text))
        {
            if (AnyRune(run, predicate))
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>
    /// The span from the first character past <paramref name="budget"/> to the end of the text, so
    /// a length finding points at the overflow rather than at the whole title.
    /// </summary>
    public static (int Start, int Length)? OverflowSpan(string? text, int budget)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var runs = Runs(text);
        if (runs.Count <= budget)
        {
            return null;
        }

        var start = runs[budget].Start;
        return (start, text.Length - start);
    }

    /// <summary>Copies a span out of the text verbatim, bounded to <see cref="ExcerptMaxLength"/>.</summary>
    public static string Excerpt(string? text, int start, int length)
    {
        if (string.IsNullOrEmpty(text) || start < 0 || start >= text.Length || length <= 0)
        {
            return string.Empty;
        }

        var take = Math.Min(length, ExcerptMaxLength);
        take = Math.Min(take, text.Length - start);

        return text.Substring(start, take);
    }

    /// <summary>True when a text-element carries a Han ideograph, a Han radical or a CJK symbol.</summary>
    public static bool IsHan(int rune) =>
        rune is >= 0x3400 and <= 0x4DBF or     // Extension A
            >= 0x4E00 and <= 0x9FFF or         // Unified Ideographs
            >= 0xF900 and <= 0xFAFF or         // Compatibility Ideographs
            >= 0x3005 and <= 0x3007 or         // Ideographic iteration and number marks
            >= 0x3040 and <= 0x30FF or         // Kana, treated with the Japanese/Chinese text
            >= 0x31C0 and <= 0x31EF or         // CJK strokes
            >= 0x20000 and <= 0x2FA1F;         // Extensions B..F and the compatibility supplement

    /// <summary>
    /// True when a rune is one of the scripts a Latin-language profile expects to find. Greek and
    /// Cyrillic are deliberately not counted: a name borrowed from either still reads as a Latin
    /// sentence, and counting them would make the check claim more than it can see.
    /// </summary>
    public static bool IsLatinLetter(int rune) =>
        rune is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= 0x00C0 and <= 0x024F or           // Latin-1 supplement and Latin Extended-A/B
            >= 0x1E00 and <= 0x1EFF;            // Latin Extended Additional

    private static bool TryMatchAt(
        IReadOnlyList<VoiceTextRun> runs,
        int from,
        string needle,
        out int length)
    {
        var accumulated = new StringBuilder();
        length = 0;

        for (var index = from; index < runs.Count; index++)
        {
            accumulated.Append(runs[index].Folded);

            if (accumulated.Length < needle.Length)
            {
                continue;
            }

            if (accumulated.ToString().Normalize(NormalizationForm.FormC)
                .Equals(needle, StringComparison.OrdinalIgnoreCase))
            {
                length = runs[index].Start + runs[index].Length - runs[from].Start;
                return true;
            }

            // One character past the needle's length is already too long to equal it, and no later
            // cluster can make it shorter, so this start position cannot match.
            return false;
        }

        return false;
    }

    private static int ClusterIndexAt(IReadOnlyList<VoiceTextRun> runs, int originalIndex)
    {
        for (var index = 0; index < runs.Count; index++)
        {
            if (runs[index].Start == originalIndex)
            {
                return index;
            }
        }

        return runs.Count;
    }

    private static bool ContainsEmoji(VoiceTextRun run) => AnyRune(run, VoiceEmoji.IsEmoji);

    private static bool AnyRune(VoiceTextRun run, Func<int, bool> predicate)
    {
        var value = run.Folded;
        var found = false;

        for (var index = 0; index < value.Length && !found;)
        {
            System.Text.Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed);
            index += Math.Max(consumed, 1);
            found = predicate(rune.Value);
        }

        return found;
    }
}

/// <summary>
/// The emoji blocks this build treats as emoji (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// The set is explicit rather than derived, and it is deliberately narrower than the full Unicode
/// emoji list. .NET ships no emoji-property lookup, and a checker that flagged every symbol in
/// U+2600..U+26FF would report an ordinary caution sign or an arrow as a voice violation, which
/// would make the <c>emoji: false</c> default - the default every profile starts from - unusable.
/// The ranges below are the pictographic, emoticon, transport, supplemental, extended-A, mahjong,
/// playing-card, regional-indicator, dingbat and miscellaneous-symbol blocks, which is where
/// ✨, ✅, 🚀 and 🎉 live.
/// <para>
/// Variation selectors, zero-width joiners and the tag characters are excluded: they join or
/// qualify a pictograph, they are not one, and counting them would report half a character.
/// </para>
/// </remarks>
internal static class VoiceEmoji
{
    public static bool IsEmoji(int rune) =>
        rune is >= 0x1F000 and <= 0x1F0FF or     // Mahjong tiles and playing cards
            >= 0x1F1E6 and <= 0x1F1FF or         // Regional indicators, which pair into flags
            >= 0x1F300 and <= 0x1F5FF or         // Miscellaneous symbols and pictographs, emoticons
            >= 0x1F600 and <= 0x1F64F or         // Emoticons
            >= 0x1F680 and <= 0x1F6FF or         // Transport and map symbols
            >= 0x1F900 and <= 0x1F9FF or         // Supplemental symbols and pictographs
            >= 0x1FA70 and <= 0x1FAFF or         // Symbols and pictographs extended-A
            >= 0x2700 and <= 0x27BF or           // Dingbats
            >= 0x2B00 and <= 0x2BFF;             // Miscellaneous symbols and arrows
}
