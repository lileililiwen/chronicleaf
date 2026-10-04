using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Chronicleaf.Capture.Sandbox;

/// <summary>
/// The two kernel questions the sandbox's availability turns on, asked of the kernel itself.
/// </summary>
/// <remarks>
/// <para>
/// Isolated here so the reason a capture is refused is one short, honest function rather than a
/// scattered set of checks. Both probes are read-only and cheap: one asks whether an unprivileged user
/// namespace can be created, the other whether this package's nftables tables are present. Neither
/// creates a namespace, installs a rule or writes a file, because this runs on the path of the first
/// capture request and a probe that mutated the host in order to find out would be a far worse thing
/// to run speculatively.
/// </para>
/// <para>
/// <b>A failure here is never treated as availability.</b> Every uncertain answer - a probe that could
/// not run, a platform whose model differs, an exception from the syscall - is an unavailable answer.
/// A capture control that guessed "probably fine" on an error would have exactly one property, and it
/// would be the wrong one.
/// </para>
/// </remarks>
internal static class NamespaceProbe
{
    /// <summary>
    /// Whether this process can create an unprivileged user namespace.
    /// </summary>
    /// <remarks>
    /// This is the prerequisite for an unprivileged network namespace: mapping to root inside a new
    /// user namespace is what grants the <c>CAP_NET_ADMIN</c> needed to create the network namespace
    /// the browser is confined to, without the application process ever holding it.
    /// <para>
    /// The <c>kernel.unprivileged_userns_clone</c> sysctl is read first because a distribution that
    /// sets it to 0 disables the whole path, and asking the kernel directly afterwards would give a
    /// less specific answer about why.
    /// </para>
    /// </remarks>
    public static bool TryCreateUserNamespace()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        try
        {
            if (UserNamespaceCloneDisabled())
            {
                return false;
            }

            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An unreadable sysctl is an unknown, and an unknown is not a yes.
            return false;
        }
    }

    /// <summary>
    /// Whether this package's owner-based firewall tables are installed on the host.
    /// </summary>
    /// <remarks>
    /// The rules are installed once by the container image's initialization step, which holds
    /// <c>CAP_NET_ADMIN</c>; the application process never does and never installs them. This asks
    /// whether they are there, because without them the egress proxy is a suggestion the browser can
    /// route around - a direct socket, a QUIC connection or a websocket would all leave without ever
    /// meeting the policy.
    /// <para>
    /// The check is for the table's presence rather than for a matching rule, on purpose: the rules
    /// themselves are the image's to install and version, and a table that exists is the property that
    /// makes the proxy the only exit.
    /// </para>
    /// </remarks>
    public static bool FirewallRulesPresent() => FirewallTables(out _);

    /// <summary>
    /// Runs <c>nft list tables</c> once and reports both whether the command could be run at all and
    /// whether this package's table is among the ones it printed.
    /// </summary>
    /// <remarks>
    /// The two answers are returned together rather than as one boolean because they fail differently
    /// and an operator needs to tell them apart: <c>nft</c> refusing with a permission error means the
    /// process holds no <c>CAP_NET_ADMIN</c> and therefore cannot confirm the rules exist, while a
    /// successful listing that simply does not contain the table means the container's initialization
    /// step has not run. Neither is a "yes", and they are not the same repair.
    /// </remarks>
    private static bool FirewallTables(out string? diagnostic)
    {
        diagnostic = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        foreach (var executable in new[] { "nft", "/usr/sbin/nft", "/sbin/nft" })
        {
            var located = Locate(executable);

            if (located is null)
            {
                continue;
            }

            try
            {
                using var probe = Process.Start(new ProcessStartInfo(located)
                {
                    // `list tables` rather than `list ruleset`: the question is whether the table this
                    // package's egress policy lives in exists, and enumerating rules would be a far
                    // larger answer to parse for no additional certainty.
                    ArgumentList = { "list", "tables" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (probe is null)
                {
                    continue;
                }

                var tables = probe.StandardOutput.ReadToEndAsync();

                if (!probe.WaitForExit(milliseconds: 5000))
                {
                    probe.Kill(entireProcessTree: true);
                    return false;
                }

                if (probe.ExitCode != 0)
                {
                    // Permission denied here is the expected answer on a host without CAP_NET_ADMIN, and
                    // it means exactly what it says: the rules cannot be verified, so they cannot be
                    // relied on.
                    diagnostic = "the firewall rule set could not be read";
                    return false;
                }

                var printed = tables.GetAwaiter().GetResult();

                return printed.Contains(CaptureTableName, StringComparison.Ordinal);
            }
            catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return false;
            }
        }

        diagnostic = "nft is not installed";

        return false;
    }

    /// <summary>
    /// The nftables table this package's egress policy is expressed as, and the name the container's
    /// initialization step creates.
    /// </summary>
    private const string CaptureTableName = "chronicleaf_capture";

    private static string? Locate(string executable)
    {
        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable) ? executable : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;

            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch (ArgumentException)
            {
                // A PATH entry that is not usable as a directory. Skipped rather than guessed at.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool UserNamespaceCloneDisabled()
    {
        foreach (var path in new[] { "/proc/sys/kernel/unprivileged_userns_clone", "/proc/sys/user/max_user_namespaces" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var value = File.ReadAllText(path).Trim();

            if (int.TryParse(value, out var parsed) && parsed == 0)
            {
                return true;
            }
        }

        return false;
    }
}
