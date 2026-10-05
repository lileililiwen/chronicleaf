using System.Globalization;
using System.Net;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Git;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;
using Chronicleaf.Infrastructure.Git;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// LOCAL-GIT-EVIDENCE-001 and LOCAL-GIT-EVIDENCE-002 against a real disposable PostgreSQL, the real API
/// host, the real operation queue, a real <c>git</c> process and real temporary repositories on a real
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing about the read is simulated.</b> Every row comes from a real PostgreSQL server started from a
/// pinned image; the connection and the collection go through the real service over the real stores; the job
/// is run by the real Hangfire server from the real queue; the append lands through the real
/// <c>MilestoneEvidenceService</c> with the database's own triggers and constraints deciding whether it is
/// honest; and the facts themselves are read by the production <see cref="GitCommandRunner"/> from a
/// repository the fixture created with a real <c>git</c> in a real directory. There is no EF in-memory
/// provider, no stubbed store, and - deliberately - no scripted transport.
/// </para>
/// <para>
/// <b>Why that matters more here than for a hosted transport.</b> A hosted collection can be faked at the
/// far end of a socket and still prove everything about Chronicleaf's own behaviour. A local collection has
/// no far end to fake: its whole subject is what this server does with a directory on a disk, and a double
/// substituted at the process boundary would leave the containment check, the argument vector, the caps and
/// the read-only claim untested. So the only seam these scenarios use is a runner that <em>delegates</em> to
/// the production one and can move a repository's head between two commands, and even that is used by
/// exactly one scenario - the boundary race that nothing outside the process could otherwise schedule.
/// </para>
/// <para>
/// <b>What these scenarios are not evidence for.</b> They are not evidence that Git works, that a particular
/// repository is well formed, or that any particular distribution's Git behaves as another does. They are
/// evidence about Chronicleaf: which refs are resolved before which facts, which bytes are never read, which
/// caps hold, that a path which leaves its root is refused before a process starts, that a repository is
/// byte-identical afterwards, and that a Hosted deployment refuses the whole surface. When this host has no
/// <c>git</c> at all, the repository-backed scenarios report prerequisite-unavailable rather than standing
/// in for a pass.
/// </para>
/// <para>
/// The two requirements' success, failure and boundary scenarios are covered one for one, and each is named
/// after the scenario it covers.
/// </para>
/// </remarks>
public sealed class LocalGitEvidenceTests(ChronicleafPostgresFixture postgres)
    : IClassFixture<ChronicleafPostgresFixture>
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 8, 16, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("3f6b9d41-8c5a-4f2b-9d31-6a4c8e5b2f70");

    private const string Alias = "workspace";

    /// <summary>A value that appears in a file nobody meant to publish, and must appear nowhere in a row.</summary>
    private const string SecretSentinel = "local-git-secret-sentinel-9c4a17be";

    /// <summary>A value that only exists in a dirty working tree, and is in no commit at all.</summary>
    private const string UncommittedSentinel = "local-git-uncommitted-sentinel-5e2f80a1";

    /// <summary>
    /// The alias an operator registered, bound to this scenario's real temporary directory.
    /// </summary>
    private static LocalRepositoryRoot RootFor(TemporaryGitRepository repository) => new(Alias, repository.Location);

    /// <summary>The same alias, bound to a root that is a bare directory rather than a repository.</summary>
    private static LocalRepositoryRoot RootFor(TemporaryGitRepository.TemporaryGitDirectory root) =>
        new(Alias, root.Location);

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-001 Success: an allowed repository has two selected commits, and the bundle
    // identifies their SHAs and their bounded tracked differences.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_001_Success_two_selected_commits_are_named_by_revision_with_bounded_tracked_differences()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_001_success");
        var oldSha = await SeedOldRevisionAsync(workspace);
        var headSha = await SeedHeadRevisionAsync(workspace);

        var scenario = await ScenarioAsync("lge_001_success", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "两次提交都可复现");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        Assert.True(accepted.Status == HttpStatusCode.Accepted, $"{accepted.Body}\n{host.Logs.Text}");

        await host.DispatchAsync();
        var operation = await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        Assert.Null(operation.ErrorCode);
        Assert.Equal(1, operation.Attempt);
        Assert.Equal($"{LocalGitEvidenceService.SyncOperationKind}:2", operation.ResultRef);

        // The bundle the owner reads is the one the server built, and it names both revisions it resolved.
        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, head.Revision);

        var repository = head.Items.Single(item => item.Kind == EvidenceKinds.ProjectMetadata);
        Assert.Equal($"local-git:{Alias}/.", repository.SourceRef);
        Assert.Equal(headSha, repository.SourceRevision);
        Assert.Contains("Base revision: " + oldSha, repository.Text, StringComparison.Ordinal);
        Assert.Contains("Head revision: " + headSha, repository.Text, StringComparison.Ordinal);
        Assert.Contains($"Registered root: {Alias}", repository.Text, StringComparison.Ordinal);
        Assert.Contains("Git commands consulted:", repository.Text, StringComparison.Ordinal);
        Assert.Contains("rev-parse head -> 0", repository.Text, StringComparison.Ordinal);
        Assert.Contains("rev-parse head-recheck -> 0", repository.Text, StringComparison.Ordinal);
        Assert.Contains("log commits -> 0", repository.Text, StringComparison.Ordinal);
        Assert.Contains("diff files -> 0", repository.Text, StringComparison.Ordinal);
        Assert.Contains("status uncommitted -> 0", repository.Text, StringComparison.Ordinal);
        Assert.Equal(EvidenceAvailability.Complete, repository.Availability);

        // The two selected commits are the two the run resolved, and the bundle identifies both: the head
        // as a commit row, and the base in the metadata item that carries the comparison's two revisions.
        // A range log holds the commits *after* the base, so a claim of two commit rows here would be a
        // claim about a different collection.
        var commits = head.Items.Where(item => item.Kind == EvidenceKinds.Commit).ToArray();
        Assert.Single(commits);
        Assert.Equal(EvidenceAvailability.Complete, commits[0].Availability);
        Assert.Equal(headSha, commits[0].SourceRevision);
        Assert.Contains("Ship the reproducible pipeline", commits[0].Text, StringComparison.Ordinal);
        Assert.Equal(40, commits[0].SourceRevision.Length);
        Assert.DoesNotContain("main", commits[0].SourceRevision, StringComparison.Ordinal);
        Assert.DoesNotContain(oldSha, commits[0].SourceRevision, StringComparison.Ordinal);

        // The bounded tracked differences: the two files the head revision added or changed, each with a
        // patch, and every one of them naming the head revision.
        var diffs = head.Items.Where(item => item.Kind == EvidenceKinds.Diff).ToArray();
        Assert.Equal(2, diffs.Length);
        Assert.All(diffs, diff => Assert.Equal(EvidenceAvailability.Complete, diff.Availability));
        Assert.All(diffs, diff => Assert.Equal(headSha, diff.SourceRevision));
        Assert.Contains(diffs, diff => diff.Text.Contains("src/app.ts", StringComparison.Ordinal));
        Assert.Contains(diffs, diff => diff.Text.Contains("docs/notes.md", StringComparison.Ordinal));
        Assert.Contains(diffs, diff => diff.Text.Contains("+console.log('reproducible');", StringComparison.Ordinal));
        Assert.All(diffs, diff => Assert.Contains("Status: added.", diff.Text, StringComparison.Ordinal));

        // The run log is a real row, written by the job, and it says what the job read.
        var run = await ReadRunAsync(database, accepted.OperationId);
        Assert.NotNull(run);
        Assert.Equal(Alias, run!.RootAlias);
        Assert.Equal(".", run.RelativePath);
        Assert.Equal(oldSha, run.BaseSha);
        Assert.Equal(headSha, run.HeadSha);
        // A range log holds the commits *after* the base, so the run recorded one commit row and named the
        // base in the metadata item instead. Two commit rows here would be a claim about a different
        // collection, which is the kind of false green a range log invites.
        Assert.Equal(1, run.CommitCount);
        Assert.Equal(2, run.ChangedFileCount);
        Assert.False(run.Truncated);
        Assert.Equal(0, run.ExcludedFileCount);
        Assert.Equal(0, run.UncommittedCount);
        Assert.NotNull(run.FetchedAt);
        Assert.Contains("rev-parse", run.CommandLog!, StringComparison.Ordinal);
        Assert.Contains("log", run.CommandLog!, StringComparison.Ordinal);
        Assert.Contains("diff", run.CommandLog!, StringComparison.Ordinal);
        Assert.Contains("status", run.CommandLog!, StringComparison.Ordinal);

        // And the run row is verbs only. The registered directory never becomes a stored value, and this
        // asserts it of the string itself rather than of the class that wrote it.
        Assert.DoesNotContain(workspace.Location, run.CommandLog!, StringComparison.Ordinal);
        Assert.DoesNotContain("/", run.CommandLog!, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Location, await ReadColumn(database, "local_repositories", "root_alias"), StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Location, await ReadColumn(database, "local_sync_sources", "relative_path"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_001_Success_the_refs_are_resolved_to_revisions_before_any_fact_is_read()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_001_order");
        var oldSha = await SeedOldRevisionAsync(workspace);
        var headSha = await SeedHeadRevisionAsync(workspace);

        // The order is the claim, so it is observed rather than assumed: a runner that records what ran,
        // delegating every command to the production one.
        var recorder = new RecordingGitRunner(new GitCommandRunner());
        var scenario = await ScenarioAsync("lge_001_order", [RootFor(workspace)], runner: recorder);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "先解析引用再读事实");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        // The registration's own two reads are Git's too, and they are not part of the collection's order.
        recorder.Clear();

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(scenario.Database, accepted.OperationId, "succeeded");

        var verbs = recorder.Subcommands.ToArray();

        // The first two reads resolve the two refs. A collection that asked Git for a comparison between two
        // ref names would be reading whatever the branch meant at that moment, which is the whole thing the
        // design's "resolve to a SHA before any fact is read" exists to prevent.
        Assert.Equal("rev-parse", verbs[0]);
        Assert.Contains("main^{commit}", string.Join(' ', recorder.Arguments[0]), StringComparison.Ordinal);
        Assert.Equal("rev-parse", verbs[1]);
        Assert.Contains("v0.9.0^{commit}", string.Join(' ', recorder.Arguments[1]), StringComparison.Ordinal);

        // Only after both are resolved does anything read a fact, and every later command is given SHAs.
        Assert.Equal("status", verbs[2]);
        Assert.Equal("log", verbs[3]);
        Assert.Contains($"{oldSha}..{headSha}", string.Join(' ', recorder.Arguments[3]), StringComparison.Ordinal);
        var facts = recorder.Arguments.ToArray();
        for (var index = 2; index < facts.Length - 1; index++)
        {
            Assert.DoesNotContain("^{commit}", string.Join(' ', facts[index]), StringComparison.Ordinal);
        }

        // The last two rev-parse reads are the final head check, so the run ends by asking the repository
        // again rather than by trusting the first answer.
        Assert.Equal("rev-parse", verbs[^1]);
        Assert.Contains("main^{commit}", string.Join(' ', recorder.Arguments[^1]), StringComparison.Ordinal);
        Assert.True(recorder.Subcommands.Count(v => v == "rev-parse") >= 3);

        // Every command ran with the closed base arguments, so the pager, the credential helper, the external
        // diff driver and the repository's hooks were all off for all of them.
        Assert.All(
            recorder.ArgumentVectors,
            vector =>
            {
                Assert.Contains("--no-pager", vector);
                Assert.Contains("core.hooksPath=/dev/null", vector);
                Assert.Contains("diff.external=", vector);
                Assert.Contains("credential.helper=", vector);
            });

        // And the whole vocabulary is four words. A read-only claim that rested on the caller choosing well
        // would not be a claim about the product.
        Assert.All(recorder.Subcommands, verb => Assert.Contains(verb, new[] { "rev-parse", "log", "diff", "status" }));
    }

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-001 Failure: an untracked or dirty secret file exists, and it is not included.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_001_Failure_an_untracked_or_dirty_secret_file_reaches_no_evidence_item()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_001_secret");
        var oldSha = await SeedOldRevisionAsync(workspace);

        // A commit whose only addition is a file whose name says it holds a secret. The content is here
        // because the exclusion is by name, and the name has to be a real one for the exclusion to fire.
        workspace.WriteFile("deploy/.env", $"API_TOKEN={SecretSentinel}\n");
        var headSha = await workspace.CommitAsync("Commit a deployed token by mistake");

        // Three more places a secret could leak from, none of them committed.
        workspace.WriteFile("untracked.env", $"API_TOKEN={SecretSentinel}\n");
        workspace.WriteFile("notes.md", $"a note that must not be collected: {UncommittedSentinel}\n");
        workspace.WriteFile("src/app.ts", $"console.log('dirty edit {UncommittedSentinel}');\n");

        var scenario = await ScenarioAsync("lge_001_secret", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "脏工作树里的密钥");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        var collected = head.Items.Where(item => item.SourceRef.StartsWith("local-git:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(collected);

        // The committed secret file is named and its bytes are never read. An unavailable item that says why
        // is the honest record; an item holding the token would move a credential into an exported table.
        var secretDiff = collected.Single(item =>
            item.Kind == EvidenceKinds.Diff && item.Text.Contains("deploy/.env", StringComparison.Ordinal));
        Assert.Equal(EvidenceAvailability.Unavailable, secretDiff.Availability);
        Assert.Contains(SecretFilenames.Reason, secretDiff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("API_TOKEN", secretDiff.Text, StringComparison.Ordinal);

        // The untracked file and the two dirty edits are not candidates at all, because every fact is read
        // from two resolved SHAs rather than from the directory.
        Assert.DoesNotContain(
            collected,
            item => item.Text.Contains("untracked.env", StringComparison.Ordinal));
        Assert.All(collected, item => Assert.DoesNotContain(UncommittedSentinel, item.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(
            collected,
            item => item.Text.Contains("dirty edit", StringComparison.Ordinal));

        // The sentinel is absent from the raw columns, read unaligned over the whole table rather than from
        // one column, so a value that happened to straddle a boundary would still be found.
        Assert.Equal(0, await CountTextAsync(database, "evidence_items", SecretSentinel));
        Assert.Equal(0, await CountTextAsync(database, "evidence_items", UncommittedSentinel));
        Assert.Equal(0, await CountTextAsync(database, "local_sync_sources", SecretSentinel));
        Assert.Equal(0, await CountTextAsync(database, "local_sync_sources", UncommittedSentinel));
        Assert.Equal(0, await CountTextAsync(database, "audit_events", SecretSentinel));
        Assert.Equal(0, await CountTextAsync(database, "audit_events", UncommittedSentinel));

        // The working tree's dirtiness is reported as a count and as nothing else. The bundle must not claim
        // to describe the directory, and it must not name the files either.
        var repository = collected.Single(item => item.Kind == EvidenceKinds.ProjectMetadata);
        Assert.Contains("Uncommitted changes in the working tree: 3", repository.Text, StringComparison.Ordinal);
        Assert.Contains("deliberately not read", repository.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("untracked.env", repository.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("app.ts", repository.Text, StringComparison.Ordinal);

        var run = await ReadRunAsync(database, accepted.OperationId);
        Assert.NotNull(run);
        Assert.Equal(1, run!.ExcludedFileCount);
        Assert.Equal(3, run.UncommittedCount);

        // The commits themselves are still collected: the head commit is a real fact even though what it
        // added is not.
        Assert.Contains(collected, item => item.Kind == EvidenceKinds.Commit && item.SourceRevision == headSha);
        Assert.DoesNotContain(
            collected,
            item => item.Kind == EvidenceKinds.Commit && item.SourceRevision == oldSha);
    }

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-001 Boundary: the repository head changes during collection, and the operation
    // reports superseded rather than mixed revisions.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_001_Boundary_a_head_that_moves_during_the_read_supersedes_the_operation_and_appends_nothing()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_001_moved");
        await SeedOldRevisionAsync(workspace);
        var firstHead = await SeedHeadRevisionAsync(workspace);

        // A second commit that exists before the run and is not the head, so the scenario can move the
        // branch onto it while the collection is reading. The branch is put back afterwards, because a head
        // that is already where the move would put it makes the trigger a no-op and the scenario would pass
        // without a race having happened at all.
        workspace.WriteFile("src/late.ts", "export const late = true;\n");
        var laterHead = await workspace.CommitAsync("A commit made while the collection was reading");
        await workspace.MoveBranchAsync("main", firstHead);
        Assert.Equal(firstHead, await workspace.RevParseAsync("main"));

        // The one seam these scenarios use. It delegates every command to the production runner and moves
        // the branch after the first diff, which is the window a bundle could otherwise be built across.
        var mover = new HeadMovingGitRunner(new GitCommandRunner())
        {
            TriggerOnSubcommand = "diff",
            TriggerAfterCalls = 0,
            OnTrigger = () => workspace.MoveBranchAsync("main", laterHead),
        };

        var scenario = await ScenarioAsync("lge_001_moved", [RootFor(workspace)], runner: mover);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "读取期间仓库前进");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        await host.DispatchAsync();

        var operation = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            LocalGitJobError.Superseded,
            TimeSpan.FromSeconds(90));

        Assert.Equal("failed", operation.Status);

        // Nothing was appended, and the stored bundle is exactly the owner's own note at revision one. A
        // bundle holding facts from two revisions is a claim no later reader could check.
        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(1, head.Revision);
        Assert.DoesNotContain(
            head.Items,
            item => item.SourceRef.StartsWith("local-git:", StringComparison.Ordinal));

        // The run row was asked for and never completed, which is a true statement about it: the resolved
        // columns are null and the completion check constraint in the database refuses any other reading.
        var run = await ReadRunAsync(database, accepted.OperationId);
        Assert.NotNull(run);
        Assert.Null(run!.HeadSha);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.CommandLog);

        // The repository really did move, so the scenario proved the race rather than a no-op.
        Assert.NotEqual(firstHead, await workspace.RevParseAsync("main"));
    }

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-002 Success: an owner selects a configured root alias, and read-only collection
    // works within that root.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_002_Success_a_configured_root_is_collected_from_and_the_repository_is_unchanged()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_002_success");
        var oldSha = await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        // A dirty working tree, so "unchanged" is a claim about bytes rather than about a clean tree.
        workspace.WriteFile("src/app.ts", "console.log('a local edit that must survive the collection');\n");

        var before = await workspace.FingerprintAsync();

        var scenario = await ScenarioAsync("lge_002_success", [RootFor(workspace)]);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "根目录内的只读收集");
        var registered = await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        Assert.Equal(Alias, registered.RootAlias);
        Assert.Equal(".", registered.RelativePath);
        Assert.Equal("main", registered.DefaultBranch);
        Assert.Equal(40, registered.HeadSha!.Length);
        Assert.DoesNotContain(workspace.Location, registered.Raw, StringComparison.Ordinal);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(scenario.Database, accepted.OperationId, "succeeded");

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, head.Revision);
        Assert.Contains(
            head.Items,
            item => item.SourceRef == $"local-git:{Alias}/."
                && item.Kind == EvidenceKinds.Diff
                && item.Text.Contains("src/app.ts", StringComparison.Ordinal));
        Assert.Contains(
            head.Items,
            item => item.Kind == EvidenceKinds.ProjectMetadata
                && item.Text.Contains("Base revision: " + oldSha, StringComparison.Ordinal));

        // The read-only claim, asserted over every byte under the repository including its .git directory.
        // A count of Git commands would only prove this repository's own idea of what a mutating command is.
        Assert.Equal(before, await workspace.FingerprintAsync());

        // And the branch really is still the one the fixture made, which is what "the repository is
        // unchanged" means to a reader who will go and look.
        Assert.Equal("main", await workspace.CurrentBranchAsync());
        Assert.Equal(
            (await workspace.RevParseAsync("main")).Trim(),
            (await ReadColumn(scenario.Database, "local_repositories", "head_sha")).Trim());
    }

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-002 Failure: a relative path escapes through traversal or a symlink, and
    // collection is rejected before Git reads it.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_002_Failure_a_path_that_leaves_its_root_through_a_symlink_is_refused_before_git_reads_it()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_002_symlink");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        var outside = workspace.CreateOutsideDirectory();
        workspace.Link("escape", outside);

        // A runner that would notice if anything reached Git, so "before Git reads it" is an observation and
        // not an inference from the status code.
        var recorder = new RecordingGitRunner(new GitCommandRunner());
        var scenario = await ScenarioAsync("lge_002_symlink", [RootFor(workspace)], runner: recorder);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "符号链接逃逸");

        // A path whose first segment is a symlink to a directory outside the root. The link's final segment
        // is a real directory, so a check that resolved only the last component would call it contained.
        var refused = await TryRegisterAsync(owner, fixture.ProjectId, Alias, "escape/passwd", host);
        Assert.True(
            refused.Status == HttpStatusCode.Forbidden,
            $"{refused.Status}: {refused.Body}\n{host.Logs.Text}");
        Assert.Contains(LocalGitError.OutsideRoot, refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(outside, refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(workspace.Location, refused.Body, StringComparison.Ordinal);
        Assert.Empty(recorder.Subcommands);

        // A path that is simply not there is a different answer from one that escapes, and a caller told it
        // had tried to leave the root would go looking for a traversal that never happened.
        var absent = await TryRegisterAsync(owner, fixture.ProjectId, Alias, "not-here", host);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, absent.Status);
        Assert.Contains(LocalGitError.NotARepository, absent.Body, StringComparison.Ordinal);

        // Traversal is refused by the shape check, before the filesystem is asked anything at all.
        foreach (var traversal in new[] { "../escape/passwd", "a/../../escape", "..", "./..", "a/b/../../.." })
        {
            var refusedPath = await TryRegisterAsync(owner, fixture.ProjectId, Alias, traversal, host);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedPath.Status);
            Assert.Contains("relativePath", refusedPath.Body, StringComparison.Ordinal);
        }

        // Nothing was stored, no job was created, and Git was never started for any of it.
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_repositories"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_sync_sources"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM operations WHERE kind = @k", command =>
            command.Parameters.AddWithValue("k", LocalGitEvidenceService.SyncOperationKind)));
        Assert.Empty(recorder.Subcommands);

        // The server's own log names neither the offending path nor the directory it pointed at.
        Assert.DoesNotContain(workspace.Location, host.Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(outside, host.Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Evidence_002_Failure_a_registered_path_that_becomes_a_link_out_of_the_root_is_refused_when_the_job_runs()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_002_toctou");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        // A real directory inside the root, registered while it is genuinely inside it.
        var inside = Path.Combine(workspace.Location, "app");
        Directory.CreateDirectory(inside);
        await TemporaryGitRepository.CreateAsync("lge_002_toctou_repo");

        var scenario = await ScenarioAsync("lge_002_toctou", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "注册之后才变成链接");
        var registered = await RegisterAsync(owner, fixture.ProjectId, Alias, "app", host);
        Assert.Equal("app", registered.RelativePath);

        // Now the directory is replaced by a link to somewhere outside. The stored row is unchanged and still
        // says `app`, which is exactly the case a check made only at registration time would miss.
        var outside = workspace.CreateOutsideDirectory();
        Directory.Delete(inside, recursive: true);
        Directory.CreateSymbolicLink(inside, outside);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        Assert.Equal(HttpStatusCode.Accepted, accepted.Status);
        await host.DispatchAsync();

        var operation = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            LocalGitJobError.OutsideRoot,
            TimeSpan.FromSeconds(90));

        Assert.Equal("failed", operation.Status);
        Assert.Null(operation.ResultRef);

        // Nothing was appended and nothing outside the root was read.
        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(1, head.Revision);
        Assert.DoesNotContain(
            head.Items,
            item => item.SourceRef.StartsWith("local-git:", StringComparison.Ordinal));
        Assert.Equal(0, await CountTextAsync(database, "evidence_items", "this must never be read"));
    }

    // ===========================================================================================
    // LOCAL-GIT-EVIDENCE-002 Boundary: Hosted requests local Git access, and the API refuses local
    // filesystem discovery.
    // ===========================================================================================

    [Fact]
    public async Task Evidence_002_Boundary_a_hosted_deployment_refuses_local_filesystem_discovery_and_reads_nothing()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_002_hosted");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        var recorder = new RecordingGitRunner(new GitCommandRunner());
        var scenario = await ScenarioAsync(
            "lge_002_hosted",
            [RootFor(workspace)],
            mode: LocalGitDeploymentMode.Hosted,
            runner: recorder);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "托管部署不读本地目录");

        // The catalogue route answers, because "this deployment does not read local directories" is exactly
        // what a panel needs in order to say so instead of offering a form that will be refused. It reports
        // the mode and no aliases, so a tenant learns nothing about the machine it shares.
        using var catalogue = await owner.GetAsync("/api/local-repository-roots");
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        using var catalogueBody = JsonDocument.Parse(await catalogue.Content.ReadAsStringAsync());
        Assert.Equal("hosted", catalogueBody.RootElement.GetProperty("deploymentMode").GetString());
        Assert.False(catalogueBody.RootElement.GetProperty("available").GetBoolean());
        Assert.Empty(catalogueBody.RootElement.GetProperty("rootAliases").EnumerateArray());
        Assert.DoesNotContain(workspace.Location, await catalogue.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Every route that could name a directory refuses it, and the refusal is this package's own code.
        var registerRefusal = await TryRegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        using var readResponse = await owner.GetAsync($"/api/projects/{fixture.ProjectId:D}/local-repository");
        var readRefusal = new Refusal(
            readResponse.StatusCode,
            await readResponse.Content.ReadAsStringAsync());

        foreach (var attempt in new[] { registerRefusal, readRefusal })
        {
            Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
            Assert.Contains(LocalGitError.NotAvailableInHosted, attempt.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Location, attempt.Body, StringComparison.Ordinal);
        }

        var sync = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        Assert.Equal(HttpStatusCode.Forbidden, sync.Status);
        Assert.Contains(LocalGitError.NotAvailableInHosted, sync.Body, StringComparison.Ordinal);

        // Nothing was written, no job exists, and no process was started: the refusal is made before the
        // alias is even resolved.
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_repositories"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_sync_sources"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM operations WHERE kind = @k", command =>
            command.Parameters.AddWithValue("k", LocalGitEvidenceService.SyncOperationKind)));
        Assert.Empty(recorder.Subcommands);
    }

    // ===========================================================================================
    // Transport and structural boundaries. These are the claims the two requirements rest on, asserted
    // without a repository where a repository would only be decoration.
    // ===========================================================================================

    [Fact]
    public void The_runner_refuses_every_subcommand_that_is_not_one_of_its_four_reads()
    {
        // A read-only claim about somebody's repository is only credible if the set of commands that can be
        // run against it is closed. This is the check, and it runs before any process exists.
        var runner = new GitCommandRunner();
        var directory = Directory.GetCurrentDirectory();

        foreach (var forbidden in new[]
        {
            "commit", "add", "checkout", "switch", "reset", "clean", "stash", "fetch", "pull", "push",
            "clone", "remote", "merge", "rebase", "tag", "branch", "gc", "update-ref", "config",
        })
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                runner.RunAsync(
                    new GitCommandRequest(directory, forbidden, []),
                    TimeSpan.FromSeconds(1),
                    1024).GetAwaiter().GetResult());

            Assert.Contains(forbidden, thrown.Message, StringComparison.Ordinal);
        }

        // And the four that are allowed are exactly the four the design names.
        var allowed = typeof(GitCommandRunner)
            .GetField("ReadOnlySubcommands", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) as string[];

        Assert.NotNull(allowed);
        Assert.Equal(["rev-parse", "log", "diff", "status"], allowed);
    }

    [Fact]
    public void The_process_this_package_starts_is_built_from_an_argument_vector_and_a_sanitised_environment()
    {
        // Read rather than inferred from a call that happened, so what production would start is the thing
        // under test rather than a side effect of it.
        var startInfo = GitCommandRunner.BuildStartInfo(
            new GitCommandRequest("/srv/workspace/app", "diff", ["--name-status", "-z", "a b/c.ts"]));

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.RedirectStandardInput);
        // No `Arguments` string is ever composed, which is what makes "no shell interpolation" a property
        // of this file rather than a habit: there is nothing for a repository or a ref to be pasted into.
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
        Assert.Equal("/srv/workspace/app", startInfo.WorkingDirectory);

        var vector = startInfo.ArgumentList.ToArray();
        Assert.Equal("--no-pager", vector[0]);

        // The subcommand sits after Git's own base arguments and before anything the caller supplied, which
        // is the order that makes a caller's argument unable to precede it. Asserted as an ordering rather
        // than as an index, because an index is a value somebody has to remember to update.
        Assert.True(Array.IndexOf(vector, "diff") < Array.IndexOf(vector, "--name-status"));
        Assert.Contains("core.hooksPath=/dev/null", vector);
        Assert.Contains("diff.external=", vector);
        Assert.Contains("credential.helper=", vector);
        Assert.Contains("GIT_TERMINAL_PROMPT", startInfo.Environment.Keys);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);

        // A repository's own environment cannot turn a read into something else: no pager to wait on, no
        // editor to open, and no credential helper to ask.
        Assert.Equal("cat", startInfo.Environment["GIT_PAGER"]);
        Assert.Equal("true", startInfo.Environment["GIT_EDITOR"]);
    }

    [Fact]
    public async Task A_ref_that_reads_like_a_git_option_is_refused_by_name_and_starts_no_process()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_option_ref");
        await SeedOldRevisionAsync(workspace);

        var recorder = new RecordingGitRunner(new GitCommandRunner());
        var scenario = await ScenarioAsync("lge_option_ref", [RootFor(workspace)], runner: recorder);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "看起来像选项的引用");

        foreach (var hostile in new[]
        {
            "--upload-pack=touch /tmp/chronicleaf-owned",
            "-x",
            "--exec=id",
        })
        {
            var refused = await TrySyncAsync(owner, fixture.MilestoneId, hostile, null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
            Assert.Contains("headRef", refused.Body, StringComparison.Ordinal);
        }

        // No job, no row, no process: the request was refused before a collection could exist.
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_sync_sources"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM operations WHERE kind = @k", command =>
            command.Parameters.AddWithValue("k", LocalGitEvidenceService.SyncOperationKind)));
        Assert.Empty(recorder.Subcommands);
    }

    [Fact]
    public async Task An_alias_nobody_registered_is_refused_and_the_registered_paths_are_never_published()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_alias");
        await SeedOldRevisionAsync(workspace);

        var scenario = await ScenarioAsync("lge_alias", [RootFor(workspace)]);
        await using var host = scenario.Host;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "未注册的根别名");

        // The catalogue publishes the operator's own names and nothing else: no directory, no absolute path,
        // and nothing that could be turned back into one.
        using var catalogue = await owner.GetAsync("/api/local-repository-roots");
        using var body = JsonDocument.Parse(await catalogue.Content.ReadAsStringAsync());
        var aliases = body.RootElement.GetProperty("rootAliases")
            .EnumerateArray()
            .Select(alias => alias.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["workspace"], aliases);
        Assert.Equal("community", body.RootElement.GetProperty("deploymentMode").GetString());
        Assert.True(body.RootElement.GetProperty("available").GetBoolean());
        Assert.DoesNotContain(workspace.Location, await catalogue.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var refused = await TryRegisterAsync(owner, fixture.ProjectId, "elsewhere", ".", host);
        Assert.Equal(HttpStatusCode.NotFound, refused.Status);
        Assert.Contains(LocalGitError.RootUnknown, refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere", refused.Body, StringComparison.Ordinal);

        // A path that names a directory is not a repository, and that is its own answer rather than a
        // validation failure the owner would go and fix by changing the path.
        var notARepository = await TryRegisterAsync(owner, fixture.ProjectId, Alias, "not-a-repo", host);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notARepository.Status);
        Assert.Contains(LocalGitError.NotARepository, notARepository.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_git_repository_is_refused_with_nothing_written()
    {
        // The root holds the repository in a subdirectory, so a sibling directory is genuinely not a
        // repository. A directory *inside* a work tree is a repository as far as Git is concerned - it would
        // answer `true` to `--is-inside-work-tree` - and a fixture that put it there would be testing nothing.
        using var root = await TemporaryGitRepository.TemporaryGitDirectory.CreateAsync("lge_not_repo");
        await root.MakeRepositoryAsync("repo");
        root.MakePlainDirectory("plain");

        var scenario = await ScenarioAsync("lge_not_repo", [RootFor(root)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "不是仓库的目录");
        var refused = await TryRegisterAsync(owner, fixture.ProjectId, Alias, "plain", host);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Contains(LocalGitError.NotARepository, refused.Body, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM local_repositories"));

        // The repository beside it registers, so the refusal is about the directory and not about the root.
        var accepted = await RegisterAsync(owner, fixture.ProjectId, Alias, "repo", host);
        Assert.Equal("repo", accepted.RelativePath);
    }

    [Fact]
    public async Task A_repeated_collection_request_under_one_key_returns_the_same_job_and_collects_once()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_idempotent");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        var scenario = await ScenarioAsync("lge_idempotent", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "同一个幂等键只收集一次");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var first = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0", key: "once");
        var second = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0", key: "once");

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.Accepted, second.Status);
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.OperationId, second.OperationId);

        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, first.OperationId, "succeeded");

        Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM local_sync_sources WHERE operation_id = @id", command =>
            command.Parameters.AddWithValue("id", first.OperationId)));
        Assert.Equal(2, (await ReadEvidenceAsync(owner, fixture.MilestoneId)).Revision);
    }

    [Fact]
    public async Task A_bundle_that_moved_during_the_import_appends_nothing_and_says_it_was_superseded()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_bundle_moved");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        var scenario = await ScenarioAsync("lge_bundle_moved", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "导入期间证据已移动");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        // The owner asks against revision one, and somebody else appends revision two before the job runs.
        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        Assert.Equal(HttpStatusCode.Accepted, accepted.Status);

        using var manual = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{fixture.MilestoneId:D}/evidence/manual",
            new { notes = "Somebody else got here first.", expectedRevision = 1 },
            1));
        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);

        await host.DispatchAsync();
        var operation = await AwaitErrorAsync(
            database,
            accepted.OperationId,
            LocalGitJobError.BundleSuperseded,
            TimeSpan.FromSeconds(90));

        Assert.Equal("failed", operation.Status);
        Assert.Null(operation.ResultRef);

        // The stored bundle is exactly what the other append wrote, and the collection added nothing to it.
        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        Assert.Equal(2, head.Revision);
        Assert.DoesNotContain(
            head.Items,
            item => item.SourceRef.StartsWith("local-git:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ref_the_owner_named_that_does_not_resolve_is_a_named_missing_part_rather_than_a_dropped_one()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_no_ref");
        await SeedOldRevisionAsync(workspace);
        await SeedHeadRevisionAsync(workspace);

        var scenario = await ScenarioAsync("lge_no_ref", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "不存在的比较基点");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.0.0-missing");
        Assert.Equal(HttpStatusCode.Accepted, accepted.Status);
        await host.DispatchAsync();

        // The head still resolved and the run still succeeded: a base that does not exist is a named part of
        // the request that could not be read, and the delivered evidence policy refuses an unavailable item
        // with no reason, so the reason is what this run records.
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        var missing = head.Items.Single(item =>
            item.Kind == EvidenceKinds.ProjectMetadata && item.Availability == EvidenceAvailability.Unavailable);
        Assert.Contains("base:v0.0.0-missing", missing.Text, StringComparison.Ordinal);
        Assert.Contains(GitUnavailableReasons.NotFound, missing.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            head.Items,
            item => item.Kind == EvidenceKinds.Diff);
    }

    [Fact]
    public async Task A_diff_past_the_documented_budget_is_marked_truncated_and_stays_inside_the_cap()
    {
        using var workspace = await TemporaryGitRepository.CreateAsync("lge_truncation");
        await SeedOldRevisionAsync(workspace);

        // Five files whose names say they hold a secret, at five different paths so each one is a real
        // exclusion rather than the same file written five times.
        foreach (var secret in new[]
        {
            ".env",
            "config/.netrc",
            "deploy/id_rsa",
            ".npmrc",
            "certs/server.pem",
        })
        {
            workspace.WriteFile(secret, $"A secret that must never be read: {SecretSentinel}\n");
        }

        // More changed files than the cap names, and a patch text that passes the evidence budget in total.
        // The sizes are chosen so the whole diff still fits under the process output cap: a read the pipe
        // truncated is a dependency failure rather than a truncated bundle, and a scenario asserting the
        // truncation record has to produce a complete diff in order to get one.
        var files = LocalGitLimits.MaxChangedFiles + 20;
        for (var index = 0; index < files; index++)
        {
            workspace.WriteFile($"src/bulk-{index}.ts", new string('x', 1500) + $"\n// {index}\n");
        }

        await workspace.CommitAsync("A large change past every cap at once");
        var headSha = await workspace.RevParseAsync("HEAD");

        var scenario = await ScenarioAsync("lge_truncation", [RootFor(workspace)]);
        await using var host = scenario.Host;
        var database = scenario.Database;
        using var owner = await OwnerClient.CreateAsync(host);

        var fixture = await CreateMilestoneAsync(owner, "超过上限的差异");
        await RegisterAsync(owner, fixture.ProjectId, Alias, ".", host);

        var accepted = await RequestSyncAsync(owner, fixture.MilestoneId, headRef: "main", baseRef: "v0.9.0");
        await host.DispatchAsync();
        await OperationRows.AwaitAsync(database, accepted.OperationId, "succeeded");

        var head = await ReadEvidenceAsync(owner, fixture.MilestoneId);
        var collected = head.Items.Where(item => item.SourceRef.StartsWith("local-git:", StringComparison.Ordinal)).ToArray();

        // The file cap holds, and the fact that it held is in the bundle rather than in a log line.
        var diffs = collected.Where(item => item.Kind == EvidenceKinds.Diff).ToArray();
        var whole = diffs
            .Where(item => !string.Equals(item.Availability, EvidenceAvailability.Partial, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            whole.Length <= LocalGitLimits.MaxChangedFiles,
            $"{whole.Length} recorded files, cap {LocalGitLimits.MaxChangedFiles}");
        Assert.Contains(
            collected,
            item => item.Availability == EvidenceAvailability.Partial
                && item.Text.Contains(GitSegments.Files, StringComparison.Ordinal)
                && item.Text.Contains(
                    $"Limit: {LocalGitLimits.MaxChangedFiles.ToString(CultureInfo.InvariantCulture)}.",
                    StringComparison.Ordinal));

        // The excluded files are named and never read, and the secret they hold is nowhere in the table.
        Assert.Contains(collected, item => item.Text.Contains(".env", StringComparison.Ordinal));
        Assert.Contains(collected, item => item.Text.Contains("config/.netrc", StringComparison.Ordinal));
        Assert.Contains(collected, item => item.Text.Contains("certs/server.pem", StringComparison.Ordinal));
        Assert.Equal(0, await CountTextAsync(database, "evidence_items", SecretSentinel));
        Assert.Equal(0, await CountTextAsync(database, "local_sync_sources", SecretSentinel));

        // Every excluded file is recorded as unavailable with the reason, and not one of them carries a
        // patch - which is the difference between "we chose not to read this" and "we read it and found
        // nothing", and the only one of the two that is true.
        foreach (var secret in new[] { ".env", "config/.netrc", "deploy/id_rsa", ".npmrc", "certs/server.pem" })
        {
            var excluded = collected.Single(item =>
                item.Kind == EvidenceKinds.Diff && item.Text.Contains($"File: {secret}", StringComparison.Ordinal));

            Assert.Equal(EvidenceAvailability.Unavailable, excluded.Availability);
            Assert.Contains(SecretFilenames.Reason, excluded.Text, StringComparison.Ordinal);
        }

        // The run row says so too, and it stays a true summary rather than being the only record.
        var run = await ReadRunAsync(database, accepted.OperationId);
        Assert.NotNull(run);
        Assert.True(run!.Truncated);
        Assert.True(run.ChangedFileCount <= LocalGitLimits.MaxChangedFiles);
        Assert.Equal(5, run.ExcludedFileCount);
        Assert.Equal(headSha, run.HeadSha);
    }

    [Fact]
    public async Task The_local_package_did_not_shadow_the_delivered_github_transport()
    {
        // The failure this guards against has happened in this repository before, in a different form: a
        // registration added to a composition root shadowed a real one, and every scenario that depended on
        // the shadowed slot silently stopped testing anything. This package registers a transport under a
        // different service type for exactly that reason, and this asserts the hosted one is still the
        // GitHub client in a production-built container.
        var scenario = await ScenarioAsync("lge_registration", []);
        await using var host = scenario.Host;
        using var scope = host.Services.CreateScope();

        Assert.IsType<GitHubEvidenceClient>(scope.ServiceProvider.GetRequiredService<IGitEvidenceSource>());
        Assert.IsType<LocalGitEvidenceSource>(scope.ServiceProvider.GetRequiredService<ILocalGitSource>());

        // The local transport is also the delivered contract, which is what makes the two interchangeable at
        // the level of a collected result rather than merely similar.
        Assert.IsAssignableFrom<IGitEvidenceSource>(scope.ServiceProvider.GetRequiredService<ILocalGitSource>());
    }

    [Fact]
    public async Task The_installed_schema_must_carry_this_packages_migration_and_its_constraints()
    {
        Assert.Contains(ChronicleafSchema.LocalGitEvidenceMigrationId, ChronicleafSchema.RequiredMigrationIds);
        Assert.Equal(ChronicleafSchema.ExpectedMigrationId, ChronicleafSchema.RequiredMigrationIds[^1]);

        var database = await postgres.CreateDatabaseAsync("lge_schema");
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var applied = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT string_agg(\"MigrationId\", '|') FROM \"__EFMigrationsHistory\"");

        Assert.NotNull(applied);
        Assert.All(ChronicleafSchema.RequiredMigrationIds, id => Assert.Contains(id, applied, StringComparison.Ordinal));
        Assert.Empty(ChronicleafSchema.MissingMigrationIds(applied.Split('|')));
        Assert.True(ChronicleafSchema.IsCurrent([.. ChronicleafSchema.RequiredMigrationIds]));

        // The containment constraint is the database's, not only the policy's: a row naming a path that
        // leaves its root is refused whatever wrote it.
        foreach (var path in new[] { "/etc", "../escape", "a/../../b", "./a", "a/./b", "..", "a\\b", "" })
        {
            await using var connection = new Npgsql.NpgsqlConnection(database);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_repositories
                    (id, scope_id, project_id, root_alias, relative_path, created_at, updated_at)
                VALUES (@id, @scope, @project, 'workspace', @path, now(), now())
                """;
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("scope", Guid.NewGuid());
            command.Parameters.AddWithValue("project", Guid.NewGuid());
            command.Parameters.AddWithValue("path", path);

            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync());
        }

        // A run that is complete has a resolved head, and one that is not has none. A row claiming both at
        // once is the state the constraint exists to refuse.
        await using (var connection = new Npgsql.NpgsqlConnection(database))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_sync_sources
                    (id, scope_id, operation_id, milestone_id, expected_bundle_revision, root_alias,
                     relative_path, head_ref, base_sha, head_sha, created_at)
                VALUES (@id, @scope, @op, @milestone, 1, 'workspace', '.', 'main', @sha, @sha, now())
                """;
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("scope", Guid.NewGuid());
            command.Parameters.AddWithValue("op", Guid.NewGuid());
            command.Parameters.AddWithValue("milestone", Guid.NewGuid());
            command.Parameters.AddWithValue("sha", new string('a', 40));

            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task The_paths_a_request_may_name_are_refused_by_their_own_shape_before_anything_is_read()
    {
        // A pure-policy scenario, and deliberately one with no repository: the shape rules are a function of
        // the text, so a filesystem would only make the answer depend on something the assertion is not about.
        Assert.NotNull(RepoRef.Local("workspace", "services/api"));
        Assert.NotNull(RepoRef.Local("workspace", "a"));
        Assert.Null(RepoRef.Local("workspace", null));
        Assert.Null(RepoRef.Local("workspace", string.Empty));
        Assert.Null(RepoRef.Local(null, "a"));
        Assert.Null(RepoRef.Local("workspace", "/etc/passwd"));
        Assert.Null(RepoRef.Local("workspace", "C:/windows"));
        Assert.Null(RepoRef.Local("workspace", ".."));
        Assert.NotNull(RepoRef.Local("workspace", "."));
        Assert.Null(RepoRef.Local("workspace", "a/../b"));
        Assert.Null(RepoRef.Local("workspace", "a/./b"));
        Assert.Null(RepoRef.Local("workspace", "a//b"));
        Assert.Null(RepoRef.Local("workspace", "a\\b"));
        Assert.Null(RepoRef.Local("-workspace", "a"));
        Assert.Null(RepoRef.Local("work space", "a"));
        Assert.Null(RepoRef.Local("workspace", new string('a', RepoRef.RelativePathMaxLength + 1)));
        Assert.Null(RepoRef.Local("workspace", string.Join('/', Enumerable.Repeat("a", RepoRef.RelativePathMaxSegments + 1))));

        // A local reference builds no host path segment, so there is no code path by which a registered
        // directory could become a request to a remote host.
        var local = RepoRef.Local("workspace", "services/api")!;
        Assert.True(local.IsLocal);
        Assert.Equal("local-git:workspace/services/api", local.ToSourceRef());
        Assert.Throws<InvalidOperationException>(() => local.ToPathSegment());

        // And a hosted reference is unchanged by any of this: its own parser still refuses everything it
        // always refused.
        Assert.Null(RepoRef.Parse("workspace", "services/api"));
        Assert.NotNull(RepoRef.Parse("owner", "name"));
        Assert.False(RepoRef.Parse("owner", "name")!.IsLocal);
        Assert.Equal("github:owner/name", RepoRef.Parse("owner", "name")!.ToSourceRef());
        Assert.Equal("owner/name", RepoRef.Parse("owner", "name")!.ToPathSegment());

        await Task.CompletedTask;
    }

    [Fact]
    public void Every_error_code_this_package_owns_is_in_the_status_mapping()
    {
        // A code with no status mapping answers 500, which would tell a client the server is broken about
        // something an operator can fix. The mapping is a contract, so the set is asserted rather than
        // discovered in production.
        Assert.All(
            LocalGitError.All,
            code => Assert.NotEqual(500, Application.Common.ApiErrorStatus.ForCode(code)));

        Assert.Equal(403, Application.Common.ApiErrorStatus.ForCode(LocalGitError.OutsideRoot));
        Assert.Equal(403, Application.Common.ApiErrorStatus.ForCode(LocalGitError.NotAvailableInHosted));
        Assert.Equal(404, Application.Common.ApiErrorStatus.ForCode(LocalGitError.RootUnknown));
        Assert.Equal(422, Application.Common.ApiErrorStatus.ForCode(LocalGitError.NotARepository));
        Assert.Equal(422, Application.Common.ApiErrorStatus.ForCode(LocalGitError.RepositoryMissing));
        Assert.Equal(503, Application.Common.ApiErrorStatus.ForCode(LocalGitError.GitUnavailable));
        Assert.Equal(503, Application.Common.ApiErrorStatus.ForCode(LocalGitError.TimedOut));
    }

    // ===========================================================================================
    // Fixtures.
    // ===========================================================================================

    /// <summary>
    /// The old revision the design's fixture names: one tracked text file, tagged.
    /// </summary>
    /// <remarks>
    /// The first commit is empty so the second one is unambiguously an addition, which is what lets a
    /// scenario assert on a status word rather than on a patch's contents. A tag is written because "a tagged
    /// old commit" is the fixture the design names, and because a base expressed as a tag is the case where
    /// resolving the ref to a SHA is the only thing that makes the comparison mean anything.
    /// </remarks>
    private static async Task<string> SeedOldRevisionAsync(TemporaryGitRepository workspace)
    {
        await workspace.CommitAsync("The first recorded milestone of this project");
        await workspace.TagAsync("v0.9.0");

        return await workspace.RevParseAsync("HEAD");
    }

    /// <summary>
    /// The new revision: one added source file and one added note, and a real author and date on both.
    /// </summary>
    private static async Task<string> SeedHeadRevisionAsync(TemporaryGitRepository workspace)
    {
        workspace.WriteFile("src/app.ts", "export const reproducible = true;\nconsole.log('reproducible');\n");
        workspace.WriteFile("docs/notes.md", "# Notes\n\nThe pipeline runs from a clean checkout.\n");

        return await workspace.CommitAsAsync(
            "Ship the reproducible pipeline",
            "A Maintainer",
            "maintainer@chronicleaf.invalid");
    }

    /// <summary>
    /// A fresh database, migrated, with the installation provisioned through the scenario's own host.
    /// </summary>
    /// <remarks>
    /// <b>One host per scenario, deliberately</b>, for the delivered GitHub host's reason: Hangfire's log
    /// provider caches the logger factory of whichever host initialised it, for the life of the process, so
    /// a second short-lived host in the same scenario would leave the next enqueue holding a disposed factory
    /// and failing with an exception that has nothing to do with the code under test.
    /// </remarks>
    private async Task<Scenario> ScenarioAsync(
        string purpose,
        IReadOnlyList<LocalRepositoryRoot> roots,
        LocalGitDeploymentMode mode = LocalGitDeploymentMode.Community,
        IGitCommandRunner? runner = null)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        var host = LocalGitApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            roots,
            mode,
            runner);

        using (var scope = host.Services.CreateScope())
        {
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return new Scenario(database, host);
    }

    /// <summary>The database and the one host a scenario runs in.</summary>
    private sealed record Scenario(string Database, LocalGitApiHost Host);

    private static async Task<MilestoneFixture> CreateMilestoneAsync(OwnerClient owner, string title)
    {
        using var projectResponse = await owner.SendAsync(
            await owner.Post("/api/projects", new { name = $"{title} 项目", description = $"{title} 说明。" }));
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);

        var projectId = Guid.Parse(JsonDocument
            .Parse(await projectResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var milestoneResponse = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/milestones",
            new
            {
                title,
                version = "0.1.0",
                occurredAt = "2026-10-08T09:00:00Z",
                type = "feature",
                notes = $"{title} 的记录。",
                knownLimitations = Array.Empty<string>(),
                nextSteps = Array.Empty<string>(),
            }));
        Assert.Equal(HttpStatusCode.Created, milestoneResponse.StatusCode);

        var milestoneId = Guid.Parse(JsonDocument
            .Parse(await milestoneResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!);

        using var evidence = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/evidence/manual",
            new { notes = "The owner recorded this by hand.", expectedRevision = 0 },
            0));
        Assert.Equal(HttpStatusCode.Created, evidence.StatusCode);

        return new MilestoneFixture(projectId, milestoneId);
    }

    private sealed record MilestoneFixture(Guid ProjectId, Guid MilestoneId);

    private static async Task<RegisteredRepository> RegisterAsync(
        OwnerClient owner,
        Guid projectId,
        string rootAlias,
        string relativePath,
        LocalGitApiHost? host = null)
    {
        var project = await ReadProjectAsync(owner, projectId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/local-repository",
            new { rootAlias, relativePath },
            project.GetProperty("revision").GetInt64()));

        var raw = await response.Content.ReadAsStringAsync();

        // The envelope travels into the failure message on purpose: a 500 from this route is a bug in this
        // package, and a scenario that cannot say which envelope it got cannot be debugged.
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{raw}\n{host?.Logs.Text}");

        using var body = JsonDocument.Parse(raw);

        return new RegisteredRepository(
            body.RootElement.GetProperty("rootAlias").GetString()!,
            body.RootElement.GetProperty("relativePath").GetString()!,
            body.RootElement.GetProperty("defaultBranch").GetString(),
            body.RootElement.GetProperty("headSha").GetString(),
            raw);
    }

    private sealed record RegisteredRepository(
        string RootAlias,
        string RelativePath,
        string? DefaultBranch,
        string? HeadSha,
        string Raw);

    private static async Task<Refusal> TryRegisterAsync(
        OwnerClient owner,
        Guid projectId,
        string rootAlias,
        string relativePath,
        LocalGitApiHost? host = null)
    {
        var project = await ReadProjectAsync(owner, projectId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/projects/{projectId:D}/local-repository",
            new { rootAlias, relativePath },
            project.GetProperty("revision").GetInt64()));

        return new Refusal(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record Refusal(HttpStatusCode Status, string Body);

    private static async Task<Refusal> TrySyncAsync(
        OwnerClient owner,
        Guid milestoneId,
        string headRef,
        string? baseRef)
    {
        var head = await ReadEvidenceAsync(owner, milestoneId);

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/local-sync",
            new
            {
                headRef,
                baseRef,
                expectedBundleRevision = head.Revision,
                idempotencyKey = "refused",
            },
            head.Revision));

        return new Refusal(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<SyncAccepted> RequestSyncAsync(
        OwnerClient owner,
        Guid milestoneId,
        string headRef,
        string? baseRef,
        string key = "sync")
    {
        var head = await ReadEvidenceAsync(owner, milestoneId);
        var expected = head.Revision;

        using var response = await owner.SendAsync(await owner.Post(
            $"/api/milestones/{milestoneId:D}/local-sync",
            new
            {
                headRef,
                baseRef,
                expectedBundleRevision = expected,
                idempotencyKey = key,
            },
            expected));

        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw);

        return new SyncAccepted(
            response.StatusCode,
            body.RootElement.TryGetProperty("operationId", out var operationId)
                ? Guid.Parse(operationId.GetString()!)
                : Guid.Empty,
            body.RootElement.TryGetProperty("created", out var created) && created.GetBoolean(),
            raw);
    }

    private sealed record SyncAccepted(HttpStatusCode Status, Guid OperationId, bool Created, string Body);

    private static async Task<JsonElement> ReadProjectAsync(OwnerClient owner, Guid projectId)
    {
        using var response = await owner.GetAsync($"/api/projects/{projectId:D}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.Clone();
    }

    private static async Task<EvidenceHead> ReadEvidenceAsync(OwnerClient owner, Guid milestoneId)
    {
        using var response = await owner.GetAsync($"/api/milestones/{milestoneId:D}/evidence");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var bundle = body.RootElement.GetProperty("items")[0];

        return new EvidenceHead(
            bundle.GetProperty("revision").GetInt64(),
            [.. bundle.GetProperty("items").EnumerateArray().Select(item => new EvidenceItem(
                item.GetProperty("kind").GetString()!,
                item.GetProperty("sourceRef").GetString()!,
                item.GetProperty("sourceRevision").GetString()!,
                item.GetProperty("availability").GetString()!,
                item.GetProperty("text").GetString()!))]);
    }

    private sealed record EvidenceHead(long Revision, IReadOnlyList<EvidenceItem> Items);

    private sealed record EvidenceItem(
        string Kind,
        string SourceRef,
        string SourceRevision,
        string Availability,
        string Text);

    /// <summary>The run log row for one operation, read back from the real table.</summary>
    private static async Task<RunRow?> ReadRunAsync(string database, Guid operationId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT root_alias, relative_path, expected_bundle_revision, head_ref, base_ref,
                   base_sha, head_sha, command_log, fetched_at, commit_count,
                   changed_file_count, truncated, excluded_file_count, uncommitted_count, completed_at
            FROM local_sync_sources
            WHERE operation_id = @id
            """;

        command.Parameters.AddWithValue("id", operationId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RunRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetBoolean(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));
    }

    private sealed record RunRow(
        string RootAlias,
        string RelativePath,
        long ExpectedBundleRevision,
        string HeadRef,
        string? BaseRef,
        string? BaseSha,
        string? HeadSha,
        string? CommandLog,
        DateTimeOffset? FetchedAt,
        int CommitCount,
        int ChangedFileCount,
        bool Truncated,
        int ExcludedFileCount,
        int UncommittedCount,
        DateTimeOffset? CompletedAt);

    /// <summary>
    /// Waits for an operation row to carry <paramref name="errorCode"/>, and returns it.
    /// </summary>
    /// <remarks>
    /// A status is not enough on its own: a job that has been enqueued and not yet attempted is
    /// <c>queued</c> too, so an assertion about a refusal has to wait for the refusal to be recorded.
    /// </remarks>
    private static async Task<StoredOperation> AwaitErrorAsync(
        string database,
        Guid operationId,
        string errorCode,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        StoredOperation? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await OperationRows.ReadAsync(database, operationId);

            if (last is not null && last.ErrorCode == errorCode)
            {
                return last;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"operation {operationId:D} did not record {errorCode} within {timeout}; last read was "
            + $"status={last?.Status}, error={last?.ErrorCode}, attempt={last?.Attempt}.");
    }

    /// <summary>Counts rows a statement matches, over a raw connection to the real database.</summary>
    private static async Task<long> CountAsync(
        string database,
        string sql,
        Action<Npgsql.NpgsqlCommand>? bind = null)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Counts rows of one table whose every text column contains <paramref name="needle"/>.
    /// </summary>
    /// <remarks>
    /// The needle is searched with <c>LIKE</c> over each text column separately, which finds a value that
    /// straddles a column boundary only if it happens to sit inside one - and a secret inside a ciphertext
    /// need not be aligned. The evidence text is stored as plain text, so the column search is the honest
    /// reading here; the vault's own scenario reads raw bytes for the case where alignment matters, and this
    /// package has no sealed value in these tables for that to apply to.
    /// </remarks>
    private static async Task<long> CountTextAsync(string database, string table, string needle)
    {
        var columns = table switch
        {
            "evidence_items" => "text",
            "local_sync_sources" => "root_alias || ' ' || relative_path || ' ' || head_ref || ' ' || "
                + "coalesce(base_ref, '') || ' ' || coalesce(base_sha, '') || ' ' || coalesce(head_sha, '') || ' ' "
                + "|| coalesce(command_log, '')",
            "local_repositories" => "root_alias || ' ' || relative_path || ' ' || "
                + "coalesce(default_branch, '') || ' ' || coalesce(head_sha, '')",
            "audit_events" => "action || ' ' || safe_metadata::text",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "This table is not searched."),
        };

        await using var connection = new Npgsql.NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table} WHERE {columns} LIKE @needle";
        command.Parameters.AddWithValue("needle", $"%{needle}%");

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>One column's whole contents, for the assertions about what a table is allowed to hold.</summary>
    private static async Task<string> ReadColumn(string database, string table, string column)
    {
        await using var connection = new Npgsql.NpgsqlConnection(database);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT coalesce(string_agg({column}, ' '), '') FROM {table}";

        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

/// <summary>
/// A real runner that records what ran and delegates every command to the production one.
/// </summary>
/// <remarks>
/// <b>Delegating rather than replacing is the whole point.</b> A scripted transport substituted here would
/// have left the argument vector, the sanitised environment, the caps and the exit codes untested, and the
/// claims these scenarios are about are exactly those. So the production <see cref="GitCommandRunner"/> runs
/// every command, and this class only watches.
/// </remarks>
public sealed class RecordingGitRunner(IGitCommandRunner inner) : IGitCommandRunner
{
    private readonly List<string> _subcommands = [];
    private readonly List<IReadOnlyList<string>> _arguments = [];
    private readonly List<string[]> _vectors = [];
    private readonly Lock _gate = new();

    /// <summary>The subcommands that ran, in order.</summary>
    public IReadOnlyList<string> Subcommands
    {
        get
        {
            lock (_gate)
            {
                return [.. _subcommands];
            }
        }
    }

    /// <summary>The arguments of each command, in the same order as <see cref="Subcommands"/>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Arguments
    {
        get
        {
            lock (_gate)
            {
                return [.. _arguments];
            }
        }
    }

    /// <summary>Every full argument vector, so the base arguments can be read rather than assumed.</summary>
    public IReadOnlyList<string[]> ArgumentVectors
    {
        get
        {
            lock (_gate)
            {
                return [.. _vectors];
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _subcommands.Clear();
            _arguments.Clear();
            _vectors.Clear();
        }
    }

    public async Task<GitCommandResult> RunAsync(
        GitCommandRequest request,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        var vector = GitCommandRunner.BuildStartInfo(request).ArgumentList.ToArray();

        lock (_gate)
        {
            _subcommands.Add(request.Subcommand);
            _arguments.Add([.. request.Arguments]);
            _vectors.Add(vector);
        }

        return await inner.RunAsync(request, timeout, maxOutputBytes, cancellationToken).ConfigureAwait(false);
    }
}
