using System.Text.Json.Serialization;
using Chronicleaf.Application.Readiness;
using Envelope = Chronicleaf.Application.Common.ApiError;
using EnvelopeStatus = Chronicleaf.Application.Common.ApiErrorStatus;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Chronicleaf.Api.Common;

/// <summary>
/// <c>GET /api/ready</c>: honest dependency readiness, deliberately separate from the
/// process-only liveness contract in <c>GET /api/health</c>.
/// </summary>
/// <remarks>
/// The endpoint answers from a real connection attempt against the configured database. It is
/// 200 <c>{"status":"ready"}</c> only when the expected schema is reachable and current, and
/// otherwise 503 with <c>database_unavailable</c> or <c>schema_mismatch</c>. A process whose
/// database is down, misconfigured or behind a migration stays alive and is still reported as
/// live by <c>GET /api/health</c>; it is simply not ready, and it never reports readiness from a
/// store that is not the real one.
/// </remarks>
public static class ReadinessEndpoint
{
    /// <summary>The success body. Field names are pinned so the contract cannot drift.</summary>
    public sealed record ReadyResponse([property: JsonPropertyName("status")] string Status)
    {
        public static ReadyResponse Instance { get; } = new("ready");
    }

    public static async Task<IResult> CheckAsync(
        IReadinessProbe probe,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        ReadinessReport report = await probe.CheckAsync(cancellationToken).ConfigureAwait(false);

        if (report.IsReady)
        {
            return Results.Json(ReadyResponse.Instance);
        }

        var statusCode = EnvelopeStatus.ForCode(report.Code);

        // Serialized by the envelope itself, so readiness failures and middleware failures
        // produce byte-identical shapes: the optional fieldErrors is omitted, never null.
        return Results.Text(
            Envelope.Create(report.Code, report.Message, context.TraceIdentifier).ToJson(),
            "application/json; charset=utf-8",
            statusCode: statusCode);
    }
}
