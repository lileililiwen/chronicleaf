using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The alias is not one an operator registered, or the registered root's directory is not readable
/// (404).
/// </summary>
/// <remarks>
/// <para>
/// A 404, and the reason is that an unregistered alias names something this server will never read: it
/// is not a malformed request, it is a request for a root that does not exist here. A 422 would tell the
/// caller to change something, and the alias it sent was a perfectly good name of a root this operator
/// happens not to have configured.
/// </para>
/// <para>
/// The message names no alias and no directory, and it does not say which of the two cases this is. A
/// caller learns that the root it named is not configured here, which is the whole of what it is
/// entitled to learn; the set of configured roots is the operator's, and a probe that could tell a
/// configured root from an unconfigured one would be a way of enumerating a machine's filesystem.
/// </para>
/// </remarks>
public sealed class LocalGitRootUnknownException()
    : ApiException(
        LocalGitError.RootUnknown,
        "That root is not one the operator registered. This installation collects from the roots its operator configured.");

/// <summary>
/// The path leaves its registered root, by traversal or through a symbolic link (403).
/// </summary>
/// <remarks>
/// <para>
/// A 403 rather than a 422, and the distinction is the point. Nothing about the request is malformed
/// - the alias is real, the words are well formed, the path even has a plausible shape - and what is
/// refused is the <em>destination</em>. A 422 would invite a client to fix the path and resend an
/// identical request, and a 404 would send it looking for a repository that does exist.
/// </para>
/// <para>
/// The message says the path is outside the root and does not say where the root is, where the path
/// pointed, or what it found there. A caller that already guessed a path outside a root has learned
/// nothing from this answer that it did not already know, and this one deliberately tells it nothing
/// about the filesystem either.
/// </para>
/// </remarks>
public sealed class LocalGitOutsideRootException()
    : ApiException(
        LocalGitError.OutsideRoot,
        "That path is not inside the registered root. Chronicleaf reads only a repository inside a root its operator registered.");

/// <summary>
/// The path is not a Git repository this process can read (422).
/// </summary>
public sealed class LocalGitNotARepositoryException()
    : ApiException(
        LocalGitError.NotARepository,
        "That path is not a Git repository Chronicleaf can read, or Git could not read its metadata.");

/// <summary>
/// The Git program is not installed, or is not on this process's PATH (503).
/// </summary>
/// <remarks>
/// Deliberately not a 500. Nothing here says this product is broken; it says a dependency is absent,
/// and the same request would be served the moment an operator installed Git.
/// </remarks>
public sealed class LocalGitUnavailableException()
    : ApiException(
        LocalGitError.GitUnavailable,
        "Git is not available to this server, so nothing was collected. Nothing about the repository was read.");

/// <summary>
/// A Git process outlived this package's own bound and was killed (503).
/// </summary>
/// <remarks>
/// Not a 504. The delivered dependency-timeout code is for a dependency that is answering slowly; this
/// is a process this server started, on a repository the operator registered, and the bound is a
/// constant of this package rather than somebody else's latency promise.
/// </remarks>
public sealed class LocalGitTimeoutException()
    : ApiException(
        LocalGitError.TimedOut,
        "Reading that repository took longer than Chronicleaf allows, so the read was stopped and nothing was collected.");

/// <summary>
/// The project has no local repository registered (422).
/// </summary>
/// <remarks>
/// Named as a validation failure rather than a 404 for the delivered GitHub package's reason, which
/// still holds: the milestone is real and reachable, and what is missing is something the owner has to
/// supply.
/// </remarks>
public sealed class LocalGitRepositoryMissingException()
    : ApiException(
        LocalGitError.RepositoryMissing,
        "This project has no local repository registered. Register one of the operator's roots first.");

/// <summary>
/// Local collection is refused in this deployment (403).
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary scenario of LOCAL-GIT-EVIDENCE-002, and it is checked before anything else -
/// before the alias is resolved, before a path is read, before a process is started. A shared Hosted
/// machine holds other tenants' directories, and a registered root there names somebody else's disk;
/// no operator configuration can make that this caller's evidence, so the answer is a refusal rather
/// than an empty catalogue.
/// </para>
/// <para>
/// The message is deliberately silent about which roots exist. A tenant that learns the alias list
/// learns the shape of the machine it shares.
/// </para>
/// </remarks>
public sealed class LocalGitNotAvailableInHostedException()
    : ApiException(
        LocalGitError.NotAvailableInHosted,
        "Reading a local repository is not available in this deployment, so no directory was searched.");
