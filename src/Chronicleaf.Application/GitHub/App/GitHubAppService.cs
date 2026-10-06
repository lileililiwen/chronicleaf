using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.GitHub.App;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The GitHub App connection use cases over the delivered stores, vault and evidence transport
/// (GITHUB-APP-CONNECTION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An installation is verified before it is stored, and a token is minted before it is
/// trusted.</b> The connect call reads the installation as the App itself, mints a token
/// restricted to the one repository the owner named, and reads that repository through it. Only
/// when all three agree is anything written - and what is written names the installation, never a
/// token.
/// </para>
/// <para>
/// <b>The existing token path is untouched.</b> A connection that authenticates with a vault token
/// keeps authenticating with it; nothing here migrates it, replaces it or reads it. The App
/// rollout adds a second way to authenticate, not a second meaning for the first one.
/// </para>
/// </remarks>
public sealed class GitHubAppService(
    IProjectStore projects,
    IGitHubConnectionStore connections,
    IGitHubAppInstallationStore installations,
    IGitHubInstallationTokenProvider tokens,
    GitHubAppInstallationTokenProvider minter,
    GitHubAppClient app,
    ISecretVault vault,
    GitHubAppOptions configured,
    GitHubAppSetupStates setups,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<GitHubAppService> logger) : IGitHubAppService
{
    /// <summary>The audit action recorded when an installation connection is stored.</summary>
    public const string AppConnectedAction = "github.app.connected";

    /// <summary>The audit action recorded when an installation is revoked.</summary>
    public const string AppRevokedAction = "github.app.revoked";

    /// <summary>The largest webhook delivery this package reads before verifying it.</summary>
    public const int WebhookMaxBytes = 1024 * 1024;

    /// <inheritdoc />
    public async Task<GitHubAppSetup> BeginSetupAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        return setups.Issue(owner.ScopeId, owner.ActorId, projectId);
    }

    /// <inheritdoc />
    public async Task<GitHubConnection> ConnectAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubAppConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (request.InstallationId < 1 || request.RepositoryId < 1)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["installationId"] = ["An installation id is one or greater."],
                ["repositoryId"] = ["A repository id is one or greater."],
            });
        }

        // The state is consumed before anything else is read, so a replayed callback is refused
        // before a single App call is made and creates nothing on its second arrival.
        if (!setups.TryConsume(request.State, owner.ScopeId, owner.ActorId, projectId))
        {
            throw new GitHubAppStateInvalidException();
        }

        if (configured.AppId is null or < 1 ||
            !Guid.TryParse(configured.PrivateKeyCredentialRef, out var keyRef) ||
            keyRef == Guid.Empty)
        {
            throw new GitHubAppNotConfiguredException();
        }

        var now = clock.GetUtcNow();
        var jwt = await AppJwtAsync(keyRef, configured.AppId.Value, cancellationToken).ConfigureAwait(false);

        // The installation is read as the App itself. A 404 here is an installation GitHub does
        // not know, which is a different refusal from a repository it will not show this caller.
        var (installationOutcome, installation) = await app
            .GetInstallationAsync(jwt, request.InstallationId, cancellationToken)
            .ConfigureAwait(false);

        if (installationOutcome == GitHubAppOutcome.NotFound || installation is null)
        {
            throw new GitHubAppInstallationUnknownException();
        }

        if (installationOutcome != GitHubAppOutcome.Ok)
        {
            throw new GitHubAppUnreachableException();
        }

        if (installation.Suspended)
        {
            // A suspended installation exists but cannot mint. Refusing it as unreachable is the
            // honest temporary answer: unsuspending the installation answers the same request.
            throw new GitHubAppUnreachableException();
        }

        GitHubConnection connection = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                // The cross-tenant check happens inside the same unit of work as the write, so a
                // guessed id cannot win a race between the check and the store: the unique index on
                // the installation id is the second holder of this promise.
                var bound = await installations
                    .FindByInstallationIdAsync(request.InstallationId, token)
                    .ConfigureAwait(false);

                if (bound is not null && bound.ScopeId != owner.ScopeId)
                {
                    throw new GitHubAppInstallationMismatchException();
                }

                // The mint names exactly the repository the owner asked for, and the repository is
                // then read through that token. A repository outside the installation's selection
                // is the same 404 as one that does not exist, and stays the same answer here.
                var minted = await minter.MintAsync(request.InstallationId, request.RepositoryId, token).ConfigureAwait(false);
                try
                {
                    var (repositoryOutcome, repository) = await app
                        .GetRepositoryAsync(minted.Token, request.RepositoryId, token)
                        .ConfigureAwait(false);

                    if (repositoryOutcome == GitHubAppOutcome.NotFound || repository is null)
                    {
                        throw new GitHubAppRepositoryUnselectedException();
                    }

                    if (repositoryOutcome != GitHubAppOutcome.Ok)
                    {
                        throw new GitHubAppUnreachableException();
                    }

                    var stored = new GitHubAppInstallation(
                        bound?.Id ?? Guid.NewGuid(),
                        owner.ScopeId,
                        request.InstallationId,
                        installation.AccountLogin,
                        installation.RepositorySelection,
                        GitHubAppInstallationStatuses.Active,
                        bound?.CreatedAt ?? now,
                        now);

                    await installations.UpsertAsync(stored, token).ConfigureAwait(false);

                    var existing = await connections
                        .FindAsync(owner.ScopeId, projectId, token)
                        .ConfigureAwait(false);

                    var connected = new GitHubConnection(
                        existing?.Id ?? Guid.NewGuid(),
                        owner.ScopeId,
                        projectId,
                        repository.Owner,
                        repository.Name,
                        credentialRef: null,
                        repository.DefaultBranch,
                        request.RepositoryId,
                        existing?.CreatedAt ?? now,
                        now,
                        GitHubAuthModes.Installation,
                        request.InstallationId);

                    var saved = await connections.UpsertAsync(connected, token).ConfigureAwait(false);

                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        AppConnectedAction,
                        saved.Id,
                        now.ToUnixTimeMilliseconds(),
                        "github app installation connected"));

                    return saved;
                }
                finally
                {
                    // The verification token proved the selection and is not needed again: the next
                    // read mints its own through the provider's cache.
                }
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GitHub App installation {InstallationId} connected for project {ProjectId} as {Owner}/{Repository}.",
            request.InstallationId,
            projectId,
            connection.Owner,
            connection.Repo);

        return connection;
    }

    /// <inheritdoc />
    public async Task<GitHubAppInstallation?> ReadInstallationAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var connection = await connections
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null ||
            !string.Equals(connection.AuthMode, GitHubAuthModes.Installation, StringComparison.Ordinal) ||
            connection.InstallationId is null)
        {
            return null;
        }

        return await installations
            .FindAsync(owner.ScopeId, connection.InstallationId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GitHubConnection?> ReadConnectionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A project this owner may not reach answers null through the scoped store, so the route
        // turns that into a 404 rather than finding a row it should not.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var connection = await connections
            .FindAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null ||
            !string.Equals(connection.AuthMode, GitHubAuthModes.Installation, StringComparison.Ordinal))
        {
            return null;
        }

        return connection;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                var connection = await connections
                    .FindAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false);

                if (connection is null ||
                    !string.Equals(connection.AuthMode, GitHubAuthModes.Installation, StringComparison.Ordinal) ||
                    connection.InstallationId is null)
                {
                    return null;
                }

                await connections.DeleteAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);
                tokens.Invalidate(connection.InstallationId.Value);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    AppRevokedAction,
                    connection.Id,
                    clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    "github app installation disconnected"));

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a webhook delivery's signature and applies it. Every authentication failure is one
    /// 401, so a caller cannot learn which installations this server knows.
    /// </summary>
    /// <inheritdoc />
    public async Task ReceiveWebhookAsync(
        string eventName,
        string signatureHeader,
        byte[] body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!Guid.TryParse(configured.WebhookSecretCredentialRef, out var secretRef) || secretRef == Guid.Empty)
        {
            throw new GitHubAppSignatureInvalidException();
        }

        string secret;
        using (var lease = await vault.ResolveAsync(new SecretRef(secretRef), cancellationToken).ConfigureAwait(false))
        {
            // The webhook secret is the server's own credential, resolved by reference with no
            // scope comparison for the same reason as the PEM: no owner scope owns it.
            secret = Encoding.UTF8.GetString(lease.Secret);
        }

        try
        {
            if (!VerifySignature(body, signatureHeader, secret))
            {
                throw new GitHubAppSignatureInvalidException();
            }
        }
        finally
        {
            secret = string.Empty;
        }

        string payload = Encoding.UTF8.GetString(body);
        await HandleWebhookAsync(eventName, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleWebhookAsync(
        string eventName,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(eventName, "installation", StringComparison.Ordinal))
        {
            return;
        }

        long installationId;
        string action;
        try
        {
            using var document = JsonDocument.Parse(payload);
            action = document.RootElement.TryGetProperty("action", out var actionElement)
                ? actionElement.GetString() ?? string.Empty
                : string.Empty;
            installationId = document.RootElement.TryGetProperty("installation", out var installationElement)
                && installationElement.TryGetProperty("id", out var idElement)
                && idElement.TryGetInt64(out var parsed)
                ? parsed
                : 0;
        }
        catch (JsonException)
        {
            return;
        }

        if (installationId < 1)
        {
            return;
        }

        // Only the removal ends local access. A suspension marks the row so the next mint is
        // refused before a socket is opened; anything else leaves the binding alone.
        var status = action switch
        {
            "deleted" => GitHubAppInstallationStatuses.Removed,
            "suspend" => GitHubAppInstallationStatuses.Suspended,
            "unsuspend" => GitHubAppInstallationStatuses.Active,
            _ => null,
        };

        if (status is null)
        {
            return;
        }

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                var bound = await installations
                    .FindByInstallationIdAsync(installationId, token)
                    .ConfigureAwait(false);

                if (bound is null)
                {
                    return null;
                }

                var now = clock.GetUtcNow();
                await installations.UpsertAsync(
                    new GitHubAppInstallation(
                        bound.Id,
                        bound.ScopeId,
                        bound.InstallationId,
                        bound.AccountLogin,
                        bound.RepositorySelection,
                        status,
                        bound.CreatedAt,
                        now),
                    token).ConfigureAwait(false);

                if (string.Equals(status, GitHubAppInstallationStatuses.Removed, StringComparison.Ordinal))
                {
                    // Revocation is immediate and total: every local connection bound to the
                    // installation is deleted, so a queued job finds no connection and fails
                    // without publishing anything. The evidence already appended stays.
                    await connections.DeleteByInstallationAsync(bound.ScopeId, installationId, token).ConfigureAwait(false);
                    tokens.Invalidate(installationId);
                }

                audit.Record(new DomainAudit(
                    Guid.Empty,
                    AppRevokedAction,
                    bound.Id,
                    now.ToUnixTimeMilliseconds(),
                    $"github app installation {status}"));

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the App JWT from the leased PEM. The lease is returned before the caller signs
    /// anything, whatever happens.
    /// </summary>
    private async Task<string> AppJwtAsync(Guid keyRef, long appId, CancellationToken cancellationToken)
    {
        string pem;
        using (var lease = await vault.ResolveAsync(new SecretRef(keyRef), cancellationToken).ConfigureAwait(false))
        {
            pem = Encoding.UTF8.GetString(lease.Secret);
        }

        try
        {
            return app.CreateAppJwt(appId, pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            throw new GitHubAppNotConfiguredException();
        }
        finally
        {
            pem = string.Empty;
        }
    }

    /// <summary>
    /// HMAC-SHA256 over the raw body, compared in constant time. The header carries
    /// <c>sha256=&lt;hex&gt;</c>; anything else is not a signature.
    /// </summary>
    internal static bool VerifySignature(byte[] body, string? signatureHeader, string secret)
    {
        const string prefix = "sha256=";

        if (string.IsNullOrWhiteSpace(signatureHeader) ||
            !signatureHeader.StartsWith(prefix, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(secret))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signatureHeader[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expected.Length != 32)
        {
            return false;
        }

        Span<byte> computed = stackalloc byte[32];
        if (HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, computed) != computed.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
