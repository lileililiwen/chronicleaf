using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a real disposable PostgreSQL and a real
/// temporary key file, with <see cref="RecordingLlmProvider"/> registered as the only
/// <see cref="ILLMProvider"/> (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Everything except the provider adapter is the production path: the real store, the real service,
/// the real routes, the real access boundary, the real evidence store, the real voice service and the
/// real credential vault over a real key file. Only the adapter is substituted, and it is substituted
/// through the same registration seam the five real adapter packages will use - so a scenario that
/// passes here is evidence about Chronicleaf's own behaviour and is explicitly not evidence that any
/// provider works.
/// </para>
/// <para>
/// <b>The key file is real, outside the repository, and per scenario.</b> A generation preview names a
/// stored credential by id, so the scenarios need a genuinely stored credential to name - which means
/// a real one, sealed by the real cipher under a real key. That is what makes the assertion that the
/// stored snapshot contains no secret a real one: the plaintext and the ciphertext both exist, and
/// the snapshot is compared against both.
/// </para>
/// <para>
/// <see cref="Provider"/> exposes the double so a scenario can read the requests it received. It is
/// the same instance the application resolved, not a second one.
/// </para>
/// </remarks>
public sealed class LlmProviderApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly RecordingLlmProvider? _provider;
    private readonly GenerationCallOptions? _callOptions;
    private readonly LogCapture _logs;

    private LlmProviderApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        RecordingLlmProvider? provider,
        GenerationCallOptions? callOptions,
        LogCapture logs)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _provider = provider;
        _callOptions = callOptions;
        _logs = logs;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>Every log line this host has written, so a scenario can prove what did not reach it.</summary>
    public LogCapture Logs => _logs;

    /// <summary>
    /// The double the application resolved, so a scenario can read what the transport received. Null
    /// on a host started without one, which is the production-registration scenario's own condition.
    /// </summary>
    public RecordingLlmProvider? Provider => _provider;

    /// <summary>Starts a host with a test double registered as the adapter for a provider slot.</summary>
    public static LlmProviderApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        RecordingLlmProvider provider,
        GenerationCallOptions? callOptions = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new LlmProviderApiHost(connectionString, clock, keyFile, provider, callOptions, new LogCapture());
    }

    /// <summary>
    /// Starts a host with the production composition root's registrations and no adapter at all.
    /// </summary>
    /// <remarks>
    /// This is the fixture that makes "the double is not reachable in production" a checked fact
    /// rather than a reviewer's assumption: the very same provider id, the very same model and the
    /// very same owner session answer 422 <c>provider_not_installed</c> here, because no
    /// <see cref="ILLMProvider"/> was added and no configuration value can add one.
    /// </remarks>
    public static LlmProviderApiHost StartProduction(string connectionString, TimeProvider clock, string? keyFile) =>
        new(connectionString, clock, keyFile, provider: null, callOptions: null, new LogCapture());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_callOptions is not null)
            {
                // The call budget's own seam, and the only way a scenario may move it. Production
                // leaves it registered by AddAIGeneration at the published default, and no
                // configuration key names it.
                services.RemoveAll<GenerationCallOptions>();
                services.AddSingleton(_callOptions);
            }

            if (_provider is not null)
            {
                // The one substitution, through the seam the real adapter packages use. It is
                // inserted first rather than appended, and that ordering is load-bearing: the
                // delivered resolver keeps the first adapter per slot, and llm-ollama-adapter
                // registered a real adapter for this suite's slot in the production composition
                // root. A double appended after it would be shadowed by the real transport, and
                // every scenario here would silently stop testing anything. Inserted first, the
                // double is the one the application resolves - which is what makes the scenarios
                // evidence about the contract rather than about a missing adapter. It is a
                // singleton so the instance a scenario holds is the one the application resolved.
                services.Insert(0, ServiceDescriptor.Singleton<ILLMProvider>(_provider));
            }
        });
    }
}
