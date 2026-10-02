namespace Chronicleaf.Application.Operations;

/// <summary>
/// Hands a unit of work to the scheduling engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this contract is local.</b> The design's reconnaissance section names a sibling
/// <c>Platform.Jobs</c> 0.1.0 as the owner of this interface and asks for adoption "through package
/// restore". That was attempted and did not succeed: the package resolves on no feed this repository
/// is configured against - only nuget.org, the GitHub Packages feed and a local feed directory - and
/// the one copy present is a pre-seeded entry in this machine's NuGet cache rather than a published
/// artifact. A restore that only works because a cache happens to be populated is a build coupled to
/// one person's machine, which is the exact outcome the design forbids, so no <c>PackageReference</c>
/// and no absolute or sibling-relative <c>ProjectReference</c> was added. This contract is therefore
/// Chronicleaf's own, declared to the same shape the sibling declares
/// (<c>EnqueueAsync(JobPayload, CancellationToken)</c> over a payload record that carries a name and
/// arguments), so adopting the published package later is a change to the using clauses and to this
/// file, not a change to every call site. The limitation is recorded in the change report; nothing
/// here claims an adoption that was not verified.
/// </para>
/// <para>
/// A payload carries the operation id and nothing else. That is the whole privacy property of the
/// queue: a serialized job is a single GUID, so no credential, token, connection string or piece of
/// evidence can travel in one, whatever an engine logs or an operator inspects.
/// </para>
/// </remarks>
public interface IJobDispatcher
{
    /// <summary>
    /// Enqueues the payload for execution. Implementations read the current time from the injected
    /// clock rather than <c>DateTimeOffset.UtcNow</c>, so scheduling stays deterministic in tests.
    /// </summary>
    /// <param name="payload">The payload to dispatch.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    Task EnqueueAsync(JobPayload payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues the payload to run no earlier than <paramref name="delay"/>. This is how the bounded
    /// retry backoff is expressed: the engine owns the delay, so a process that stops during a backoff
    /// still runs the attempt when it comes back.
    /// </summary>
    /// <param name="payload">The payload to dispatch.</param>
    /// <param name="delay">How long to wait before the attempt may run.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    Task EnqueueAsync(JobPayload payload, TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// The documented payload an <see cref="IJobDispatcher"/> accepts.
/// </summary>
/// <remarks>
/// <see cref="Arguments"/> is deliberately untyped and never inspected by the queue. In this build it
/// holds exactly one entry, <see cref="OperationIdArgumentName"/>. The name is validated as
/// non-empty because a job with no name cannot be dispatched at all, which is a programming error
/// rather than a runtime condition to tolerate.
/// </remarks>
/// <param name="Name">The job name; never empty.</param>
/// <param name="Arguments">Engine-specific arguments. In this build: the operation id, and nothing else.</param>
public sealed record JobPayload(string Name, IReadOnlyDictionary<string, object?>? Arguments = null)
{
    /// <summary>The single argument this build ever puts in a payload.</summary>
    public const string OperationIdArgumentName = "operationId";

    /// <summary>Creates a payload, refusing a name the engine could not dispatch.</summary>
    /// <exception cref="ArgumentException">The name is null, empty or whitespace.</exception>
    public static JobPayload Create(string name, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A job name must be a non-empty string.", nameof(name));
        }

        return new JobPayload(name, arguments);
    }

    /// <summary>
    /// The payload for one operation. This is the only way one is built anywhere in the repository,
    /// which is how "the queue carries the operation id and nothing else" stays true by construction
    /// rather than by review.
    /// </summary>
    public static JobPayload ForOperation(Guid operationId) =>
        Create(
            OperationJobName,
            new Dictionary<string, object?>(StringComparer.Ordinal) { [OperationIdArgumentName] = operationId });

    /// <summary>Reads the operation id back, or throws when the payload is not one of ours.</summary>
    public Guid ReadOperationId()
    {
        if (Arguments is null ||
            !Arguments.TryGetValue(OperationIdArgumentName, out var value) ||
            value is not Guid operationId)
        {
            throw new InvalidOperationException(
                $"A {Name} payload must carry exactly one {OperationIdArgumentName} argument.");
        }

        return operationId;
    }

    /// <summary>The one job name this build dispatches.</summary>
    public const string OperationJobName = "chronicleaf.operation.execute";
}
