using System.Globalization;
using System.Text;
using Chronicleaf.Application.Git;
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Infrastructure.Git;

/// <summary>
/// Reads committed revisions off the operator's own disk, through Git, and nothing else
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every fact comes from comparing two resolved commits.</b> A ref is resolved to a SHA first, and every
/// later command takes SHAs rather than ref names. That is what makes the exclusion of the working tree
/// structural rather than a filter: <c>git diff</c> between two SHAs and <c>git log</c> over a range of them
/// never read an index, a worktree or a file path, so an untracked file, a staged change and a dirty edit
/// are not candidates for anything - there is no point in the code where they could be read and then decided
/// against.
/// </para>
/// <para>
/// <b>Only the four read verbs are ever issued</b>, and the runner enforces that set rather than trusting
/// this class. The transport cannot fetch, pull, clone, push, commit, checkout, reset, clean or stash,
/// because those subcommands are not in the set it will run. A repository this product reads is a
/// repository it only reads, and that is a property of the shape rather than of a policy that could be
/// forgotten.
/// </para>
/// <para>
/// <b>Containment is re-established before every read.</b> The alias is resolved against the operator's
/// table, the relative path is resolved segment by segment with symlinks followed, and the result must be
/// inside the registered root or nothing runs. A path that was inside a root when it was registered can be
/// a link out of it by the time a job runs, and the design's boundary scenario is exactly that case - so the
/// check is made here, immediately before the process starts, and not only at registration.
/// </para>
/// <para>
/// <b>The last read of a run re-resolves the head.</b> A local collection is several commands over some
/// seconds rather than one HTTP call, and a commit made in the middle of that window would otherwise
/// produce a bundle whose items name one revision and hold facts from another. So the head is resolved again
/// at the end and a difference is reported as its own outcome - nothing is appended, and the operation says
/// superseded rather than succeeding with a mixed set.
/// </para>
/// <para>
/// <b>Nothing here needs a credential and nothing here could use one.</b> A local repository is read with the
/// operator's own Git against the operator's own disk; there is no remote, no token and no host. The
/// credential parameter of the shared contract is therefore refused outright rather than ignored, because a
/// caller that passed one was expecting a network transport and would otherwise be collecting from a place
/// it did not name.
/// </para>
/// </remarks>
public sealed class LocalGitEvidenceSource(
    IGitCommandRunner runner,
    ILocalRepositoryCatalog catalog,
    TimeProvider clock) : IGitEvidenceSource, ILocalGitSource
{
    /// <summary>The separator between records in the machine-readable formats this transport asks for.</summary>
    private const char RecordSeparator = '\u001e';

    /// <summary>The separator between fields within one record of those formats.</summary>
    private const char UnitSeparator = '\u001f';

    /// <summary>
    /// One log record: the commit, its first parent, the author, the timestamp and the subject, all
    /// separated by the two control characters above.
    /// </summary>
    /// <remarks>
    /// The separators are control characters because a commit subject may contain anything at all, including
    /// newlines, and a format built from spaces or tabs would be ambiguous in exactly the case where a
    /// message is most worth reading. The subject is bounded afterwards rather than in the format, because
    /// Git has no way to bound it.
    /// </remarks>
    private const string LogFormat = "%x1e%H%x1f%P%x1f%an%x1f%aI%x1f%s%x1f";


    /// <inheritdoc />
    public async Task<GitEvidenceResult> DescribeRepositoryAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (credential is not null)
        {
            // Refused rather than ignored. This transport reads a directory on the operator's disk and has no
            // remote to authenticate to, so a caller that supplied a credential was expecting a network
            // transport; collecting anyway would produce a bundle from a place it did not name.
            return GitEvidenceResult.Rejected(clock.GetUtcNow());
        }

        var now = clock.GetUtcNow();

        if (!repository.IsLocal)
        {
            // The mirror image of the check the local reference performs when a URL is asked for. A hosted
            // repository reference handed to the local transport is a wiring mistake, and answering with a
            // refusal is safer than resolving a path from a name that was never one.
            return GitEvidenceResult.Rejected(now);
        }

        var directory = Resolve(repository);

        if (directory is null)
        {
            return Failure(repository, now);
        }

        // Two reads, and each answers a question the registration needs before a row exists: is this a
        // repository at all, and which commit is its HEAD.
        var topLevel = await ReadAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(topLevel, "true", StringComparison.Ordinal))
        {
            return GitEvidenceResult.NotARepository(now);
        }

        // `--abbrev-ref` rather than `symbolic-ref`, so this stays inside the four verbs the runner allows.
        // A detached HEAD answers "HEAD", which is a true statement about the repository and is recorded as
        // such rather than being replaced with a branch nobody checked out.
        var branch = await ReadAsync(directory, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        var head = await ReadAsync(
                directory,
                ["rev-parse", "--verify", "--quiet", "--end-of-options", "HEAD^{commit}"],
                cancellationToken)
            .ConfigureAwait(false);

        if (head is null)
        {
            // A repository with no commit yet is a real repository with nothing to collect, and the honest
            // answer is "it is one" rather than a refusal: the registration stays valid and the first commit
            // will be collectable a moment later.
            return new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, now)
            {
                DefaultBranch = branch is "HEAD" ? null : branch,
            };
        }

        return new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, now)
        {
            DefaultBranch = branch is "HEAD" ? null : branch,
            ResolvedHeadSha = head,
        };
    }

    /// <inheritdoc />
    public async Task<GitEvidenceResult> FetchAsync(
        RepoRef repository,
        GitEvidenceRequest request,
        string? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();

        if (credential is not null || !repository.IsLocal)
        {
            return GitEvidenceResult.Rejected(now);
        }

        var directory = Resolve(repository);

        if (directory is null)
        {
            return Failure(repository, now);
        }

        var endpoints = new List<GitEndpoint>();
        var exclusions = new List<GitExclusion>();
        var truncations = new List<GitTruncation>();
        var unavailable = new List<GitUnavailable>();
        var commits = new List<GitCommit>();
        GitCompare? compare = null;

        // 1. The head, resolved before anything else. `--end-of-options` is what makes a ref beginning with a
        //    dash an argument rather than an option, and the policy has already refused that shape.
        var head = await ReadAsync(
                directory,
                ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{request.HeadRef}^{{commit}}"],
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(head))
        {
            return GitEvidenceResult.Rejected(now);
        }

        endpoints.Add(new GitEndpoint("rev-parse", "head", 0));

        // 2. The base, when one was asked for. Resolved before any fact is read, for the same reason the
        //    head is: a comparison between two revisions has to be between two revisions that were both named
        //    while this run started.
        string? baseSha = null;

        if (!string.IsNullOrWhiteSpace(request.BaseRef))
        {
            var resolved = await ReadAsync(
                    directory,
                    ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{request.BaseRef}^{{commit}}"],
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(resolved))
            {
                // A base that does not resolve is a named part of the request that could not be read, recorded
                // as missing rather than dropped. An owner told a comparison covers an issue it does not
                // mention is worse off than one told plainly that the base was not found.
                unavailable.Add(new GitUnavailable($"base:{request.BaseRef}", GitUnavailableReasons.NotFound));

                return new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, now)
                {
                    ResolvedHeadSha = head,
                    Unavailable = unavailable,
                    Endpoints = endpoints,
                };
            }

            baseSha = resolved;
            endpoints.Add(new GitEndpoint("rev-parse", "base", 0));
        }

        // 3. The working-tree notice, read before the diff so a dirty tree is recorded even when the diff
        //    turns out to be empty. A count and never a list of names, because one of those names could be a
        //    file nobody meant to publish.
        var uncommitted = await CountUncommittedAsync(directory, endpoints, cancellationToken).ConfigureAwait(false);

        // 4. The commits between them, asking for one more than the limit and treating the extra as a
        //    truncation. Reading exactly the limit would make "there were more" indistinguishable from
        //    "there were exactly this many".
        var logLimit = (request.MaxCommits + 1).ToString(CultureInfo.InvariantCulture);
        var range = baseSha is null ? head : $"{baseSha}..{head}";

        var log = await ReadAsync(
                directory,
                [
                    "log",
                    "--no-decorate",
                    "--no-color",
                    $"--max-count={logLimit}",
                    "--format=" + LogFormat,
                    "--end-of-options",
                    range,
                ],
                cancellationToken)
            .ConfigureAwait(false);

        if (log is null)
        {
            return GitEvidenceResult.CouldNotBeRead(now);
        }

        endpoints.Add(new GitEndpoint("log", "commits", 0));

        foreach (var (sha, author, authoredAt, summary) in ParseCommits(log))
        {
            if (commits.Count == request.MaxCommits)
            {
                truncations.Add(new GitTruncation(GitSegments.Commits, commits.Count, request.MaxCommits));
                break;
            }

            commits.Add(new GitCommit(sha, Bounded(summary, 200), Bounded(author, 120), authoredAt));
        }

        // 5. The changed files and their patches, from the two SHAs rather than from the worktree.
        if (baseSha is not null)
        {
            var collected = await CollectDiffAsync(
                    directory,
                    baseSha,
                    head,
                    request,
                    commits.Count,
                    endpoints,
                    exclusions,
                    truncations,
                    cancellationToken)
                .ConfigureAwait(false);

            if (collected is null)
            {
                return GitEvidenceResult.CouldNotBeRead(now);
            }

            compare = collected;
        }

        // 6. The head again. This is the read that turns a repository that moved under the run into a named
        //    outcome rather than a bundle holding two revisions at once.
        var finalHead = await ReadAsync(
                directory,
                ["rev-parse", "--verify", "--quiet", "--end-of-options", $"{request.HeadRef}^{{commit}}"],
                cancellationToken)
                .ConfigureAwait(false);

        endpoints.Add(new GitEndpoint("rev-parse", "head-recheck", 0));

        if (!string.Equals(finalHead, head, StringComparison.Ordinal))
        {
            return GitEvidenceResult.Superseded(now);
        }

        return new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, now)
        {
            ResolvedHeadSha = head,
            ResolvedBaseSha = baseSha,
            Commits = commits,
            Compare = compare,
            Unavailable = unavailable,
            Endpoints = endpoints,
            Exclusions = exclusions,
            Truncations = truncations,
            UncommittedChanges = uncommitted,
        };
    }

    /// <summary>
    /// The one place a name becomes a directory, and it is refused unless the filesystem agrees the path is
    /// inside a registered root (LOCAL-GIT-EVIDENCE-002).
    /// </summary>
    /// <remarks>
    /// Returning a value rather than throwing is deliberate: the three ways this can fail - an alias nobody
    /// registered, a path that leaves its root, and a root whose directory is unreadable - are three different
    /// answers for the owner and the operation row, and one exception would collapse them into one.
    /// </remarks>
    private string? Resolve(RepoRef repository) => catalog.Find(repository.Owner) is { } root
        ? LocalGitPolicy.ResolveWithin(root, repository.Name).Directory
        : null;

    /// <summary>
    /// Which of the refusals a directory that could not be resolved deserves.
    /// </summary>
    /// <remarks>
    /// The distinction is made once, from the same two facts, so a caller reading a refusal code knows
    /// whether to change an alias, a path or an operator's configuration.
    /// </remarks>
    private GitEvidenceResult Failure(RepoRef repository, DateTimeOffset now)
    {
        if (catalog.Find(repository.Owner) is null)
        {
            return new GitEvidenceResult(GitEvidenceOutcome.RootUnknown, null, now);
        }

        // The two containment answers are told apart here, and the mapping is the same one the registration
        // route uses: a link that left the root is a refusal to look outside, and a path that is not there
        // is a fact about a directory.
        var resolution = LocalGitPolicy.ResolveWithin(catalog.Find(repository.Owner)!, repository.Name).Resolution;

        return resolution is LocalPathResolution.Outside
            ? new GitEvidenceResult(GitEvidenceOutcome.OutsideRoot, null, now)
            : new GitEvidenceResult(GitEvidenceOutcome.NotARepository, null, now);
    }

    /// <summary>
    /// Runs one read and returns its trimmed output, or <c>null</c> when the process could not run, timed
    /// out, was truncated by the pipe, or refused.
    /// </summary>
    /// <remarks>
    /// A missing Git and a process that outlived its bound are raised as their own exceptions, because they
    /// are operator-repairable conditions rather than answers about a repository. Everything else is
    /// <c>null</c>: a ref that does not exist and a directory that is not a repository both refuse, and
    /// telling them apart from a message would put a repository's own words into this package's answer.
    /// </remarks>
    private async Task<string?> ReadAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await runner
            .RunAsync(
                new GitCommandRequest(directory, arguments[0], [.. arguments.Skip(1)]),
                LocalGitLimits.ProcessTimeout,
                LocalGitLimits.MaxOutputBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return Interpret(result);
    }

    /// <summary>
    /// The one place a process result becomes either text or a refusal, so every read in this class is
    /// classified the same way.
    /// </summary>
    private static string? Interpret(GitCommandResult result)
    {
        if (result.CouldNotStart)
        {
            throw new LocalGitUnavailableException();
        }

        if (result.TimedOut)
        {
            throw new LocalGitTimeoutException();
        }

        // Truncated output is refused rather than published. A prefix of an answer with nothing in it saying
        // what is missing is exactly the kind of fact this product's evidence contract exists to prevent.
        if (result.Truncated || !result.Succeeded)
        {
            return null;
        }

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// How many uncommitted changes the working tree held, or <c>null</c> when Git would not say.
    /// </summary>
    /// <remarks>
    /// <c>--porcelain</c> because it is a stable machine format, and the lines it produces are counted and
    /// dropped immediately - never accumulated, never stored. The count is what reaches the bundle, and it is
    /// the honest report: this package read committed revisions only, and a reader who does not know the tree
    /// was dirty would read the bundle as a description of the directory rather than of the commit.
    /// </remarks>
    private async Task<int?> CountUncommittedAsync(
        string directory,
        List<GitEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        var result = await runner
            .RunAsync(
                new GitCommandRequest(
                    directory,
                    "status",
                    ["--porcelain", "--untracked-files=normal", "--ignore-submodules=none"]),
                LocalGitLimits.ProcessTimeout,
                LocalGitLimits.MaxOutputBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.CouldNotStart)
        {
            throw new LocalGitUnavailableException();
        }

        if (result.TimedOut)
        {
            throw new LocalGitTimeoutException();
        }

        if (result.Truncated || !result.Succeeded)
        {
            // Not an error: an unanswerable question is reported as "not reported" rather than as zero, because
            // zero would claim a clean tree.
            return null;
        }

        endpoints.Add(new GitEndpoint("status", "uncommitted", 0));

        var counted = 0;
        using var reader = new StringReader(result.StandardOutput);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (++counted >= LocalGitLimits.MaxUncommittedCounted)
            {
                break;
            }
        }

        return counted;
    }

    /// <summary>
    /// Reads the changed files and their patches between two resolved commits.
    /// </summary>
    /// <remarks>
    /// <b>Two processes, not one per file.</b> A name-status pass gives the paths and their statuses, then a
    /// single diff gives every patch at once. A repository with a thousand changed files therefore costs two
    /// processes rather than a thousand, and the diff budget is spent in the order the files were listed rather
    /// than in whatever order the filesystem happened to answer in.
    /// </remarks>
    private async Task<GitCompare?> CollectDiffAsync(
        string directory,
        string baseSha,
        string headSha,
        GitEvidenceRequest request,
        int commitCount,
        List<GitEndpoint> endpoints,
        List<GitExclusion> exclusions,
        List<GitTruncation> truncations,
        CancellationToken cancellationToken)
    {
        var nameStatus = await runner
            .RunAsync(
                new GitCommandRequest(
                    directory,
                    "diff",
                    [
                        "--no-ext-diff",
                        "--no-textconv",
                        "--no-color",
                        "--find-renames",
                        "--name-status",
                        "-z",
                        "--end-of-options",
                        $"{baseSha}..{headSha}",
                    ]),
                LocalGitLimits.ProcessTimeout,
                LocalGitLimits.MaxOutputBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (nameStatus.CouldNotStart)
        {
            throw new LocalGitUnavailableException();
        }

        if (nameStatus.TimedOut)
        {
            throw new LocalGitTimeoutException();
        }

        if (nameStatus.Truncated || !nameStatus.Succeeded)
        {
            return null;
        }

        endpoints.Add(new GitEndpoint("diff", "files", 0));

        var status = await CompareStatusAsync(directory, baseSha, headSha, endpoints, cancellationToken)
            .ConfigureAwait(false);

        var files = new List<GitFile>();
        var readable = new List<(string Path, string Status)>();

        // The file cap is applied to every file this run *records*, an excluded one included, and it is
        // applied before any patch is read. Both halves matter. Counting only the readable ones would let a
        // repository of a thousand secrets blow past the cap while recording nothing about them; reading the
        // files past the cap and then dropping them would spend the diff budget on text nothing keeps.
        var recorded = 0;

        foreach (var (path, fileStatus) in ParseNameStatus(nameStatus.StandardOutput))
        {
            if (recorded >= request.MaxChangedFiles)
            {
                truncations.Add(new GitTruncation(GitSegments.Files, recorded, request.MaxChangedFiles));
                break;
            }

            if (SecretFilenames.IsExcluded(path))
            {
                // The name is recorded and the content is never read at all - not truncated, not hashed, not
                // stored. What is kept is the fact that a file was excluded, which is a fact about the
                // collection rather than about the secret.
                exclusions.Add(new GitExclusion(path, SecretFilenames.Reason));
                files.Add(new GitFile(path, fileStatus, 0, 0, null, PatchMissing: true, SecretFilenames.Reason));
                recorded++;
                continue;
            }

            readable.Add((path, fileStatus));
            recorded++;
        }

        if (readable.Count > 0)
        {
            var paths = new List<string>(readable.Count);

            foreach (var (path, _) in readable)
            {
                paths.Add(path);
            }

            var diff = await runner
                .RunAsync(
                    new GitCommandRequest(
                        directory,
                        "diff",
                        PatchArguments($"{baseSha}..{headSha}", paths)),
                    LocalGitLimits.ProcessTimeout,
                    LocalGitLimits.MaxDiffOutputBytes,
                    cancellationToken)
                .ConfigureAwait(false);

            if (diff.CouldNotStart)
            {
                throw new LocalGitUnavailableException();
            }

            if (diff.TimedOut)
            {
                throw new LocalGitTimeoutException();
            }

            if (diff.Truncated)
            {
                // A prefix of the whole diff with no declared cap. Reported as unavailable rather than
                // published, because a reader could not tell it from a complete patch.
                return null;
            }

            endpoints.Add(new GitEndpoint("diff", "patches", diff.Succeeded ? 0 : -1));

            if (diff.Succeeded)
            {
                ApplyPatches(readable, diff.StandardOutput, request.MaxDiffBytes, files, truncations);
            }
        }

        return new GitCompare(status, commitCount, files);
    }

    /// <summary>
    /// The one patch-bearing diff: the flags, the two resolved revisions, the pathspec separator, and the
    /// readable paths.
    /// </summary>
    /// <remarks>
    /// <b>The order is the correctness of the whole read</b>, and it is not cosmetic. Git treats everything
    /// after a bare <c>--</c> as a pathspec, so a range placed after the separator is read as the name of a
    /// file rather than as two revisions - which produces an empty diff, an exit code of zero and no error
    /// anywhere. That is a silent no-op of exactly the kind this repository has been bitten by before, so
    /// the range comes first and the separator comes before the paths and not before the range.
    /// <para>
    /// A path whose name begins with a dash is therefore a path and not an option, because it is after the
    /// separator. The flags that could change what the diff does are all spelled out and none of them is
    /// left to a repository's own configuration: no external diff driver, no textconv filter, no colour.
    /// </para>
    /// </remarks>
    private static string[] PatchArguments(string range, List<string> paths)
    {
        var arguments = new string[7 + paths.Count];
        arguments[0] = "--no-ext-diff";
        arguments[1] = "--no-textconv";
        arguments[2] = "--no-color";
        arguments[3] = "--find-renames";
        arguments[4] = "--unified=3";
        arguments[5] = range;
        arguments[6] = "--";
        paths.CopyTo(0, arguments, 7, paths.Count);

        return arguments;
    }

    /// <summary>
    /// Spends the diff budget across the readable files in the order they were listed, and records what was
    /// left out.
    /// </summary>
    private static void ApplyPatches(
        List<(string Path, string Status)> readable,
        string diffText,
        int maxDiffBytes,
        List<GitFile> files,
        List<GitTruncation> truncations)
    {
        var patches = ParsePatches(diffText);
        var budget = maxDiffBytes;
        var budgetReported = false;

        foreach (var (path, fileStatus) in readable)
        {
            if (!patches.TryGetValue(path, out var patch) || patch is null)
            {
                // A binary file, or one Git declined to patch. Recorded as missing with its reason rather than
                // as an empty diff, because "no patch" and "no change" are different facts and only one of
                // them is true.
                files.Add(new GitFile(
                    path,
                    fileStatus,
                    0,
                    0,
                    null,
                    PatchMissing: true,
                    GitUnavailableReasons.PatchAbsent));
                continue;
            }

            var size = Encoding.UTF8.GetByteCount(patch);

            if (size > budget)
            {
                // The budget is spent here and the file becomes unavailable with the reason, which is what makes
                // the cut visible: a file with no patch and a file past the cap are the same shape to a reader
                // and must not read the same.
                files.Add(new GitFile(
                    path,
                    fileStatus,
                    0,
                    0,
                    null,
                    PatchMissing: true,
                    GitSegments.DiffText));

                if (!budgetReported)
                {
                    truncations.Add(new GitTruncation(GitSegments.DiffText, budget, maxDiffBytes));
                    budgetReported = true;
                }

                continue;
            }

            budget -= size;
            files.Add(new GitFile(path, fileStatus, 0, 0, patch, PatchMissing: false, null));
        }
    }

    /// <summary>
    /// Whether the head is ahead of, behind, identical to, or diverged from the base, computed from the two
    /// rev-list counts Git itself produces.
    /// </summary>
    /// <remarks>
    /// The delivered GitHub vocabulary, kept identical so a reader of a bundle does not have to learn a second
    /// set of words for the same question. Diverged is checked before ahead, because a repository with commits
    /// on both sides satisfies both counts and the more specific answer is the useful one.
    /// </remarks>
    private async Task<string> CompareStatusAsync(
        string directory,
        string baseSha,
        string headSha,
        List<GitEndpoint> endpoints,
        CancellationToken cancellationToken)
    {
        var ahead = await runner
            .RunAsync(
                new GitCommandRequest(
                    directory,
                    "log",
                    ["--format=%H", "--end-of-options", $"{baseSha}..{headSha}"]),
                LocalGitLimits.ProcessTimeout,
                LocalGitLimits.MaxOutputBytes,
                cancellationToken)
            .ConfigureAwait(false);

        var behind = await runner
            .RunAsync(
                new GitCommandRequest(
                    directory,
                    "log",
                    ["--format=%H", "--end-of-options", $"{headSha}..{baseSha}"]),
                LocalGitLimits.ProcessTimeout,
                LocalGitLimits.MaxOutputBytes,
                cancellationToken)
            .ConfigureAwait(false);

        if (ahead.CouldNotStart || behind.CouldNotStart)
        {
            throw new LocalGitUnavailableException();
        }

        if (ahead.TimedOut || behind.TimedOut)
        {
            throw new LocalGitTimeoutException();
        }

        if (ahead.Truncated || behind.Truncated || !ahead.Succeeded || !behind.Succeeded)
        {
            return "unknown";
        }

        endpoints.Add(new GitEndpoint("log", "compare", 0));

        return (CountLines(ahead.StandardOutput), CountLines(behind.StandardOutput)) switch
        {
            (0, 0) => "identical",
            (> 0, 0) => "ahead",
            (0, > 0) => "behind",
            _ => "diverged",
        };
    }

    private static int CountLines(string text)
    {
        var count = 0;

        foreach (var character in text)
        {
            if (character is '\n')
            {
                count++;
            }
        }

        return text.Length > 0 && !text.EndsWith('\n') ? count + 1 : count;
    }

    /// <summary>
    /// The commits from a <c>git log</c> in the fixed machine format, one record per commit.
    /// </summary>
    private static IEnumerable<(string Sha, string? Author, DateTimeOffset? AuthoredAt, string Summary)> ParseCommits(
        string log)
    {
        foreach (var record in log.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split(UnitSeparator);

            if (fields.Length < 5)
            {
                continue;
            }

            var authoredAt = DateTimeOffset.TryParse(
                fields[3],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

            yield return (fields[0].Trim(), fields[2], authoredAt, fields[4].Trim());
        }
    }

    /// <summary>
    /// The paths and statuses from a <c>--name-status -z</c> pass.
    /// </summary>
    /// <remarks>
    /// <c>-z</c> because a path may contain a space, a quote and a newline, and the line-based format quotes
    /// and escapes all three. A rename is a status letter followed by a second path, which is dropped: the
    /// evidence names the new path, and the old one is a path the repository no longer has.
    /// </remarks>
    private static List<(string Path, string Status)> ParseNameStatus(string text)
    {
        var fields = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<(string Path, string Status)>();

        for (var index = 0; index < fields.Length;)
        {
            var status = fields[index].Trim();
            index++;

            if (status.Length == 0)
            {
                continue;
            }

            if (index >= fields.Length)
            {
                break;
            }

            var path = fields[index];
            index++;

            if (status[0] is 'R' or 'C' && index < fields.Length)
            {
                // The old path of a rename or copy, which the evidence does not name.
                index++;
            }

            entries.Add((path, NormaliseStatus(status)));
        }

        return entries;
    }

    /// <summary>
    /// Git's status letters as the single lower-case words the delivered evidence kinds use.
    /// </summary>
    /// <remarks>
    /// A closed mapping rather than the letter, because the letters are Git's implementation vocabulary and
    /// <c>R100</c> is not something a reader can act on. Anything unrecognised becomes <c>modified</c>, which
    /// is the honest reading of "the file changed": a status this package does not know is not a status it
    /// should report as something it invented.
    /// </remarks>
    private static string NormaliseStatus(string status) => status[0] switch
    {
        'A' => "added",
        'D' => "removed",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type-changed",
        _ => "modified",
    };

    /// <summary>
    /// The patch text per path from a unified diff, keyed by the path Git named.
    /// </summary>
    /// <remarks>
    /// A binary file is a value of <c>null</c> rather than a missing key, which is how a binary file stays
    /// distinguishable from a file that was not in the diff at all. A repository that renames a file puts the
    /// new path in the <c>+++</c> header, which is the one a reader would search for.
    /// </remarks>
    private static Dictionary<string, string?> ParsePatches(string diff)
    {
        var patches = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var section in diff.Split("diff --git ", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = section.Split('\n');
            string? path = null;
            var binary = false;
            var body = new StringBuilder();

            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                if (line.StartsWith("Binary files", StringComparison.Ordinal) ||
                    line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                {
                    binary = true;
                    continue;
                }

                if (path is null && line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    // `/dev/null` on the left of a deletion is a file that no longer exists, so the name comes
                    // from the `---` line instead.
                    var target = line[4..].Trim();

                    if (target == "/dev/null" &&
                        index > 0 &&
                        lines[index - 1].StartsWith("--- ", StringComparison.Ordinal))
                    {
                        target = lines[index - 1][4..].Trim();
                    }

                    path = target.StartsWith('b') ? target[2..] : target;

                    // A hunk header means the real patch begins on the next line, and it is part of the patch.
                    if (index + 1 < lines.Length && lines[index + 1].StartsWith("@@", StringComparison.Ordinal))
                    {
                        body.Append(lines[index + 1]).Append('\n');
                        index++;
                    }

                    continue;
                }

                if (path is not null)
                {
                    body.Append(line).Append('\n');
                }
            }

            if (path is not null)
            {
                patches[path] = binary ? null : body.ToString();
            }
        }

        return patches;
    }

    /// <summary>
    /// A commit subject or author name, bounded.
    /// </summary>
    /// <remarks>
    /// Bounded because both come from the repository and both end up in an evidence item, which has a length
    /// limit of its own. A subject long enough to overflow that is a subject nobody reads; cutting it with a
    /// visible marker is better than refusing the whole collection over one commit.
    /// </remarks>
    private static string Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
