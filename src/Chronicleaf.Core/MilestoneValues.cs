namespace Chronicleaf.Core;

/// <summary>
/// The one status a milestone may hold (PROJECTS-AND-MILESTONES-001). A record is created as
/// <see cref="Candidate"/> or as <see cref="Confirmed"/> and can only change through an explicit
/// owner action; nothing else in this package writes this value.
/// </summary>
public static class MilestoneStatus
{
    /// <summary>Arrived from a source and has not been looked at by the owner yet.</summary>
    public const string Candidate = "candidate";

    /// <summary>The owner confirmed it. Only a confirmed milestone counts as history.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>The owner dismissed it. It stays stored, and returning it is an owner action.</summary>
    public const string Dismissed = "dismissed";

    public static IReadOnlyList<string> All { get; } = [Candidate, Confirmed, Dismissed];
}

/// <summary>
/// The fixed milestone vocabulary. The set is closed: a record is one of these values or it is not
/// a milestone, which is what keeps a list, an export and a later narrative package reading the same
/// words. <c>custom</c> is the owner's escape hatch for work that genuinely fits none of the others.
/// </summary>
public static class MilestoneTypes
{
    public const string Feature = "feature";
    public const string Release = "release";
    public const string Deployment = "deployment";
    public const string Ui = "ui";
    public const string Bugfix = "bugfix";
    public const string Performance = "performance";
    public const string Payment = "payment";
    public const string Feedback = "feedback";
    public const string Stars = "stars";
    public const string Migration = "migration";
    public const string OpenSource = "open-source";
    public const string FirstCustomer = "first-customer";
    public const string Custom = "custom";

    /// <summary>Every accepted type, in the order the product presents them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Feature,
        Release,
        Deployment,
        Ui,
        Bugfix,
        Performance,
        Payment,
        Feedback,
        Stars,
        Migration,
        OpenSource,
        FirstCustomer,
        Custom,
    ];
}

/// <summary>
/// Where a milestone came from. The distinction is the reason the status exists at all: a record the
/// owner wrote is confirmed by the act of saving it, and a record that arrived from anywhere else
/// begins as a candidate no matter what produced it.
/// </summary>
public static class MilestoneSource
{
    /// <summary>The owner typed it. Never a candidate: the explicit save is the confirmation.</summary>
    public const string Manual = "manual";

    /// <summary>How long a source label may be; see the <c>milestones.source</c> column.</summary>
    public const int MaxLength = 40;

    /// <summary>How long a source key may be; see the <c>milestones.source_key</c> column.</summary>
    public const int SourceKeyMaxLength = 200;

    /// <summary>
    /// True for a source label this package is willing to store: a lower-case token that a later
    /// package can claim for itself (<c>github</c>, <c>capture</c>, …) without this package having to
    /// know the name in advance. It carries no authority: a source label decides where a record came
    /// from, never whether it is confirmed.
    /// </summary>
    public static bool IsWellFormed(string source) =>
        source.Length is > 0 and <= MaxLength
        && (char.IsAsciiLetter(source[0]) || source[0] is '-' or '_')
        && source.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
