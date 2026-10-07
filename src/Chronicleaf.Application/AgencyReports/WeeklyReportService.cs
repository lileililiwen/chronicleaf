using System.Globalization;
using Chronicleaf.Application.Agency;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.AgencyReports;

/// <summary>
/// The agency weekly-report use cases over the delivered agency, milestone, evidence, media
/// and export contracts (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A report aggregates; it never generates.</b> The sections are confirmed milestones
/// already inside the window, the complete screenshot items of their evidence heads, and the
/// owner's own explicit notes. No provider is consulted, so no consent hash travels anywhere
/// and no credential is leased: the "reject before model transfer" clause holds structurally,
/// because there is no transfer a foreign project could reach.
/// </para>
/// <para>
/// <b>Approval is a stamp on one stored row, and the export re-checks every binding.</b> The
/// stamp names the revision, the body hash, the attribution hash and the source refs. A
/// regrouped project moves the client's membership generation, and an appended evidence item
/// moves a head revision, so either one makes the stamp describe a state that no longer
/// holds. The export refuses that state as stale rather than serving it.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. Titles,
/// notes, captions and the rendered body travel from a stored row into a response; none
/// appears in a log line, in an exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class WeeklyReportService(
    IClientStore clients,
    IProjectStore projects,
    IMilestoneStore milestones,
    IEvidenceStore evidence,
    IMediaStore media,
    IWeeklyReportStore reports,
    IContentExportAdapter html,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider clock,
    ILogger<WeeklyReportService> logger) : IWeeklyReportService
{
    /// <summary>The audit action recorded when a report is generated.</summary>
    public const string ReportGeneratedAction = "weekly-report.generated";

    /// <summary>The audit action recorded when a report is approved.</summary>
    public const string ReportApprovedAction = "weekly-report.approved";

    /// <summary>The audit action recorded when an approved report is exported.</summary>
    public const string ReportExportedAction = "weekly-report.exported";

    private const int MilestonePageSize = 200;

    /// <inheritdoc />
    public async Task<WeeklyReportView> GenerateAsync(
        OwnerScope owner,
        Guid clientId,
        GenerateWeeklyReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var projectIds = NormalizeProjectIds(request.ProjectIds);
        var inProgressNotes = NormalizeNotes(request.InProgressNotes, "inProgressNotes");
        var nextWeekNotes = NormalizeNotes(request.NextWeekNotes, "nextWeekNotes");
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);

        var (windowStartUtc, windowEndUtc) = WeeklyReportRenderer.ResolveWindow(request.WeekStart, request.TimeZone);

        var membership = await ReadMembershipAsync(owner, clientId, projectIds, cancellationToken).ConfigureAwait(false);

        var attributionHash = WeeklyReportRenderer.AttributionHash(
            clientId,
            membership.Client.ContextRevision,
            projectIds);

        if (idempotencyKey is not null)
        {
            var replayed = await reports
                .FindByIdempotencyKeyAsync(owner.ScopeId, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);

            if (replayed is not null)
            {
                var inputsHash = WeeklyReportRenderer.InputsHash(
                    clientId,
                    request.WeekStart,
                    request.TimeZone,
                    projectIds,
                    inProgressNotes,
                    nextWeekNotes);

                if (replayed.ClientId != clientId || !string.Equals(replayed.InputsHash, inputsHash, StringComparison.Ordinal))
                {
                    throw new WeeklyReportIdempotencyConflictException();
                }

                return WeeklyReportView.From(replayed);
            }
        }

        var collected = await CollectAsync(
                owner,
                membership,
                projectIds,
                request.WeekStart,
                request.TimeZone,
                windowStartUtc,
                windowEndUtc,
                inProgressNotes,
                nextWeekNotes,
                cancellationToken)
            .ConfigureAwait(false);

        var now = clock.GetUtcNow();

        var committed = await unitOfWork.ExecuteAsync(
            async token =>
            {
                // The membership is re-read inside the write, so a regroup that lands between
                // the render and the insert supersedes this generation instead of storing a
                // report against an attribution the owner no longer sees.
                var current = await ReadMembershipAsync(owner, clientId, projectIds, token).ConfigureAwait(false);

                if (current.Client.ContextRevision != membership.Client.ContextRevision
                    || !current.Projects.Select(entry => entry.Id).SequenceEqual(membership.Projects.Select(entry => entry.Id)))
                {
                    throw new RevisionConflictException(current.Client.ContextRevision);
                }

                var latest = await reports
                    .FindLatestAsync(owner.ScopeId, clientId, request.WeekStart, token)
                    .ConfigureAwait(false);

                var report = new WeeklyReport(
                    Guid.NewGuid(),
                    owner.ScopeId,
                    clientId,
                    request.WeekStart,
                    request.TimeZone,
                    windowStartUtc,
                    windowEndUtc,
                    (latest?.Revision ?? 0) + 1,
                    projectIds,
                    attributionHash,
                    collected.SourceRefsJson,
                    WeeklyReportRenderer.InputsHash(
                        clientId,
                        request.WeekStart,
                        request.TimeZone,
                        projectIds,
                        inProgressNotes,
                        nextWeekNotes),
                    idempotencyKey,
                    collected.Body,
                    ContentRevision.Hash(collected.Body),
                    inProgressNotes,
                    nextWeekNotes,
                    approvedRevision: null,
                    approvedBodyHash: null,
                    approvedAttributionHash: null,
                    approvedSourceRefsJson: null,
                    approvedAt: null,
                    now);

                if (!await reports.TryInsertAsync(report, token).ConfigureAwait(false))
                {
                    var reread = await reports
                        .FindLatestAsync(owner.ScopeId, clientId, request.WeekStart, token)
                        .ConfigureAwait(false);

                    throw new RevisionConflictException(reread?.Revision ?? report.Revision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ReportGeneratedAction,
                    report.Id,
                    report.Revision,
                    $"client={clientId:D};week={request.WeekStart};"
                    + $"attribution={attributionHash};sources={collected.SourceRefsJson};"));

                return report;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Weekly report generated for client {ClientId} week {WeekStart} as report {ReportId} revision {Revision}.",
            clientId,
            request.WeekStart,
            committed.Id,
            committed.Revision);

        return WeeklyReportView.From(committed);
    }

    /// <inheritdoc />
    public async Task<WeeklyReportView> GetAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await RequireClientAsync(owner, clientId, cancellationToken).ConfigureAwait(false);

        return WeeklyReportView.From(await RequireReportAsync(owner, clientId, reportId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<WeeklyReportApprovalView> ApproveAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        RequireRevision(expectedRevision);

        var report = await RequireReportAsync(owner, clientId, reportId, cancellationToken).ConfigureAwait(false);

        if (expectedRevision != report.Revision)
        {
            throw new RevisionConflictException(report.Revision);
        }

        var bindings = await CurrentBindingsAsync(owner, clientId, report, cancellationToken).ConfigureAwait(false);

        if (report.ApprovedRevision == report.Revision
            && string.Equals(report.ApprovedBodyHash, report.BodyHash, StringComparison.Ordinal)
            && string.Equals(report.ApprovedAttributionHash, bindings.AttributionHash, StringComparison.Ordinal)
            && string.Equals(report.ApprovedSourceRefsJson, bindings.SourceRefsJson, StringComparison.Ordinal))
        {
            // Approving the state that is already approved changes nothing: a retry is a read
            // of the stored stamp.
            return new WeeklyReportApprovalView(report.Id, report.Revision, report.BodyHash, report.ApprovedAt!.Value);
        }

        if (report.ApprovedRevision is not null)
        {
            // A stamp exists but no longer names this state. The row is kept as history; the
            // remedy is a regeneration, not an overwrite of what was approved.
            throw new ApprovalStaleException("revision");
        }

        if (!string.Equals(bindings.AttributionHash, report.AttributionHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("attribution");
        }

        if (!string.Equals(bindings.SourceRefsJson, report.SourceRefsJson, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("evidence");
        }

        var now = clock.GetUtcNow();

        var stamped = await unitOfWork.ExecuteAsync(
            async token =>
            {
                // The bindings are re-checked inside the write, so a regroup or an evidence
                // append that lands between the read and the stamp supersedes the approval
                // instead of binding it to a state the owner did not see.
                var rechecked = await CurrentBindingsAsync(owner, clientId, report, token).ConfigureAwait(false);

                if (!string.Equals(rechecked.AttributionHash, report.AttributionHash, StringComparison.Ordinal))
                {
                    throw new ApprovalStaleException("attribution");
                }

                if (!string.Equals(rechecked.SourceRefsJson, report.SourceRefsJson, StringComparison.Ordinal))
                {
                    throw new ApprovalStaleException("evidence");
                }

                var affected = await reports.TryApproveAsync(
                        owner.ScopeId,
                        reportId,
                        report.Revision,
                        report.BodyHash,
                        report.AttributionHash,
                        report.SourceRefsJson,
                        now,
                        token)
                    .ConfigureAwait(false);

                if (affected == 0)
                {
                    var reread = await reports.FindAsync(owner.ScopeId, reportId, token).ConfigureAwait(false);

                    if (reread is not null
                        && reread.ApprovedRevision == reread.Revision
                        && reread.ApprovedAt is not null)
                    {
                        return reread.ApprovedAt.Value;
                    }

                    throw new RevisionConflictException(reread?.Revision ?? report.Revision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ReportApprovedAction,
                    reportId,
                    report.Revision,
                    $"client={clientId:D};week={report.WeekStart};attribution={report.AttributionHash};"));

                return now;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Weekly report {ReportId} revision {Revision} approved.",
            reportId,
            report.Revision);

        return new WeeklyReportApprovalView(reportId, report.Revision, report.BodyHash, stamped);
    }

    /// <inheritdoc />
    public async Task<WeeklyReportExport> ExportAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        string format,
        long? revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(format);

        if (!string.Equals(format, "markdown", StringComparison.Ordinal)
            && !string.Equals(format, "html", StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "format",
                "The weekly report is exported as markdown or html, and nothing else.");
        }

        var client = await RequireClientAsync(owner, clientId, cancellationToken).ConfigureAwait(false);
        var report = await RequireReportAsync(owner, clientId, reportId, cancellationToken).ConfigureAwait(false);

        if (report.ApprovedRevision is null)
        {
            throw new ApprovalRequiredException();
        }

        var wanted = revision ?? report.ApprovedRevision.Value;

        if (wanted != report.ApprovedRevision.Value || wanted != report.Revision)
        {
            // The approval names exactly one stored revision: a newer generation supersedes
            // it, and an explicit older revision is no longer authorized.
            throw new ApprovalStaleException("revision");
        }

        if (!string.Equals(ContentRevision.Hash(report.BodyMarkdown), report.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("body");
        }

        var bindings = await CurrentBindingsAsync(owner, clientId, report, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(bindings.AttributionHash, report.ApprovedAttributionHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("attribution");
        }

        if (!string.Equals(bindings.SourceRefsJson, report.ApprovedSourceRefsJson, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("evidence");
        }

        WeeklyReportExport export;

        if (string.Equals(format, "markdown", StringComparison.Ordinal))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(report.BodyMarkdown);

            export = new WeeklyReportExport(
                $"weekly-report-{reportId:N}-r{report.Revision.ToString(CultureInfo.InvariantCulture)}.md",
                "text/markdown; charset=utf-8",
                bytes,
                ContentRevision.Hash(report.BodyMarkdown));
        }
        else
        {
            export = await ExportHtmlAsync(owner, client, report, bindings, cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.ExecuteAsync(
            token =>
            {
                // The audit row names the revision and the approval, never the prose: an export
                // is a record that bytes left the building, not a copy of them.
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ReportExportedAction,
                    reportId,
                    report.Revision,
                    $"revision={report.Revision.ToString(CultureInfo.InvariantCulture)};format={format};"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Weekly report {ReportId} revision {Revision} exported as {Format}.",
            reportId,
            report.Revision,
            format);

        return export;
    }

    private async Task<WeeklyReportExport> ExportHtmlAsync(
        OwnerScope owner,
        Client client,
        WeeklyReport report,
        ReportBindings bindings,
        CancellationToken cancellationToken)
    {
        // The pictures the approval covers, with the bytes the media store holds right now.
        // A picture whose bytes are gone ends the export as media_unavailable: the approval
        // bound pictures the artifact would have to carry, so serving the text without them
        // would be a silent claim of completeness.
        var pictures = new List<ApprovedExportPicture>();

        foreach (var screenshot in bindings.Screenshots.OrderBy(shot => shot.MediaId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            MediaContent content;

            try
            {
                content = await media.OpenAsync(owner, screenshot.MediaId, cancellationToken).ConfigureAwait(false);
            }
            catch (MediaNotFoundException)
            {
                throw new MediaUnavailableException();
            }

            await using (content.ConfigureAwait(false))
            {
                if (!string.Equals(content.Ref.MediaType, MediaTypes.Png, StringComparison.Ordinal))
                {
                    throw new UnsupportedMediaTypeException();
                }

                pictures.Add(new ApprovedExportPicture(
                    screenshot.MediaId,
                    screenshot.Caption,
                    content.Ref.MediaType,
                    await ReadFullyAsync(content.Content, cancellationToken).ConfigureAwait(false)));
            }
        }

        // The snapshot's single evidence revision is the newest bound source head: the
        // per-project bindings live in the approval row this export already re-checked, and
        // the receipt carries the newest of them. Zero means the window bound no evidence.
        var artifact = await html.ExportAsync(
                new ApprovedAssetSnapshot(
                    report.Id,
                    report.Revision,
                    $"Weekly progress — {client.Name} — {report.WeekStart}",
                    report.BodyMarkdown,
                    report.BodyHash,
                    bindings.MaxSourceRevision,
                    pictures),
                cancellationToken)
            .ConfigureAwait(false);

        return new WeeklyReportExport(artifact.FileName, artifact.MediaType, artifact.Bytes, artifact.Sha256);
    }

    // -----------------------------------------------------------------------------------------
    // Reads. Every mutation re-reads its own inputs, so it can tell an absent record from a
    // regrouped one from a revision that moved, and answer with the right refusal.
    // -----------------------------------------------------------------------------------------

    private async Task<Membership> ReadMembershipAsync(
        OwnerScope owner,
        Guid clientId,
        IReadOnlyList<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        var client = await clients
            .FindAsync(owner.ScopeId, clientId, cancellationToken)
            .ConfigureAwait(false) ?? throw new ResourceNotFoundException("client");

        if (client.IsArchived)
        {
            throw new ClientArchivedException();
        }

        var names = new List<(Guid Id, string Name)>(projectIds.Count);

        foreach (var projectId in projectIds)
        {
            var project = await projects
                .FindAsync(owner.ScopeId, projectId, cancellationToken)
                .ConfigureAwait(false) ?? throw new ResourceNotFoundException("project");

            // Only a project grouped under exactly this client is covered. Anything else -
            // personal, grouped elsewhere, archived-but-moved or out of scope - is refused
            // before the render, so no byte of another client's work reaches the report.
            if (project.ClientId != clientId)
            {
                throw new WeeklyReportProjectOutsideClientException();
            }

            names.Add((project.Id, project.Name));
        }

        return new Membership(client, names);
    }

    private async Task<Client> RequireClientAsync(
        OwnerScope owner,
        Guid clientId,
        CancellationToken cancellationToken) =>
        await clients
            .FindAsync(owner.ScopeId, clientId, cancellationToken)
            .ConfigureAwait(false) ?? throw new ResourceNotFoundException("client");

    private async Task<WeeklyReport> RequireReportAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        var report = await reports
            .FindAsync(owner.ScopeId, reportId, cancellationToken)
            .ConfigureAwait(false);

        // A row for another client is the delivered 404, so a caller cannot probe for another
        // client's reports by comparing answers.
        if (report is null || report.ClientId != clientId)
        {
            throw new ResourceNotFoundException("weekly report");
        }

        return report;
    }

    private async Task<CollectedWindow> CollectAsync(
        OwnerScope owner,
        Membership membership,
        IReadOnlyList<Guid> projectIds,
        string weekStart,
        string timeZone,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        string inProgressNotes,
        string nextWeekNotes,
        CancellationToken cancellationToken)
    {
        var names = membership.Projects.ToDictionary(entry => entry.Id, entry => entry.Name);
        var projectSections = new List<WeeklyReportProject>(projectIds.Count);
        var sources = new List<WeeklyReportSource>();

        foreach (var projectId in projectIds)
        {
            var windowMilestones = await WindowMilestonesAsync(
                    owner,
                    projectId,
                    windowStartUtc,
                    windowEndUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            var screenshots = new List<WeeklyReportScreenshot>();

            foreach (var milestone in windowMilestones)
            {
                var head = await evidence
                    .FindHeadAsync(owner.ScopeId, milestone.Id, cancellationToken)
                    .ConfigureAwait(false);

                sources.Add(new WeeklyReportSource(
                    projectId,
                    milestone.Id,
                    head?.Id,
                    head?.Revision ?? 0));

                if (head is null)
                {
                    continue;
                }

                screenshots.AddRange(head.Items
                    .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                        && item.MediaId is not null
                        && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
                    .Select(item => new WeeklyReportScreenshot(item.MediaId!.Value, item.Text)));
            }

            projectSections.Add(new WeeklyReportProject(
                projectId,
                names[projectId],
                [.. windowMilestones.Select(milestone => new WeeklyReportMilestone(
                    milestone.Id,
                    milestone.Title,
                    milestone.OccurredAt,
                    milestone.Version,
                    milestone.KnownLimitations,
                    milestone.NextSteps))],
                screenshots));
        }

        var body = WeeklyReportRenderer.Render(new WeeklyReportContent(
            membership.Client.Name,
            weekStart,
            timeZone,
            windowStartUtc,
            windowEndUtc,
            projectSections,
            sources,
            inProgressNotes,
            nextWeekNotes));

        return new CollectedWindow(body, WeeklyReportRenderer.SourceRefsJson(sources));
    }

    private async Task<IReadOnlyList<Milestone>> WindowMilestonesAsync(
        OwnerScope owner,
        Guid projectId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        CancellationToken cancellationToken)
    {
        var collected = new List<Milestone>();
        DateTimeOffset? occurredAfter = windowStartUtc.AddTicks(-1);
        Guid? idAfter = null;

        while (true)
        {
            var page = await milestones.ListChronologicalAsync(
                    owner.ScopeId,
                    projectId,
                    includeCandidates: false,
                    occurredAfter,
                    idAfter,
                    MilestonePageSize,
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            foreach (var milestone in page)
            {
                if (milestone.OccurredAt >= windowEndUtc)
                {
                    break;
                }

                if (milestone.OccurredAt >= windowStartUtc && milestone.IsConfirmed)
                {
                    collected.Add(milestone);
                }
            }

            var last = page[^1];

            if (page.Count < MilestonePageSize || last.OccurredAt >= windowEndUtc)
            {
                break;
            }

            occurredAfter = last.OccurredAt;
            idAfter = last.Id;
        }

        return collected;
    }

    private async Task<ReportBindings> CurrentBindingsAsync(
        OwnerScope owner,
        Guid clientId,
        WeeklyReport report,
        CancellationToken cancellationToken)
    {
        var client = await clients
            .FindAsync(owner.ScopeId, clientId, cancellationToken)
            .ConfigureAwait(false) ?? throw new ResourceNotFoundException("client");

        foreach (var projectId in report.ProjectIds)
        {
            var project = await projects
                .FindAsync(owner.ScopeId, projectId, cancellationToken)
                .ConfigureAwait(false);

            // A project that left the client breaks the attribution the report pinned. Absent
            // and regrouped are one answer here: either way the stored set no longer holds.
            if (project?.ClientId != clientId)
            {
                return new ReportBindings(
                    WeeklyReportRenderer.AttributionHash(clientId, -1, report.ProjectIds),
                    "[]",
                    [],
                    0);
            }
        }

        var attributionHash = WeeklyReportRenderer.AttributionHash(
            clientId,
            client.ContextRevision,
            report.ProjectIds);

        var sources = new List<WeeklyReportSource>();
        var screenshots = new List<WeeklyReportScreenshot>();
        long maxSourceRevision = 0;

        foreach (var projectId in report.ProjectIds)
        {
            var windowMilestones = await WindowMilestonesAsync(
                    owner,
                    projectId,
                    report.WindowStartUtc,
                    report.WindowEndUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var milestone in windowMilestones)
            {
                var head = await evidence
                    .FindHeadAsync(owner.ScopeId, milestone.Id, cancellationToken)
                    .ConfigureAwait(false);

                sources.Add(new WeeklyReportSource(
                    projectId,
                    milestone.Id,
                    head?.Id,
                    head?.Revision ?? 0));

                if (head is not null)
                {
                    maxSourceRevision = Math.Max(maxSourceRevision, head.Revision);

                    screenshots.AddRange(head.Items
                        .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                            && item.MediaId is not null
                            && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
                        .Select(item => new WeeklyReportScreenshot(item.MediaId!.Value, item.Text)));
                }
            }
        }

        return new ReportBindings(
            attributionHash,
            WeeklyReportRenderer.SourceRefsJson(sources),
            screenshots,
            maxSourceRevision);
    }

    private static IReadOnlyList<Guid> NormalizeProjectIds(IReadOnlyList<Guid>? projectIds)
    {
        if (projectIds is null || projectIds.Count == 0)
        {
            throw new ValidationFailedException("projectIds", "A report covers at least one project.");
        }

        if (projectIds.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException("projectIds", "Every projectIds entry must be a project id.");
        }

        var distinct = projectIds.Distinct().ToList();

        if (distinct.Count > WeeklyReport.MaxProjects)
        {
            throw new ValidationFailedException(
                "projectIds",
                $"A report covers at most {WeeklyReport.MaxProjects.ToString(CultureInfo.InvariantCulture)} projects.");
        }

        distinct.Sort();
        return distinct;
    }

    private static string NormalizeNotes(string? notes, string field)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        var trimmed = notes.Trim();

        if (trimmed.Length > WeeklyReport.MaxNoteChars)
        {
            throw new ValidationFailedException(
                field,
                $"Owner notes are at most {WeeklyReport.MaxNoteChars.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        return trimmed;
    }

    private static string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();

        if (trimmed.Length > WeeklyReport.IdempotencyKeyMaxLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key is at most {WeeklyReport.IdempotencyKeyMaxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        return trimmed;
    }

    private static void RequireRevision(long expectedRevision)
    {
        if (expectedRevision < 1)
        {
            throw new ValidationFailedException("If-Match", "The revision must be a positive integer.");
        }
    }

    private static async Task<byte[]> ReadFullyAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
        return buffered.ToArray();
    }

    private sealed record Membership(Client Client, IReadOnlyList<(Guid Id, string Name)> Projects);

    private sealed record CollectedWindow(string Body, string SourceRefsJson);

    private sealed record ReportBindings(
        string AttributionHash,
        string SourceRefsJson,
        IReadOnlyList<WeeklyReportScreenshot> Screenshots,
        long MaxSourceRevision);
}
