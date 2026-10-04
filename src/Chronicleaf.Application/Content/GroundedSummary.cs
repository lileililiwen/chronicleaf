using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The bounds one structured summary runs inside (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// These are constants rather than settings for the reason the generation contract's own limits are:
/// each one is a safety property, not a preference. An owner who could raise the item ceiling could
/// ask one call to enumerate five hundred separate claims; an owner who could raise the string ceiling
/// could ask one call to reproduce the whole evidence payload back.
/// </remarks>
public static class ContentLimits
{
    /// <summary>
    /// At most this many items in one factual array. The design fixes 100, and it is a bound on
    /// <em>each</em> array rather than on their sum, so a summary of four arrays at the ceiling is a
    /// real possibility and is still inside the call's own output-token ceiling.
    /// </summary>
    public const int MaxItemsPerArray = 100;

    /// <summary>
    /// At most this many characters in any one summary string, including the prose fields. The design
    /// fixes 8000: long enough for a paragraph of a real milestone note, short enough that a summary
    /// cannot become a second copy of the evidence payload it was supposed to be drawn from.
    /// </summary>
    public const int MaxStringLength = 8000;

    /// <summary>At most this many characters in the summary's own title field.</summary>
    public const int MaxTitleLength = 160;

    /// <summary>
    /// How many provider calls one phase is allowed to make.
    /// </summary>
    /// <remarks>
    /// Two, because the design permits exactly one repair: the first answer, and one more after the
    /// system has told the provider precisely which part of its own schema it broke. A third call
    /// would be a third thing the owner may be billed for, spent on the same broken answer.
    /// </remarks>
    public const int MaxSummaryCalls = 2;

    /// <summary>
    /// How many repair calls the whole draft may spend. One, and it is only ever spent on a summary
    /// that broke its schema.
    /// </summary>
    public const int MaxRepairCalls = 1;

    /// <summary>
    /// The instruction version this package records on every revision it writes.
    /// </summary>
    /// <remarks>
    /// Bumped when the phase-1 or phase-2 instruction text changes in a way that could change a
    /// draft's wording. A stored revision names the version that made it, so a reader months later can
    /// tell which instruction was used rather than inferring it from the output - and a stored
    /// revision whose instruction was quietly rewritten under it would otherwise be indistinguishable
    /// from one that was regenerated.
    /// </remarks>
    public const string PromptVersion = "grounded-markdown/1";

    /// <summary>The structured-summary schema version recorded on every revision.</summary>
    public const string SummarySchemaVersion = "grounded-summary/1";
}

/// <summary>
/// One factual statement in a structured summary, with the evidence it says it came from
/// (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <param name="Text">The claim, in the provider's own words.</param>
/// <param name="EvidenceIds">
/// The evidence item ids this claim is based on. Every id must be inside the frozen snapshot; one
/// that is not is a refusal rather than a finding, because a claim pointing at material the owner
/// never authorized is the whole failure this package exists to prevent.
/// </param>
public sealed record GroundedClaim(string Text, IReadOnlyList<Guid> EvidenceIds);

/// <summary>
/// The accepted intermediate representation, before any prose exists
/// (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// <para>
/// This is the only thing phase 1 is allowed to produce, and the only thing phase 2 is allowed to use.
/// The order is the requirement: <b>structured content is validated against the frozen evidence
/// snapshot before any Markdown is created</b>, so a model that invents something loses at the
/// summary stage where the invented thing is still a field a validator can read, rather than at the
/// prose stage where it is a sentence in a document an owner is about to believe.
/// </para>
/// <para>
/// It is a value with no behaviour beyond its own bounds. Nothing here resolves an evidence id, and
/// nothing here fetches anything: the ids are compared against the snapshot's set and that is the
/// whole of the membership question.
/// </para>
/// </remarks>
/// <param name="MilestoneTitle">The title the draft will carry.</param>
/// <param name="Summary">The prose summary, in the provider's words.</param>
/// <param name="Changes">What changed.</param>
/// <param name="ProblemsSolved">What problem that solved.</param>
/// <param name="KnownLimitations">
/// What is known not to work yet. A limitation that reaches this record is a limitation the body is
/// then required to carry, because a draft that silently drops a caveat is worse than no draft.
/// </param>
/// <param name="NextSteps">What comes next.</param>
public sealed record GroundedSummary(
    string MilestoneTitle,
    string Summary,
    IReadOnlyList<GroundedClaim> Changes,
    IReadOnlyList<GroundedClaim> ProblemsSolved,
    IReadOnlyList<GroundedClaim> KnownLimitations,
    IReadOnlyList<GroundedClaim> NextSteps)
{
    /// <summary>
    /// The four factual arrays, in the fixed order the claim map walks them. The order is written out
    /// as literals rather than reflected from a property, so renaming a member cannot silently
    /// reorder every footnote id a stored draft cites.
    /// </summary>
    public static IReadOnlyList<GroundedSummarySection> Sections { get; } =
    [
        GroundedSummarySection.Changes,
        GroundedSummarySection.ProblemsSolved,
        GroundedSummarySection.KnownLimitations,
        GroundedSummarySection.NextSteps,
    ];

    /// <summary>The claims of one section, by name.</summary>
    public IReadOnlyList<GroundedClaim> ClaimsIn(GroundedSummarySection section) => section switch
    {
        GroundedSummarySection.Changes => Changes,
        GroundedSummarySection.ProblemsSolved => ProblemsSolved,
        GroundedSummarySection.KnownLimitations => KnownLimitations,
        GroundedSummarySection.NextSteps => NextSteps,
        _ => [],
    };

    /// <summary>
    /// The canonical JSON of this summary, which is what the revision stores.
    /// </summary>
    /// <remarks>
    /// Built here rather than by a serializer over the record, for two reasons. The member names are
    /// the wire names a provider is asked for, so a renamed property cannot change the stored shape
    /// without a deliberate edit; and the member order is fixed, so the same summary always produces
    /// the same bytes and a stored document can be compared with a byte comparison.
    /// </remarks>
    public string ToCanonicalJson()
    {
        var document = new JsonObject
        {
            ["milestoneTitle"] = MilestoneTitle,
            ["summary"] = Summary,
        };

        foreach (var section in Sections)
        {
            var array = new JsonArray();

            foreach (var claim in ClaimsIn(section))
            {
                var ids = new JsonArray();
                foreach (var id in claim.EvidenceIds)
                {
                    ids.Add(id.ToString("d"));
                }

                array.Add(new JsonObject
                {
                    ["text"] = claim.Text,
                    ["evidenceIds"] = ids,
                });
            }

            document[SectionKey(section)] = array;
        }

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>Reads a stored summary back, or answers <c>null</c> when the document is not one.</summary>
    /// <remarks>
    /// A row this cannot read is a row this build will not pretend to understand. Returning
    /// <c>null</c> rather than a partly-read summary matters because the alternative is a draft whose
    /// provenance record has quietly lost the limitations it was written with.
    /// </remarks>
    public static GroundedSummary? FromCanonicalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject document)
        {
            return null;
        }

        var title = ReadString(document, "milestoneTitle");
        var summary = ReadString(document, "summary");

        if (title is null || summary is null)
        {
            return null;
        }

        var changes = ReadClaims(document, SectionKey(GroundedSummarySection.Changes));
        var problems = ReadClaims(document, SectionKey(GroundedSummarySection.ProblemsSolved));
        var limitations = ReadClaims(document, SectionKey(GroundedSummarySection.KnownLimitations));
        var steps = ReadClaims(document, SectionKey(GroundedSummarySection.NextSteps));

        if (changes is null || problems is null || limitations is null || steps is null)
        {
            return null;
        }

        return new GroundedSummary(title, summary, changes, problems, limitations, steps);
    }

    /// <summary>
    /// The wire name of a section, which is the name the provider is asked for and the name the
    /// stored document uses.
    /// </summary>
    public static string SectionKey(GroundedSummarySection section) => section switch
    {
        GroundedSummarySection.Changes => "changes",
        GroundedSummarySection.ProblemsSolved => "problemsSolved",
        GroundedSummarySection.KnownLimitations => "knownLimitations",
        GroundedSummarySection.NextSteps => "nextSteps",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Not a summary section."),
    };

    private static string? ReadString(JsonObject document, string member) =>
        document[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IReadOnlyList<GroundedClaim>? ReadClaims(JsonObject document, string member)
    {
        if (document[member] is not JsonArray array)
        {
            return null;
        }

        var claims = new List<GroundedClaim>(array.Count);

        foreach (var node in array)
        {
            if (node is not JsonObject item ||
                ReadString(item, "text") is not { } text ||
                item["evidenceIds"] is not JsonArray rawIds)
            {
                return null;
            }

            var ids = new List<Guid>(rawIds.Count);
            foreach (var idNode in rawIds)
            {
                if (idNode is JsonValue idValue &&
                    idValue.TryGetValue<string>(out var idText) &&
                    Guid.TryParse(idText, out var id))
                {
                    ids.Add(id);
                }
                else
                {
                    return null;
                }
            }

            claims.Add(new GroundedClaim(text, ids));
        }

        return claims;
    }
}

/// <summary>The four factual sections of a structured summary, in the order the claim map walks them.</summary>
/// <remarks>
/// An enum rather than four string constants, because a section is a position in a fixed walk and a
/// string is a value a caller could spell two ways. The wire and stored names are given by
/// <see cref="GroundedSummary.SectionKey"/>, which is the only place a spelling exists - so a
/// renaming of the stored member is a one-line change here rather than a search.
/// </remarks>
public enum GroundedSummarySection
{
    /// <summary>What changed.</summary>
    Changes,

    /// <summary>What problem that change solved.</summary>
    ProblemsSolved,

    /// <summary>What is known not to work yet.</summary>
    KnownLimitations,

    /// <summary>What comes next.</summary>
    NextSteps,
}

/// <summary>
/// The strict schema a structured summary must satisfy, and the validator that enforces it
/// (GROUNDED-MARKDOWN-DRAFTS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is the same document the provider is given and the same shape this validates.</b>
/// They are built from one place rather than written twice, because a prompt that asks for a shape and
/// a validator that checks a different one is a system that fails for reasons its own error messages
/// cannot explain - and the failure looks like a model that ignored the instruction when in fact the
/// instruction and the check disagreed.
/// </para>
/// <para>
/// <b>Validation runs locally whatever the provider claims.</b> The delivered generation contract skips
/// its local validator when an adapter reports native schema enforcement, because for its own
/// milestone-story schema that is a reasonable trade. It is not reasonable here: two of the checks in
/// this file are about membership in a frozen evidence snapshot, and a provider enforcing a JSON shape
/// is not a provider enforcing which ids exist. So the capability is passed in only to be reported -
/// never to switch the checks off.
/// </para>
/// <para>
/// The failures are split into two kinds, and the split is the design's. A <em>structural</em> failure
/// - bad JSON, a missing key, a null, an extra key, an over-long string, an over-full array - is
/// something a repair call can plausibly fix, so it is the only thing a repair call is ever spent on.
/// A <em>grounding</em> failure - an id that is not in the snapshot, a number that appears in no
/// evidence - is a statement about the world rather than about the formatting, and repairing it would
/// be asking the same model to guess its way out of having invented something. Those fail immediately.
/// </para>
/// </remarks>
public static class GroundedSummarySchema
{
    /// <summary>
    /// The canonical JSON Schema text, sent to a provider that can enforce it and quoted into the
    /// instruction for one that cannot.
    /// </summary>
    /// <remarks>
    /// Written as a literal document rather than composed from the members of a CLR type, so the
    /// prompt a provider sees is a fact in this file that a reader can check by eye, and so a change
    /// to a property name cannot silently change the shape a stored draft was validated against.
    /// </remarks>
    public const string Json = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": [
            "milestoneTitle",
            "summary",
            "changes",
            "problemsSolved",
            "knownLimitations",
            "nextSteps"
          ],
          "properties": {
            "milestoneTitle": { "type": "string", "maxLength": 160 },
            "summary": { "type": "string", "maxLength": 8000 },
            "changes": { "$ref": "#/$defs/claims" },
            "problemsSolved": { "$ref": "#/$defs/claims" },
            "knownLimitations": { "$ref": "#/$defs/claims" },
            "nextSteps": { "$ref": "#/$defs/claims" }
          },
          "$defs": {
            "claim": {
              "type": "object",
              "additionalProperties": false,
              "required": ["text", "evidenceIds"],
              "properties": {
                "text": { "type": "string", "maxLength": 8000 },
                "evidenceIds": {
                  "type": "array",
                  "maxItems": 100,
                  "items": { "type": "string" }
                }
              }
            },
            "claims": {
              "type": "array",
              "maxItems": 100,
              "items": { "$ref": "#/$defs/claim" }
            }
          }
        }
        """;

    /// <summary>The schema's version label, recorded on every revision this package writes.</summary>
    public const string Version = ContentLimits.SummarySchemaVersion;

    /// <summary>
    /// The instruction appended when the provider cannot enforce the schema itself.
    /// </summary>
    /// <remarks>
    /// It states the consequence as well as the shape, for the same reason the delivered schema's
    /// fallback instruction does: a provider told that a malformed answer is thrown away spends one
    /// call finding out rather than producing a plausible object that fails later and further from the
    /// cause.
    /// </remarks>
    public static string FallbackInstruction =>
        "Return exactly one JSON object and nothing else - no prose before or after it, no code fence. "
        + "It must satisfy this JSON Schema exactly: " + Json
        + " Every text you write is a claim, so every claim's evidenceIds must list only ids that appear "
        + "in the untrusted evidence section. If you have nothing to say about one of the four arrays, "
        + "return an empty array for it rather than inventing an item.";

    /// <summary>
    /// Checks a returned text against the schema, strictly and locally.
    /// </summary>
    /// <returns>
    /// A short structural reason, or <c>null</c> when the text satisfies the schema. The reason names
    /// a member or a bound and never quotes the answer: this string reaches a log line, and a log line
    /// that reproduces a model's answer is a log line holding the owner's evidence back out.
    /// </returns>
    public static string? ValidateStructure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "the summary carried no text";
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return "the summary was not valid JSON";
        }

        if (root is not JsonObject document)
        {
            return "the summary was not a JSON object";
        }

        foreach (var member in document)
        {
            if (!IsDeclaredMember(member.Key))
            {
                // An extra key is refused rather than ignored. A provider that volunteers a member this
                // schema does not describe is a provider whose answer nobody has agreed how to read, and
                // dropping the member quietly would let it carry content the design never bounded.
                return $"the summary carried the undeclared member '{member.Key}'";
            }
        }

        var title = ReadRequiredString(document, "milestoneTitle");
        if (title is null)
        {
            return "the summary was missing a non-null 'milestoneTitle'";
        }

        if (title.Length > ContentLimits.MaxTitleLength)
        {
            return "the summary's 'milestoneTitle' exceeded the schema's maximum length";
        }

        var summary = ReadRequiredString(document, "summary");
        if (summary is null)
        {
            return "the summary was missing a non-null 'summary'";
        }

        if (summary.Length > ContentLimits.MaxStringLength)
        {
            return "the summary's 'summary' exceeded the schema's maximum length";
        }

        foreach (var section in GroundedSummary.Sections)
        {
            var key = GroundedSummary.SectionKey(section);
            var reason = ValidateClaims(document, key);

            if (reason is not null)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a structurally valid answer into the value the rest of the pipeline works with.
    /// </summary>
    /// <remarks>
    /// Called only after <see cref="ValidateStructure"/> returned <c>null</c>. A null here is therefore
    /// a defect rather than a provider outcome, and it is thrown rather than returned as an empty
    /// summary - a draft built from an empty value would be a draft that looks grounded and is not.
    /// </remarks>
    public static GroundedSummary Read(string text)
    {
        var reason = ValidateStructure(text);

        if (reason is not null)
        {
            throw new InvalidOperationException(
                $"A summary was read that did not satisfy the schema ({reason}).");
        }

        var document = JsonNode.Parse(text)!.AsObject();

        return new GroundedSummary(
            ReadRequiredString(document, "milestoneTitle")!,
            ReadRequiredString(document, "summary")!,
            ReadClaims(document, "changes"),
            ReadClaims(document, "problemsSolved"),
            ReadClaims(document, "knownLimitations"),
            ReadClaims(document, "nextSteps"));
    }

    private static string? ValidateClaims(JsonObject document, string key)
    {
        if (!document.ContainsKey(key))
        {
            return $"the summary was missing '{key}'";
        }

        if (document[key] is not JsonArray array)
        {
            return $"the summary's '{key}' was not an array";
        }

        if (array.Count > ContentLimits.MaxItemsPerArray)
        {
            return $"the summary's '{key}' carried more than {ContentLimits.MaxItemsPerArray} items";
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                return $"an item of '{key}' was not an object";
            }

            foreach (var member in item)
            {
                if (member.Key is not ("text" or "evidenceIds"))
                {
                    return $"an item of '{key}' carried the undeclared member '{member.Key}'";
                }
            }

            var text = ReadRequiredString(item, "text");
            if (text is null)
            {
                return $"an item of '{key}' was missing a non-null 'text'";
            }

            if (text.Length > ContentLimits.MaxStringLength)
            {
                return $"an item of '{key}' had text past the schema's maximum length";
            }

            if (item["evidenceIds"] is not JsonArray ids)
            {
                return $"an item of '{key}' was missing an 'evidenceIds' array";
            }

            if (ids.Count > ContentLimits.MaxItemsPerArray)
            {
                return $"an item of '{key}' cited more than {ContentLimits.MaxItemsPerArray} evidence ids";
            }

            foreach (var id in ids)
            {
                if (id is not JsonValue value ||
                    !value.TryGetValue<string>(out var text2) ||
                    !Guid.TryParse(text2, out _))
                {
                    return $"an item of '{key}' cited an evidence id that is not an identifier";
                }
            }
        }

        return null;
    }

    private static bool IsDeclaredMember(string member) =>
        member is "milestoneTitle" or "summary" or "changes" or "problemsSolved"
            or "knownLimitations" or "nextSteps";

    private static string? ReadRequiredString(JsonObject document, string member) =>
        document[member] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static IReadOnlyList<GroundedClaim> ReadClaims(JsonObject document, string key)
    {
        var claims = new List<GroundedClaim>();

        foreach (var node in document[key]!.AsArray())
        {
            var item = node!.AsObject();
            var ids = new List<Guid>();

            foreach (var id in item["evidenceIds"]!.AsArray())
            {
                ids.Add(Guid.Parse(id!.GetValue<string>()));
            }

            claims.Add(new GroundedClaim(item["text"]!.GetValue<string>(), ids));
        }

        return claims;
    }
}

/// <summary>
/// The internal claim map: every factual statement of an accepted summary, under a footnote id this
/// system chose (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The ids are ours, not the model's. Phase 2 is handed the map and is told to cite from it, and the
/// body is then checked against it - so a citation is checkable because the set of legal citations was
/// fixed before the prose existed. A system that let the model invent its own footnote names would be
/// validating the model's footnotes against the model's footnotes, which is not a check at all.
/// </para>
/// <para>
/// The id format is deliberately narrow: the letter <c>c</c> and a one-based index, so <c>c1</c> is the
/// first claim. <b>The caret is not part of the id</b> - in Markdown the caret is the sigil that makes
/// <c>[^c1]</c> a footnote reference at all, and the label inside the brackets is what a renderer
/// matches a definition against. An id carrying its own caret would make the stored claim ids and the
/// footnote labels two different strings, and the citation check would then refuse every citation a
/// correct body contains. The label is a token rather than a GUID so a body stays readable, and it
/// carries a prefix a human would not write, so a hand-added footnote cannot collide with one.
/// </para>
/// </remarks>
/// <param name="Id">The footnote id a body cites this claim by.</param>
/// <param name="Section">Which summary section the claim came from.</param>
/// <param name="Index">The claim's position inside that section, from zero.</param>
/// <param name="Text">The claim's text, as the summary recorded it.</param>
/// <param name="EvidenceIds">The evidence ids the claim cited.</param>
public sealed record DraftClaim(
    string Id,
    GroundedSummarySection Section,
    int Index,
    string Text,
    IReadOnlyList<Guid> EvidenceIds);

/// <summary>Builds and holds the claim map for one accepted summary.</summary>
public static class ClaimMap
{
    /// <summary>Walks the summary's sections in their fixed order and assigns every claim an id.</summary>
    public static IReadOnlyList<DraftClaim> Build(GroundedSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var claims = new List<DraftClaim>();
        var next = 1;

        foreach (var section in GroundedSummary.Sections)
        {
            var items = summary.ClaimsIn(section);

            for (var index = 0; index < items.Count; index++)
            {
                var claim = items[index];
                claims.Add(new DraftClaim(
                    string.Create(CultureInfo.InvariantCulture, $"c{next}"),
                    section,
                    index,
                    claim.Text,
                    claim.EvidenceIds));
                next++;
            }
        }

        return claims;
    }

    /// <summary>
    /// The map as the canonical JSON a phase-2 instruction quotes: one entry per claim, with the
    /// footnote id, the section it came from and the evidence ids it stands for.
    /// </summary>
    /// <remarks>
    /// The claim's own text is included, because phase 2 has to be able to write from the summary and
    /// cite from the same document. It carries no provider answer: every value here came out of the
    /// summary that was already validated against the snapshot.
    /// </remarks>
    public static string ToCanonicalJson(IReadOnlyList<DraftClaim> claims)
    {
        var array = new JsonArray();

        foreach (var claim in claims)
        {
            var ids = new JsonArray();
            foreach (var id in claim.EvidenceIds)
            {
                ids.Add(id.ToString("d"));
            }

            array.Add(new JsonObject
            {
                ["id"] = claim.Id,
                ["section"] = GroundedSummary.SectionKey(claim.Section),
                ["index"] = claim.Index,
                ["text"] = claim.Text,
                ["evidenceIds"] = ids,
            });
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// The numbers in a piece of prose, as the metric rule sees them.
    /// </summary>
    /// <remarks>
    /// <b>Footnote labels are removed first, and that is not a loophole.</b> A label is a provenance token
    /// this system itself minted from the claim map, not a figure anybody asserted, and <c>[^c1]</c>
    /// would otherwise be read as the number one and refused as an invented statistic in every body that
    /// cited anything at all - which would make the rule fail on correct text and pass on text that
    /// happened to omit its citations. The same is true of an evidence id, which is why the summary scan
    /// reads a claim's text rather than the claim's JSON.
    /// <para>
    /// What is left is prose, and every digit run in prose has to exist in the authorized evidence.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> NumbersInProse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Both the reference form and the definition form, so a label is removed whether it is being
        // referred to or being declared.
        var withoutLabels = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\[\^[A-Za-z0-9_.-]+\]:?",
            " ");

        return NumbersIn(withoutLabels);
    }

    /// <summary>
    /// The numbers in a piece of text, as the metric rule sees them.
    /// </summary>
    /// <remarks>
    /// A run of digits, of a digit run with a decimal point, of a digit run with a thousands
    /// separator, or of a digit run followed by a percent sign. Version strings are included
    /// deliberately: <c>0.1.0</c> yields <c>0</c>, <c>1</c> and <c>0</c>, and a claim that says
    /// "version 0.1.0" is quoting something the evidence very likely contains. The rule is about a
    /// number with no origin anywhere in the evidence, not about versions.
    /// </remarks>
    public static IReadOnlyList<string> NumbersIn(string text)
    {
        var found = new List<string>();
        var index = 0;

        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] is '.' or ',' or '%'))
            {
                index++;
            }

            var token = text[start..index].TrimEnd('.', ',');

            if (token.Length > 0)
            {
                found.Add(token);
            }
        }

        return found;
    }

    /// <summary>
    /// True when <paramref name="needle"/> appears in <paramref name="haystack"/> as a number, which
    /// means a bare substring comparison is not enough: the number "3" must not be considered present
    /// in the version "0.3.1" merely because the character is there.
    /// </summary>
    /// <remarks>
    /// Both sides are compared as runs of digits, so a token matches when it appears as a whole run.
    /// That is a deliberately narrow test and it is the one the rule needs: a fabricated figure is a
    /// figure that appears nowhere, not one that appears with different punctuation.
    /// </remarks>
    public static bool ContainsNumber(string haystack, string needle) =>
        NumberRuns(haystack).Contains(needle, StringComparer.Ordinal);

    private static HashSet<string> NumberRuns(string text)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < text.Length)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            runs.Add(text[start..index]);
        }

        return runs;
    }
}
