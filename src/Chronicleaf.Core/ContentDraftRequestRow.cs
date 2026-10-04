using System.Globalization;

namespace Chronicleaf.Core;

/// <summary>
/// The words an owner typed when they asked for a draft, kept where the job that runs can read them
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This row exists for the same reason <c>capture_requests</c> and <c>sync_sources</c> do: a queued
/// job payload carries an operation id and nothing else, so every value the worker needs has to be
/// durable somewhere it can be read back. A handler that had to hold the selection in memory would be
/// a handler that could not survive the restart its whole queue is built around.
/// </para>
/// <para>
/// <b>What is here is a reference, never a secret and never the input.</b> <see cref="PreviewId"/>
/// names the owner-authorized snapshot, and <see cref="ConsentHash"/> is the binding over it - the
/// delivered generation contract's own value, recomputed and compared at commit time. The evidence
/// itself is not copied into this row: the bytes that will be sent are read back from the frozen
/// preview, which is what makes "the same consented bytes" a fact about one stored document rather
/// than about this table.
/// </para>
/// <para>
/// <see cref="EvidenceRevision"/> and <see cref="VoiceRevision"/> are the revisions the owner was
/// looking at when they asked. They are re-read and compared at commit, so a draft whose input moved
/// is superseded rather than committed against a bundle the owner never saw.
/// </para>
/// </remarks>
/// <param name="id">This request's identifier.</param>
/// <param name="scopeId">The owning scope; the worker's lookup is scoped by it.</param>
/// <param name="operationId">The job that will perform the generation.</param>
/// <param name="projectId">The project the milestone belongs to.</param>
/// <param name="milestoneId">The milestone the draft is about.</param>
/// <param name="previewId">The owner-authorized generation preview the request is bound to.</param>
/// <param name="consentHash">The SHA-256 binding the owner presented for that preview.</param>
/// <param name="evidenceRevision">The evidence head revision the owner was looking at.</param>
/// <param name="voiceRevision">The project voice profile revision the owner was looking at.</param>
/// <param name="providerId">The provider slot named in the request; must agree with the preview.</param>
/// <param name="model">The model named in the request; must agree with the preview.</param>
/// <param name="createdAt">When the request was accepted; UTC.</param>
public sealed class ContentDraftRequestRow
{
    public ContentDraftRequestRow(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid projectId,
        Guid milestoneId,
        Guid previewId,
        string consentHash,
        long evidenceRevision,
        long voiceRevision,
        string providerId,
        string model,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A draft request needs a non-empty id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A draft request needs the job that will run it.", nameof(operationId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A draft request needs a non-empty project.", nameof(projectId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A draft request needs a non-empty milestone.", nameof(milestoneId));
        }

        if (previewId == Guid.Empty)
        {
            // Without the preview there is no consent and there is no frozen input, so a request that
            // could be constructed without one is a request that could reach a provider unconsented.
            throw new ArgumentException("A draft request needs the authorized preview it is bound to.", nameof(previewId));
        }

        if (string.IsNullOrWhiteSpace(consentHash))
        {
            throw new ArgumentException("A draft request needs the consent binding it was made with.", nameof(consentHash));
        }

        if (consentHash.Length > ConsentHashLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consentHash),
                consentHash.Length,
                $"A consent binding is {ConsentHashLength} hexadecimal characters.");
        }

        if (evidenceRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceRevision),
                evidenceRevision,
                "A draft is only ever asked for against a committed evidence revision.");
        }

        if (voiceRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(voiceRevision), voiceRevision, "A voice revision is zero or greater.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (providerId.Length > ContentAssetLimits.LabelMaxLength ||
            model.Length > ContentAssetLimits.LabelMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerId),
                "The provider and model labels are bounded by the column width.");
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        ProjectId = projectId;
        MilestoneId = milestoneId;
        PreviewId = previewId;
        ConsentHash = consentHash;
        EvidenceRevision = evidenceRevision;
        VoiceRevision = voiceRevision;
        ProviderId = providerId;
        Model = model;
        CreatedAt = createdAt;
    }

    /// <summary>The width of the <c>consent_hash</c> column: 64 lowercase hex characters.</summary>
    public const int ConsentHashLength = 64;

    public Guid Id { get; }

    public Guid ScopeId { get; }

    /// <summary>The job, and half of the uniqueness tuple the database enforces.</summary>
    public Guid OperationId { get; }

    public Guid ProjectId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The frozen, owner-authorized input this draft will be built from.</summary>
    public Guid PreviewId { get; }

    /// <summary>The binding the owner presented. Recomputed at commit and compared, never trusted.</summary>
    public string ConsentHash { get; }

    public long EvidenceRevision { get; }

    public long VoiceRevision { get; }

    public string ProviderId { get; }

    public string Model { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>A stable diagnostic label for a request row. It names the job, never the consent.</summary>
    public static string Label(Guid requestId) =>
        string.Create(CultureInfo.InvariantCulture, $"content-draft-requests/{requestId:N}");
}
