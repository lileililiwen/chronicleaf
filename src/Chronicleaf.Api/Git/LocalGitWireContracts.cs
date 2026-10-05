using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Git;

/// <summary>
/// The wire shape of a registered local repository (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// Field names are pinned explicitly, as every other package's are, so the contract cannot drift with
/// serializer policy.
/// </para>
/// <para>
/// <b>What a client is told:</b> the alias and the relative path it asked for, the branch the repository's
/// HEAD named, and the revision HEAD resolved to at validation time. <b>What it is not told:</b> the
/// registered directory. The absolute path stays in the operator's configuration and appears in no response,
/// no log line and no stored row - so an owner's browser learns nothing about the machine the server runs
/// on, and a bundle this package writes can be exported without carrying a filesystem layout with it.
/// </para>
/// </remarks>
public sealed record LocalRepositoryResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("rootAlias")] string RootAlias,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch,
    [property: JsonPropertyName("headSha")] string? HeadSha,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    public static LocalRepositoryResponse From(LocalRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return new LocalRepositoryResponse(
            repository.Id.ToString("D"),
            repository.ProjectId.ToString("D"),
            repository.RootAlias,
            repository.RelativePath,
            repository.DefaultBranch,
            repository.HeadSha,
            Stamp(repository.CreatedAt),
            Stamp(repository.UpdatedAt));
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The body a local repository is registered from (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// Two words, and the absence of a third is the security property. There is no absolute path, no
/// directory and no URL: the alias names a root the <em>operator</em> registered and the relative path is
/// resolved inside it and refused if it leaves. A body that could name a directory would be a body that
/// decides what this server reads, and an owner could then point it at a directory that has nothing to do
/// with the product.
/// </para>
/// <para>
/// There is no credential field either, and that is a property of the feature rather than an omission: a
/// local repository is read off the operator's own disk with the operator's own Git, so there is no token
/// for this surface to hold and none for it to leak.
/// </para>
/// </remarks>
public sealed record LocalRepositoryWireRequest(
    [property: JsonPropertyName("rootAlias")] string? RootAlias,
    [property: JsonPropertyName("relativePath")] string? RelativePath);

/// <summary>
/// The body a local collection is asked for (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// The evidence revision travels in the body and as <c>If-Match</c>, and the server refuses a body that
/// disagrees with the header rather than picking one. The idempotency key is the caller's: a repeat of the
/// same key returns the job the first request created instead of starting a second collection of the same
/// repository.
/// </remarks>
public sealed record LocalGitSyncWireRequest(
    [property: JsonPropertyName("headRef")] string? HeadRef,
    [property: JsonPropertyName("baseRef")] string? BaseRef,
    [property: JsonPropertyName("expectedBundleRevision")] long? ExpectedBundleRevision,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey)
{
    /// <summary>
    /// The request a body that did not arrive produces: every field empty and the revision absent, so an
    /// empty body is refused by name rather than read as "revision zero, no refs" and matched against a
    /// milestone that happens to have no evidence yet.
    /// </summary>
    public static LocalGitSyncWireRequest Empty { get; } = new(
        HeadRef: null,
        BaseRef: null,
        ExpectedBundleRevision: null,
        IdempotencyKey: null);
}

/// <summary>
/// The answer to a local collection request: the job that will perform it, and where to read its progress
/// (LOCAL-GIT-EVIDENCE-001).
/// </summary>
/// <remarks>
/// A 202 and an operation id, never a bundle. The collection runs several Git processes against a disk, so
/// the work happens in the delivered queue and this response points at the delivered operation route.
/// <c>operationUrl</c> is included so a client does not have to build the path itself, and <c>created</c>
/// says whether this request created the job or replayed one.
/// </remarks>
public sealed record LocalGitSyncAcceptedResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("operationUrl")] string OperationUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created")] bool Created);

/// <summary>
/// The registered roots this deployment offers, and the deployment they may be read in
/// (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Aliases only, and no paths.</b> This is what lets the panel offer a choice instead of a text field -
/// a repository picker that listed directories would be a file browser, and a file browser is how a caller
/// starts guessing at a filesystem. The aliases are the operator's own names, and an owner needs them to
/// choose; the directories behind them are the operator's business and appear nowhere.
/// </para>
/// <para>
/// <b>An empty list is an answer, not a failure.</b> A Community install whose operator registered nothing
/// has a catalogue with no entries, and a panel that renders that as an error would be reporting a
/// misconfiguration as a broken product. The boolean says which of the two it is, so a client can tell
/// "no roots are configured" from "this deployment does not read local directories at all".
/// </para>
/// </remarks>
public sealed record LocalRepositoryCatalogResponse(
    [property: JsonPropertyName("deploymentMode")] string DeploymentMode,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("rootAliases")] IReadOnlyList<string> RootAliases);
