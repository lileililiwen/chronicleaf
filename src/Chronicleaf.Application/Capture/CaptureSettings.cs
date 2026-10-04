using Chronicleaf.Core;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// The two deployment facts the capture policy is evaluated against (SCREENSHOT-CAPTURE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>These are inputs, not documentation.</b> The deployment mode is what makes the same request two
/// different answers, and the allowances are the operator's own statement that one exact local origin
/// may be photographed for a while. A service that decided either by convention would not be
/// implementing the design's boundary, it would be describing it.
/// </para>
/// <para>
/// <b>Hosted reads no allowance at all.</b> On a shared machine the private ranges a single-tenant
/// install may photograph belong to other tenants, so the policy refuses a local destination there
/// whatever this list contains - which is why the mode is checked first and the list second.
/// </para>
/// <para>
/// The list is empty by default and is never populated from a request body. An allowance is an
/// operator's configuration decision made out of band, which is why it carries an expiry: a preview
/// server is up for an afternoon, and a permanent hole with an extra step is not a control.
/// </para>
/// </remarks>
public sealed class CaptureSettings
{
    /// <summary>Configuration section, matching the sandbox settings' own section.</summary>
    public const string SectionName = "Chronicleaf:Capture";

    /// <summary>
    /// Which deployment this is. Defaults to Community, and is the one setting that changes an
    /// outcome rather than a detail.
    /// </summary>
    public CaptureDeploymentMode DeploymentMode { get; set; } = CaptureDeploymentMode.Community;

    /// <summary>
    /// The operator-configured local allowances, consulted only in Community.
    /// </summary>
    /// <remarks>
    /// Empty rather than a permissive default. A capture that may reach a private address because no
    /// list was configured would be a capture that may reach every private address, which is the
    /// opposite of the intended reading.
    /// </remarks>
    public IReadOnlyList<CaptureLocalAllowance> LocalAllowances { get; set; } = [];
}
