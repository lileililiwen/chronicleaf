using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Secrets;

namespace Chronicleaf.Api.Secrets;

/// <summary>The body of a save: the value, and nothing else (CREDENTIAL-VAULT-001).</summary>
/// <remarks>
/// <para>
/// The value is the only field. There is no provider in the body - it is the route - no scope, no
/// actor, no id and no revision: those come from the session, the path and the <c>If-Match</c>
/// header, and a body that could carry one of them would be a body that could ask for a different
/// installation's credential.
/// </para>
/// <para>
/// The shape is intentionally the minimum. A "test the credential" flag, a display hint or a
/// remember-me option would all be a value derived from the secret travelling to the browser.
/// </para>
/// </remarks>
public sealed record CredentialSaveRequestBody(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("expectedRevision")] long? ExpectedRevision);

/// <summary>
/// What a caller is told about one provider (CREDENTIAL-VAULT-002).
/// </summary>
/// <remarks>
/// Four fields, and no field can carry a secret or anything derived from one: no masked value, no
/// length, no fingerprint, no last four. The current revision travels in the <c>ETag</c> header
/// rather than here, which is where a client presents it from on the next write.
/// </remarks>
public sealed record CredentialMetadataResponse(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt)
{
    public static CredentialMetadataResponse From(CredentialStatus status) =>
        new(status.Id, status.Provider, status.Configured, status.UpdatedAt);
}

/// <summary>One item of the settings list: the same four fields, for each configured provider.</summary>
public sealed record CredentialListItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("configured")] bool Configured,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt)
{
    public static CredentialListItem From(CredentialMetadata metadata) =>
        new(metadata.Id, metadata.Provider, Configured: true, metadata.UpdatedAt);
}

/// <summary>One bounded page of the scope's configured providers, with the cursor that continues it.</summary>
public sealed record CredentialListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<CredentialListItem> Items,
    [property: JsonPropertyName("nextCursor")] string? NextCursor)
{
    public static CredentialListResponse From(CredentialPage page) =>
        new([.. page.Credentials.Select(CredentialListItem.From)], page.NextCursor);
}

/// <summary>
/// Turns the request shapes into application inputs and application results back into responses, so
/// the application layer stays free of HTTP.
/// </summary>
public static class CredentialMapping
{
    /// <summary>
    /// Reads the revision a save is conditional on. The header is the precondition, and the body's
    /// <c>expectedRevision</c> has to agree with it.
    /// </summary>
    /// <remarks>
    /// Required (428) and authoritative, for the same reason the delivered evidence append is: two
    /// answers to one question would let a client act on a revision other than the one it read, and
    /// the failure would only surface as a conflict after a request that looked consistent. A body
    /// naming a different revision is a 422 naming <c>expectedRevision</c>, and the request is
    /// refused rather than resolved in favour of either.
    /// <para>
    /// 0 is the token that means "this scope has nothing stored for this provider yet", which is the
    /// same empty-state token the delivered evidence surface publishes.
    /// </para>
    /// </remarks>
    public static long RequiredRevision(HttpContext context, long? bodyRevision)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException("credential");
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the credential revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        if (bodyRevision is not null && bodyRevision.Value != revision)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        return revision;
    }

    /// <summary>The entity-tag for a revision, in the form <c>If-Match</c> accepts back.</summary>
    public static string EntityTag(long revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Builds the save input. The value is passed straight through to the vault, which is the only
    /// component that seals it; nothing here keeps a copy, logs it or reflects it into a response.
    /// </summary>
    public static CredentialSaveRequest ToSave(
        OwnerScope owner,
        string provider,
        CredentialSaveRequestBody? request,
        long expectedRevision) =>
        new(owner, provider, request?.Value ?? string.Empty, expectedRevision);
}
