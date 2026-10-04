using Chronicleaf.Application.AI;
using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The codes this package adds to the delivered error envelope, and the codes it writes to a job's
/// operation row (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping is
/// unchanged; this file is vocabulary, not a second envelope. Every message is authored here in fixed
/// words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope and into
/// the log line beside it. A message that interpolated a provider's answer, an evidence item's text or
/// a claim's own words would publish all three - and a draft's whole purpose is to publish text a
/// human has not approved yet.
/// </para>
/// <para>
/// <b>The two vocabularies are kept apart on purpose.</b> The envelope codes answer a request; the job
/// codes answer a run that has already been accepted. A draft that failed grounding is a job that
/// ended, not a request that was refused, and a client that saw one as the other would be told a
/// generation failed when what actually happened is that this system declined to store a draft.
/// </para>
/// </remarks>
public static class ContentError
{
    /// <summary>Semantic validation failed (422), with per-field detail. The delivered code, reused.</summary>
    public const string ValidationFailed = ApiError.ValidationFailed;

    /// <summary>The resource is absent, or belongs to another scope (404). The delivered code, reused.</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>The request was over a bound (413). The delivered code, reused.</summary>
    public const string TooLarge = ApiError.PayloadTooLarge;

    /// <summary>
    /// The consent no longer covers the input the request named (409).
    /// </summary>
    /// <remarks>
    /// The delivered <c>consent_stale</c> code, reused deliberately rather than invented. This package
    /// does not issue consents, so it has no vocabulary for one being withdrawn; the generation
    /// contract does, and a caller that has to handle one refusal should not have to learn that the
    /// same condition has a second spelling depending on which package raised it.
    /// </remarks>
    public const string ConsentStale = AiError.ConsentStale;

    /// <summary>
    /// The milestone is not confirmed, so a draft was not created for it (422).
    /// </summary>
    /// <remarks>
    /// A requirement rather than a policy preference: only a confirmed milestone counts as history, so
    /// a story written for a candidate would be a story about something the owner has not yet said
    /// happened. The message says which status it holds, because that is a fact the owner can change
    /// and one call to <c>confirm</c> fixes.
    /// </remarks>
    public const string MilestoneNotConfirmed = "draft_milestone_not_confirmed";

    /// <summary>
    /// The selected evidence holds nothing that was actually collected (422).
    /// </summary>
    /// <remarks>
    /// The design's "empty evidence requires manual notes before generation". The delivered evidence
    /// package already refuses a milestone with no evidence at all; this is the narrower and later
    /// question, which is whether anything in the selection was <em>collected</em>. An item whose
    /// availability is <c>unavailable</c> carries the reason it could not be read, and drafting from
    /// those is drafting from refusals. The answer is not a worse draft, it is a note recorded by hand
    /// - which is exactly what the evidence package's own <c>manual</c> kind is for.
    /// </remarks>
    public const string NoGrounding = "draft_no_grounding";

    // ---------------------------------------------------------------------------------------------
    // Job error codes. Written to the operation row, never to a response body, and deliberately
    // without a status in ApiErrorStatus: no route answers a draft that already failed.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The request row for this job is gone, so there is nothing to draft from and no honest way to
    /// guess what was authorized.
    /// </summary>
    public const string RequestMissing = "content_draft_request_missing";

    /// <summary>
    /// The structured summary did not satisfy its schema, and the one permitted repair call did not
    /// either.
    /// </summary>
    /// <remarks>
    /// Spelled exactly <c>invalid_summary</c>, because the requirement names that outcome. It is the
    /// one job code here that is not prefixed with this package's own namespace, and that is a
    /// deliberate exception: a published specification's words are the contract, and a rename would
    /// make the delivered spec and the running product disagree about what happened.
    /// </remarks>
    public const string InvalidSummary = "invalid_summary";

    /// <summary>
    /// A claim cited an evidence id that is not in the frozen snapshot. Failed immediately, with no
    /// repair call spent on it.
    /// </summary>
    public const string UnsupportedReference = "content_draft_unsupported_reference";

    /// <summary>
    /// A claim, or the body, carried a number that appears in no evidence item at all.
    /// </summary>
    public const string FabricatedMetric = "content_draft_fabricated_metric";

    /// <summary>
    /// The body carried a citation whose footnote id is not in the claim map, or a reference with no
    /// definition.
    /// </summary>
    public const string CitationUnknown = "content_draft_citation_unknown";

    /// <summary>The body did not carry a known limitation the accepted summary recorded.</summary>
    public const string LimitationDropped = "content_draft_limitation_dropped";

    /// <summary>
    /// The body carried no citation at all, so nothing in it is grounded.
    /// </summary>
    /// <remarks>
    /// Its own code rather than a variant of <see cref="CitationUnknown"/>, because the two mean
    /// different things to an owner. A body citing something unknown is a body that over-reaches and
    /// the fix is to remove the citation. A body citing nothing is a body that was not grounded at
    /// all, and the fix is to draft it again from the evidence.
    /// </remarks>
    public const string UngroundedBody = "content_draft_ungrounded_body";

    /// <summary>The body was over the 100 KiB Markdown bound, so no revision was written.</summary>
    public const string BodyTooLarge = "content_draft_body_too_large";

    /// <summary>
    /// The evidence, the voice or the milestone moved while the job was running, so nothing was
    /// committed and the previous draft is untouched.
    /// </summary>
    /// <remarks>
    /// Deliberately a permanent failure rather than a retry. The input this run was built on no longer
    /// exists, and a second attempt from the same authorized snapshot would produce the same answer
    /// against a bundle the owner has since moved past. The owner asks again, which re-runs the whole
    /// consent against what is current.
    /// </remarks>
    public const string Superseded = "content_draft_superseded";

    /// <summary>The provider declined to generate. Recorded on the job, never as a draft.</summary>
    public const string ProviderRefused = "content_draft_provider_refused";

    /// <summary>The provider rejected the stored credential, and will again until it is replaced.</summary>
    public const string CredentialRejected = "content_draft_credential_rejected";

    /// <summary>The provider answered with something unusable, in either phase.</summary>
    public const string ProviderInvalid = "content_draft_provider_invalid";

    /// <summary>
    /// The preview names a provider slot this deployment has no adapter for, so the draft could not
    /// start.
    /// </summary>
    /// <remarks>
    /// The same fact the delivered envelope calls <c>provider_not_installed</c>, reported as a job code
    /// because a handler has no status to return. It is permanent: an adapter is a build, so a second
    /// attempt from the same job would find the same absence, and letting the delivered worker treat it
    /// as transient would spend its whole attempt budget learning nothing.
    /// </remarks>
    public const string AdapterUnavailable = "content_draft_adapter_unavailable";

    /// <summary>
    /// The preview names a model the installed adapter's provider does not accept.
    /// </summary>
    /// <remarks>
    /// Also permanent, and also not a substitution. A near-miss model would bill the owner for a
    /// generation other than the one the consent names, and the consent is the whole reason this package
    /// is allowed to call anything at all.
    /// </remarks>
    public const string ModelUnsupported = "content_draft_model_unsupported";

    /// <summary>Every job code, so a scenario can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> JobCodes =
    [
        RequestMissing,
        InvalidSummary,
        UnsupportedReference,
        FabricatedMetric,
        CitationUnknown,
        LimitationDropped,
        UngroundedBody,
        BodyTooLarge,
        Superseded,
        ProviderRefused,
        CredentialRejected,
        ProviderInvalid,
        AdapterUnavailable,
        ModelUnsupported,
    ];
}

/// <summary>
/// The milestone a draft was asked for is not confirmed (422).
/// </summary>
public sealed class MilestoneNotConfirmedException(string status)
    : ApiException(
        ContentError.MilestoneNotConfirmed,
        "A draft is only written for a confirmed milestone. Confirm this milestone and ask again.")
{
    /// <summary>
    /// The status the milestone actually holds. A milestone's status is the owner's own record and is
    /// already on the screen they would act from, so naming it helps; nothing else about the
    /// milestone is carried here.
    /// </summary>
    public string MilestoneStatus { get; } = status;
}

/// <summary>
/// Nothing in the selected evidence was actually collected (422).
/// </summary>
/// <remarks>
/// Refused at request time rather than after a call, so an owner finds out before they have read a
/// redacted snapshot and are about to press the button that spends money. The message names the
/// remedy the design gives - record a note by hand - because a refusal an owner cannot act on is only
/// half an answer.
/// </remarks>
public sealed class DraftGroundingMissingException()
    : ApiException(
        ContentError.NoGrounding,
        "Nothing in the selected evidence was actually collected, so there is nothing factual to draft "
        + "from. Record a manual note on this milestone first, then ask for the draft again.");
