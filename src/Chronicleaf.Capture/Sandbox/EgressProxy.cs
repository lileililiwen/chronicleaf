using System.Net;
using System.Net.Sockets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Capture.Sandbox;

/// <summary>
/// The per-job egress policy, as the proxy holds it while one job runs (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// An immutable value built once, from the request the job is capturing, and read by every connection
/// the browser attempts. It carries the deployment mode, the operator's allowances and the instant the
/// expiry is compared against - the clock is a field rather than something read at use, so a policy
/// cannot change under a job that is already running.
/// </para>
/// <para>
/// <b>This is the enforcement oracle, not a filter.</b> Chromium is started with this proxy as its only
/// route and with QUIC disabled, and the kernel rules deny the browser's UID everything else, so a
/// direct socket, a websocket or a DNS lookup that bypasses the browser's own request interception
/// still meets this decision. Route interception inside Playwright would not: it is advisory, it sees
/// what the page asks for, and a page is not obliged to ask.
/// </para>
/// </remarks>
/// <param name="Mode">Which deployment this is, which decides whether allowances are read at all.</param>
/// <param name="Allowances">The operator-configured local allowances.</param>
/// <param name="Now">The instant the allowance expiries are compared against, in UTC.</param>
public sealed record CaptureEgressPolicy(
    CaptureDeploymentMode Mode,
    IReadOnlyList<CaptureLocalAllowance> Allowances,
    DateTimeOffset Now)
{
    /// <summary>The policy a job runs under when no allowance has been configured.</summary>
    public static CaptureEgressPolicy Default(CaptureDeploymentMode mode, DateTimeOffset now) =>
        new(mode, [], now);
}

/// <summary>
/// The verdict the proxy returns for one destination, and the reason in a closed set of words.
/// </summary>
/// <param name="Allowed">Whether the connection may be made.</param>
/// <param name="Reason">The policy's reason word, or <c>null</c> when the destination is permitted.</param>
/// <param name="Addresses">The validated addresses, so the socket is pinned to one that was checked.</param>
public sealed record CaptureEgressVerdict(bool Allowed, string? Reason, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>Permit, and pin to one of these addresses.</summary>
    public static CaptureEgressVerdict Permit(IReadOnlyList<IPAddress> addresses) => new(true, null, addresses);

    /// <summary>Deny, and connect to nothing.</summary>
    public static CaptureEgressVerdict Deny(string reason) => new(false, reason, []);
}

/// <summary>
/// The destination decision the proxy makes, separated from the socket so it can be exercised
/// without a listener on the other end (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// This is the single point where "may this capture reach there" is decided, and it is a pure
/// function of the destination, the already-resolved addresses, the policy and the clock. Keeping the
/// decision free of the socket work is what makes the policy testable on a host that cannot run the
/// socket half: the same method the proxy calls per connection is the one a test calls, so a scenario
/// that asserts a destination is denied is asserting the real decision rather than a copy of it.
/// </para>
/// <para>
/// <b>Revalidation happens here, at connect time.</b> The addresses handed in are the ones the name
/// resolved to a moment ago, and the verdict is about those addresses rather than about the name. A
/// name that answered with a public address when the request was made and answers with a private one
/// now is refused, which is the whole of DNS rebinding: there is no window in which the checked name
/// and the dialled address can differ.
/// </para>
/// </remarks>
public sealed class CaptureEgressGate(TimeProvider clock, ILogger<CaptureEgressGate> logger)
{
    /// <summary>
    /// Decides whether one destination may be connected to under one policy.
    /// </summary>
    /// <param name="destination">
    /// The host and port the browser asked for. Only the host is judged; the port is not a destination
    /// control, because a page that can reach a host on 443 can reach it on any port the host offers.
    /// </param>
    /// <param name="resolved">
    /// Every address the destination resolved to, immediately before this call. Supplied by the caller
    /// so that the resolution and the judgement are adjacent and no cache can sit between them.
    /// </param>
    /// <param name="policy">The job's policy.</param>
    public CaptureEgressVerdict Evaluate(
        string destination,
        IReadOnlyList<IPAddress> resolved,
        CaptureEgressPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(policy);

        if (!Uri.TryCreate($"tcp://{destination}", UriKind.Absolute, out var url))
        {
            return CaptureEgressVerdict.Deny(CaptureUrlPolicy.ReasonNotAbsolute);
        }

        var decision = CaptureUrlPolicy.EvaluateAddress(url, resolved, policy.Mode, policy.Allowances, policy.Now);

        if (!decision.Allowed)
        {
            // The reason word is logged; the host and the resolved addresses are not. A log line that
            // quoted what was refused would be a record of what this host can see and cannot reach, and
            // the log is readable more widely than the control is.
            logger.LogInformation(
                "A capture connection to {Reason} was denied by the egress policy.",
                decision.Reason);

            return CaptureEgressVerdict.Deny(decision.Reason!);
        }

        return CaptureEgressVerdict.Permit(decision.AllowedAddresses);
    }

    /// <summary>
    /// Resolves a destination and decides it, in one step.
    /// </summary>
    /// <remarks>
    /// Resolution happens here rather than at the call site so the addresses judged are always the
    /// addresses about to be dialled. A caller that resolved earlier and passed the result in would be
    /// creating exactly the gap this is meant to close.
    /// </remarks>
    public async Task<CaptureEgressVerdict> ResolveAndEvaluateAsync(
        string destination,
        CaptureEgressPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate($"tcp://{destination}", UriKind.Absolute, out var url))
        {
            return CaptureEgressVerdict.Deny(CaptureUrlPolicy.ReasonNotAbsolute);
        }

        IPAddress[] resolved;

        try
        {
            resolved = await Dns.GetHostAddressesAsync(url.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // A destination that cannot be resolved has no address that could have been checked, and a
            // capture that cannot say where it is going should not go.
            return CaptureEgressVerdict.Deny(CaptureUrlPolicy.ReasonNoAddress);
        }

        return Evaluate(destination, resolved, policy);
    }

    /// <summary>The clock this gate compares an allowance expiry against.</summary>
    public DateTimeOffset Now => clock.GetUtcNow();
}
