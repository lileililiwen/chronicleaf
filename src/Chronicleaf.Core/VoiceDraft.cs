using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Chronicleaf.Core;

/// <summary>
/// The stable fingerprint of a <see cref="VoiceSnapshot"/>'s resolved values
/// (VOICE-PROFILES-001).
/// </summary>
/// <remarks>
/// The canonical form is written by hand, field by field and in a fixed order, with a separator that
/// cannot appear inside any of the values. That is deliberate: the fingerprint has to be the same
/// string in the API process, in the database and in a test, so it cannot depend on serializer
/// options, on a dictionary's iteration order or on a property name changing.
/// </remarks>
public static class VoiceSnapshotHasher
{
    /// <summary>The normalized Unicode form a snapshot's values are canonicalized to.</summary>
    public const string Canonicalization = "NFC";

    /// <summary>Lowercase SHA-256 over the canonical form of a snapshot's resolved values.</summary>
    public static string Fingerprint(VoiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Fingerprint(snapshot.Values);
    }

    /// <summary>Lowercase SHA-256 over the canonical form of a set of resolved values.</summary>
    public static string Fingerprint(VoiceValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var canonical = new StringBuilder()
            .Append(values.Language).Append('\u001F')
            .Append(values.Tone).Append('\u001F')
            .Append(values.Emoji ? "true" : "false").Append('\u001F')
            .Append(values.TitleMaxChars.ToString(CultureInfo.InvariantCulture)).Append('\u001F')
            .Append(values.TechnicalDetail).Append('\u001F')
            .Append(values.Marketing).Append('\u001F')
            .Append(values.FirstPerson ? "true" : "false").Append('\u001F')
            .Append(string.Join('\u001F', values.ForbiddenTerms))
            .ToString();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

/// <summary>
/// The closed set of rules the deterministic checker evaluates (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// Every entry here is a rule this repository actually evaluates against text. The set exists so
/// the checker's rule list and the settings surface's rule list cannot drift, and so a caller can
/// ask "which rules ran" and get the honest answer rather than the whole vocabulary.
/// </remarks>
public static class VoiceFindingRules
{
    /// <summary>A configured forbidden term occurs in the text, in any case.</summary>
    public const string ForbiddenTerm = "forbidden_term";

    /// <summary>An emoji occurs in text that the effective profile does not allow emoji in.</summary>
    public const string EmojiNotAllowed = "emoji_not_allowed";

    /// <summary>The title is longer, in characters, than the effective profile's budget.</summary>
    public const string TitleTooLong = "title_too_long";

    /// <summary>The text is not written in the language the effective profile declares.</summary>
    public const string LanguageMismatch = "language_mismatch";

    public static readonly IReadOnlyList<string> All =
    [
        ForbiddenTerm,
        EmojiNotAllowed,
        TitleTooLong,
        LanguageMismatch,
    ];

    /// <summary>The two fields a finding can point at.</summary>
    public static readonly IReadOnlyList<string> Fields = ["title", "body"];

    public const string TitleField = "title";
    public const string BodyField = "body";
}

/// <summary>
/// One rule violation, reported against a span of the text the owner can see
/// (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// <para>
/// A finding is a <em>report</em>, never a correction. Nothing in this repository constructs one by
/// changing text: the checker receives text, produces this record, and returns. The bytes the owner
/// wrote are the bytes that stay, which is the difference between "your draft breaks a rule" and
/// "your draft was rewritten to satisfy one".
/// </para>
/// <para>
/// <see cref="Start"/> and <see cref="Length"/> address the offending run in the <em>original</em>
/// field text, counted in UTF-16 code units, so a client can select exactly the span rather than a
/// fuzzy neighbourhood of it. <see cref="Excerpt"/> is that same run copied out verbatim, and
/// <see cref="Term"/>/<see cref="NormalizedTerm"/> say which configured value matched and what form
/// matching actually ran against - which is what lets an owner see that <c>Game-Changing</c> in
/// their own text was found by the configured <c>game-changing</c>.
/// </para>
/// </remarks>
public sealed record VoiceFinding(
    string Rule,
    string Field,
    int Start,
    int Length,
    string Excerpt,
    string Term,
    string NormalizedTerm,
    string Message)
{
    /// <summary>The end of the reported span, exclusive, in the original field text.</summary>
    public int End => Start + Length;
}

/// <summary>
/// The closed set of states an owner-written voice draft can be in (VOICE-PROFILES-002).
/// </summary>
/// <remarks>
/// A draft is written by the owner in this package; generated prose is a later package's concern.
/// Approval is the only transition, it is refused while any finding remains, and the text is never
/// altered on the way in or out.
/// </remarks>
public static class VoiceDraftStatuses
{
    /// <summary>Owner text, not yet approved. Findings may or may not be present.</summary>
    public const string Draft = "draft";

    /// <summary>Owner text that passed every rule. Terminal: the stored bytes never change again.</summary>
    public const string Approved = "approved";

    public static readonly IReadOnlyList<string> All = [Draft, Approved];

    public static bool IsKnown(string? status) => status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// One owner-written piece of text, judged under the voice it was created with
/// (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is a copied value, not a link. It is written once, in the same statement that
/// creates the draft, from the profile's resolved values at that instant; afterwards nothing in this
/// repository updates it. A profile edit therefore produces a new revision that only
/// <em>future</em> drafts pick up, and a draft written under the old voice keeps the old one - which
/// is exactly the requirement's boundary scenario, and why an owner can approve a draft against a
/// voice that the profile has since moved past.
/// </para>
/// <para>
/// The title and body are stored exactly as sent. There is no normalisation, trimming or
/// re-encoding on the write path, so a byte comparison before and after a check is meaningful.
/// </para>
/// </remarks>
public sealed class VoiceDraft
{
    public VoiceDraft(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string title,
        string body,
        string status,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        VoiceSnapshot voiceSnapshot)
    {
        ArgumentNullException.ThrowIfNull(voiceSnapshot);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A voice draft needs a non-empty id.", nameof(id));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A voice draft needs a non-empty project.", nameof(projectId));
        }

        if (title is null)
        {
            throw new ArgumentNullException(nameof(title));
        }

        if (body is null)
        {
            throw new ArgumentNullException(nameof(body));
        }

        if (!VoiceDraftStatuses.IsKnown(status))
        {
            throw new ArgumentException("The status is not one of the supported draft statuses.", nameof(status));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "The first stored revision is 1.");
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        Title = title;
        Body = body;
        Status = status;
        Revision = revision;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        VoiceSnapshot = voiceSnapshot;
    }

    /// <summary>This draft's identity.</summary>
    public Guid Id { get; }

    /// <summary>The installation scope the draft belongs to and is readable from.</summary>
    public Guid ScopeId { get; }

    /// <summary>The project the draft belongs to.</summary>
    public Guid ProjectId { get; }

    /// <summary>The title, stored exactly as the owner sent it.</summary>
    public string Title { get; }

    /// <summary>The body, stored exactly as the owner sent it.</summary>
    public string Body { get; }

    /// <summary>One of <see cref="VoiceDraftStatuses"/>.</summary>
    public string Status { get; }

    /// <summary>The optimistic revision an <c>If-Match</c> must carry to replace this draft.</summary>
    public long Revision { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>
    /// The voice this draft was created under. Copied at creation and never rewritten afterwards.
    /// </summary>
    public VoiceSnapshot VoiceSnapshot { get; }

    /// <summary>The first version of a draft: owner text, the voice of this instant, revision 1.</summary>
    public static VoiceDraft Create(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string title,
        string body,
        DateTimeOffset now,
        VoiceSnapshot voiceSnapshot) =>
        new(id, scopeId, projectId, title, body, VoiceDraftStatuses.Draft, revision: 1, now, now, voiceSnapshot);
}
