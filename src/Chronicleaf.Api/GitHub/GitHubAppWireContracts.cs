using System.Globalization;
using System.Text.Json.Serialization;

namespace Chronicleaf.Api.GitHub;

/// <summary>
/// The setup an App connection is completed with (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// The state is a single-use nonce bound to the session it was issued to. It carries no
/// installation, no repository and no secret: it only proves the callback that carries it back
/// started here, within the last ten minutes.
/// </remarks>
public sealed record GitHubAppSetupResponse(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt);

/// <summary>
/// The body an installation connection is stored from (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// Two numbers and the setup state, and the absence of everything else is the security property:
/// no token, no secret, no URL and no account name. The server reads the installation and the
/// repository itself, through the App, before it stores anything.
/// </remarks>
public sealed record GitHubAppConnectRequest(
    [property: JsonPropertyName("installationId")] long? InstallationId,
    [property: JsonPropertyName("repositoryId")] long? RepositoryId,
    [property: JsonPropertyName("state")] string? State);

/// <summary>
/// The wire shape of an installation connection (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// What a client is told: the two names the server read, the installation and repository it
/// verified, and the account they belong to. What it is not told: any token - none is stored and
/// none could be returned - and no URL.
/// </remarks>
public sealed record GitHubAppConnectionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch,
    [property: JsonPropertyName("repositoryId")] long? RepositoryId,
    [property: JsonPropertyName("installationId")] long InstallationId,
    [property: JsonPropertyName("accountLogin")] string AccountLogin,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The answer to a verified webhook delivery: received and recorded, never the binding itself.
/// </summary>
public sealed record GitHubAppWebhookReceipt(
    [property: JsonPropertyName("received")] bool Received);
