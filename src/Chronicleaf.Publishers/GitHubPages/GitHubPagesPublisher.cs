using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Publishers.GitHubPages;

/// <summary>
/// The GitHub Pages reviewed publishing adapter
/// (PUBLISHER-GITHUB-PAGES-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An adapter converts generic content; it updates nothing.</b> It holds no store and receives
/// no store: the snapshot carries everything it may send, the lease carries the one credential it
/// may use, and neither outlives the call. It cannot update a milestone, a bundle, an approval
/// or an attempt, because it cannot reach any of them.
/// </para>
/// <para>
/// <b>Only the ref move publishes.</b> Blobs, trees and commit objects without a ref are
/// unreachable: no reader, no Pages build and no reconciler can find them. A transport failure
/// before the ref move is therefore a definitive failure with a redacted code, and only the ref
/// move itself losing its answer is ambiguous - raised as the contract's own timeout with the
/// planned commit SHA, so the reconciler can ask the branch what it holds.
/// </para>
/// <para>
/// <b>The preview binds the target configuration.</b> The target line names the configured
/// repository, branch and subdirectory, and it travels in the preview's warnings - which the
/// contract hashes. An operator reconfiguration after the owner read the preview changes what a
/// send would do, so the stale confirmation is refused before a job exists rather than as a
/// failed run.
/// </para>
/// </remarks>
public sealed class GitHubPagesPublisher : IPublisherAdapter
{
    /// <summary>The registry id this adapter sends to.</summary>
    public const string TargetId = "github-pages";

    /// <summary>The fixed commit author name. A label, never an owner identity.</summary>
    public const string CommitAuthorName = "Chronicleaf Publisher";

    /// <summary>The fixed commit author address. Never a mailbox that receives mail.</summary>
    public const string CommitAuthorEmail = "chronicleaf-publisher@localhost";

    /// <summary>Where the rendered document is written under the target subdirectory.</summary>
    public const string IndexFileName = "index.html";

    /// <summary>Where approval-covered pictures are written under the target subdirectory.</summary>
    public const string AssetsDirectory = "assets";

    private readonly IOptionsMonitor<GitHubPagesOptions> targets;
    private readonly GitHubPagesTransport transport;
    private readonly IContentExportAdapter htmlExport;
    private readonly ILogger<GitHubPagesPublisher> logger;

    public GitHubPagesPublisher(
        IOptionsMonitor<GitHubPagesOptions> targets,
        HttpMessageHandler handler,
        IContentExportAdapter htmlExport,
        ILogger<GitHubPagesPublisher>? logger = null)
        : this(targets, new GitHubPagesTransport(handler), htmlExport, logger)
    {
    }

    internal GitHubPagesPublisher(
        IOptionsMonitor<GitHubPagesOptions> targets,
        GitHubPagesTransport transport,
        IContentExportAdapter htmlExport,
        ILogger<GitHubPagesPublisher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(htmlExport);

        this.targets = targets;
        this.transport = transport;
        this.htmlExport = htmlExport;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPagesPublisher>.Instance;
    }

    /// <inheritdoc />
    string IPublisherAdapter.TargetId => TargetId;

    /// <inheritdoc />
    public PublisherCapabilities Describe() => new(TargetId, SupportsDraft: false, SupportsPublish: true, SupportsReconcile: true);

    /// <inheritdoc />
    public async Task<PublishPreview> PrepareAsync(
        ApprovedAssetSnapshot snapshot,
        PublisherTargetConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(config);

        // The preview is a pure function of the snapshot and the operator's target: it writes
        // nothing remotely, leases no credential and creates no row. Rendering here is CPU only.
        var target = CurrentTarget();
        var files = await BuildSiteFilesAsync(snapshot, target, cancellationToken).ConfigureAwait(false);

        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes += file.Bytes.LongLength;
        }

        var mediaPlan = snapshot.Pictures
            .Select(picture => picture.MediaId)
            .OrderBy(id => id)
            .ToList();

        return new PublishPreview(
            TargetId,
            snapshot.BodyHash,
            mediaPlan,
            [target.TargetLine(files.Count, totalBytes)]);
    }

    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(
        PublishCommand command,
        ApprovedAssetSnapshot snapshot,
        SecretLease credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(credential);

        var target = CurrentTarget();
        var repository = target.Repository;
        var key = Encoding.UTF8.GetString(credential.Secret.ToArray());
        var files = await BuildSiteFilesAsync(snapshot, target, cancellationToken).ConfigureAwait(false);

        string? baseSha;
        bool branchExists;

        try
        {
            await transport
                .ReadRepositoryAsync(repository, key, cancellationToken)
                .ConfigureAwait(false);

            var branch = await transport
                .ReadRefAsync(repository, target.Branch, key, cancellationToken)
                .ConfigureAwait(false);

            branchExists = branch.Exists;
            baseSha = branch.Sha;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Two GETs cannot have published anything: no ref was named, so no ref could have
            // moved. Failed, not unknown.
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} failed before any write: the upstream did not answer.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: GitHubPagesError.Unavailable);
        }
        catch (HttpRequestException)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} failed before any write: the transport failed.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: GitHubPagesError.Unavailable);
        }
        catch (GitHubPagesUpstreamException refused)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} refused with {ErrorCode} before any write.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        string commitSha;

        try
        {
            var blobs = new List<(string Path, string BlobSha)>(files.Count);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var blobSha = await transport
                    .CreateBlobAsync(repository, file.Bytes, key, cancellationToken)
                    .ConfigureAwait(false);

                blobs.Add((file.Path, blobSha));
            }

            var treeSha = await transport
                .CreateTreeAsync(repository, baseSha, blobs, key, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<string> parents = baseSha is null ? [] : [baseSha];

            commitSha = await transport
                .CreateCommitAsync(
                    repository,
                    CommitMessageFor(snapshot),
                    treeSha,
                    parents,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Blobs, trees and commits without a ref are unreachable: no reader, no Pages build
            // and no reconciler can find them. The branch still points where it pointed.
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} failed while staging content: nothing reachable changed.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: GitHubPagesError.Unavailable);
        }
        catch (HttpRequestException)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} failed while staging content: nothing reachable changed.",
                command.OperationId);
            return new PublishResult(PublishStates.Failed, ErrorCode: GitHubPagesError.Unavailable);
        }
        catch (GitHubPagesUpstreamException refused)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} refused with {ErrorCode} while staging content.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        // From here the write may have happened before the answer was lost. The planned commit
        // SHA is content-addressed - known before the move - so it travels on the timeout for the
        // reconciler to ask the branch about.
        try
        {
            if (branchExists)
            {
                await transport
                    .UpdateRefAsync(repository, target.Branch, commitSha, key, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await transport
                    .CreateRefAsync(repository, target.Branch, commitSha, key, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} lost the ref-move answer: unknown until reconciled.",
                command.OperationId);
            throw new PublisherTimeoutException(commitSha);
        }
        catch (HttpRequestException)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} lost the ref-move transport: unknown until reconciled.",
                command.OperationId);
            throw new PublisherTimeoutException(commitSha);
        }
        catch (GitHubPagesUpstreamException refused)
        {
            logger.LogInformation(
                "GitHub Pages publish for operation {OperationId} refused with {ErrorCode} at the ref move.",
                command.OperationId,
                refused.ErrorCode);
            return new PublishResult(PublishStates.Failed, ErrorCode: refused.ErrorCode);
        }

        logger.LogInformation(
            "GitHub Pages publish for operation {OperationId} moved {Branch} to {CommitSha}.",
            command.OperationId,
            target.Branch,
            commitSha);

        return new PublishResult(
            PublishStates.Published,
            RemoteId: commitSha,
            RemoteUrl: CommitUrlFor(repository, commitSha));
    }

    /// <inheritdoc />
    public async Task<PublishResult> ReconcileAsync(
        PublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        if (string.IsNullOrWhiteSpace(receipt.RemoteId))
        {
            // No planned SHA was recorded, so there is nothing to ask the branch about. The
            // receipt stays unknown and the owner inspects the remote side by hand.
            return new PublishResult(PublishStates.Unknown);
        }

        GitBranchRef branch;

        var target = CurrentTarget();

        try
        {
            // Anonymous on purpose: the reconciler holds no credential - the contract calls it
            // with the receipt alone - so it can only ask what the branch says in public. A
            // private branch stays unknown rather than becoming a credentialed read.
            branch = await transport
                .ReadRefAsync(target.Repository, target.Branch, credential: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception unreadable) when (unreadable is not OperationCanceledException)
        {
            return new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl);
        }

        if (branch.Exists
            && string.Equals(branch.Sha, receipt.RemoteId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "GitHub Pages attempt {AttemptId} reconciled: the branch holds the planned commit.",
                receipt.AttemptId);

            return new PublishResult(
                PublishStates.Published,
                receipt.RemoteId,
                CommitUrlFor(target.Repository, receipt.RemoteId));
        }

        // The branch holds anything else - or nothing. The planned commit may still be in its
        // history behind a later push, so this is not a failure: it stays unknown.
        return new PublishResult(PublishStates.Unknown, receipt.RemoteId, receipt.RemoteUrl);
    }

    /// <summary>
    /// Renders the approved snapshot to the fixed file layout: one document plus the
    /// approval-covered pictures under generated paths, prefixed by the target subdirectory.
    /// </summary>
    /// <remarks>
    /// Every path is generated here from ids, never from owner text: the subdirectory comes from
    /// the operator's validated options, the document name is fixed, and each picture is named by
    /// its media id. No entry can escape the subdirectory and no render can reach the network,
    /// because the renderer only reads the bytes it was handed.
    /// </remarks>
    /// <summary>
    /// The operator's target as of this call, re-validated: a reloaded section that no longer
    /// validates is a refusal, never a send to a half-read target.
    /// </summary>
    private GitHubPagesOptions CurrentTarget()
    {
        var current = targets.CurrentValue;

        return GitHubPagesOptions.Create(
            current.RepositoryOwner,
            current.RepositoryName,
            current.Branch,
            current.Subdirectory);
    }

    private async Task<IReadOnlyList<SiteFile>> BuildSiteFilesAsync(
        ApprovedAssetSnapshot snapshot,
        GitHubPagesOptions target,
        CancellationToken cancellationToken)
    {
        var ordered = snapshot.Pictures.OrderBy(picture => picture.MediaId).ToList();

        var render = new ApprovedAssetSnapshot(
            snapshot.AssetId,
            snapshot.ContentRevision,
            snapshot.Title,
            snapshot.BodyMarkdown,
            snapshot.BodyHash,
            snapshot.EvidenceRevision,
            ordered);

        var artifact = await htmlExport.ExportAsync(render, cancellationToken).ConfigureAwait(false);

        var prefix = target.Subdirectory.Length == 0 ? string.Empty : target.Subdirectory + "/";
        var files = new List<SiteFile>();

        if (string.Equals(artifact.MediaType, HtmlExportAdapter.HtmlMediaType, StringComparison.Ordinal))
        {
            files.Add(new SiteFile(prefix + IndexFileName, artifact.Bytes));
        }
        else
        {
            using var archive = new ZipArchive(new MemoryStream(artifact.Bytes), ZipArchiveMode.Read);

            var index = archive.GetEntry(IndexFileName)
                ?? throw new InvalidOperationException(
                    "The rendered artifact carries no document, so it cannot be published.");

            string document;
            using (var stream = index.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                document = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var position = 0; position < ordered.Count; position++)
            {
                var mediaId = ordered[position].MediaId;
                var source = string.Create(
                    CultureInfo.InvariantCulture, $"src=\"images/image-{position}.png\"");
                var rewritten = string.Create(
                    CultureInfo.InvariantCulture, $"src=\"{AssetsDirectory}/{mediaId:N}.png\"");

                document = document.Replace(source, rewritten, StringComparison.Ordinal);
            }

            files.Add(new SiteFile(prefix + IndexFileName, Encoding.UTF8.GetBytes(document)));

            for (var position = 0; position < ordered.Count; position++)
            {
                var entry = archive.GetEntry($"images/image-{position}.png")
                    ?? throw new InvalidOperationException(
                        "The rendered artifact is missing a picture it names, so it cannot be published.");

                using var stream = entry.Open();
                using var buffered = new MemoryStream();
                await stream.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);

                files.Add(new SiteFile(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{prefix}{AssetsDirectory}/{ordered[position].MediaId:N}.png"),
                    buffered.ToArray()));
            }
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The fixed commit message: ids and digests, never prose and never a secret.
    /// </summary>
    private static string CommitMessageFor(ApprovedAssetSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Publish asset {snapshot.AssetId:D} revision {snapshot.ContentRevision}\n\n"
            + $"body-sha256:{snapshot.BodyHash}\n"
            + $"template:{HtmlExportAdapter.TemplateVersion}");

    private static string CommitUrlFor(Chronicleaf.GitHub.Evidence.RepoRef repository, string commitSha) =>
        $"https://github.com/{repository.Owner}/{repository.Name}/commit/{commitSha}";

    /// <summary>
    /// The lowercase hex SHA-256 over a file's bytes, as the preview lists it.
    /// </summary>
    public static string FileHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record SiteFile(string Path, byte[] Bytes);
}
