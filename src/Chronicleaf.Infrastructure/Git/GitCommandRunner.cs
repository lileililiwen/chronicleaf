using System.Diagnostics;
using System.Text;

namespace Chronicleaf.Infrastructure.Git;

/// <summary>
/// What one Git process answered (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <b>Stderr is counted, never kept.</b> Git writes a repository's own words to stderr - a path, a
/// filename, a line of a commit message - and a value this class held could be interpolated into a log
/// line or an exception by a caller who did not mean to. The count is enough for every decision made
/// here: an empty stderr with a non-zero exit is a refusal, and the refusal's own outcome says why.
/// </remarks>
/// <param name="ExitCode">Git's exit code, or -1 when the process was stopped before it answered.</param>
/// <param name="StandardOutput">Everything Git wrote to stdout, already capped by the caller.</param>
/// <param name="StandardErrorBytes">How many bytes Git wrote to stderr. Never the bytes themselves.</param>
/// <param name="Truncated">
/// True when Git wrote more to stdout than the cap allowed, so the text is a prefix and not the whole
/// answer.
/// </param>
/// <param name="TimedOut">True when the process was killed for outliving its own bound.</param>
/// <param name="Started">False when Git could not be started at all.</param>
public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    long StandardErrorBytes,
    bool Truncated,
    bool TimedOut,
    bool Started)
{
    /// <summary>True when Git ran and said yes.</summary>
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    /// <summary>True when Git could not be started, which is a missing program rather than a refusal.</summary>
    public bool CouldNotStart => !Started;
}

/// <summary>
/// One Git invocation this package is willing to make (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the closed vocabulary of a read-only transport, and it is enforced by the runner rather
/// than trusted from here.</b> The subcommand is one of <c>rev-parse</c>, <c>log</c>, <c>diff</c> and
/// <c>status</c>: verbs that answer a question about committed state. There is no <c>commit</c>, no
/// <c>add</c>, no <c>checkout</c>, no <c>fetch</c> and no <c>push</c> in the set, so no caller - including
/// one that has not been written yet - can start one through this transport.
/// </para>
/// <para>
/// <b>Arguments are a list, never a string.</b> There is no shell, so nothing in a repository, a ref or a
/// filename is ever concatenated into a command line that something would parse. A ref written as
/// <c>--upload-pack=…</c> is refused by the policy before it reaches here, and the argument vector puts
/// the subcommand first and a <c>--</c> in front of the ref anyway, so it would be an argument even if the
/// policy were weakened.
/// </para>
/// </remarks>
/// <param name="Directory">The repository directory to run in, already resolved inside a registered root.</param>
/// <param name="Subcommand">The Git subcommand. One of the four this transport is allowed to run.</param>
/// <param name="Arguments">Everything after the subcommand, as separate arguments.</param>
public sealed record GitCommandRequest(string Directory, string Subcommand, IReadOnlyList<string> Arguments);

/// <summary>
/// Runs Git, and only the four reads this transport needs (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than a direct <see cref="Process"/> call, for one reason that has nothing to do with
/// testing convenience: the process boundary is where a timeout, a tree kill, an output cap and an
/// argument-vector guarantee live. A caller that could construct its own <see cref="Process"/> would not
/// have those, so all four are behind this seam where each of them is written once.
/// </para>
/// <para>
/// <b>Bounded on all three axes.</b> Time, by a per-process deadline that kills the whole tree. Output, by
/// a byte cap applied while the bytes arrive rather than after they have all been buffered. Standard
/// input, which is closed rather than inherited. The deadline is the real bound: a cap alone would let a
/// Git that prints nothing keep running until the next full moon.
/// </para>
/// </remarks>
public interface IGitCommandRunner
{
    /// <summary>Runs one read and returns what it answered.</summary>
    Task<GitCommandResult> RunAsync(
        GitCommandRequest request,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The real runner: one process, one argument vector, no shell, bounded time and bounded output
/// (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The subcommand is checked against a fixed set before the process starts.</b> This is the point of
/// the whole class. A read-only claim about a repository is only credible if the set of commands that can
/// be run against it is closed, and closing it here means the claim is a run-time fact rather than a
/// convention somebody could quietly extend.
/// </para>
/// <para>
/// <b>Everything hostile is turned off twice over.</b> The leading arguments from
/// <see cref="Application.Git.LocalGitPolicy.BaseArguments"/> disable the pager, the credential helper,
/// the external diff driver and repository hooks; the environment removes the terminal prompt and the
/// pager. A repository that would rather this did not read it can decline, and a repository that tries
/// to run a program on the way has nothing to run.
/// </para>
/// <para>
/// <b>Output is drained while it arrives and the process is killed, not merely abandoned.</b> Both streams
/// are read on background events so Git never blocks on a full pipe, and a Git that outlives its bound is
/// killed with its whole tree - which matters because a <c>git diff</c> on a very large repository can
/// outlive the deadline and would otherwise leave a process behind holding the worker's resources.
/// </para>
/// </remarks>
public sealed class GitCommandRunner : IGitCommandRunner
{
    /// <summary>
    /// The four subcommands this transport may run, and no others.
    /// </summary>
    /// <remarks>
    /// A property of the class rather than a caller-supplied value, which is what makes the read-only
    /// claim structural. Each entry answers a question; a verb that changes state is absent because it is
    /// not in this list, and adding one is a change a reviewer has to see in this file.
    /// </remarks>
    private static readonly string[] ReadOnlySubcommands = ["rev-parse", "log", "diff", "status"];

    private static readonly HashSet<string> Allowed = new(ReadOnlySubcommands, StringComparer.Ordinal);

    public async Task<GitCommandResult> RunAsync(
        GitCommandRequest request,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Allowed.Contains(request.Subcommand))
        {
            // Thrown rather than answered as a refusal, because reaching this line is a mistake in this
            // repository rather than something an operator can cause. The message names the four that
            // exist, so the fix is legible from the exception.
            throw new InvalidOperationException(
                $"This runner only runs {string.Join(", ", ReadOnlySubcommands)}; it was handed "
                + $"'{request.Subcommand}', which is not one of them.");
        }

        if (GitExecutable.Path is null)
        {
            // Answered before the process is constructed, because starting a process that does not exist
            // throws a platform exception whose message names the path it looked for - and that is the
            // operator's filesystem, which this package does not publish.
            return new GitCommandResult(-1, string.Empty, 0, Truncated: false, TimedOut: false, Started: false);
        }

        using var process = new Process { StartInfo = BuildStartInfo(request) };

        var output = new StringBuilder();
        long errorBytes = 0;
        var truncated = false;
        var gate = new object();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (gate)
            {
                if (Encoding.UTF8.GetByteCount(output.ToString()) >= maxOutputBytes)
                {
                    // The cap is enforced here rather than after the read, so a process that prints
                    // gigabytes costs this worker a bounded amount of memory. The bytes past the cap are
                    // dropped and the answer is marked as a prefix, which is what makes the truncation
                    // visible in evidence rather than silent.
                    truncated = true;
                    return;
                }

                output.Append(args.Data).Append('\n');
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            // Counted and discarded, never accumulated: stderr is where Git puts a path, a filename and
            // occasionally a line of a commit message, and none of that is this package's to hold.
            Interlocked.Add(ref errorBytes, args.Data.Length);
        };

        try
        {
            if (!process.Start())
            {
                return new GitCommandResult(-1, string.Empty, 0, Truncated: false, TimedOut: false, Started: false);
            }
        }
        catch (Exception failure) when (
            failure is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new GitCommandResult(-1, string.Empty, 0, Truncated: false, TimedOut: false, Started: false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(timeout);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var timedOut = false;

        try
        {
            // `WaitForExitAsync` rather than the `Exited` event, and that is a correction rather than a
            // preference: `Exited` is only raised once `EnableRaisingEvents` has been asked for, so a wait
            // built on it can simply never complete - which it did, for every command, until this line
            // replaced it. It also drains both redirected streams before returning, so a Git that wrote a
            // great deal is not truncated by its own cleanup.
            await process.WaitForExitAsync(stopped.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The caller stopped waiting. The process is ended and the cancellation is re-thrown, so a
                // cancelled collection is a cancelled collection rather than a read that happened to run
                // out of time.
                Kill(process);
                throw;
            }

            // The deadline. The process is killed with its whole tree and the answer says so, rather than
            // reporting a prefix of whatever it managed to write before the bound.
            timedOut = true;
            Kill(process);
        }

        // The exit code is read after any kill, so a killed process's -1 does not throw.
        var exitCode = ReadExitCode(process);

        lock (gate)
        {
            return new GitCommandResult(
                exitCode,
                output.ToString(),
                Interlocked.Read(ref errorBytes),
                truncated,
                timedOut,
                Started: true);
        }
    }

    /// <summary>
    /// The exact process this package starts, assembled here so a test can read it rather than infer it
    /// from a call that happened.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(GitCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startInfo = new ProcessStartInfo(GitExecutable.Path ?? "git")
        {
            // The working directory rather than `-C`: Git honours both, and the working directory is the
            // one the operating system resolves, so the path that survived the containment check is the
            // path the process actually starts in. No shell is involved, so nothing here is ever parsed.
            WorkingDirectory = request.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Git's own base arguments come first, so nothing a caller supplies can precede them.
        foreach (var argument in Application.Git.LocalGitPolicy.BaseArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(request.Subcommand);

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in Application.Git.LocalGitPolicy.ProcessEnvironment())
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    /// <summary>
    /// Ends the process and everything it started, and is safe to call on a process that already exited.
    /// </summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception failure) when (
            failure is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // A process that exited between the check and the kill, or one this platform refuses to kill.
            // Either way there is nothing left to do, and an exception here would replace the real answer -
            // a timeout, a cancellation or an exit code - with a bookkeeping failure.
        }
    }

    private static int ReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}

/// <summary>
/// Where this package looks for Git, and the answer it gives when there is none (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved once, and never taken from configuration.</b> An operator who could point this package at
/// a different Git binary could make every read run something else entirely, and a configuration key for a
/// program path is exactly the kind of setting that gets pointed somewhere temporary during an incident.
/// The program is Git, found on PATH or in one of two standard locations, and nothing else is ever run.
/// </para>
/// <para>
/// <b>A missing Git is its own answer, not an exit code.</b> "Git is not installed" and "this repository
/// said no" have nothing in common operationally - one is an operator's installation, the other is a fact
/// about a directory - and a caller that cannot tell them apart retries the wrong one forever.
/// </para>
/// </remarks>
public static class GitExecutable
{
    private static readonly Lock Gate = new();
    private static string? _resolved;
    private static bool _looked;

    /// <summary>The full path Git was found at, or <c>null</c> when it was not found.</summary>
    public static string? Path
    {
        get
        {
            lock (Gate)
            {
                if (!_looked)
                {
                    _resolved = Locate();
                    _looked = true;
                }

                return _resolved;
            }
        }
    }

    /// <summary>
    /// Forgets the cached answer so a scenario can act on a PATH that has changed. Maintenance and tests
    /// only; production reads it once and never again.
    /// </summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            _resolved = null;
            _looked = false;
        }
    }

    private static string? Locate()
    {
        // `System.IO.Path` is spelled out throughout this method because the class's own `Path` property -
        // the answer to "where is Git" - would otherwise shadow it, and a reader would have to work out
        // which of the two a bare `Path` meant.
        foreach (var candidate in Candidates())
        {
            if (System.IO.Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string full;

                try
                {
                    full = System.IO.Path.Combine(directory, candidate);
                }
                catch (ArgumentException)
                {
                    // A PATH entry that is not usable as a directory. Skipped rather than guessed at.
                    continue;
                }

                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return OperatingSystem.IsWindows() ? "git.exe" : "git";
        yield return "/usr/bin/git";
        yield return "/usr/local/bin/git";
    }
}
