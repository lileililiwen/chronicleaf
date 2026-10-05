using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The delivery did not authenticate (401).
/// </summary>
/// <remarks>
/// <para>
/// Raised for a missing signature, a malformed signature, a signature computed over different bytes,
/// an unknown connection id, a connection with no subscription, and a subscription whose archived state
/// this server could not read. Every one of them is the same answer, and the reason is in the message:
/// the route exists to accept a signed delivery from GitHub, and anything else is a sender that did
/// not sign one.
/// </para>
/// <para>
/// It is not a 404 and it is not a 403. A 404 would confirm that the connection id in the URL exists,
/// which turns this route into a way of enumerating an installation's repository connections; a 403
/// would tell a sender its signature was <em>accepted in principle</em> and its request otherwise
/// unacceptable, which is a distinction an attacker can iterate against for free.
/// </para>
/// </remarks>
public sealed class GitHubCandidateSignatureInvalidException()
    : ApiException(
        GitHubCandidateError.SignatureInvalid,
        "That delivery did not authenticate. A signed webhook delivery is the only thing this route accepts.");

/// <summary>
/// The delivery authenticated but this build could not read it (422).
/// </summary>
/// <remarks>
/// Named separately from <see cref="GitHubCandidateSignatureInvalidException"/> on purpose: the sender
/// proved who it was, and the problem is what it sent. Collapsing the two would tell a correctly
/// configured sender that its signature was wrong when the signature was fine.
/// </remarks>
public sealed class GitHubCandidateDeliveryMalformedException(string detail)
    : ApiException(GitHubCandidateError.DeliveryMalformed, detail);

/// <summary>
/// The project has no repository connection (422).
/// </summary>
/// <remarks>
/// The same shape as the delivered github-evidence refusal, for the same reason: the project is real
/// and reachable, what is missing is something the owner supplies, and a 404 would send a client
/// looking for a project that does exist.
/// </remarks>
public sealed class GitHubCandidateConnectionMissingException()
    : ApiException(
        GitHubCandidateError.ConnectionMissing,
        "This project has no repository connection. Connect a public repository first.");

/// <summary>
/// GitHub could not answer, or answered with something this build cannot read (503).
/// </summary>
/// <remarks>
/// Deliberately not a 500: nothing here says this server is broken, and the same refusal is answerable
/// once GitHub is.
/// </remarks>
public sealed class GitHubCandidateUnavailableException()
    : ApiException(
        GitHubCandidateError.Unreachable,
        "GitHub could not be read right now. Nothing was proposed.");

/// <summary>
/// The delivery body is over the one-mebibyte ceiling (413).
/// </summary>
/// <remarks>
/// The ceiling is this package's, because a delivery is somebody else's document arriving unbidden
/// and the amount of it that is worth reading is small. It is refused before the body is buffered, so
/// an oversized delivery costs the sender a 413 and this server nothing.
/// </remarks>
public sealed class GitHubCandidateDeliveryTooLargeException(long limitBytes)
    : ApiException(GitHubCandidateError.DeliveryTooLarge, $"The delivery body exceeds the {limitBytes} byte limit.");
