using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The repository could not be read, and the reason is deliberately not distinguishable
/// (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// A repository that is private and a repository that does not exist produce the same status and the
/// same code, because GitHub answers them identically and because an owner must not be able to turn
/// this route into a way of discovering which private repositories exist. The message says the
/// repository could not be read and stops there.
/// </remarks>
public sealed class GitHubRepositoryUnavailableException()
    : ApiException(
        GitHubError.RepositoryUnavailable,
        "That repository could not be read. A repository that is not public and a repository that does not exist are the same answer.");

/// <summary>GitHub refused the credential, or required one and none was configured (422).</summary>
/// <remarks>
/// Permanent and answerable before anything is spent, so it is a validation failure rather than a
/// conflict: the owner has to change the credential, and repeating the request changes nothing.
/// </remarks>
public sealed class GitHubCredentialRejectedException()
    : ApiException(
        GitHubError.CredentialRejected,
        "GitHub refused the credential for this repository. Check the stored token, or connect a repository that is public.");

/// <summary>
/// The project has no repository connection (422).
/// </summary>
/// <remarks>
/// Named as a validation failure rather than a 404 on purpose: the milestone is real and reachable,
/// what is missing is something the owner has to supply, and a 404 would send a client looking for a
/// milestone that does not exist.
/// </remarks>
public sealed class GitHubConnectionMissingException()
    : ApiException(
        GitHubError.ConnectionMissing,
        "This project has no repository connection. Connect a public repository first.");

/// <summary>
/// GitHub could not answer, or answered with something this build cannot read (503).
/// </summary>
/// <remarks>
/// Deliberately not a 500: nothing here says the server is broken, and the same refusal is
/// answerable once GitHub is.
/// </remarks>
public sealed class GitHubUnavailableException()
    : ApiException(
        GitHubError.Unreachable,
        "GitHub could not be read right now. Nothing was collected.");
