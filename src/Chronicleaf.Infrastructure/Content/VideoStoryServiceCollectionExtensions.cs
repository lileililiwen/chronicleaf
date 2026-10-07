using Chronicleaf.Application.VideoStory;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// Registers the video-story service (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One service over the delivered stores, unit of work and audit trail. Nothing here migrates and
/// nothing here substitutes a store: scripts are new rows in the drafts package's own
/// <c>content_assets</c> and <c>content_revisions</c> tables, so a database without the
/// grounded-markdown-drafts migration reports <c>schema_mismatch</c> at readiness and every
/// generation fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can synthesize, render or publish anything.</b> There is no provider, no
/// renderer, no encoder, no credential and no publisher behind this registration: generation
/// validates an owner's shot list against stored rows, approval stays the review package's
/// table, and publication is a refusal rather than a call. Registration is the whole of this
/// package's switch.
/// </para>
/// </remarks>
public static class VideoStoryServiceCollectionExtensions
{
    public static IServiceCollection AddVideoStory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVideoStoryService, VideoStoryService>();

        return services;
    }
}
