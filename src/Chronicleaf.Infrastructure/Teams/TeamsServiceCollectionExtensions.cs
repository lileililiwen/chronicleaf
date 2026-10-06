using Chronicleaf.Application.Teams;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Teams;

/// <summary>
/// Registers the Hosted team invitation and project-role boundary
/// (TEAM-PROJECT-PERMISSIONS-001/002).
/// </summary>
/// <remarks>
/// The service is request-scoped like the tenancy service it reads through, so the session it
/// validates is the session the boundary authenticated. Registration is the whole rollback
/// lever: removing it disables only this feature's routes, and the stored invitations, grants
/// and audit rows stay exactly as they are.
/// </remarks>
public static class TeamsServiceCollectionExtensions
{
    public static IServiceCollection AddTeamProjectPermissions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<TeamPermissionService>();
        services.AddScoped<ITeamPermissionService>(provider => provider.GetRequiredService<TeamPermissionService>());

        return services;
    }
}
