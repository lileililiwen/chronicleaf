using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Api.Operations;

/// <summary>
/// The private operation routes (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// <para>
/// Every route here is private, and that is the access package's default rather than something these
/// handlers re-check: a matched <c>/api</c> endpoint without the public marker has already been given
/// an authenticated owner session, the trusted origin and the antiforgery value, and anything else
/// was refused before this code ran. The public allow-list is unchanged, and a route that did not
/// exist would still be private.
/// </para>
/// <para>
/// <b>Reading one operation</b> answers the question the UI asks and the tests assert against:
/// what is its status, how many attempts has it used, and does it hold a result. There is no route
/// here that sets a status, counts an attempt or writes a result. Those happen only inside the
/// worker's conditional statements, which is why a client - including an operator with a token - has
/// no way to make an operation look finished.
/// </para>
/// <para>
/// <b>Cancelling</b> is a mutation without a body: it takes no <c>If-Match</c> because it does not
/// edit a revision the owner authors, and it does not wait for a worker. The status becomes terminal
/// immediately, which is exactly what discards a result that arrives afterwards. Cancelling an
/// operation that has already finished is a 409 rather than a quiet no-op, so a caller is never told
/// a stop happened when it did not.
/// </para>
/// <para>
/// <b>Submitting work</b> exists because the enqueue path is a product behaviour and a contract
/// nothing else could exercise. It is private, it writes only the operation and its outbox row in one
/// transaction, and its body carries no status, so it cannot be used to fabricate a result.
/// </para>
/// </remarks>
public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/operations", CreateOperationAsync).RequireOwnerSession();
        endpoints.MapGet("/api/operations", ListOperationsAsync).RequireOwnerSession();
        endpoints.MapGet("/api/operations/{operationId:guid}", GetOperationAsync).RequireOwnerSession();
        endpoints.MapPost("/api/operations/{operationId:guid}/cancel", CancelOperationAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> CreateOperationAsync(
        CreateOperationRequest? request,
        IOperationService operations,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var scopeId = await ScopeIdAsync(access, cancellationToken).ConfigureAwait(false);
        var body = request ?? new CreateOperationRequest(null, null, null, null);

        Validate(body);

        var result = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                scopeId,
                body.Kind!.Trim(),
                Guid.Parse(body.TargetId!, CultureInfo.InvariantCulture),
                body.InputRevision!.Value,
                body.IdempotencyKey!.Trim()),
            cancellationToken).ConfigureAwait(false);

        var response = OperationResponse.From(result.Operation);
        var location = $"/api/operations/{result.Operation.Id.ToString("D")}";

        // A replay is a success, not a new resource, so it answers 200 with the original operation
        // and no Location. A caller that retries can therefore tell the two cases apart without
        // parsing anything.
        return result.Created
            ? Results.Created(location, response)
            : Results.Json(response);
    }

    private static async Task<IResult> ListOperationsAsync(
        HttpContext context,
        IOperationService operations,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var scopeId = await ScopeIdAsync(access, cancellationToken).ConfigureAwait(false);
        var items = await operations.ListAsync(scopeId, ReadLimit(context), cancellationToken).ConfigureAwait(false);

        return Results.Json(OperationListResponse.From(items));
    }

    private static async Task<IResult> GetOperationAsync(
        Guid operationId,
        IOperationService operations,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var scopeId = await ScopeIdAsync(access, cancellationToken).ConfigureAwait(false);
        var operation = await operations.GetAsync(scopeId, operationId, cancellationToken).ConfigureAwait(false);

        return Results.Json(OperationResponse.From(operation));
    }

    private static async Task<IResult> CancelOperationAsync(
        Guid operationId,
        IOperationService operations,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var scopeId = await ScopeIdAsync(access, cancellationToken).ConfigureAwait(false);
        var operation = await operations.CancelAsync(scopeId, operationId, cancellationToken).ConfigureAwait(false);

        return Results.Json(OperationResponse.From(operation));
    }

    private static async Task<Guid> ScopeIdAsync(IOwnerAccessService access, CancellationToken cancellationToken)
    {
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
        return identity.InstallationId;
    }

    /// <summary>
    /// Refuses a body this build cannot act on, in the delivered envelope's own shape: one code, one
    /// authored message, and a field error naming the field that is wrong.
    /// </summary>
    private static void Validate(CreateOperationRequest request)
    {
        var fields = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            fields["kind"] = ["A kind is required."];
        }

        if (string.IsNullOrWhiteSpace(request.TargetId) ||
            !Guid.TryParse(request.TargetId, CultureInfo.InvariantCulture, out _))
        {
            fields["targetId"] = ["A target id must be a UUID."];
        }

        if (request.InputRevision is null or < 1)
        {
            fields["inputRevision"] = ["An input revision of 1 or greater is required."];
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            fields["idempotencyKey"] = ["An idempotency key is required."];
        }

        if (fields.Count > 0)
        {
            throw new ValidationFailedException(fields);
        }
    }

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, CultureInfo.InvariantCulture, out var limit) ? limit : null;
    }
}
