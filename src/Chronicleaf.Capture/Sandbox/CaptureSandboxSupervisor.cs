using System.Runtime.InteropServices;
using Chronicleaf.Application.Capture;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Capture.Sandbox;

/// <summary>
/// What the host can and cannot do about a capture browser, decided once and cheaply (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the package's answer to a machine that cannot run a capture safely, and the answer is a
/// refusal rather than a degraded mode.</b> The production backend depends on two Linux capabilities
/// the application process does not have by design: a private network namespace the browser can be
/// confined to, and owner-based firewall rules that deny its outbound connections. Together they are
/// what make "a capture can only reach a public page" an enforced property rather than an intention. A
/// host that has neither cannot offer that property, and a browser started anyway would be a browser
/// with the host's entire network reach - which is exactly the exposure this package exists to
/// prevent.
/// </para>
/// <para>
/// <b>There is no override, no configuration flag and no "less secure" fallback.</b> That is a
/// deliberate decision and it is the one that matters most here: a flag that said
/// <c>Capture:SandboxRequired=false</c> would be read exactly once, on the one host where it was
/// needed, and the boundary would be gone on that host and nowhere else - which is the worst possible
/// shape for a security control. The design states it directly: native capture outside this sandbox is
/// unavailable rather than a less-secure fallback.
/// </para>
/// <para>
/// <b>Chromium's own sandbox stays on.</b> No <c>--no-sandbox</c>, no equivalent, and no default that
/// would disable it. A browser that needs its sandbox turned off to start is a browser this host
/// cannot run, and saying so is the correct answer rather than a problem to work around.
/// </para>
/// </remarks>
public sealed class CaptureSandboxSettings
{
    /// <summary>Configuration section, matching the other packages' shape.</summary>
    public const string SectionName = "Chronicleaf:Capture";

    /// <summary>
    /// The fixed packaged Chromium executable, provisioned in the container image.
    /// </summary>
    /// <remarks>
    /// Absolute and fixed rather than resolved from <c>PATH</c> or a Playwright cache, because a host
    /// browser found by search is a browser nobody audited. When this is unset the supervisor looks in
    /// the packaged location below; it never falls back to whatever is installed.
    /// </remarks>
    public string BrowserExecutablePath { get; set; } = "/opt/chronicleaf/chromium/chrome";

    /// <summary>
    /// The unix socket the supervisor listens on for <c>StartJob</c> and <c>StopJob</c>.
    /// </summary>
    /// <remarks>
    /// Private to the container's own filesystem and 0600, because the socket is a capability: anything
    /// that can connect can ask for a browser.
    /// </remarks>
    public string SupervisorSocketPath { get; set; } = "/run/chronicleaf/capture.sock";

    /// <summary>
    /// The loopback address the egress proxy listens on inside the browser's network namespace.
    /// </summary>
    /// <remarks>
    /// The only destination the firewall permits, which is what makes the proxy the only way out.
    /// </remarks>
    public string ProxyAddress { get; set; } = "127.0.0.1";

    /// <summary>The port the egress proxy listens on inside the namespace.</summary>
    public int ProxyPort { get; set; } = 8901;

    /// <summary>
    /// Which deployment this is, which decides whether a local-network allowance may be configured at
    /// all.
    /// </summary>
    public CaptureDeploymentMode DeploymentMode { get; set; } = CaptureDeploymentMode.Community;
}

/// <summary>
/// The host capability probe, and the reason it answers the way it does.
/// </summary>
/// <param name="Available">Whether a capture may be started here at all.</param>
/// <param name="Reason">A closed-set word explaining the refusal, for a log line and a job error code.</param>
/// <param name="Detail">What was actually observed, with no host names and no absolute paths.</param>
public sealed record CaptureSandboxAvailability(bool Available, string? Reason, string? Detail)
{
    /// <summary>The sandbox is usable.</summary>
    public static readonly CaptureSandboxAvailability AvailableSandbox =
        new(true, null, "capture sandbox available");

    /// <summary>
    /// The host cannot run a capture safely, and no browser will be launched.
    /// </summary>
    public static CaptureSandboxAvailability Unavailable(string reason, string detail) =>
        new(false, reason, detail);
}

/// <summary>
/// Decides whether this host can confine a capture browser, and starts it when it can.
/// </summary>
/// <remarks>
/// <para>
/// The probe is deliberately explicit about what it checks and why, because the refusal has to be
/// debuggable by an operator without being negotiable by one. It asks three questions, in order, and
/// the first that fails is the reason reported:
/// </para>
/// <list type="number">
/// <item>Is this Linux? A namespace and firewall model that only exists on Linux cannot be
/// approximated elsewhere.</item>
/// <item>Can an unprivileged network namespace be created? Without it there is no namespace for the
/// browser to be confined to, and root-owned rules would have to be applied to the host's own
/// interface.</item>
/// <item>Can firewall rules be installed? Without them the browser's outbound connections are the
/// host's, and the proxy becomes advisory rather than the only exit.</item>
/// </list>
/// <para>
/// <b>The probe is cheap and read-only.</b> It creates no namespace and installs no rule; it asks
/// whether it could. That matters because it runs on the request path's first capture: a probe that
/// mutated the host in order to find out would be a much worse thing to run speculatively.
/// </para>
/// </remarks>
public sealed class CaptureSandboxSupervisor(
    CaptureSandboxSettings settings,
    TimeProvider clock,
    ILogger<CaptureSandboxSupervisor> logger)
{
    /// <summary>The closed-set reasons a capture cannot run here.</summary>
    public static class Reasons
    {
        /// <summary>Not Linux. The confinement model this package depends on does not exist here.</summary>
        public const string UnsupportedPlatform = "unsupported_platform";

        /// <summary>A private network namespace cannot be created by this process.</summary>
        public const string NamespaceUnavailable = "namespace_unavailable";

        /// <summary>Firewall rules cannot be installed, so the proxy would not be the only exit.</summary>
        public const string FirewallUnavailable = "firewall_unavailable";

        /// <summary>The packaged Chromium is not present.</summary>
        public const string BrowserMissing = "browser_missing";
    }

    /// <summary>
    /// Asks whether a capture could run here, without starting anything.
    /// </summary>
    public CaptureSandboxAvailability Probe()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return Refuse(
                Reasons.UnsupportedPlatform,
                "the capture sandbox is a Linux network-namespace and firewall boundary and this host is not Linux");
        }

        var namespaceProbe = ProbeNetworkNamespace();

        if (!namespaceProbe)
        {
            return Refuse(
                Reasons.NamespaceUnavailable,
                "an unprivileged network namespace could not be created, so a capture browser could not be confined to one");
        }

        if (!ProbeFirewall())
        {
            return Refuse(
                Reasons.FirewallUnavailable,
                "owner-based firewall rules could not be installed, so a capture browser would not be restricted to the egress proxy");
        }

        if (!File.Exists(settings.BrowserExecutablePath))
        {
            return Refuse(
                Reasons.BrowserMissing,
                "the packaged Chromium executable is not present at the configured capture path");
        }

        return CaptureSandboxAvailability.AvailableSandbox;
    }

    /// <summary>
    /// Raises the refusal when the sandbox is not usable, and returns otherwise.
    /// </summary>
    /// <remarks>
    /// The one path every capture goes through, and the reason there is no code below it that starts a
    /// browser on unrestricted networking. A caller that catches this has learned that nothing was
    /// launched, which is the only fact it needs to report honestly.
    /// </remarks>
    /// <exception cref="CaptureSandboxUnavailableException">The sandbox is not usable here.</exception>
    public void EnsureAvailable()
    {
        var availability = Probe();

        if (availability.Available)
        {
            return;
        }

        logger.LogWarning(
            "A capture was refused: {Reason}. {Detail}. No browser was started.",
            availability.Reason,
            availability.Detail);

        throw new CaptureSandboxUnavailableException(
            "Screenshot capture is not available on this host because its network sandbox could not be established. No page was visited.");
    }

    /// <summary>The instant the supervisor would use, for a per-job nonce's creation time.</summary>
    public DateTimeOffset Now => clock.GetUtcNow();

    private CaptureSandboxAvailability Refuse(string reason, string detail) =>
        CaptureSandboxAvailability.Unavailable(reason, detail);

    /// <summary>
    /// Whether an unprivileged network namespace can be created by this process.
    /// </summary>
    /// <remarks>
    /// Asks the kernel the same way the sandbox would have to, rather than reading a configuration
    /// value or a capability bit and inferring: an unprivileged user namespace is what makes the
    /// mapping to root inside the new network namespace possible, and without it the browser would
    /// have to share the host's network interfaces.
    /// </remarks>
    private static bool ProbeNetworkNamespace()
    {
        // A user namespace is the prerequisite for an unprivileged network namespace, so this is the
        // cheapest question whose answer implies the one that matters.
        if (!CanCreateUserNamespace())
        {
            return false;
        }

        return true;
    }

    private static bool ProbeFirewall()
    {
        // The rules are installed by the container's own initialization step as CAP_NET_ADMIN, which the
        // application process itself never holds. Their presence is therefore read, not created: the
        // two nftables tables this package's egress policy is expressed as are checked for, and a host
        // without them is a host whose browser would not be restricted to the proxy.
        return FirewallRulesPresent();
    }

    private static bool CanCreateUserNamespace() => NamespaceProbe.TryCreateUserNamespace();

    private static bool FirewallRulesPresent() => NamespaceProbe.FirewallRulesPresent();
}
