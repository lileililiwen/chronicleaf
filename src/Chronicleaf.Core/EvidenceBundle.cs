using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// How completely one source was collected (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// The set is closed, and it is the honest part of this package. A story may only say a commit
/// shipped if that commit was actually read; when the source could not be reached, or was only
/// partly read, the record says so and carries the reason instead of the fact. An empty
/// false-success source is the failure this vocabulary exists to make impossible, which is why
/// <see cref="Partial"/> and <see cref="Unavailable"/> are first-class values rather than a note
/// attached to a complete item.
/// <para>
/// <see cref="Unavailable"/> means nothing was collected: there is no payload, no hash of a
/// payload, and no media. <see cref="Partial"/> means something was collected and the rest was
/// excluded by a limit, a size cap or a redaction, and the exclusions are named in
/// <see cref="EvidenceItem.Redactions"/>. Both keep <see cref="EvidenceItem.SourceRevision"/> so a
/// reader can tell which version of the source the gap is about.
/// </para>
/// </remarks>
public static class EvidenceAvailability
{
    /// <summary>The whole source was collected and the payload hash covers all of it.</summary>
    public const string Complete = "complete";

    /// <summary>Part of the source was collected; the exclusions are named on the item.</summary>
    public const string Partial = "partial";

    /// <summary>Nothing was collected, and the item text says why instead of holding a fact.</summary>
    public const string Unavailable = "unavailable";

    public static IReadOnlyList<string> All { get; } = [Complete, Partial, Unavailable];

    /// <summary>
    /// True for <see cref="Complete"/> and <see cref="Partial"/>, which is to say: the item carries
    /// something that was actually collected. A reference check or a serving path that must not run
    /// on a gap tests this rather than comparing against <see cref="Unavailable"/> directly.
    /// </summary>
    public static bool WasCollected(string availability) =>
        !string.Equals(availability, Unavailable, StringComparison.Ordinal);
}

/// <summary>
/// The closed vocabulary of evidence kinds (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// Only the owner-declared kinds - <see cref="Manual"/>, <see cref="Limitations"/> and
/// <see cref="NextSteps"/> - may be appended from a request in this package. The source kinds exist
/// now so a list, an export and a later narrative package read one vocabulary, and so a row written
/// by a future collector cannot invent a kind that no reader recognises.
/// </remarks>
public static class EvidenceKinds
{
    /// <summary>What the project itself declared: version, license, repository URL.</summary>
    public const string ProjectMetadata = "project-metadata";

    /// <summary>One commit, identified by its revision.</summary>
    public const string Commit = "commit";

    /// <summary>The textual difference between two source revisions.</summary>
    public const string Diff = "diff";

    /// <summary>A published release or tag.</summary>
    public const string Release = "release";

    /// <summary>One issue, as the source reported it.</summary>
    public const string Issue = "issue";

    /// <summary>One pull request, as the source reported it.</summary>
    public const string Pr = "pr";

    /// <summary>A captured screenshot, referenced by <see cref="EvidenceItem.MediaId"/>.</summary>
    public const string Screenshot = "screenshot";

    /// <summary>The owner's own note about this milestone.</summary>
    public const string Manual = "manual";

    /// <summary>Something the owner knows does not work yet.</summary>
    public const string Limitations = "limitations";

    /// <summary>What the owner intends to do next.</summary>
    public const string NextSteps = "next-steps";

    public static IReadOnlyList<string> All { get; } =
    [
        ProjectMetadata,
        Commit,
        Diff,
        Release,
        Issue,
        Pr,
        Screenshot,
        Manual,
        Limitations,
        NextSteps,
    ];

    /// <summary>
    /// The kinds an owner may append by hand. A request cannot claim to be a commit or a release: a
    /// collected fact has to be collected, and only a collector may say it was.
    /// </summary>
    public static IReadOnlyList<string> OwnerWritable { get; } = [Manual, Limitations, NextSteps];

    public static bool IsOwnerWritable(string kind) =>
        OwnerWritable.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// The bounds a single evidence bundle must respect (MILESTONE-EVIDENCE-002).
/// </summary>
/// <remarks>
/// A bundle that is too large is refused with 413 rather than trimmed. Trimming would drop facts the
/// owner never agreed to lose, and the caller would have no way to tell a short story from a
/// silently shortened one - so the honest answer is to refuse and let the caller choose.
/// </remarks>
public static class EvidenceLimits
{
    /// <summary>The combined text of every item in one bundle, in UTF-8 bytes.</summary>
    public const int MaxTextBytesPerBundle = 256 * 1024;

    /// <summary>At most this many items in one bundle, carried-forward ones included.</summary>
    public const int MaxItemsPerBundle = 500;

    /// <summary>At most <see cref="RedactionMaxLength"/> characters per redaction label.</summary>
    public const int RedactionMaxLength = 120;

    /// <summary>At most <see cref="MaxRedactions"/> redaction labels on one item.</summary>
    public const int MaxRedactions = 20;

    /// <summary>At most <see cref="SourceRefMaxLength"/> characters naming the source.</summary>
    public const int SourceRefMaxLength = 200;

    /// <summary>At most <see cref="SourceRevisionMaxLength"/> characters naming the source revision.</summary>
    public const int SourceRevisionMaxLength = 200;

    /// <summary>At most <see cref="ItemTextMaxLength"/> characters in one item's text.</summary>
    public const int ItemTextMaxLength = 64 * 1024;
}

/// <summary>
/// One collected fact inside an <see cref="EvidenceBundle"/> (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// An item is a statement about one source at one revision, and it carries the three things that
/// make the statement checkable: which source (<see cref="SourceRef"/>), which version of it
/// (<see cref="SourceRevision"/>) and the hash of exactly what was collected
/// (<see cref="PayloadSha256"/>). Together they are also the identity that makes a repeated append
/// a no-op rather than a duplicate: the same fact at the same version of the same source is the
/// same fact.
/// <para>
/// <see cref="Text"/> holds the collected payload - except when
/// <see cref="Availability"/> is <see cref="EvidenceAvailability.Unavailable"/>, where it holds the
/// reason the source could not be read and the database refuses a reason-free gap. A
/// <see cref="EvidenceAvailability.Partial"/> item keeps what was collected and names every
/// exclusion in <see cref="Redactions"/>.
/// </para>
/// <para>
/// <see cref="MediaId"/> references stored media rather than copying its bytes, and is null unless
/// something was actually collected: an item that collected nothing has no media to point at, which
/// the <c>evidence_items</c> check constraint enforces. <see cref="SupersedesId"/> names the item a
/// later correction replaced; the replaced row is kept, never rewritten, so a reader can still see
/// what was corrected.
/// </para>
/// </remarks>
public sealed class EvidenceItem
{
    /// <summary>This item's identity, stable for the life of the row.</summary>
    public Guid Id { get; }

    /// <summary>The bundle this item belongs to.</summary>
    public Guid BundleId { get; }

    /// <summary>The milestone whose scope the item was collected in.</summary>
    public Guid MilestoneId { get; }

    /// <summary>One of the fixed <see cref="EvidenceKinds"/> values.</summary>
    public string Kind { get; }

    /// <summary>Where it came from, for example a repository path or <c>owner</c>.</summary>
    public string SourceRef { get; }

    /// <summary>Which version of that source, so a reader knows what the claim is about.</summary>
    public string SourceRevision { get; }

    /// <summary>When the source was read; always UTC.</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>Lowercase SHA-256 of the collected payload, or of the reason when unavailable.</summary>
    public string PayloadSha256 { get; }

    /// <summary>
    /// The collected payload, or the reason the source was unavailable, or a limitation or next
    /// step the owner wrote. Untrusted data throughout: it is never an instruction to this system.
    /// </summary>
    public string Text { get; }

    /// <summary>Stored media this item points at, or null when it points at nothing.</summary>
    public Guid? MediaId { get; }

    /// <summary>What was deliberately left out; empty when nothing was.</summary>
    public IReadOnlyList<string> Redactions { get; }

    /// <summary>One of the fixed <see cref="EvidenceAvailability"/> values.</summary>
    public string Availability { get; }

    /// <summary>The item this one corrects, or null when it corrects nothing.</summary>
    public Guid? SupersedesId { get; }

    public EvidenceItem(
        Guid id,
        Guid bundleId,
        Guid milestoneId,
        string kind,
        string sourceRef,
        string sourceRevision,
        DateTimeOffset capturedAt,
        string payloadSha256,
        string text,
        Guid? mediaId,
        IReadOnlyList<string> redactions,
        string availability,
        Guid? supersedesId = null)
    {
        Id = id;
        BundleId = bundleId;
        MilestoneId = milestoneId;
        Kind = kind;
        SourceRef = sourceRef;
        SourceRevision = sourceRevision;
        CapturedAt = capturedAt;
        PayloadSha256 = payloadSha256;
        Text = text;
        MediaId = mediaId;
        Redactions = redactions;
        Availability = availability;
        SupersedesId = supersedesId;
    }

    /// <summary>True when something was actually collected, whether or not it was everything.</summary>
    public bool WasCollected => EvidenceAvailability.WasCollected(Availability);
}

/// <summary>
/// One immutable snapshot of a milestone's evidence (MILESTONE-EVIDENCE-001).
/// </summary>
/// <remarks>
/// A bundle is a complete snapshot, not a delta: it holds every item that was collected for the
/// milestone so far, carried forward from the previous revision by reference plus whatever this
/// append added. That is what lets a story built on revision 7 still be reproducible, and it is
/// why a manual append of one limitation cannot quietly drop the four commits that came before it.
/// <para>
/// Nothing here is ever rewritten. Appending produces a new bundle at the next
/// <see cref="Revision"/> and leaves every earlier bundle and its items exactly as they were; the
/// database refuses the update and the delete outright rather than trusting callers not to try.
/// </para>
/// <para>
/// The manual note is kept separate from this record on purpose: the body of an evidence item is
/// a source's words, and publishing them is a later package's decision. Re-deriving an exported
/// story can always re-read this snapshot; it cannot recover a note that was merged into it.
/// </para>
/// </remarks>
public sealed class EvidenceBundle
{
    // The persistence model has to be able to materialise this type without ever being handed the
    // item set, because the items of a snapshot are read by the store's own statement and are never
    // tracked. The default constructor exists for that model only; nothing in the product builds a
    // bundle this way, and a bundle produced this way has no items rather than empty ones it invented.
    private EvidenceBundle()
    {
        Items = [];
    }

    /// <summary>This bundle's identity.</summary>
    public Guid Id { get; private set; }

    /// <summary>The milestone whose evidence this bundle holds.</summary>
    public Guid MilestoneId { get; private set; }

    /// <summary>The optimistic revision; the first committed bundle of a milestone is 1.</summary>
    public long Revision { get; private set; }

    /// <summary>When the bundle was committed; always UTC.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The whole snapshot, in stable id order.</summary>
    public IReadOnlyList<EvidenceItem> Items { get; private set; }

    public EvidenceBundle(
        Guid id,
        Guid milestoneId,
        long revision,
        DateTimeOffset createdAt,
        IReadOnlyList<EvidenceItem> items)
    {
        Id = id;
        MilestoneId = milestoneId;
        Revision = revision;
        CreatedAt = createdAt;
        Items = items;
    }
}

/// <summary>
/// The shape helpers every evidence read and write agrees on.
/// </summary>
public static class EvidenceValues
{
    private const string BundlesSegment = "evidence-bundles";
    private const string ItemsSegment = "evidence-items";

    /// <summary>
    /// The scope-and-milestone key an item's idempotence is scoped to. Idempotence must never reach
    /// across milestones: the same commit hash in two milestones is two separate facts, each of
    /// which still has to be recorded.
    /// </summary>
    public static string ItemKey(Guid milestoneId, string kind, string sourceRef, string sourceRevision, string payloadSha256) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{milestoneId:N}/{kind}/{sourceRef}/{sourceRevision}/{payloadSha256}");

    /// <summary>A stable diagnostic label for a bundle row.</summary>
    public static string BundleLabel(Guid bundleId) => $"{BundlesSegment}/{bundleId:N}";

    /// <summary>A stable diagnostic label for an item row.</summary>
    public static string ItemLabel(Guid itemId) => $"{ItemsSegment}/{itemId:N}";

    /// <summary>
    /// True only for a lowercase 64-character hexadecimal SHA-256. Uppercase is refused rather than
    /// normalized, so a hash can never be recorded in two spellings for one payload and then fail
    /// to match itself.
    /// </summary>
    public static bool IsSha256Hex(string value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isLowerHex = character is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isLowerHex)
            {
                return false;
            }
        }

        return true;
    }
}
