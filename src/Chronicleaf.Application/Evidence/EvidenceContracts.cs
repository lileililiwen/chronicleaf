using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Evidence;

/// <summary>
/// One fact a caller wants to append to a milestone's evidence, before the store has given it an
/// identity (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// The item is <see cref="Kind"/>, <see cref="SourceRef"/>, <see cref="SourceRevision"/>,
/// <see cref="PayloadSha256"/>, <see cref="Text"/>, an optional <see cref="MediaId"/>,
/// <see cref="Redactions"/> and <see cref="Availability"/> - the same fields a stored item has. The
/// id, the bundle and the milestone are decided by the append, never by the caller.
/// </remarks>
public sealed class EvidenceAppendItem
{
    public EvidenceAppendItem(
        string kind,
        string sourceRef,
        string sourceRevision,
        DateTimeOffset capturedAt,
        string payloadSha256,
        string text,
        Guid? mediaId = null,
        IReadOnlyList<string>? redactions = null,
        string availability = EvidenceAvailability.Complete,
        Guid? supersedesId = null)
    {
        Kind = kind;
        SourceRef = sourceRef;
        SourceRevision = sourceRevision;
        CapturedAt = capturedAt;
        PayloadSha256 = payloadSha256;
        Text = text;
        MediaId = mediaId;
        Redactions = redactions ?? [];
        Availability = availability;
        SupersedesId = supersedesId;
    }

    public string Kind { get; }

    public string SourceRef { get; }

    public string SourceRevision { get; }

    public DateTimeOffset CapturedAt { get; }

    public string PayloadSha256 { get; }

    public string Text { get; }

    public Guid? MediaId { get; }

    public IReadOnlyList<string> Redactions { get; }

    public string Availability { get; }

    public Guid? SupersedesId { get; }
}

/// <summary>
/// What the owner wants written by hand for a milestone (MILESTONE-EVIDENCE-002).
/// </summary>
/// <remarks>
/// The owner may say what happened, what is known not to work yet and what comes next. At least one
/// of the three has to carry something: an append of three empty fields would commit a new revision
/// that claims to add evidence and adds none.
/// <para>
/// These become items of kind <see cref="EvidenceKinds.Manual"/>,
/// <see cref="EvidenceKinds.Limitations"/> and <see cref="EvidenceKinds.NextSteps"/>, appended to
/// the snapshot the milestone already has. The earlier evidence is carried into the new revision
/// rather than replaced, so a limitation never costs the story a commit.
/// </para>
/// </remarks>
public sealed class ManualEvidenceRequest
{
    public const int MaxListItems = Milestone.ListItemMax;

    public ManualEvidenceRequest(
        string? notes,
        IReadOnlyList<string>? knownLimitations,
        IReadOnlyList<string>? nextSteps,
        long expectedRevision)
    {
        Notes = notes;
        KnownLimitations = knownLimitations ?? [];
        NextSteps = nextSteps ?? [];
        ExpectedRevision = expectedRevision;
    }

    /// <summary>The owner's own note, at most <see cref="Milestone.NotesMaxLength"/> characters.</summary>
    public string? Notes { get; }

    /// <summary>What is known not to work yet, one item per entry.</summary>
    public IReadOnlyList<string> KnownLimitations { get; }

    /// <summary>What comes next, one item per entry.</summary>
    public IReadOnlyList<string> NextSteps { get; }

    /// <summary>
    /// The revision the owner was looking at. It is carried on every manual item as the source
    /// revision, so a re-sent identical append is recognised as the same statement and a reader can
    /// see which evidence state the owner was answering.
    /// </summary>
    public long ExpectedRevision { get; }
}

/// <summary>One bundle and the opaque cursor that continues past it.</summary>
public sealed record EvidencePage(IReadOnlyList<EvidenceBundle> Bundles, string? NextCursor);

/// <summary>
/// Appends source material to a milestone's evidence (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// The append is conditional on <c>expectedRevision</c> and lands at exactly
/// <c>expectedRevision + 1</c>, which is what makes two concurrent appends resolve to one committed
/// bundle and one 409 rather than a lost update. Re-appending facts the head bundle already holds is
/// not an error and not a new revision: the existing bundle comes back unchanged.
/// <para>
/// The scope comes from the owner session, never from the arguments. An install has one owner and
/// the delivered access boundary resolves the session to that owner's installation; a body-supplied
/// scope would be exactly the kind of id this system refuses to take from a request.
/// </para>
/// </remarks>
public interface IAppendEvidence
{
    /// <summary>
    /// Appends <paramref name="items"/> to the milestone's evidence as a new bundle revision.
    /// </summary>
    /// <returns>
    /// The new bundle, or the existing head bundle when every requested fact is already in it.
    /// </returns>
    /// <exception cref="ResourceNotFoundException">
    /// The milestone is not in the owner's scope, or a referenced media id is absent, in another
    /// scope, in another project or has no bytes left. One answer for all four, so a caller cannot
    /// probe outside its own installation.
    /// </exception>
    /// <exception cref="RevisionConflictException">The expected revision is stale.</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived.</exception>
    /// <exception cref="ValidationFailedException">An item is not well formed.</exception>
    /// <exception cref="PayloadTooLargeException">The resulting bundle would exceed its bounds.</exception>
    Task<EvidenceBundle> AppendAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        IReadOnlyList<EvidenceAppendItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends what the owner wrote by hand. The kinds are decided here, not by the caller: a manual
    /// append adds a <see cref="EvidenceKinds.Manual"/> note, one
    /// <see cref="EvidenceKinds.Limitations"/> item per stated limitation and one
    /// <see cref="EvidenceKinds.NextSteps"/> item per stated next step, and nothing else. A request
    /// therefore cannot name a commit, a release or a screenshot, because there is nowhere in this
    /// method to put such a name.
    /// </summary>
    /// <returns>The new bundle, which retains everything the previous revision held.</returns>
    /// <exception cref="ResourceNotFoundException">The milestone is not in the owner's scope.</exception>
    /// <exception cref="RevisionConflictException">The expected revision is stale.</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived.</exception>
    /// <exception cref="ValidationFailedException">Nothing was written, or a field is malformed.</exception>
    /// <exception cref="PayloadTooLargeException">The resulting bundle would exceed its bounds.</exception>
    Task<EvidenceBundle> AppendManualAsync(
        OwnerScope owner,
        Guid milestoneId,
        ManualEvidenceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a milestone's evidence history (MILESTONE-EVIDENCE-001/002).
/// </summary>
public interface IReadEvidence
{
    /// <summary>
    /// The milestone's bundles, newest revision first, bounded to the shared list limits and paged
    /// by a scope-bound opaque cursor.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The milestone is not in the owner's scope.</exception>
    Task<EvidencePage> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        PageRequest page,
        CancellationToken cancellationToken = default);
}
