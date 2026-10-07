using Chronicleaf.Application.AgencyReports;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.AgencyReports;

/// <summary>
/// Registers the agency weekly-report service (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// The service over the delivered agency, milestone, evidence, media and export contracts,
/// plus this package's own report-row store. Nothing here migrates: reports are new rows in
/// this package's own <c>weekly_reports</c> table, so a database without the weekly-report
/// migration reports <c>schema_mismatch</c> at readiness and every generation fails, which
/// is the honest outcome.
/// <para>
/// <b>Nothing here can generate prose, send mail or invoice.</b> There is no provider, no
/// credential and no transport behind this registration: generation re-renders confirmed
/// milestones, evidence heads and owner words through a deterministic template, approval is
/// this package's own stamp, and the export serves reviewed bytes. Registration is the whole
/// of this package's switch.
/// </para>
/// </remarks>
public static class WeeklyReportServiceCollectionExtensions
{
    public static IServiceCollection AddAgencyWeeklyReports(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IWeeklyReportStore, PostgresWeeklyReportStore>();
        services.AddScoped<IWeeklyReportService, WeeklyReportService>();

        return services;
    }
}
