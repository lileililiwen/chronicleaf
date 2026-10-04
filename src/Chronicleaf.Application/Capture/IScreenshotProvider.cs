using Chronicleaf.Core;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// The one browser-shaped seam in this package, and the only part of a capture this repository does
/// not own (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a seam at all.</b> A capture is a browser navigating a page and returning bytes. The
/// policy that decides whether a browser may be started at all - the sandbox, the egress proxy, the
/// kernel rules - is a property of the deployment and cannot be exercised on a machine that lacks
/// it, so a design that made the browser a direct dependency of the service would be able to prove
/// nothing at all on a developer machine and everything or nothing in production. Splitting it here
/// lets two things be tested honestly and separately: the policy, which is pure and runs anywhere, and
/// the service, which can be driven by a recording provider with no browser on the host.
/// </para>
/// <para>
/// <b>What the seam deliberately does not carry.</b> There is no scope, no owner, no credential, no
/// media root and no database handle on either side of it. The provider is handed a project id, a URL
/// and a viewport, and it hands back bytes and provenance. A provider that could reach the credential
/// vault could photograph a logged-in page, and the isolated browser context this package requires -
/// no saved cookies, no login, no secrets - would be unenforceable rather than merely intended.
/// </para>
/// <para>
/// <b>The production implementation must refuse rather than fall back.</b> When the sandbox is not
/// usable, the implementation raises <see cref="CaptureSandboxUnavailableException"/> and starts no
/// process. An implementation that launched a browser on unrestricted networking to be useful would
/// make the network isolation requirement optional, which is the one property this package exists to
/// provide.
/// </para>
/// </remarks>
public interface IScreenshotProvider
{
    /// <summary>
    /// Navigates one page and returns its picture, or raises.
    /// </summary>
    /// <remarks>
    /// Implementations must not swallow a refusal. A blocked destination, an expired allowance and an
    /// absent sandbox are three different answers an owner needs to be able to tell apart, and a
    /// provider that returned an empty result for all of them would leave the service guessing.
    /// </remarks>
    /// <param name="request">What to capture, already validated and already policy-checked.</param>
    /// <param name="cancellationToken">
    /// Cancelled on owner request, lease loss or the job limit. An implementation must stop the
    /// browser process when this is cancelled, not merely stop waiting for it.
    /// </param>
    /// <exception cref="CaptureTargetBlockedException">
    /// The page tried to reach a destination the policy refuses. The connection was denied, so nothing
    /// reached the far side.
    /// </exception>
    /// <exception cref="CaptureSandboxUnavailableException">
    /// The sandbox could not be started. No browser was launched and no page was visited.
    /// </exception>
    /// <exception cref="CaptureTimedOutException">The page did not finish inside the job limit.</exception>
    /// <exception cref="CapturePageTooLargeException">
    /// The picture is over the byte or pixel bound, so it is not stored.
    /// </exception>
    /// <exception cref="CapturePageNotRenderedException">
    /// The page produced no picture at all - an error page, a download, an empty document.
    /// </exception>
    Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// One page, as the provider is asked to capture it (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// The design's shape, and deliberately nothing added. There is no credential, no header, no
/// user-agent, no cookie jar and no way to name a proxy: a capture is a page an owner chose to
/// photograph, and every one of those is a way to change what the browser presents to the page or
/// what the page is allowed to reach.
/// </remarks>
/// <param name="ProjectId">The project the picture is for. Names the owner, never grants a capability.</param>
/// <param name="Url">The absolute http or https URL to navigate to.</param>
/// <param name="Viewport">The viewport to take the picture at.</param>
/// <param name="WaitSelector">
/// An optional CSS selector to wait for before the picture is taken. Bounded by
/// <see cref="CaptureBounds.MaximumSelectorLength"/> and never evaluated as anything else.
/// </param>
/// <param name="WaitMilliseconds">
/// How long to settle after the page loads, zero to <see cref="CaptureBounds.MaximumWaitMilliseconds"/>.
/// </param>
/// <param name="FullPage">
/// Whether to capture the whole scrollable page rather than the viewport. Bounded by
/// <see cref="CaptureBounds.MaximumPixelCount"/> either way, which is the check that stops a
/// full-page capture of a very long document from becoming an unbounded allocation.
/// </param>
public sealed record CaptureRequest(
    Guid ProjectId,
    Uri Url,
    CaptureViewport Viewport,
    string? WaitSelector,
    int WaitMilliseconds,
    bool FullPage);

/// <summary>
/// One captured page, as the provider gives it back (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// The bytes are the provider's to hand over exactly once; the service writes them through the media
/// store and does not keep a reference. <see cref="Sha256"/> is computed by the provider from the
/// bytes it returns so the service can compare it with what the media store independently recorded -
/// two independent measurements of the same buffer, which is what makes "the picture stored is the
/// picture captured" a checked claim rather than an assumption.
/// </remarks>
/// <param name="Content">The PNG bytes. Disposed by the caller.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of exactly <paramref name="Content"/>.</param>
/// <param name="ByteLength">How many bytes those are.</param>
/// <param name="Width">The decoded pixel width, which is the viewport width unless the page reported otherwise.</param>
/// <param name="Height">The decoded pixel height.</param>
/// <param name="FinalUrl">Where the page actually ended up, after its redirects.</param>
/// <param name="CapturedAt">When the bytes were produced, from the injected clock, always UTC.</param>
public sealed record CaptureResult(
    Stream Content,
    string Sha256,
    long ByteLength,
    int Width,
    int Height,
    Uri FinalUrl,
    DateTimeOffset CapturedAt) : IDisposable
{
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// The bounds this package enforces on a capture, in one place (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every number here is a value the design fixes, and every one of them is a promise a caller may rely
/// on rather than a current setting. A capture job is bounded work: at most three pages, at most two
/// viewports, ten seconds of settling and forty-five seconds of job, on a picture no larger than
/// twenty mebibytes and forty megapixels. An owner asking for a whole site through one request gets a
/// 413, which is a better answer than a job that runs for an hour.
/// </para>
/// <para>
/// <b>The pixel bound is a memory bound.</b> A full-page capture of a long document produces an image
/// whose height is the document's height, and 40 megapixels is where that stops being a picture and
/// starts being a denial of service against the process that decodes it. The check is applied to the
/// produced bytes, because the only honest place to measure a picture is the picture.
/// </para>
/// </remarks>
public static class CaptureBounds
{
    /// <summary>At most three URL paths in one job.</summary>
    public const int MaximumPagesPerJob = 3;

    /// <summary>At most two viewports in one job. The set has exactly two members.</summary>
    public const int MaximumViewportsPerJob = 2;

    /// <summary>At most ten seconds of settling after a page loads.</summary>
    public const int MaximumWaitMilliseconds = 10_000;

    /// <summary>Forty-five seconds for the whole job, all pages and viewports together.</summary>
    public static readonly TimeSpan JobTimeLimit = TimeSpan.FromSeconds(45);

    /// <summary>At most 20 MiB in one PNG.</summary>
    public const long MaximumByteLength = 20L * 1024 * 1024;

    /// <summary>At most 40 megapixels in one PNG.</summary>
    public const long MaximumPixelCount = 40_000_000;

    /// <summary>
    /// At most 512 characters in a wait selector. Bounded because a selector is a string the browser
    /// parses, and a megabyte of one is a way to spend the job's budget on parsing.
    /// </summary>
    public const int MaximumSelectorLength = 512;

    /// <summary>
    /// At most 200 characters in an idempotency key, matching the other job-owning packages.
    /// </summary>
    public const int MaximumIdempotencyKeyLength = 200;

    /// <summary>At most 2048 characters in a requested URL, matching the stored column.</summary>
    public const int MaximumUrlLength = ScreenshotCandidateLimits.MaximumUrlLength;
}

/// <summary>
/// A page that did not finish inside the job limit, so nothing was written for it (504).
/// </summary>
public sealed class CaptureTimedOutException(string detail)
    : Chronicleaf.Application.Common.ApiException(CaptureError.Timeout, detail);

/// <summary>
/// A picture over the byte or pixel bound, so it is refused rather than truncated (422).
/// </summary>
/// <remarks>
/// Refused rather than scaled down. A screenshot that was quietly resized is a picture that no longer
/// shows what the page showed, and its dimensions are exactly the thing a later package will reason
/// about when it decides which candidate to keep.
/// </remarks>
public sealed class CapturePageTooLargeException(string detail)
    : Chronicleaf.Application.Common.ApiException(CaptureError.ValidationFailed, detail);

/// <summary>
/// The page produced no picture at all (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// Raised rather than returned, because for a provider there is no partial answer to give: a browser
/// either rendered something at the requested viewport or it did not, and an empty result would be
/// indistinguishable from a page that legitimately has no content. The service turns this into a
/// per-page outcome so one such page does not end a job whose other pages succeeded.
/// </remarks>
public sealed class CapturePageNotRenderedException(string detail)
    : Exception(detail);
