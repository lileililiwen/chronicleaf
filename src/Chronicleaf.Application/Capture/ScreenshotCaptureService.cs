using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using MediaRef = Chronicleaf.Application.Media.MediaRef;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// The capture service: what an owner may ask a milestone to photograph, and what a job does when it
/// runs (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This is where the design's contract is enforced, and it is enforced twice for two different
/// reasons. A request is checked when it arrives, so a destination that cannot be photographed is a
/// 403 the owner sees immediately rather than a job that can only fail. The same policy is applied
/// again immediately before every page, because a name that resolved to a public address a minute ago
/// may resolve to a private one now - that is DNS rebinding, and it is why the design says "revalidate
/// before every connection and after every redirect" rather than "validate the request once". The
/// kernel-enforced half of that second check is the sandbox's; this call is the decision the sandbox
/// and the provider share, so the two cannot disagree about what is allowed.
/// </para>
/// <para>
/// <b>A media failure fails the candidate.</b> The bytes are written through the delivered
/// <see cref="IMediaStore"/>, and a row is only inserted once that write has returned a ready record.
/// If the write raises, nothing is inserted - so the table never holds a candidate pointing at an
/// image that does not exist, which would be a picture the owner can see listed and cannot open.
/// </para>
/// <para>
/// <b>What this class will not do.</b> It does not select, reorder, promote or delete a candidate, and
/// it does not append anything to an evidence bundle. Both are screenshot-curation's, and doing either
/// here would make selection a side effect of asking for a picture - the one thing the design names as
/// the reason the two packages are separate.
/// </para>
/// </remarks>
public sealed class ScreenshotCaptureService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IScreenshotCandidateStore candidates,
    ICaptureRequestStore requests,
    IScreenshotProvider provider,
    CaptureSettings settings,
    IMediaStore media,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ScreenshotCaptureService> logger) : IScreenshotCaptureService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to: a
    /// milestone, so the delivered supersede check reads the milestone's own revision and turns a
    /// milestone edited mid-run into a superseded operation rather than a commit against a record
    /// somebody else has since changed.
    /// </summary>
    public const string CaptureOperationKind = "milestone.screenshot-capture";

    /// <summary>The audit action recorded when a capture is asked for.</summary>
    public const string CaptureRequestedAction = "capture.requested";

    /// <summary>
    /// The media type a capture produces. Named here so the provider and the store agree on it, and
    /// so a reader knows a candidate is always a PNG without opening it.
    /// </summary>
    public const string CaptureMediaType = "image/png";

    // -----------------------------------------------------------------------------------------
    // Asking for a capture.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<ScreenshotCaptureAccepted> RequestCaptureAsync(
        OwnerScope owner,
        Guid milestoneId,
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = ValidateRequest(request);

        // The destination is checked here, before a job exists, so an owner who asked for a private
        // address is told so now. A host that is an IP literal is judged here because there is nothing
        // to defer; a name is judged again when the job runs, because the address it resolves to is not
        // knowable until then.
        foreach (var url in prepared.Urls)
        {
            if (!CaptureUrlPolicy
                .EvaluateTarget(url, settings.DeploymentMode, settings.LocalAllowances, clock.GetUtcNow())
                .Allowed)
            {
                throw new CaptureTargetBlockedException();
            }
        }

        // The milestone, its project and the milestone's revision are all read before a job exists, so
        // a request that could never run is refused here rather than as a failed job an owner has to
        // go and find.
        var target = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("milestone");

                var project = await projects
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                return new PreparedCapture(milestone.ProjectId, milestone.Revision);
            },
            cancellationToken).ConfigureAwait(false);

        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                CaptureOperationKind,
                milestoneId,
                target.MilestoneRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The request row is written after the job exists, because it names the job. A replay under the
        // same key gets the original job back and its own insert changes no row, so one capture can
        // never grow two request rows.
        var row = new Core.CaptureRequestRow(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            target.ProjectId,
            milestoneId,
            [.. prepared.Urls.Select(url => url.AbsoluteUri)],
            [.. prepared.Viewports.Select(viewport => viewport.Name)],
            prepared.WaitSelector,
            prepared.WaitMilliseconds,
            prepared.FullPage,
            request.ExpectedBundleRevision,
            clock.GetUtcNow());

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await requests.TryInsertAsync(row, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        CaptureRequestedAction,
                        row.Id,
                        request.ExpectedBundleRevision,
                        "screenshot capture requested"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Screenshot capture requested for milestone {MilestoneId} as operation {OperationId} ({Urls} urls, {Viewports} viewports, created {Created}).",
            milestoneId,
            accepted.Operation.Id,
            row.Urls.Count,
            row.Viewports.Count,
            inserted);

        return new ScreenshotCaptureAccepted(accepted.Operation, accepted.Created);
    }

    // -----------------------------------------------------------------------------------------
    // Reading candidates.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScreenshotCandidate>> ListCandidatesAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The milestone is read first and scoped, so a milestone in another installation is a 404
        // rather than an empty list that happens to look like "nothing captured yet".
        _ = await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        return await candidates
            .ListAsync(owner.ScopeId, milestoneId, ListQueryPolicy.MaxPageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------
    // Running the job.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<CaptureJobOutcome> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A job is handed an operation id and nothing else, so the words the owner typed are read back
        // from the request row. A job whose row is gone cannot be captured honestly and says so rather
        // than guessing at a target.
        var row = await requests
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(CaptureError.RequestMissing);

        // A stored viewport name this package does not recognise cannot be rounded into one it does: a
        // viewport quietly substituted is a picture that does not match its own label.
        var viewports = ResolveViewports(row)
            ?? throw new OperationPermanentException(CaptureError.ValidationFailed);

        // The milestone's revision is re-read here as well as at commit. The delivered conditional
        // commit is the authority, but a job that can see the milestone has moved does not need to
        // spend the rest of its budget photographing pages nobody can attach.
        _ = await milestones
            .FindAsync(owner.ScopeId, row.MilestoneId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(CaptureError.NotFound);

        var outcomes = new List<CapturePageOutcome>(row.Urls.Count * viewports.Length);

        foreach (var url in row.Urls)
        {
            foreach (var viewport in viewports)
            {
                cancellationToken.ThrowIfCancellationRequested();

                outcomes.Add(await CapturePageAsync(owner, row, url, viewport, cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        var captured = outcomes.Count(page => page.Succeeded);

        if (captured == 0)
        {
            // Nothing at all was produced, so there is no partial result to report and the job fails
            // with the reason it hit. A job that produced nothing must not commit a result reference,
            // because a reference naming zero candidates is indistinguishable from one that names a job
            // which never ran.
            throw new OperationPermanentException(outcomes[0].Failure?.Code ?? CaptureError.NoPageCaptured);
        }

        return CaptureJobOutcome.FromPages(outcomes);
    }

    /// <summary>
    /// Captures one URL at one viewport, and writes a candidate if the bytes were stored.
    /// </summary>
    /// <remarks>
    /// Every failure below becomes a per-page outcome rather than ending the job. That is what makes a
    /// partial run a partial run: the design permits one page of three to fail while the other two are
    /// kept, and the only way to express that is for the failure to be a value rather than an exception
    /// on the way out.
    /// </remarks>
    private async Task<CapturePageOutcome> CapturePageAsync(
        OwnerScope owner,
        CaptureRequestRow row,
        string url,
        CaptureViewport viewport,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
        {
            // Only reachable if a stored row was edited outside this package. Refused rather than
            // guessed at, for the same reason the policy refuses an unparseable destination.
            return Failed(url, viewport, CapturePageError.TargetBlocked);
        }

        try
        {
            // Re-checked here, not only at request time. This is the call that makes DNS rebinding
            // detectable: whatever this host resolves to right now is what will be dialled, so the
            // address that is checked is the address that connects. The sandbox's kernel rules enforce
            // the same decision independently; this is the shared statement of what it is.
            if (!CaptureUrlPolicy
                .EvaluateTarget(target, settings.DeploymentMode, settings.LocalAllowances, clock.GetUtcNow())
                .Allowed)
            {
                return Failed(url, viewport, CapturePageError.TargetBlocked);
            }

            var request = new CaptureRequest(
                row.ProjectId,
                target,
                viewport,
                row.WaitSelector,
                row.WaitMilliseconds,
                row.FullPage);

            // One linked deadline for the whole job, so a page that hangs cannot silently consume the
            // budget and this limit is the same forty-five seconds the delivered queue enforces.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(CaptureBounds.JobTimeLimit);

            using var capture = await provider.CaptureAsync(request, budget.Token).ConfigureAwait(false);

            if (capture.ByteLength > CaptureBounds.MaximumByteLength)
            {
                return Failed(url, viewport, CapturePageError.TooLarge);
            }

            if ((long)capture.Width * capture.Height > CaptureBounds.MaximumPixelCount)
            {
                return Failed(url, viewport, CapturePageError.TooLarge);
            }

            // The bytes go to the delivered media store, which generates the key, verifies the image and
            // registers a ready record. Nothing is written to this package's table before that returns,
            // so a candidate can never point at bytes that were not stored.
            MediaRef stored;
            try
            {
                stored = await media.PutAsync(
                    new OwnerScope(owner.ScopeId, owner.ActorId),
                    new PutMediaInput(row.ProjectId, capture.Content, CaptureMediaType, row.OperationId),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is MediaTooLargeException or MediaImageTooLargeException)
            {
                // The delivered store refused the bytes on its own bounds, which is the same verdict this
                // package reached from the length and the dimensions. A page failure rather than a raise,
                // so one oversized page does not discard the pages that succeeded.
                logger.LogInformation(
                    "A capture of {Url} at {Viewport} was refused by the media store as too large.",
                    url,
                    viewport.Name);

                return Failed(url, viewport, CapturePageError.TooLarge);
            }
            catch (Exception failure) when (failure is MediaStorageUnavailableException or UnsupportedMediaTypeException)
            {
                // A media write that fails fails the candidate. No row is inserted, so the table cannot
                // hold a candidate whose picture was never stored, and the owner is told this page
                // produced nothing rather than being shown a picture that will not open.
                logger.LogWarning(
                    "Media write failed for a capture of {Url} at {Viewport} on milestone {MilestoneId}; no candidate was written.",
                    url,
                    viewport.Name,
                    row.MilestoneId);

                throw new OperationPermanentException(CaptureError.MediaWriteFailed);
            }

            var candidate = new ScreenshotCandidate(
                Guid.NewGuid(),
                owner.ScopeId,
                row.ProjectId,
                row.MilestoneId,
                stored.Id,
                row.OperationId,
                url,
                capture.FinalUrl.AbsoluteUri,
                viewport,
                capture.CapturedAt,
                clock.GetUtcNow());

            var created = await unitOfWork.ExecuteAsync(
                async token => await candidates.TryInsertAsync(candidate, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (!created)
            {
                // The unique tuple (operation_id, source_url, viewport) already holds this exact page. A
                // retry or a resumed job arrived here twice, and writing again would be a second copy
                // of one picture; the stored media is retired so the retry leaves nothing behind.
                await RetireQuietlyAsync(owner, stored, cancellationToken).ConfigureAwait(false);

                return Failed(url, viewport, CapturePageError.Duplicate);
            }

            return new CapturePageOutcome(url, viewport.Name, candidate.Id, null);
        }
        catch (CaptureTargetBlockedException)
        {
            return Failed(url, viewport, CapturePageError.TargetBlocked);
        }
        catch (CaptureSandboxUnavailableException)
        {
            // A missing sandbox is not a page that failed to load. It is this host being unable to run a
            // browser safely at all, so it ends the job rather than being retried page by page: there
            // is no honest per-page answer when the thing that would take the picture is absent.
            throw new OperationPermanentException(CaptureError.JobSandboxUnavailable);
        }
        catch (CapturePageTooLargeException)
        {
            return Failed(url, viewport, CapturePageError.TooLarge);
        }
        catch (CaptureTimedOutException)
        {
            return Failed(url, viewport, CapturePageError.TimedOut);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The page ran out of time inside the job budget. A cancellation by the owner or by lease
            // loss is not caught here: that is a different condition and the delivered worker owns it.
            return Failed(url, viewport, CapturePageError.TimedOut);
        }
        catch (CapturePageNotRenderedException)
        {
            return Failed(url, viewport, CapturePageError.NotRendered);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Validation.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Checks every bound the design fixes, before a job exists.
    /// </summary>
    /// <remarks>
    /// A field that is not one this package accepts is a 422; a request that asks for more work than
    /// the bounds allow is a 413. They are different refusals with different next steps - correct the
    /// value, or ask for less - and folding them together would tell an owner to retype something that
    /// was never the problem.
    /// </remarks>
    private static PreparedRequest ValidateRequest(ScreenshotCaptureRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > CaptureBounds.MaximumIdempotencyKeyLength)
        {
            errors["idempotencyKey"] =
                [$"An idempotency key must be 1 to {CaptureBounds.MaximumIdempotencyKeyLength} characters."];
        }

        if (request.WaitMilliseconds < 0 || request.WaitMilliseconds > CaptureBounds.MaximumWaitMilliseconds)
        {
            errors["waitMilliseconds"] = [$"Wait between 0 and {CaptureBounds.MaximumWaitMilliseconds} milliseconds."];
        }

        if (request.WaitSelector is { Length: > CaptureBounds.MaximumSelectorLength })
        {
            errors["waitSelector"] = [$"A wait selector is at most {CaptureBounds.MaximumSelectorLength} characters."];
        }

        if (request.ExpectedBundleRevision < 0)
        {
            errors["expectedBundleRevision"] = ["A bundle revision is zero or greater."];
        }

        if (request.Urls is null || request.Urls.Count == 0)
        {
            errors["urls"] = ["Name at least one URL to capture."];
        }
        else if (request.Urls.Count > CaptureBounds.MaximumPagesPerJob)
        {
            throw new PayloadTooLargeException(CaptureBounds.MaximumPagesPerJob);
        }

        if (request.Viewports is null || request.Viewports.Count == 0)
        {
            errors["viewports"] = ["Name at least one viewport."];
        }
        else if (request.Viewports.Count > CaptureBounds.MaximumViewportsPerJob)
        {
            throw new PayloadTooLargeException(CaptureBounds.MaximumViewportsPerJob);
        }

        var urls = new List<Uri>();

        foreach (var url in request.Urls ?? [])
        {
            if (url is null || !url.IsAbsoluteUri || url.AbsoluteUri.Length > CaptureBounds.MaximumUrlLength)
            {
                errors["urls"] = ["Each URL is absolute and at most 2048 characters."];
                continue;
            }

            // A scheme, userinfo and absoluteness problem is a 422 the owner can fix by retyping; a
            // destination that merely resolves somewhere forbidden is a 403 raised by the caller below.
            if (!CaptureUrlPolicy.Evaluate(url).Allowed)
            {
                errors["urls"] = ["Each URL is an absolute http or https URL with no credentials."];
                continue;
            }

            if (!urls.Contains(url))
            {
                urls.Add(url);
            }
        }

        var viewports = new List<CaptureViewport>();

        foreach (var viewport in request.Viewports ?? [])
        {
            if (viewport is null || CaptureViewport.All.All(known => !known.Equals(viewport)))
            {
                errors["viewports"] = ["Each viewport is either desktop or mobile."];
                continue;
            }

            if (!viewports.Contains(viewport))
            {
                viewports.Add(viewport);
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return new PreparedRequest(
            urls,
            viewports,
            request.WaitSelector?.Trim() is { Length: > 0 } selector ? selector : null,
            request.WaitMilliseconds,
            request.FullPage);
    }

    private static CaptureViewport[]? ResolveViewports(CaptureRequestRow row)
    {
        var resolved = new List<CaptureViewport>(row.Viewports.Count);

        foreach (var name in row.Viewports)
        {
            var viewport = CaptureViewport.FromName(name);

            if (viewport is null)
            {
                return null;
            }

            resolved.Add(viewport);
        }

        return [.. resolved];
    }

    // -----------------------------------------------------------------------------------------
    // Small helpers.
    // -----------------------------------------------------------------------------------------

    private static CapturePageOutcome Failed(string url, CaptureViewport viewport, string code) =>
        new(url, viewport.Name, null, new CapturePageFailure(code, url, viewport.Name));

    /// <summary>
    /// Retires an image this run stored but did not keep, and never lets that become a job failure.
    /// </summary>
    /// <remarks>
    /// The orphan reconciler the delivered media package ships is the real safety net; this is the eager
    /// path for the one case it would otherwise have to wait for. A retirement that fails is logged and
    /// swallowed, because the page is already being reported as a failure and turning a duplicate into a
    /// hard error would make a retry look worse than the thing it was avoiding.
    /// </remarks>
    private async Task RetireQuietlyAsync(OwnerScope owner, MediaRef stored, CancellationToken cancellationToken)
    {
        try
        {
            await media
                .RetireAsync(new OwnerScope(owner.ScopeId, owner.ActorId), stored.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is MediaInUseException or MediaNotFoundException or MediaStorageUnavailableException)
        {
            logger.LogWarning(
                "A duplicate capture of media {MediaId} could not be retired immediately; the reconciler will release it.",
                stored.Id);
        }
    }

    private sealed record PreparedCapture(Guid ProjectId, long MilestoneRevision);

    private sealed record PreparedRequest(
        IReadOnlyList<Uri> Urls,
        IReadOnlyList<CaptureViewport> Viewports,
        string? WaitSelector,
        int WaitMilliseconds,
        bool FullPage);
}
