using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.ContentVariants;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private variant routes (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, both private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A variant is a draft a human has not approved, so it is readable only by the owner it
/// belongs to and by nobody else.
/// </para>
/// <para>
/// <b>The derive answers 201 with the new asset, not a job.</b> Deriving is deterministic - no
/// provider, no repair call, no queue - so there is nothing to wait for and no operation to watch.
/// The revisions the owner was looking at travel in the body, and both must still be current: a
/// body that names a moved evidence or voice head is refused rather than rendered against stale
/// provenance, because a variant of stale content is stale.
/// </para>
/// <para>
/// <b>No route here can approve anything, and no route here writes a repository.</b> The derivative
/// starts life as a draft with no approval row, and the readme-update format is a fenced proposal
/// block: this package holds no repository reference and performs no write.
/// </para>
/// </remarks>
public static class ContentVariantEndpoints
{
    public static IEndpointRouteBuilder MapContentVariantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/assets", DeriveVariantAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/milestones/{milestoneId:guid}/assets", ListVariantsAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> DeriveVariantAsync(
        Guid milestoneId,
        HttpContext context,
        DeriveVariantWireRequest? request,
        IContentVariantService variants,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then
            // refuses by name rather than being interpreted as a request for a default format. A
            // variant with no named format is a variant nobody asked for.
            throw new ValidationFailedException(
                "type",
                "The format must be one this product derives.");
        }

        var derived = await variants
            .DeriveAsync(
                owner,
                milestoneId,
                new DeriveVariantRequest(
                    request.Type,
                    ParseOptionalId(request.SourceAssetId, "sourceAssetId"),
                    request.EvidenceRevision,
                    request.VoiceRevision),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new DerivedVariantResponse(
                derived.AssetId,
                derived.Type,
                derived.Revision,
                derived.BodyHash,
                derived.SourceAssetId,
                derived.SourceRevision,
                derived.EvidenceRevision,
                derived.VoiceRevision),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListVariantsAsync(
        Guid milestoneId,
        HttpContext context,
        IContentVariantService variants,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // CONTENT-PACK-PANELS-004. The pack picker reads this same delivered
        // route with ?kinds=packs; every older caller reads it without the
        // query and gets exactly the variant formats it always got. Any other
        // value is a 422 naming the field rather than a silent default.
        var kinds = (context.Request.Query["kinds"].FirstOrDefault() ?? string.Empty).Trim();
        bool includePacks = kinds switch
        {
            "" => false,
            "packs" => true,
            _ => throw new ValidationFailedException(
                "kinds",
                "The asset kinds are \"packs\" or, when unnamed, the variant formats."),
        };

        var listed = await variants.ListAsync(owner, milestoneId, cancellationToken, includePacks).ConfigureAwait(false);

        return Results.Json(new VariantListResponse(
            [.. listed.Select(entry => new VariantSummaryResponse(
                entry.AssetId,
                entry.Type,
                entry.CurrentRevision,
                entry.BodyHash,
                entry.EvidenceRevision,
                entry.VoiceRevision,
                entry.UpdatedAt))]));
    }

    private static Guid? ParseOptionalId(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out var parsed))
        {
            // An id is an identifier, so a value that is not one is a malformed field. Nothing about
            // the value is echoed back.
            throw new ValidationFailedException(field, "This field is an identifier.");
        }

        return parsed;
    }

    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }
}

/// <summary>
/// What an owner sends when they derive a variant from an approved story.
/// </summary>
/// <remarks>
/// Strings rather than parsed identifiers, because the route parses and refuses them by name. A
/// <c>Guid</c> binding would have the deserializer answer 400 for a malformed id where the rest of
/// this repository answers 422 with a field name an owner can act on.
/// </remarks>
internal sealed record DeriveVariantWireRequest(
    string? Type,
    string? SourceAssetId,
    long EvidenceRevision,
    long VoiceRevision);

/// <summary>One derived variant, as the derive route answers it.</summary>
internal sealed record DerivedVariantResponse(
    Guid AssetId,
    string Type,
    long Revision,
    string BodyHash,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision);

/// <summary>One variant asset in a milestone's list.</summary>
internal sealed record VariantSummaryResponse(
    Guid AssetId,
    string Type,
    long CurrentRevision,
    string BodyHash,
    long EvidenceRevision,
    long VoiceRevision,
    DateTimeOffset UpdatedAt);

/// <summary>The variants a milestone holds, in picker order.</summary>
internal sealed record VariantListResponse(IReadOnlyList<VariantSummaryResponse> Variants);
