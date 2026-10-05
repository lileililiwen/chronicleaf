using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Builds real Git repositories in a real temporary directory, for the local evidence scenarios.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture runs its own <c>git</c>, deliberately and separately from the application's runner.</b>
/// The application reads a repository with the production <see cref="Infrastructure.Git.GitCommandRunner"/>,
/// which is what the scenarios are about. If the fixture shared that runner, a bug in the runner would
/// produce an empty repository that the scenario would then "prove" was read correctly. Two independent
/// invocations mean the repository on disk is a real repository created by Git itself, and everything the
/// application does with it is production code.
/// </para>
/// <para>
/// Every scenario gets its own directory under one per-scenario temporary root, removed when the
/// scenario ends. Nothing here is shared between scenarios, so one scenario's commits cannot be another's
/// history.
/// </para>
/// </remarks>
public sealed class TemporaryGitRepository : IDisposable
{
    private readonly string _root;

    internal TemporaryGitRepository(string root, string path)
    {
        _root = root;
        Location = path;
    }

    /// <summary>
    /// The absolute directory of the repository. Never leaves a test and never reaches a route.
    /// </summary>
    /// <remarks>
    /// Named <c>Location</c> rather than <c>Path</c> so this class's own members can still say
    /// <see cref="Path.Combine"/> and mean the framework's, which a property called <c>Path</c> would
    /// shadow and turn every such call into a compile error nobody could read.
    /// </remarks>
    public string Location { get; }

    /// <summary>The alias this repository is registered under, which is the only name a request may use.</summary>
    public string Alias { get; private init; } = "workspace";

    /// <summary>Creates a root directory holding one repository, with a deterministic identity.</summary>
    public static async Task<TemporaryGitRepository> CreateAsync(string purpose)
    {
        // The purpose is a readable prefix and the guid is what makes the directory unique, so a failed
        // scenario can be found on disk afterwards and two parallel runs cannot collide.
        var root = Path.Combine(
            Path.GetTempPath(),
            "chronicleaf-localgit",
            $"{purpose}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(root);

        var repository = new TemporaryGitRepository(root, root);
        await repository.InitAsync();

        return repository;
    }

    /// <summary>
    /// Creates a second directory under the same root, for a symlink that leaves it.
    /// </summary>
    /// <remarks>
    /// The escape target is a real directory that exists, because a link to nothing would be refused for
    /// not existing rather than for pointing outside - which is a different refusal and would not prove
    /// anything about containment.
    /// </remarks>
    public string CreateOutsideDirectory()
    {
        var outside = Path.Combine(_root, "..", $"{System.IO.Path.GetFileName(_root)}-outside-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "passwd"), "root:x:0:0:this must never be read\n");

        return outside;
    }

    /// <summary>Writes a file, creating its directory, without staging or committing anything.</summary>
    public string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Location, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Without a byte-order mark: `Encoding.UTF8` writes one, and a fixture that put a BOM at the head
        // of every file it created would have Git report it as part of the first line of every patch, which
        // is a fact about the fixture rather than about the code under test.
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return full;
    }

    /// <summary>Stages everything and commits it, returning the full revision.</summary>
    public async Task<string> CommitAsync(string message)
    {
        await GitAsync("add", "-A");
        await GitAsync(
            "-c",
            "user.name=Chronicleaf Fixture",
            "-c",
            "user.email=fixture@chronicleaf.invalid",
            "commit",
            "--no-gpg-sign",
            "--allow-empty",
            "-m",
            message);

        return await RevParseAsync("HEAD");
    }

    /// <summary>Stages everything and commits it under a chosen identity, for a scenario about authorship.</summary>
    public async Task<string> CommitAsAsync(string message, string name, string email)
    {
        await GitAsync("add", "-A");
        await GitAsync(
            "-c",
            $"user.name={name}",
            "-c",
            $"user.email={email}",
            "commit",
            "--no-gpg-sign",
            "--allow-empty",
            "-m",
            message);

        return await RevParseAsync("HEAD");
    }

    /// <summary>Writes a light tag, which is the "tagged old commit" the design's fixture names.</summary>
    public async Task TagAsync(string name, string? revision = null)
    {
        await GitAsync("tag", name, revision ?? "HEAD");
    }

    /// <summary>
    /// Moves the branch the working tree has checked out to a revision, so a ref can move under a run.
    /// </summary>
    /// <remarks>
    /// <c>update-ref</c> rather than <c>branch --force</c>, because Git refuses to force-update the branch a
    /// worktree has checked out - which is exactly the branch this needs to move. A fixture that hit that
    /// refusal would throw inside the scenario's trigger and be recorded as an unclassified job failure, so
    /// the verb that works is the one written here.
    /// </remarks>
    public async Task MoveBranchAsync(string branch, string revision)
    {
        await GitAsync("update-ref", $"refs/heads/{branch}", revision);
        await GitAsync("symbolic-ref", "HEAD", $"refs/heads/{branch}");
    }

    /// <summary>Creates a symbolic link inside the repository at a chosen relative path.</summary>
    public string Link(string relativePath, string target)
    {
        var full = Path.Combine(Location, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.CreateSymbolicLink(full, target);

        return full;
    }

    /// <summary>Resolves a ref to its full revision, the same way the application does.</summary>
    public async Task<string> RevParseAsync(string reference) =>
        (await GitAsync("rev-parse", "--verify", reference)).Trim();

    /// <summary>The branch the repository's HEAD names.</summary>
    public async Task<string> CurrentBranchAsync() =>
        (await GitAsync("rev-parse", "--abbrev-ref", "HEAD")).Trim();

    /// <summary>
    /// A fingerprint of every byte under the repository, including <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// <b>The read-only claim is asserted with this and nothing else.</b> A scenario that hashes the tree
    /// before and after a collection has shown that no file changed, no file was added, no file was removed
    /// and no object was written - which is stronger than any count of Git commands, because it does not
    /// depend on this repository's own idea of what a mutating command is. A repository whose working tree
    /// was dirty when the fingerprint was taken is still usable: the hash is of the bytes, and a byte that
    /// changes changes the hash.
    /// </remarks>
    public async Task<string> FingerprintAsync()
    {
        var digest = SHA256.Create();
        var buffer = new byte[64 * 1024];

        foreach (var file in EnumerateFiles())
        {
            // The name is hashed too, so a rename is as visible as an edit.
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(Location, file).Replace('\\', '/'));
            digest.TransformBlock(name, 0, name.Length, null, 0);

            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    digest.TransformBlock(buffer, 0, read, null, 0);
                }
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        digest.TransformFinalBlock([], 0, 0);

        return Convert.ToHexStringLower(digest.Hash!);
    }

    /// <summary>Every file under the repository, deterministically ordered, for a fingerprint or a count.</summary>
    public IReadOnlyList<string> EnumerateFiles() =>
    [
        .. Directory
            .EnumerateFiles(Location, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal),
    ];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A directory this process cannot remove is a cleanup problem and not a scenario's result; the
            // temporary root is under the OS temp directory and is not this repository's responsibility.
        }
    }

    private async Task<string> GitAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(GitPath ?? "git")
        {
            WorkingDirectory = Location,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("commit.gpgsign=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.autocrlf=false");

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        startInfo.Environment["GIT_AUTHOR_DATE"] = "2026-10-08T09:00:00+00:00";
        startInfo.Environment["GIT_COMMITTER_DATE"] = "2026-10-08T09:00:00+00:00";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fixture could not start git.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The fixture's git {string.Join(' ', arguments)} failed with {process.ExitCode}.");
        }

        return stdout;
    }

    internal async Task InitAsync()
    {
        await GitAsync("init", "--quiet", "--initial-branch=main", ".");
        await GitAsync("config", "user.name", "Chronicleaf Fixture");
        await GitAsync("config", "user.email", "fixture@chronicleaf.invalid");
        await GitAsync("config", "commit.gpgsign", "false");
    }

    /// <summary>
    /// A bare temporary directory that holds repositories and plain directories as its children.
    /// </summary>
    /// <remarks>
    /// A separate fixture from <see cref="TemporaryGitRepository"/> for one reason: a registered root that is
    /// <em>itself</em> a work tree answers <c>true</c> to Git's <c>--is-inside-work-tree</c> for every
    /// directory inside it, so a scenario that needs "a directory which is genuinely not a repository" cannot
    /// use one as its root. This one is an ordinary directory that contains both, which is also the shape a
    /// real operator registers when one machine holds several projects.
    /// </remarks>
    public sealed class TemporaryGitDirectory : IDisposable
    {
        private TemporaryGitDirectory(string root)
        {
            Location = root;
        }

        /// <summary>The registered root. Never leaves a test; never reaches a route.</summary>
        public string Location { get; }

        public static async Task<TemporaryGitDirectory> CreateAsync(string purpose)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "chronicleaf-localgit",
                $"{purpose}-root-{Guid.NewGuid():N}");

            Directory.CreateDirectory(root);

            return new TemporaryGitDirectory(root);
        }

        /// <summary>Creates a Git repository in a child directory, with one commit in it.</summary>
        public async Task<TemporaryGitRepository> MakeRepositoryAsync(string name)
        {
            var path = Path.Combine(Location, name);
            Directory.CreateDirectory(path);

            var repository = new TemporaryGitRepository(Location, path);
            await repository.InitAsync();

            repository.WriteFile("README.md", $"# {name}\n");
            await repository.CommitAsync("The first commit in this repository");

            return repository;
        }

        /// <summary>Creates an ordinary directory that Git will not recognise as a repository.</summary>
        public void MakePlainDirectory(string name)
        {
            var path = Path.Combine(Location, name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "notes.txt"), "not a repository\n");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Location))
                {
                    Directory.Delete(Location, recursive: true);
                }
            }
            catch (IOException)
            {
                // A directory this process cannot remove is a cleanup problem and not a scenario's result.
            }
        }
    }

    /// <summary>
    /// Where the fixture found Git, resolved once.
    /// </summary>
    /// <remarks>
    /// The scenarios that need a repository are skipped, loudly, when Git is absent - and the skip message
    /// names the reason. That is a recorded unavailability rather than a pass, which is the only honest
    /// treatment for a dependency the host does not have.
    /// </remarks>
    internal static string? GitPath
    {
        get
        {
            foreach (var candidate in new[] { "git", "/usr/bin/git", "/usr/local/bin/git" })
            {
                if (Path.IsPathRooted(candidate))
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
                    var full = System.IO.Path.Combine(directory, candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
            }

            return null;
        }
    }
}
