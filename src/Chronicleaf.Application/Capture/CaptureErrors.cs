using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// The capture codes this package adds to the delivered envelope (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// They extend the repository's <c>{code,message,traceId,fieldErrors?}</c> shape without changing it,
/// and each one has exactly one meaning. <see cref="NotFound"/> reuses the delivered 404 rather than
/// inventing a capture-specific one, so "there is nothing here" is the same answer for an unknown
/// milestone and for one belonging to another installation - a caller cannot probe for another
/// owner's milestones by comparing codes.
/// </para>
/// <para>
/// <b>The four the design fixes.</b> 413 for a request that asks for more than the bounds allow, 422
/// for a URL or viewport that is not one this package accepts, 403 for a destination the policy will
/// not photograph, and 504 for a capture that did not finish inside the job limit. Each is a distinct
/// condition with a distinct next step for the owner, so none of them is folded into a generic
/// failure.
/// </para>
/// </remarks>
public static class CaptureError
{
    /// <summary>An id this caller may not reach (404). The delivered code, reused deliberately.</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>
    /// The request asked for more pages, viewports, wait time or bytes than the bounds allow (413).
    /// The delivered payload-too-large code: an oversized ask and an oversized body mean the same
    /// thing to a client, and the status a shipped client already handles should not be renumbered.
    /// </summary>
    public const string TooLarge = ApiError.PayloadTooLarge;

    /// <summary>
    /// The URL is not capturable as a shape - not http or https, carrying userinfo, or not absolute -
    /// or a viewport is not one of the two this package offers (422).
    /// </summary>
    public const string ValidationFailed = ApiError.ValidationFailed;

    /// <summary>
    /// The destination resolved somewhere a capture may not go: a metadata service, a loopback, link
    /// local, private, multicast or reserved address (403).
    /// </summary>
    /// <remarks>
    /// 403 rather than 404 because the milestone is real and the request is well formed - it is the
    /// destination that is refused, and telling the owner that is what lets them fix it. It is not 422
    /// either, because nothing about the URL is malformed: a perfectly good <c>https</c> URL can
    /// resolve to an address this package will not dial.
    /// </remarks>
    public const string TargetBlocked = "capture_target_blocked";

    /// <summary>
    /// The capture did not finish inside the job limit (504). The delivered dependency-timeout code,
    /// reused: a page that never finishes rendering is the same condition as a dependency that never
    /// answers, and the delivered queue already has a bounded retry policy for it.
    /// </summary>
    public const string Timeout = ApiError.DependencyTimeout;

    /// <summary>
    /// The capture sandbox could not be started, so no page was visited and no browser was launched
    /// (503).
    /// </summary>
    /// <remarks>
    /// The refusal the design names for a host without the required namespace and firewall support.
    /// It is a 503 rather than a 500 because nothing here is broken in the product: the deployment is
    /// missing a kernel capability, and it is answerable by running where the capability exists. It
    /// deliberately never becomes a less-secure path - there is no code that navigates a page without
    /// the sandbox, so a host like this reports this and does nothing else.
    /// </remarks>
    public const string SandboxUnavailable = "capture_sandbox_unavailable";

    // ---------------------------------------------------------------------------------------------
    // Job error codes. These are written to the operation row rather than to a response body, and
    // they deliberately have no status in ApiErrorStatus: no route answers a capture that already
    // failed, so a status here would be a mapping nothing uses.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The request row for this job is gone, so there is nothing to capture from and no honest way to
    /// guess what was asked for.
    /// </summary>
    public const string RequestMissing = "capture_request_missing";

    /// <summary>A page did not finish inside the job limit, so nothing was written for it.</summary>
    public const string JobTimeout = "capture_timeout";

    /// <summary>
    /// The sandbox could not be started. A job's form of <see cref="SandboxUnavailable"/>, carrying
    /// the same refusal for the same reason.
    /// </summary>
    public const string JobSandboxUnavailable = "capture_sandbox_unavailable";

    /// <summary>Every page in the job failed, so there is no result at all rather than a partial one.</summary>
    public const string NoPageCaptured = "capture_no_page_captured";

    /// <summary>
    /// The bytes were captured but could not be stored, so no candidate was written and whatever the
    /// media write had staged was retired.
    /// </summary>
    /// <remarks>
    /// A distinct code from a capture failure on purpose. A page that rendered and then could not be
    /// saved is a different problem from a page that did not render, and an owner watching this job
    /// needs to be able to tell "the site was slow" from "this server cannot write files".
    /// </remarks>
    public const string MediaWriteFailed = "capture_media_write_failed";

    /// <summary>The milestone was edited while the job was running, so nothing was committed.</summary>
    public const string JobSuperseded = "capture_job_superseded";
}

/// <summary>
/// The destination, or one of the destinations, a capture may not be pointed at (403).
/// </summary>
/// <remarks>
/// The message never names the host, the address or the range. A refusal that quoted what it refused
/// would confirm to a caller that a host exists and that it holds something, which is the information
/// this control exists to withhold.
/// </remarks>
public sealed class CaptureTargetBlockedException(string reason = "That destination cannot be captured.")
    : ApiException(CaptureError.TargetBlocked, reason);

/// <summary>
/// The capture sandbox is not usable on this host, so nothing was captured (503).
/// </summary>
/// <remarks>
/// This is the refusal the design requires rather than a degraded path. There is no overload, no
/// fallback and no configuration that turns this into a capture on unrestricted host networking: the
/// kernel enforcement the package relies on is missing, and a browser that starts anyway would be
/// able to reach anything the machine can reach.
/// </remarks>
public sealed class CaptureSandboxUnavailableException(string detail)
    : ApiException(CaptureError.SandboxUnavailable, detail);
