using Chronicleaf.Application.Capture;
using Chronicleaf.Application.Operations;
using Chronicleaf.Capture.Playwright;
using Chronicleaf.Capture.Sandbox;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Capture;

/// <summary>
/// Registers the capture stores, service, job handler and production browser (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as every other package's: two stores over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the request, and one handler
/// registered by its kind so the delivered registry finds it. Nothing here migrates and nothing here
/// substitutes a store, so a database without the screenshot-capture migration reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>The production provider is registered here and is the sandbox-backed one.</b> A test removes
/// <see cref="IScreenshotProvider"/> and adds a recording provider over the same interface; it does
/// not reconfigure the browser, so the seam is the only thing that differs between a scenario and
/// production. The real provider still refuses on a host that cannot confine a browser, and there is no
/// setting that makes it do otherwise.
/// </para>
/// </remarks>
public static class CaptureServiceCollectionExtensions
{
    public static IServiceCollection AddScreenshotCapture(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = new CaptureSandboxSettings();
        var policy = new CaptureSettings();

        // Bound, not defaulted past. The packaged Chromium path and the proxy address are deployment
        // facts, and reading them from configuration is the only way an operator can point this at the
        // image it actually built. The two settings objects are bound from the same section because
        // they answer one question - how this deployment may photograph a page - from two sides: the
        // browser side and the service side. Nothing read here can weaken the boundary: the settings
        // carry a browser path, a socket path, a proxy address, a deployment mode and a list of
        // expiring allowances, and there is no flag that turns the sandbox off.
        configuration.GetSection(CaptureSandboxSettings.SectionName).Bind(settings);
        configuration.GetSection(CaptureSettings.SectionName).Bind(policy);

        services.AddSingleton(settings);
        services.AddSingleton(policy);

        // The supervisor and the gate are singletons because they hold no per-request state: the probe is
        // read-only and the gate's only state is the clock it compares an expiry against. A capture job
        // builds its own policy, so two concurrent jobs cannot affect each other's decisions.
        services.AddSingleton<CaptureSandboxSupervisor>();
        services.AddSingleton<CaptureEgressGate>();

        services.AddScoped<IScreenshotCandidateStore, PostgresScreenshotCaptureStore>();
        services.AddScoped<ICaptureRequestStore, PostgresScreenshotCaptureStore>();
        services.AddScoped<IScreenshotCaptureService, ScreenshotCaptureService>();
        services.AddScoped<IOperationHandler, ScreenshotCaptureHandler>();

        services.AddSingleton<IScreenshotProvider>(provider => new PlaywrightScreenshotProvider(
            provider.GetRequiredService<CaptureSandboxSupervisor>(),
            provider.GetRequiredService<CaptureEgressGate>(),
            provider.GetRequiredService<CaptureSandboxSettings>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlaywrightScreenshotProvider>>()));

        return services;
    }
}
