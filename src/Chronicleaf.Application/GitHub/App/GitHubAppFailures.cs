using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The setup state is unknown, expired, bound to another session or already used (400).
/// </summary>
/// <remarks>
/// A replayed callback creates nothing: the state is consumed when it is accepted, so a second
/// arrival of the same state is refused exactly like a forged one.
/// </remarks>
public sealed class GitHubAppStateInvalidException()
    : ApiException(
        GitHubAppError.StateInvalid,
        "That setup did not complete. Start the connection again from the project.");

/// <summary>
/// The installation belongs to another scope (403).
/// </summary>
/// <remarks>
/// Deliberately a 403 rather than a 404: the installation exists and the request is well formed,
/// and what is refused is this scope's claim on it. A tenant that guesses another tenant's
/// installation id learns only that it cannot have it.
/// </remarks>
public sealed class GitHubAppInstallationMismatchException()
    : ApiException(
        GitHubAppError.InstallationMismatch,
        "That installation belongs to another workspace. Only its own workspace may connect it.");

/// <summary>GitHub knows no such installation (404).</summary>
public sealed class GitHubAppInstallationUnknownException()
    : ApiException(
        GitHubAppError.InstallationUnknown,
        "GitHub knows no such installation. Complete the installation first, then connect it.");

/// <summary>The repository is not in the installation's selection (404).</summary>
/// <remarks>
/// Deliberately the same status as an unknown installation: a repository outside the selection and
/// one that does not exist are the same answer from GitHub, and this package must not add a
/// second answer that lets a caller enumerate another account's repositories.
/// </remarks>
public sealed class GitHubAppRepositoryUnselectedException()
    : ApiException(
        GitHubAppError.RepositoryUnselected,
        "That repository is not in the installation's selection. Select it on the installation first.");

/// <summary>The server has no GitHub App configured (503).</summary>
public sealed class GitHubAppNotConfiguredException()
    : ApiException(
        GitHubAppError.NotConfigured,
        "No GitHub App is configured on this server. Ask the operator to configure one.");

/// <summary>GitHub could not answer the App's own calls (503).</summary>
/// <remarks>
/// Deliberately not a 500: nothing here says this server is broken, and the same request is
/// answerable once GitHub is. No provider body, token or path reaches the message.
/// </remarks>
public sealed class GitHubAppUnreachableException()
    : ApiException(
        GitHubAppError.Unreachable,
        "GitHub could not be read right now. Nothing was stored and nothing was collected.");

/// <summary>A webhook delivery that did not authenticate (401).</summary>
/// <remarks>
/// Unknown installation, missing signature and wrong signature are one answer, so a caller cannot
/// learn which installations this server knows by comparing codes.
/// </remarks>
public sealed class GitHubAppSignatureInvalidException()
    : ApiException(
        GitHubAppError.SignatureInvalid,
        "That delivery did not authenticate.");
