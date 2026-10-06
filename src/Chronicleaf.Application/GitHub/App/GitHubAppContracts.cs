using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The operator's GitHub App configuration. The App id names the App; the PEM and the webhook
/// secret live in the delivered vault and travel here as references only.
/// </summary>
/// <remarks>
/// Not sealed so a test host can hold the same object and fill it in after the vault decides the
/// credential ids. The production service reads this object, so a scenario exercises the real
/// option checks and only the source of the values differs.
/// </remarks>
public class GitHubAppOptions
{
    public const string SectionName = "Chronicleaf:GitHubApp";

    /// <summary>The App's numeric identity, as GitHub assigned it. Null until an operator sets it.</summary>
    public long? AppId { get; set; }

    /// <summary>The vault reference of the App's PEM private key. Null until an operator stores one.</summary>
    public string? PrivateKeyCredentialRef { get; set; }

    /// <summary>The vault reference of the App webhook secret. Null until an operator stores one.</summary>
    public string? WebhookSecretCredentialRef { get; set; }
}

/// <summary>
/// The delivered vault slots the App's own credentials are stored under
/// (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// Two names because the two secrets are not interchangeable: the PEM signs App JWTs and the
/// webhook secret authenticates GitHub's deliveries, and the unique index on
/// <c>(scope_id, provider)</c> is what keeps saving one from overwriting the other.
/// </remarks>
public static class GitHubAppCredentialProviders
{
    /// <summary>The slot holding the server App's PEM private key.</summary>
    public const string AppPrivateKey = "github-app";

    /// <summary>The slot holding the App webhook secret.</summary>
    public const string AppWebhookSecret = "github-app-webhook";
}

/// <summary>
/// What an owner asked to connect through an App installation.
/// </summary>
/// <param name="InstallationId">GitHub's installation id, from the completed setup.</param>
/// <param name="RepositoryId">GitHub's repository id, which must be in the installation's selection.</param>
/// <param name="State">The single-use setup state this scope was issued.</param>
public sealed record GitHubAppConnectRequest(long InstallationId, long RepositoryId, string? State);

/// <summary>
/// A setup this scope may complete: the state the callback must carry back, and when it lapses.
/// </summary>
/// <param name="State">The random state the callback must carry back, exactly once.</param>
/// <param name="ExpiresAt">When the state lapses.</param>
public sealed record GitHubAppSetup(string State, DateTimeOffset ExpiresAt);

/// <summary>
/// The installation side of storage. Every method is scoped, except the webhook's lookup by
/// installation id, which resolves the scope a delivery belongs to.
/// </summary>
public interface IGitHubAppInstallationStore
{
    Task<GitHubAppInstallation?> FindAsync(
        Guid scopeId,
        long installationId,
        CancellationToken cancellationToken = default);

    Task<GitHubAppInstallation?> FindByInstallationIdAsync(
        long installationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubAppInstallation>> ListAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default);

    Task<GitHubAppInstallation> UpsertAsync(
        GitHubAppInstallation installation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mints installation tokens restricted to one repository
/// (GITHUB-APP-CONNECTION-002).
/// </summary>
/// <remarks>
/// <para>
/// The token is held in memory until a minute before GitHub expires it, and a concurrent refresh
/// mints once no matter how many callers arrive together. No token reaches a row, a log line, an
/// operation payload or an exception: the caller hands it straight to the transport.
/// </para>
/// <para>
/// A token is minted for exactly one repository id. The connection that names another repository
/// cannot borrow it, and a publisher that needs to write needs its own explicit grant - never this
/// read token.
/// </para>
/// </remarks>
public interface IGitHubInstallationTokenProvider
{
    /// <summary>Mints or renews the token for one installation and one repository.</summary>
    /// <exception cref="GitHubAppNotConfiguredException">No App is configured.</exception>
    /// <exception cref="GitHubAppInstallationMismatchException">The installation belongs elsewhere.</exception>
    /// <exception cref="GitHubAppInstallationUnknownException">GitHub knows no such installation.</exception>
    /// <exception cref="GitHubAppUnreachableException">GitHub could not mint.</exception>
    Task<string> GetAsync(
        Guid scopeId,
        long installationId,
        long? repositoryId,
        CancellationToken cancellationToken = default);

    /// <summary>Drops every cached token for an installation, so revocation is immediate.</summary>
    void Invalidate(long installationId);
}

/// <summary>
/// The App connection use cases (GITHUB-APP-CONNECTION-001/002).
/// </summary>
public interface IGitHubAppService
{
    /// <summary>Issues the single-use setup state a callback must carry back.</summary>
    Task<GitHubAppSetup> BeginSetupAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Verifies an installation and binds it to a project through the selected repository.</summary>
    Task<GitHubConnection> ConnectAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubAppConnectRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the installation bound to a project, or <c>null</c>.</summary>
    Task<GitHubAppInstallation?> ReadInstallationAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the installation connection bound to a project, or <c>null</c>.</summary>
    Task<GitHubConnection?> ReadConnectionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Detaches the installation bound to a project, revoking its local connections.</summary>
    Task DisconnectAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a webhook delivery's signature and applies it. Every authentication failure is one
    /// refusal, so a caller cannot learn which installations this server knows.
    /// </summary>
    Task ReceiveWebhookAsync(
        string eventName,
        string signatureHeader,
        byte[] body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an <c>installation</c> webhook delivery after its signature was verified: a removed
    /// installation is marked removed and every local connection bound to it is revoked.
    /// </summary>
    Task HandleWebhookAsync(
        string eventName,
        string payload,
        CancellationToken cancellationToken = default);
}
