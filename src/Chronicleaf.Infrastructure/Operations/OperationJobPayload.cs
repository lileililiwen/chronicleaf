using System.Text.Json;
using Platform.Jobs;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The one payload shape this build dispatches, built on the platform's <see cref="JobPayload"/>.
/// </summary>
/// <remarks>
/// <para>
/// The contract and the record are the shared platform's: <c>Platform.Jobs.IJobDispatcher</c> and
/// <c>Platform.Jobs.JobPayload</c> come from the committed feed, and this file adds no interface,
/// no record and no field of its own. What it does own is the single place a payload is built and the
/// single place one is read back, which is what makes "the queue carries the operation id and nothing
/// else" true by construction rather than by review: a serialized job is a job name and a single GUID.
/// </para>
/// <para>
/// A payload that does not carry a readable operation id is refused. A handler that guessed - read the
/// first argument whatever it was, or defaulted to <see cref="Guid.Empty"/> - would run a claim
/// against a row nobody asked about, and the row is the only authority on what happens next.
/// </para>
/// </remarks>
public static class OperationJobPayload
{
    /// <summary>The one job name this build dispatches.</summary>
    public const string Name = "chronicleaf.operation";

    /// <summary>The one argument this build ever puts in a payload.</summary>
    public const string OperationIdArgumentName = "operationId";

    /// <summary>Builds the payload for one operation.</summary>
    public static JobPayload ForOperation(Guid operationId) =>
        JobPayload.Create(
            Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [OperationIdArgumentName] = operationId.ToString(),
            });

    /// <summary>
    /// Reads the operation id back out of a dispatched payload, or throws when the payload is not one
    /// of ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value survives a round trip through the engine as JSON, so it arrives in whichever shape
    /// JSON deserialization produced. <c>HangfireJobExecutor</c> serializes with
    /// <c>JsonSerializerDefaults.Web</c> and normalizes what it reads back, which turns the string
    /// this method writes into a <see cref="string"/> for the handler; a caller that hands a payload
    /// straight to the handler instead of through the engine still has the raw
    /// <see cref="JsonElement"/>, and a caller that never serialized it has the original string. All
    /// three are accepted by conversion, and anything else is refused rather than guessed at.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The payload is null, carries no <see cref="OperationIdArgumentName"/>, or carries a value that
    /// is not a <see cref="Guid"/>.
    /// </exception>
    public static Guid ReadOperationId(JobPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Arguments is null ||
            !payload.Arguments.TryGetValue(OperationIdArgumentName, out var value))
        {
            throw new InvalidOperationException(
                $"A {payload.Name} payload must carry a {OperationIdArgumentName} argument.");
        }

        return value switch
        {
            Guid operationId => operationId,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String &&
                                    Guid.TryParse(element.GetString(), out var parsed) => parsed,
            _ => throw new InvalidOperationException(
                $"A {payload.Name} payload must carry a {OperationIdArgumentName} argument that is a Guid."),
        };
    }
}
