using System.Net;
using System.Net.Sockets;

namespace Chronicleaf.Core;

/// <summary>
/// The viewport a capture is taken at (SCREENSHOT-CAPTURE-001).
/// </summary>
/// <remarks>
/// <para>
/// Two values, because a full-page desktop picture and a full-page mobile picture of the same site are
/// different artifacts and one of them is usually the one an owner wants. The name is the contract -
/// a request says <c>desktop</c> or <c>mobile</c> and the dimensions come from here, so there is no
/// way to ask for 1440x901 and no way for a caller to invent a viewport this package did not
/// document.
/// </para>
/// <para>
/// The set is closed. A viewport that is neither of these is refused at validation (422) rather than
/// rounded, because a silently substituted size is a picture that does not match the label an owner
/// put on it - and because the viewport is one third of the tuple the database makes unique, so
/// rounding would let two differently-labelled requests collide on one row.
/// </para>
/// </remarks>
/// <param name="Name">The wire name, <c>desktop</c> or <c>mobile</c>.</param>
/// <param name="Width">The pixel width Chromium is given.</param>
/// <param name="Height">The pixel height Chromium is given.</param>
public sealed record CaptureViewport(string Name, int Width, int Height)
{
    /// <summary>1440x900. The design's default for the wide case.</summary>
    public static readonly CaptureViewport Desktop = new("desktop", 1440, 900);

    /// <summary>390x844. The design's default for the narrow case.</summary>
    public static readonly CaptureViewport Mobile = new("mobile", 390, 844);

    public static IReadOnlyList<CaptureViewport> All { get; } = [Desktop, Mobile];

    /// <summary>
    /// The viewport a wire name means, or <c>null</c> when the name is not one of the two.
    /// </summary>
    /// <remarks>
    /// Case-insensitive because a name arriving from a hand-typed request should not be refused for
    /// capitalisation, but never fuzzy: <c>DeskTop</c> is this and <c>lap</c> is not anything.
    /// </remarks>
    public static CaptureViewport? FromName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(viewport => string.Equals(viewport.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which deployment the capture policy is being evaluated for (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// A real input to the decision, never a comment. The two modes differ in exactly one way, and it is
/// the way that matters: <see cref="Community"/> may be given an operator-configured allowance for one
/// exact local origin so a self-hosted install can photograph its own dev server, and
/// <see cref="Hosted"/> may not. That is a property of where the process runs - a single-tenant
/// install where the loopback interface is the operator's own preview server, versus a shared
/// machine where it is somebody else's - and it has to be a value the policy branches on rather than
/// a deployment note, or the refusal would be a promise this repository does not keep.
/// </remarks>
public enum CaptureDeploymentMode
{
    /// <summary>
    /// A single-tenant Community install. May hold an exact-origin, expiring local allowance; may
    /// never hold one for a metadata endpoint under any configuration.
    /// </summary>
    Community = 0,

    /// <summary>
    /// The shared Hosted deployment. Refuses every local-network allowance, including one an operator
    /// configures, because the private ranges a Community install may photograph belong to other
    /// tenants here.
    /// </summary>
    Hosted = 1,
}

/// <summary>
/// An operator's permission to photograph one exact local origin for a while (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// Three things, all of which have to be present, and the shape is the enforcement:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>An exact origin.</b> Scheme, host and port as text. Not a suffix, not a wildcard, not a
/// regex - a prefix match would let <c>http://localhost.attacker.example</c> satisfy a rule written
/// for <c>http://localhost</c>, which is the whole class of bug an SSRF control exists to stop.
/// </item>
/// <item>
/// <b>The resolved address, pinned.</b> The address the host resolved to when the operator configured
/// this. The name is not enough on its own, because a name that resolves to a permitted address now
/// can resolve somewhere else by the time the browser connects - that is DNS rebinding, and it is
/// what makes "resolve and validate before every connection" necessary rather than decorative.
/// </item>
/// <item>
/// <b>An expiry.</b> A preview server is up for an afternoon. An allowance with no expiry is a
/// permanent hole with an extra step, so the clock is a required field and a lapsed allowance
/// refuses exactly like no allowance at all.
/// </item>
/// </list>
/// <para>
/// <b>There is no allowance for a metadata endpoint, in any mode, at any expiry.</b> A cloud instance
/// metadata service hands out credentials to anything that can make a request to it, so a capture
/// that could reach one could publish a temporary key as a picture. That is refused in
/// <see cref="EvaluateAddress"/> before any mode, allowance or expiry is consulted, which is why the
/// metadata ranges are written as constants here and not as a configuration value an operator could
/// remove.
/// </para>
/// </remarks>
/// <param name="Origin">The exact origin, as <c>scheme://host[:port]</c> with no path.</param>
/// <param name="Address">The address the host resolved to, which is what the connection is pinned to.</param>
/// <param name="ExpiresAt">When this stops being allowed. Always compared in UTC.</param>
public sealed record CaptureLocalAllowance(string Origin, IPAddress Address, DateTimeOffset ExpiresAt);

/// <summary>
/// The answer the URL policy gives, and nothing more (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <param name="Allowed">Whether the capture may proceed to a connection attempt.</param>
/// <param name="Reason">
/// Why, when it may not. A closed set of words, because this string is what ends up in a log line and
/// in the refusal a caller sees, and a reason built from an arbitrary host would defeat the point of
/// the control.
/// </param>
/// <param name="AllowedAddresses">
/// The addresses the destination resolved to that survived the policy, in the order the resolver
/// returned them. The caller pins its socket to one of these rather than re-resolving, so the
/// address that was checked is the address that is dialled.
/// </param>
public sealed record CaptureUrlDecision(bool Allowed, string? Reason, IReadOnlyList<IPAddress> AllowedAddresses)
{
    /// <summary>A decision that permits the connection attempt.</summary>
    public static CaptureUrlDecision Permit(IReadOnlyList<IPAddress> addresses) => new(true, null, addresses);

    /// <summary>A decision that refuses it, with the closed-set reason why.</summary>
    public static CaptureUrlDecision Refuse(string reason) => new(false, reason, []);
}

/// <summary>
/// Which page destinations a capture job may reach (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the control the package exists for, so it is deliberately the smallest, purest and most
/// heavily tested piece of code in the repository.</b> It is a pure function of its arguments: no
/// clock of its own beyond the instant it is handed, no file, no socket, no registry, no environment
/// variable and no service locator. Every answer it gives can therefore be reproduced in a unit test
/// with nothing running at all, which is the only reason to be confident in a list of ranges that is
/// otherwise unverifiable by reading.
/// </para>
/// <para>
/// It is called at three distinct moments, and all three matter:
/// </para>
/// <list type="number">
/// <item>
/// When an owner asks for a capture, so a request for a private address is refused at validation
/// (403 <c>capture_target_blocked</c>) instead of becoming a job that can only fail.
/// </item>
/// <item>
/// Immediately before every connection, because the name that was allowed at request time may resolve
/// differently now - that is DNS rebinding, and the design's "resolve and validate before every
/// connection" is exactly this call.
/// </item>
/// <item>
/// After every redirect and for every subresource, because a page can name any destination it likes
/// and a top-level URL that is perfectly public can pull a private one in behind it.
/// </item>
/// </list>
/// <para>
/// <b>What is refused, unconditionally.</b> Anything that is not http or https; any URL carrying
/// userinfo, because credentials in a URL are both a phishing channel and a way to make a host
/// <em>look</em> like a permitted one; and any resolved address in a metadata, loopback, link-local,
/// private, multicast, broadcast, reserved or otherwise unroutable range. A name that resolves to
/// <em>no</em> address is refused too: a capture that cannot say where it is going should not go.
/// </para>
/// <para>
/// <b>What may be allowed.</b> In <see cref="CaptureDeploymentMode.Community"/>, one operator-
/// configured <see cref="CaptureLocalAllowance"/> can permit one exact local origin. The allowance
/// has to match the origin exactly, the resolved address has to be the pinned one, and the clock has
/// to be inside the window; any one of those failing refuses. In <see cref="CaptureDeploymentMode.Hosted"/>
/// no allowance is consulted at all, and a local-network destination is refused whatever is
/// configured.
/// </para>
/// </remarks>
public static class CaptureUrlPolicy
{
    /// <summary>The address a caller matches on. Closed set, one word per reason.</summary>
    public const string ReasonSchemeNotAllowed = "scheme_not_allowed";

    /// <summary>A URL carrying a user or password. Refused in every mode, with no allowance.</summary>
    public const string ReasonUserInfoNotAllowed = "userinfo_not_allowed";

    /// <summary>The URL is not absolute, or has no host to resolve.</summary>
    public const string ReasonNotAbsolute = "not_absolute";

    /// <summary>Nothing resolved, so there is no address that could have been checked.</summary>
    public const string ReasonNoAddress = "no_address";

    /// <summary>The destination is local, private, link-local, reserved or otherwise not a public page.</summary>
    public const string ReasonLocalNetwork = "local_network";

    /// <summary>A cloud instance metadata service. Refused in every mode, at every expiry, always.</summary>
    public const string ReasonMetadataService = "metadata_service";

    /// <summary>This deployment does not accept local-network allowances, whatever was configured.</summary>
    public const string ReasonLocalNotAllowedHere = "local_network_not_allowed_here";

    /// <summary>The allowance named a different origin than the one being reached.</summary>
    public const string ReasonAllowanceOriginMismatch = "allowance_origin_mismatch";

    /// <summary>The address resolved to is not the one the allowance pinned.</summary>
    public const string ReasonAllowanceAddressMismatch = "allowance_address_mismatch";

    /// <summary>The allowance existed for this destination but has expired.</summary>
    public const string ReasonAllowanceExpired = "allowance_expired";

    /// <summary>
    /// Decides whether one URL may be captured at all, before anything is resolved.
    /// </summary>
    /// <remarks>
    /// The half of the policy that needs no name server: the shape of the URL. A refusal here is one a
    /// caller can fix, and returning it synchronously is what lets the request be refused at
    /// validation rather than becoming a job.
    /// </remarks>
    public static CaptureUrlDecision Evaluate(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return CaptureUrlDecision.Refuse(ReasonNotAbsolute);
        }

        if (url.Scheme is not ("http" or "https"))
        {
            // file:, data:, blob:, ftp:, gopher: and the rest. A capture that could read the operator's
            // filesystem or a data: document is a capture with no network boundary at all.
            return CaptureUrlDecision.Refuse(ReasonSchemeNotAllowed);
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            // Refused in every mode, with no allowance able to permit it. A URL that carries
            // credentials is either a phishing channel or an attempt to make a hostile host read as a
            // permitted one.
            return CaptureUrlDecision.Refuse(ReasonUserInfoNotAllowed);
        }

        if (string.IsNullOrWhiteSpace(url.Host))
        {
            return CaptureUrlDecision.Refuse(ReasonNotAbsolute);
        }

        return CaptureUrlDecision.Permit([]);
    }

    /// <summary>
    /// The whole decision for one URL: its shape, and - when its host is already an address rather
    /// than a name - the destination itself.
    /// </summary>
    /// <remarks>
    /// <b>Two refusals, deliberately distinguished.</b> A URL that is not http, carries userinfo or is
    /// not absolute is a <c>422</c> the owner can fix by retyping. A URL that is perfectly well formed
    /// but names an address a capture may not go to is a <c>403</c>, and the owner needs to be told that
    /// rather than to have their spelling corrected.
    /// <para>
    /// A name is not resolved here - a request must not depend on a name server - so a host like
    /// <c>http://internal.example/</c> is judged when the job runs, at which point the address it
    /// resolves to is known. An IP <em>literal</em> needs no resolver, and judging it here is what
    /// stops a request for <c>https://10.1.2.3/</c> from becoming a job that can only fail.
    /// </para>
    /// </remarks>
    /// <param name="url">The destination.</param>
    /// <param name="mode">Which deployment this is, which decides whether allowances are read at all.</param>
    /// <param name="allowances">The operator-configured local allowances.</param>
    /// <param name="now">The instant the allowance expiries are compared against, in UTC.</param>
    public static CaptureUrlDecision EvaluateTarget(
        Uri? url,
        CaptureDeploymentMode mode,
        IReadOnlyList<CaptureLocalAllowance>? allowances,
        DateTimeOffset now)
    {
        var shape = Evaluate(url);

        if (!shape.Allowed || url is null)
        {
            return shape;
        }

        if (IPAddress.TryParse(url.Host, out var literal))
        {
            // The host is the destination rather than a name for one, so there is nothing to defer.
            return EvaluateAddress(url, [literal], mode, allowances, now);
        }

        return shape;
    }

    /// <summary>
    /// Decides whether a set of already-resolved addresses may be connected to.
    /// </summary>
    /// <remarks>
    /// The half that needs the name server, called immediately before each connection and after each
    /// redirect, because a name that resolved to a public address when it was allowed can resolve to
    /// a private one by the time the browser dials it. A destination with several addresses is
    /// permitted only if at least one survives; the survivors are returned so the caller can pin its
    /// socket to one of them rather than resolving the name a second time and racing itself.
    /// </remarks>
    /// <param name="url">The destination being connected to, for the allowance's origin comparison.</param>
    /// <param name="resolved">Every address the destination resolved to.</param>
    /// <param name="mode">Which deployment this is.</param>
    /// <param name="allowances">
    /// The operator-configured local allowances. Ignored entirely in
    /// <see cref="CaptureDeploymentMode.Hosted"/>.
    /// </param>
    /// <param name="now">The instant the expiry is compared against, in UTC.</param>
    public static CaptureUrlDecision EvaluateAddress(
        Uri url,
        IReadOnlyList<IPAddress> resolved,
        CaptureDeploymentMode mode,
        IReadOnlyList<CaptureLocalAllowance>? allowances,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (resolved is null || resolved.Count == 0)
        {
            // A destination that resolves to nothing cannot have been checked, and a capture that
            // cannot say where it is going should not go.
            return CaptureUrlDecision.Refuse(ReasonNoAddress);
        }

        var surviving = new List<IPAddress>(resolved.Count);

        foreach (var address in resolved)
        {
            if (IsMetadataAddress(address))
            {
                // Checked before anything else and refused in every mode at every expiry. A metadata
                // service answers with credentials, so this is the one range that must not be
                // reachable even by an operator who has configured exactly that.
                return CaptureUrlDecision.Refuse(ReasonMetadataService);
            }

            if (IsPubliclyRoutable(address))
            {
                surviving.Add(address);
            }
        }

        if (surviving.Count > 0)
        {
            return CaptureUrlDecision.Permit(surviving);
        }

        // Everything resolved was local, private, link-local, reserved or otherwise unroutable. In
        // Hosted that is the end of the question: this deployment never photographs a local network,
        // so the allowance list is not read and cannot be argued with.
        if (mode is CaptureDeploymentMode.Hosted)
        {
            return CaptureUrlDecision.Refuse(ReasonLocalNotAllowedHere);
        }

        var origin = FormatOrigin(url);

        foreach (var allowance in allowances ?? [])
        {
            if (allowance is null)
            {
                continue;
            }

            if (!string.Equals(allowance.Origin, origin, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (now >= allowance.ExpiresAt)
            {
                // Reached only after an exact origin match, so this says the allowance named this
                // destination and has lapsed - which is a different problem from one that never
                // applied, and one an operator needs to be able to tell apart in a log.
                return CaptureUrlDecision.Refuse(ReasonAllowanceExpired);
            }

            return resolved.Any(address => address.Equals(allowance.Address))
                ? CaptureUrlDecision.Permit([allowance.Address])
                : CaptureUrlDecision.Refuse(ReasonAllowanceAddressMismatch);
        }

        return CaptureUrlDecision.Refuse(ReasonAllowanceOriginMismatch);
    }

    /// <summary>
    /// Whether one address is a cloud instance metadata service, which is never capturable.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived, because a derived rule ("is it link-local?", "is it
    /// loopback?") drifts as address families change and this answer must not. A metadata service
    /// hands out temporary credentials to anything that can make a request to it, so reaching one
    /// from a capture is a credential disclosure, not a rendering problem.
    /// </remarks>
    public static bool IsMetadataAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4 address written in IPv6 spelling - ::ffff:169.254.169.254 - is the same address, and
        // the IPv6 rules would not recognise it: its first byte is 0x00, so it is neither unique-local
        // nor link-local and it would be judged a public page. It is unwrapped here so both metadata
        // functions judge the address it actually names, whatever spelling a URL used to write it.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily is AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            // 169.254.169.254 is the IPv4 instance metadata address, and the whole 169.254.0.0/16 is
            // listed because a metadata service behind a different address in the link-local range is
            // the same class of target.
            return octets[0] == 169 && octets[1] == 254;
        }

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            // The two addresses a cloud metadata service is published at, written out rather than
            // derived. fd00:ec2::254 is AWS's IPv6 metadata address and sits in unique-local space, not
            // in the fe80::/10 link-local range, so a rule built on link-local alone would miss it -
            // and it is the address that hands out an IAM role's temporary credentials.
            if (IPAddress.IsLoopback(address) || address.Equals(Ipv6MetadataAddress))
            {
                return true;
            }

            // fe80::/10 link-local, where the other published IPv6 metadata addresses live. Whole
            // prefix, not just the one well-known address.
            var bytes = address.GetAddressBytes();

            return bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80;
        }

        return false;
    }

    /// <summary>
    /// The IPv6 instance metadata address, fd00:ec2::254.
    /// </summary>
    /// <remarks>
    /// In unique-local space rather than link-local, which is why it is named here instead of falling
    /// out of a prefix rule. It is the address whose response is a set of temporary credentials, so it
    /// is refused in every mode at every expiry exactly as its IPv4 counterpart is.
    /// </remarks>
    private static readonly IPAddress Ipv6MetadataAddress = IPAddress.Parse("fd00:ec2::254");

    /// <summary>
    /// Whether one address is a public, routable destination.
    /// </summary>
    /// <remarks>
    /// The inverse of what this package refuses: loopback, private ranges, carrier-grade NAT,
    /// link-local, multicast, broadcast, the IPv4 "this network" and documentation ranges, unspecified
    /// addresses, and the IPv6 equivalents of all of them. What is left is a public page.
    /// </remarks>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Unwrapped for the same reason as in IsMetadataAddress, and it is the one that decides whether
        // a private destination is reached: ::ffff:10.0.0.5 has a first byte of 0x00, so the IPv6 rules
        // below would call it a public page while the socket reaches a host on the owner's own network.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IsMetadataAddress(address))
        {
            return false;
        }

        if (address.AddressFamily is AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            if (octets[0] == 0 || octets[0] >= 224)
            {
                // 0.0.0.0/8 "this network" and 224.0.0.0/3 multicast plus broadcast.
                return false;
            }

            if (octets[0] == 127)
            {
                // 127.0.0.0/8, the whole loopback block and not only 127.0.0.1. The distinction matters
                // more here than it usually does: this package is exactly the thing an SSRF attempt
                // points at a loopback address, so 127.0.0.2 has to be refused for the same reason
                // 127.0.0.1 is.
                return false;
            }

            if (octets[0] == 10)
            {
                return false;
            }

            if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            {
                return false;
            }

            if (octets[0] == 192 && octets[1] == 168)
            {
                return false;
            }

            if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127)
            {
                // Carrier-grade NAT. A shared prefix an operator is very likely to photograph by
                // accident.
                return false;
            }

            if (octets[0] == 192 && octets[1] == 0 && octets[2] == 0)
            {
                return false;
            }

            if (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19))
            {
                return false;
            }

            if (octets[0] == 198 && octets[1] == 51 && octets[2] == 100)
            {
                return false;
            }

            if (octets[0] == 203 && octets[1] == 0 && octets[2] == 113)
            {
                return false;
            }

            return true;
        }

        if (address.AddressFamily is AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address))
            {
                return false;
            }

            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // :: and ::1 are caught by the loopback check; fc00::/7 unique-local is the v6 private
            // range and has no IsIPv6X equivalent, so it is written out.
            var bytes = address.GetAddressBytes();

            if (bytes[0] == 0xFC || bytes[0] == 0xFD)
            {
                return false;
            }

            return true;
        }

        // An address family this policy has not been taught to judge is not a public page. Refusing an
        // unknown family is the safe direction; permitting it would be a hole in a control whose only
        // job is to have none.
        return false;
    }

    /// <summary>
    /// The exact origin of a URL - scheme, host and explicit port - for comparison against an
    /// allowance.
    /// </summary>
    /// <remarks>
    /// The port is always explicit, including the default one, so that a comparison cannot be
    /// satisfied by one side omitting it. Case-insensitive comparison of scheme and host; the path,
    /// query, fragment and userinfo are all excluded, because an allowance is for an origin and an
    /// origin is exactly these three parts.
    /// </remarks>
    public static string FormatOrigin(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var port = url.IsDefaultPort
            ? (url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? 80 : 443)
            : url.Port;

        return $"{url.Scheme.ToLowerInvariant()}://{url.Host.ToLowerInvariant()}:{port}";
    }
}
