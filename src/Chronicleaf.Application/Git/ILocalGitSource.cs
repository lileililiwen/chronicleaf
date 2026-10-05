using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The local transport's own seam, which the shared <see cref="IGitEvidenceSource"/> contract does not cover
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same two reads, declared separately, and that is the whole reason this interface exists.</b>
/// <see cref="LocalGitEvidenceSource"/> satisfies <see cref="IGitEvidenceSource"/> as well, which is what
/// makes the two transports interchangeable at the level of a collected result, and the delivered GitHub
/// registration claims that service as <c>IGitEvidenceSource</c> alone.
/// </para>
/// <para>
/// One service type can only be the single <c>IGitEvidenceSource</c> a container resolves, so a second
/// registration of the local transport under the same contract would shadow the hosted one and silently move
/// every GitHub collection onto a directory. Rather than let the last registration win by accident, the
/// local transport is reached through this interface, which only this package resolves. A test that wants a
/// scripted local transport replaces <b>this</b> and leaves the hosted one exactly as production registered
/// it - the two seams are separate, so a scenario about one can never have stopped testing the other.
/// </para>
/// <para>
/// The shapes are the delivered ones, deliberately: the same <see cref="GitEvidenceRequest"/> with the same
/// caps, the same <see cref="GitEvidenceResult"/> with the same exclusions, truncations and unavailable
/// records. That is what lets one set of evidence items, one hashing policy and one append serve both, and
/// it is why a bundle cannot tell which transport produced a fact except by the source reference it carries.
/// </para>
/// <para>
/// The credential parameter is present for contract parity and is refused by the implementation. A local
/// repository is read off the operator's own disk with the operator's own Git; there is no remote, no host
/// and no token, and a caller that supplied one was expecting something else entirely.
/// </para>
/// </remarks>
public interface ILocalGitSource
{
    /// <summary>
    /// Confirms the named path is a Git repository inside a registered root, and reports what it says about
    /// itself: its HEAD, and the branch that HEAD names when it names one. This is the check a registration is
    /// validated with, and it never writes.
    /// </summary>
    Task<GitEvidenceResult> DescribeRepositoryAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the requested refs to immutable revisions and collects the bounded committed facts between
    /// them. Both refs are resolved before any fact is read, and the head is resolved again at the end.
    /// </summary>
    Task<GitEvidenceResult> FetchAsync(
        RepoRef repository,
        GitEvidenceRequest request,
        string? credential,
        CancellationToken cancellationToken = default);
}
