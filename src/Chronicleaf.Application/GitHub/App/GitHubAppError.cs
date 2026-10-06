namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The envelope codes the GitHub App connection package answers with
/// (GITHUB-APP-CONNECTION-001/002).
/// </summary>
public static class GitHubAppError
{
    /// <summary>The setup state is unknown, expired or already used. No connection is created.</summary>
    public const string StateInvalid = "github_app_state_invalid";

    /// <summary>The installation belongs to another scope. The guessed id is refused, not shared.</summary>
    public const string InstallationMismatch = "github_app_installation_mismatch";

    /// <summary>GitHub knows no such installation. Nothing is stored.</summary>
    public const string InstallationUnknown = "github_app_installation_unknown";

    /// <summary>The repository is not in the installation's selection. Nothing is stored.</summary>
    public const string RepositoryUnselected = "github_app_repository_unselected";

    /// <summary>The server has no App configured, so no installation can be verified or minted.</summary>
    public const string NotConfigured = "github_app_not_configured";

    /// <summary>GitHub could not be read, or refused the App's own credential.</summary>
    public const string Unreachable = "github_app_unreachable";

    /// <summary>A delivery that did not authenticate. The installation id is never the secret.</summary>
    public const string SignatureInvalid = "github_app_signature_invalid";

    /// <summary>Every envelope code this package owns, so a test can assert the set is closed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        StateInvalid,
        InstallationMismatch,
        InstallationUnknown,
        RepositoryUnselected,
        NotConfigured,
        Unreachable,
        SignatureInvalid,
    ];
}
