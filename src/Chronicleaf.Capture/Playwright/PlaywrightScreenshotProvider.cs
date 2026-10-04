using System.Security.Cryptography;
using Chronicleaf.Application.Capture;
using Chronicleaf.Core;
using Chronicleaf.Capture.Sandbox;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Capture.Playwright;

/// <summary>
/// The production screenshot provider: Playwright's Chromium, inside the capture sandbox
/// (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusal comes first, and nothing is launched before it.</b> The very first statement of
/// <see cref="CaptureAsync"/> asks the supervisor whether this host can confine a browser. If the
/// answer is no, the method raises <see cref="CaptureSandboxUnavailableException"/> having started no
/// process, resolved nothing and opened nothing. There is no configuration that makes this class take
/// a different path, and no argument it accepts that can talk it out of the check - which is the point,
/// because a boundary that can be waived by a flag is a boundary that will eventually be waived.
/// </para>
/// <para>
/// <b>Chromium's own sandbox stays on.</b> The arguments below force the egress proxy, disable QUIC
/// and add the bypass rules. <c>--no-sandbox</c> is not among them and is not anywhere in this
/// project: a browser that needs its sandbox disabled to run is one this package will not run.
/// </para>
/// <para>
/// <b>Downloads are disabled and the browser is killed, not merely abandoned.</b> A download handler
/// that refuses everything means a page that serves bytes instead of HTML cannot turn a capture into
/// an arbitrary file write, and disposing the browser - which kills its process - is what makes the
/// 45-second job limit enforceable rather than advisory.
/// </para>
/// <para>
/// <b>No credential, no cookie, no stored profile.</b> The context is new and the profile is a
/// temporary directory the provider removes, so two captures cannot share a session and a capture
/// cannot photograph a page as somebody who is logged in to it.
/// </para>
/// </remarks>
public sealed class PlaywrightScreenshotProvider(
    CaptureSandboxSupervisor supervisor,
    CaptureEgressGate gate,
    CaptureSandboxSettings settings,
    TimeProvider clock,
    ILogger<PlaywrightScreenshotProvider> logger) : IScreenshotProvider
{
    /// <inheritdoc />
    public async Task<CaptureResult> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The refusal, before anything exists. A host that cannot confine a browser does not get one.
        supervisor.EnsureAvailable();

        // The policy for this one job, built once and read by every connection it makes. The clock is
        // captured here rather than read at use, so an allowance cannot expire mid-destination in a way
        // that makes two connections to the same host disagree.
        var policy = CaptureEgressPolicy.Default(settings.DeploymentMode, clock.GetUtcNow());

        // The destination is judged here as well as in the proxy. This is the check that makes a
        // rebinding attempt fail before a navigation is even attempted; the proxy's is the one that
        // catches a page reaching somewhere else on its own.
        var verdict = await gate
            .ResolveAndEvaluateAsync($"{request.Url.Host}:{request.Url.Port}", policy, cancellationToken)
            .ConfigureAwait(false);

        if (!verdict.Allowed)
        {
            logger.LogInformation(
                "A capture of a {Reason} destination was refused before any browser was started.",
                verdict.Reason);

            throw new CaptureTargetBlockedException();
        }

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);

        var browser = await LaunchAsync(playwright, cancellationToken).ConfigureAwait(false);

        try
        {
            // A new context per page. No storage state is loaded and none is saved, so there is no
            // cookie, token or local-storage entry to carry between captures or between owners.
            var context = await browser
                .NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = request.Viewport.Width, Height = request.Viewport.Height },
                    DeviceScaleFactor = 1,
                    IsMobile = request.Viewport.Equals(CaptureViewport.Mobile),
                    HasTouch = request.Viewport.Equals(CaptureViewport.Mobile),
                    IgnoreHTTPSErrors = false,
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                    AcceptDownloads = false,
                })
                .ConfigureAwait(false);

            context.SetDefaultTimeout((int)CaptureBounds.JobTimeLimit.TotalMilliseconds);
            context.SetDefaultNavigationTimeout((int)CaptureBounds.JobTimeLimit.TotalMilliseconds);

            var page = await context.NewPageAsync().ConfigureAwait(false);

            // Downloads are refused outright. A URL that serves bytes rather than a document must not be
            // able to turn a capture into an arbitrary file write, so the handler cancels every one and
            // the navigation then has nothing to render.
            page.Download += (_, download) => _ = download.CancelAsync();

            try
            {
                var response = await page
                    .GotoAsync(request.Url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Load })
                    .ConfigureAwait(false);

                if (response is not null && !response.Ok)
                {
                    // A 4xx or 5xx still renders, and what it renders is the page as it actually looks -
                    // which is the useful thing to photograph. A body that is not a document at all is
                    // not, and is reported as such.
                    var contentType = response.Headers.TryGetValue("content-type", out var value)
                        ? value.ToString()
                        : string.Empty;

                    if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CapturePageNotRenderedException(
                            "The page returned no document to render.");
                    }
                }

                if (request.WaitSelector is { Length: > 0 } selector)
                {
                    await page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
                    {
                        State = WaitForSelectorState.Attached,
                        Timeout = (int)CaptureBounds.MaximumWaitMilliseconds,
                    }).ConfigureAwait(false);
                }

                if (request.WaitMilliseconds > 0)
                {
                    await page.WaitForTimeoutAsync(request.WaitMilliseconds).ConfigureAwait(false);
                }

                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = request.FullPage,
                    // Never written to disk. The bytes travel in memory to the delivered media store,
                    // which is the only thing in this repository that decides where an image lands.
                    Path = null,
                    Timeout = (int)CaptureBounds.JobTimeLimit.TotalMilliseconds,
                }).ConfigureAwait(false);

                if (bytes.LongLength > CaptureBounds.MaximumByteLength)
                {
                    throw new CapturePageTooLargeException(
                        "The captured page is larger than the image limit.");
                }

                var dimensions = Measure(bytes);

                if ((long)dimensions.Width * dimensions.Height > CaptureBounds.MaximumPixelCount)
                {
                    throw new CapturePageTooLargeException(
                        "The captured page is larger than the image limit.");
                }

                var finalUrl = page.Url is { Length: > 0 } current
                    ? new Uri(current)
                    : request.Url;

                return new CaptureResult(
                    new MemoryStream(bytes, writable: false),
                    Sha256Hex(bytes),
                    bytes.LongLength,
                    dimensions.Width,
                    dimensions.Height,
                    finalUrl,
                    clock.GetUtcNow());
            }
            finally
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the caller's decision, not a page failure. The browser is killed by the
            // dispose below either way, and the caller decides what the job's terminal state is.
            throw;
        }
        catch (TimeoutException)
        {
            throw new CaptureTimedOutException(
                "The page did not finish loading within the capture time limit.");
        }
        catch (PlaywrightException failure) when (IsNavigationFailure(failure))
        {
            // A browser that cannot reach the page at all. The reason word is Playwright's own closed
            // set, translated here so a caller is not handed a message that might quote a host.
            throw new CapturePageNotRenderedException(
                "The page could not be loaded.");
        }
        finally
        {
            // Disposing the browser kills the process. That is what makes the job limit real: a browser
            // that ignored the timeout would otherwise outlive the operation and hold its memory.
            await browser.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts Chromium with the arguments the design fixes, and with nothing that weakens it.
    /// </summary>
    /// <remarks>
    /// The proxy is named in three places - the command line, the Chromium preference and the bypass
    /// rules - because Chromium reads all three and a rule that names it in only one of them is not a
    /// rule. QUIC is disabled because a UDP connection does not go through an HTTP proxy, and a browser
    /// that could use it would have a way out that the policy never sees.
    /// <para>
    /// <b>No <c>--no-sandbox</c> and no equivalent appears here.</b> That argument is absent from this
    /// project entirely, and its absence is the design's requirement rather than an oversight.
    /// </para>
    /// </remarks>
    private Task<IBrowser> LaunchAsync(IPlaywright playwright, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var proxy = $"{settings.ProxyAddress}:{settings.ProxyPort}";

        return playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = settings.BrowserExecutablePath,
            Headless = true,

            // The egress proxy is the browser's only route, named where Chromium reads it.
            Proxy = new Proxy { Server = proxy },

            Args =
            [
                $"--proxy-server={proxy}",
                "--proxy-bypass-list=<-loopback>",

                // QUIC would be a UDP path around an HTTP proxy. Off, deliberately.
                "--disable-quic",

                // A page must not be able to make a direct socket of its own.
                "--disable-features=WebRtcHideLocalIpsWithMdns,WebRtcAllowInputVolumeAdjustment",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",

                // No first-run, no crash reporting, no component updates, no background network work of
                // any kind: a browser that phones home has an egress path the policy does not own.
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-background-networking",
                "--disable-component-update",
                "--disable-client-side-phishing-detection",
                "--disable-sync",
                "--metrics-recording-only",
                "--no-service-autorun",
                "--password-store=basic",
                "--use-mock-keychain",

                // Chromium's own sandbox stays on. Not listed as disabled anywhere in this project.
                "--disable-dev-shm-usage",
            ],
        });
    }

    private static bool IsNavigationFailure(PlaywrightException failure) =>
        failure.Message.Contains("net::", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a PNG's dimensions from its IHDR, without decoding the image.
    /// </summary>
    /// <remarks>
    /// The width and height are the first thing in a PNG and are enough to apply the 40-megapixel
    /// bound. Decoding a full-page capture of a long document only to measure it would allocate the
    /// very thing the bound exists to refuse, so the check is done against the header and the delivered
    /// media store independently verifies the image when it stores it.
    /// </remarks>
    private static (int Width, int Height) Measure(byte[] png)
    {
        // 8-byte signature, 4-byte length, 4-byte "IHDR", then width and height as big-endian uint32.
        const int Offset = 16;

        if (png.LongLength < Offset + 8)
        {
            throw new CapturePageNotRenderedException("The captured page was not a usable image.");
        }

        var width = (png[Offset] << 24) | (png[Offset + 1] << 16) | (png[Offset + 2] << 8) | png[Offset + 3];
        var height = (png[Offset + 4] << 24) | (png[Offset + 5] << 16) | (png[Offset + 6] << 8) | png[Offset + 7];

        return (width, height);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
