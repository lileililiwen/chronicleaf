using System.Text;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL and a real temporary operator key file, with a scripted
/// <see cref="ILLMProvider"/> registered for one provider slot (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Everything except the provider answer is the production path: the real stores, the real service, the
/// real job handler on the real Hangfire server, the real routes, the real access boundary, the real
/// evidence store, the real voice service, the real consent handshake and the real credential vault
/// over a real key file. Only the provider's <em>answer</em> is the test's.
/// </para>
/// <para>
/// <b>The slot is <c>ollama</c>, and that choice is load-bearing and asserted.</b> The production
/// composition root registers a real adapter for <c>openai</c>, <c>gemini</c> and <c>anthropic</c>,
/// and the delivered resolver takes the <em>first</em> registration for a slot. A double registered
/// for one of those would therefore be shadowed by the real adapter: every scenario in this file would
/// receive a real provider call instead of its scripted answer, the scripted answer would never be
/// read, and the suite would either fail loudly or - worse, in a suite that did not assert a call
/// count - pass without having tested anything.
/// <c>ollama</c> is a slot no production adapter claims, and
/// <c>the_slot_this_double_owns_is_claimed_by_no_production_adapter</c> fails the moment a future
/// adapter package takes it. That test is the guard against this exact defect returning, and it is the
/// one assertion in this file that exists about the fixture rather than about the product.
/// </para>
/// <para>
/// The key file is real, outside the repository, and per scenario. A draft names a stored credential by
/// id, so the scenarios need a genuinely stored one - which is what makes "the stored draft request
/// carries no secret" a real assertion rather than a vacuous one.
/// </para>
/// <para>
/// The automated dispatch loop is off and the polling is fast, on the same terms as the delivered
/// GitHub host: the loop is the seam between "the request committed" and "the queue knows", and a
/// scenario has to be able to place a draft on a chosen side of it.
/// </para>
/// </remarks>
public sealed class GroundedMarkdownDraftsApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _keyFile;
    private readonly ScriptedDraftProvider? _provider;
    private readonly LogCapture _logs;

    private GroundedMarkdownDraftsApiHost(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        ScriptedDraftProvider? provider,
        LogCapture logs)
    {
        _connectionString = connectionString;
        _clock = clock;
        _keyFile = keyFile;
        _provider = provider;
        _logs = logs;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>Every log line this host has written, so a scenario can prove what did not reach it.</summary>
    public LogCapture Logs => _logs;

    /// <summary>
    /// The double the application resolved, so a scenario can read what the transport received. Null on a
    /// host started without one, which is the production-registration scenario's own condition.
    /// </summary>
    public ScriptedDraftProvider? Provider => _provider;

    /// <summary>Starts a host with the scripted double as the adapter for the ollama slot.</summary>
    public static GroundedMarkdownDraftsApiHost Start(
        string connectionString,
        TimeProvider clock,
        string? keyFile,
        ScriptedDraftProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new GroundedMarkdownDraftsApiHost(
            connectionString,
            clock,
            keyFile,
            provider,
            new LogCapture());
    }

    /// <summary>
    /// Starts a host with the production composition root and no double at all.
    /// </summary>
    /// <remarks>
    /// This is what makes "the scripted answer is not reachable in production" a checked fact rather
    /// than a reviewer's assumption: the very same provider id, model, preview and owner session answer
    /// 422 <c>provider_not_installed</c> here, because nothing was added and no configuration value can
    /// add it.
    /// </remarks>
    public static GroundedMarkdownDraftsApiHost StartProduction(
        string connectionString,
        TimeProvider clock,
        string? keyFile) =>
        new(connectionString, clock, keyFile, provider: null, new LogCapture());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a draft can be staged either side of
        // the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // A poll interval, not a behaviour: the queue, the workers, the claiming, the lease and the
        // conditional commit are all production's.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

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

            if (_provider is not null)
            {
                // The one substitution, through the same registration seam the real adapter packages
                // will use. It is a singleton so the instance a scenario holds is the one the
                // application resolved - and the resolver reports which adapter it actually chose, so
                // a scenario can prove this was the one that ran rather than inferring it from an
                // answer it happened to recognise.
                services.AddSingleton<ILLMProvider>(_provider);
            }
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass, the loop's own body called by hand.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return results;
    }

    /// <summary>
    /// The slots this host's production registrations actually resolve to an adapter, by name.
    /// </summary>
    /// <remarks>
    /// Read from the delivered resolver, which is the same object the service resolves through. It is
    /// what makes the slot-ownership assertion a fact about the composition root rather than a fact about
    /// this fixture: a future adapter package that claims <c>ollama</c> changes what this reports, and
    /// the scenario that reads it fails.
    /// </remarks>
    public IReadOnlyList<string> InstalledAdapters()
    {
        using var scope = Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<ILLMProviderResolver>().Installed;
    }
}

/// <summary>
/// The scripted provider the grounded-draft scenarios drive, for the <c>ollama</c> slot
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>It answers by phase, not by call index.</b> A draft is two calls and possibly three, and which
/// call a scenario's next answer belongs to depends on whether a repair was spent - so a script keyed
/// on "the first call" would put a Markdown body where a summary was expected whenever a repair
/// intervened. The phase is read out of the request itself: the two phases ask for two different response
/// schemas, and matching on the schema is a positive assertion that <em>the right schema went out in
/// the right phase</em> rather than a guess about ordering.
/// </para>
/// <para>
/// <b>It can act while it answers.</b> A scenario has to be able to make the world move between the two
/// provider calls, because that is exactly the window the design's supersede scenario lives in. The
/// hook runs inside the call, so the evidence append a scenario performs really does land between phase
/// one and the commit-time re-check rather than being simulated by a flag somewhere else.
/// </para>
/// <para>
/// <b>What it is not.</b> It is not a provider and no claim in this suite may rest on it. It opens no
/// socket and sends nothing. The scenarios assert what Chronicleaf does with an answer - the strict
/// schema, the grounding rules, the citation check, the repair cap, the revision and its provenance -
/// and each of those is a property of this system rather than of any transport.
/// </para>
/// </remarks>
public sealed class ScriptedDraftProvider : ILLMProvider
{
    /// <summary>
    /// The slot this double serves. A slot no production adapter claims; see the host remarks.
    /// </summary>
    public const string Slot = GenerationProviderIds.Ollama;

    private PhaseAnswers _answers;
    private int _summaryCalls;
    private int _markdownCalls;

    private ScriptedDraftProvider(PhaseAnswers answers) => _answers = answers;

    public string ProviderId => Slot;

    /// <summary>
    /// A descriptor that reports <c>nativeJsonSchema: false</c>, on purpose.
    /// </summary>
    /// <remarks>
    /// The weaker adapter, because it is the case that has to be interesting. With a provider that
    /// enforces a schema itself, the local validator's job looks redundant; with one that does not, the
    /// strict check in this package is the only thing standing between a malformed answer and a stored
    /// draft. A scenario that only ran against a native-schema provider would be asserting the fallback
    /// path's contract through a shortcut the real deployment may not take.
    /// </remarks>
    public ProviderCapability Capability { get; } =
        new(Slot, StructuredOutputSupport: true, NativeJsonSchema: false, LocalEndpoint: false, RequiresCredential: true);

    /// <summary>The model this double's provider is scripted to accept, and nothing else.</summary>
    public const string KnownModel = "test-model-2026-10";

    /// <summary>Every request this double received, in order, with its phase.</summary>
    public IReadOnlyList<RecordedCall> Calls => _calls.ToArray();

    private readonly List<RecordedCall> _calls = [];

    /// <summary>How many times the transport was invoked, across both phases.</summary>
    public int CallCount => _calls.Count;

    /// <summary>How many calls were made for the structured summary. One, or two if a repair was spent.</summary>
    public int SummaryCalls => _summaryCalls;

    /// <summary>How many calls were made for the Markdown body. Zero unless a summary was accepted.</summary>
    public int MarkdownCalls => _markdownCalls;

    /// <summary>Builds a double answering with the given summary and body.</summary>
    public static ScriptedDraftProvider With(string summary, string bodyMarkdown) =>
        new(new PhaseAnswers(bodyMarkdown, [summary]));

    /// <summary>
    /// Builds a double whose phase-one answers are a list consumed in order.
    /// </summary>
    /// <param name="bodyMarkdown">The phase-two answer, repeated for every phase-two call.</param>
    /// <param name="summaries">
    /// Successive phase-one answers. The last repeats, so a scenario that scripts a first failure and a
    /// repair does not have to predict whether a third call would happen to prove the repair cap.
    /// </param>
    public static ScriptedDraftProvider SummariesThen(string bodyMarkdown, params string[] summaries) =>
        new(new PhaseAnswers(bodyMarkdown, [.. summaries]));

    /// <summary>
    /// Replaces the script, for a scenario that needs a second generation of the same asset to be drafted
    /// differently.
    /// </summary>
    public void Rescript(string summary, string bodyMarkdown) =>
        _answers = new PhaseAnswers(bodyMarkdown, [summary]);

    /// <summary>
    /// Zeroes the per-phase counters and forgets the recorded calls.
    /// </summary>
    /// <remarks>
    /// A scenario that generates twice and then asserts on call counts needs to read the second run's
    /// calls rather than a total. The recorded requests are cleared with the counters because they are
    /// the same fact - a call the counters no longer count would be a call a scenario could still see.
    /// </remarks>
    public void ResetCallCounts()
    {
        _summaryCalls = 0;
        _markdownCalls = 0;
        _calls.Clear();
    }

    /// <summary>
    /// The action run inside the named call, before its answer is returned.
    /// </summary>
    /// <remarks>
    /// The window this exists for: a scenario makes the world move <em>between</em> the two provider
    /// calls, which is the only honest way to test that the commit-time re-check sees a change. A flag
    /// consulted by a validator would be a second implementation of the check, and a test against it
    /// would prove nothing.
    /// </remarks>
    public Func<Task>? BeforeMarkdown { get; set; }

    public bool SupportsModel(string model) =>
        string.Equals(model, KnownModel, StringComparison.Ordinal);

    public async Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var markdown = IsMarkdownPhase(request);
        var index = markdown ? _markdownCalls++ : _summaryCalls++;
        var answer = markdown
            ? _answers.BodyMarkdown
            : _answers.Summaries[Math.Min(index, _answers.Summaries.Count - 1)];

        _calls.Add(new RecordedCall(markdown, index, request));

        if (markdown && BeforeMarkdown is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        // Every scripted answer here is a completed text result. A scenario that needs a refusal or a
        // provider failure uses the delivered RecordingLlmProvider, whose failure answers are already
        // covered by the two packages that own them - this suite is about the two phases and the rules
        // between them, not about re-testing a transport's error mapping.
        return GenerationResult.Completed(answer, GenerationFinishReasons.Stop, $"resp_scripted_{index + 1}");
    }

    /// <summary>
    /// True when this request is phase two, read out of the response schema it carries.
    /// </summary>
    /// <remarks>
    /// The two phases quote two different schemas, and matching on them is a positive assertion that the
    /// right one went out - which a call-index script could not make. A request quoting neither is phase
    /// one, because that is the only phase whose schema this suite expects to see.
    /// </remarks>
    private static bool IsMarkdownPhase(GenerationRequest request) =>
        request.ResponseSchema.Contains("bodyMarkdown", StringComparison.Ordinal);

    /// <summary>One call, with the phase it belonged to and the request it was given.</summary>
    public sealed record RecordedCall(bool IsMarkdownPhase, int IndexInPhase, GenerationRequest Request)
    {
        /// <summary>
        /// The untrusted data section as it went out, decoded. A scenario asserts on this to show what
        /// the provider actually received rather than trusting that the system built the request right.
        /// </summary>
        public System.Text.Json.JsonElement Evidence() =>
            System.Text.Json.JsonDocument.Parse(Request.UntrustedEvidenceJson).RootElement.Clone();
    }

    private sealed record PhaseAnswers(string BodyMarkdown, IReadOnlyList<string> Summaries);
}

/// <summary>
/// The fixtures the design names for this package, as a real raw read of the content tables
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// Read over a raw connection rather than through the API on purpose. The claims this suite makes are
/// about what is <em>stored</em> - that a revision's summary is the accepted one, that its body hash
/// describes its body, that revision 1 is byte-identical after revision 2 committed - and a value read
/// back through the same serializer that wrote it would agree with itself whether or not the bytes on
/// disk were right.
/// </remarks>
public static class ContentRows
{
    /// <summary>One stored revision, with the two JSON columns as the database holds them.</summary>
    public sealed record Revision(
        Guid Id,
        Guid AssetId,
        Guid ScopeId,
        long Number,
        Guid EvidenceBundleId,
        long EvidenceRevision,
        long VoiceRevision,
        string VoiceSnapshot,
        string Summary,
        string BodyMarkdown,
        string BodyHash,
        string ProviderId,
        string Model,
        string PromptVersion,
        string SchemaVersion,
        DateTimeOffset CreatedAt);

    /// <summary>One stored asset row.</summary>
    public sealed record Asset(
        Guid Id,
        Guid ScopeId,
        Guid ProjectId,
        Guid MilestoneId,
        string Type,
        string Status,
        long CurrentRevision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    public static async Task<Revision?> ReadRevisionAsync(string connectionString, Guid assetId, long revision)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, asset_id, scope_id, revision, evidence_bundle_id, evidence_revision, voice_revision,
                   voice_snapshot, summary, body_markdown, body_hash, provider_id, model, prompt_version,
                   schema_version, created_at
            FROM content_revisions
            WHERE asset_id = @asset AND revision = @revision
            """;

        command.Parameters.AddWithValue("asset", assetId);
        command.Parameters.AddWithValue("revision", revision);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Revision(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetGuid(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    public static async Task<IReadOnlyList<Revision>> ListRevisionsAsync(
        string connectionString,
        Guid assetId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, asset_id, scope_id, revision, evidence_bundle_id, evidence_revision, voice_revision,
                   voice_snapshot, summary, body_markdown, body_hash, provider_id, model, prompt_version,
                   schema_version, created_at
            FROM content_revisions
            WHERE asset_id = @asset
            ORDER BY revision
            """;

        command.Parameters.AddWithValue("asset", assetId);

        var rows = new List<Revision>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Revision(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetGuid(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetFieldValue<DateTimeOffset>(15)));
        }

        return rows;
    }

    public static async Task<Asset?> ReadAssetForMilestoneAsync(string connectionString, Guid milestoneId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, scope_id, project_id, milestone_id, type, status, current_revision, created_at, updated_at
            FROM content_assets
            WHERE milestone_id = @milestone
            ORDER BY created_at
            LIMIT 1
            """;

        command.Parameters.AddWithValue("milestone", milestoneId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new Asset(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    /// <summary>Every value column of one table, concatenated, for a leak sweep.</summary>
    public static async Task<string> DumpTableAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT coalesce(string_agg(t::text, E'\n'), '')
            FROM "{table}" AS t
            """;

        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    /// <summary>How many tables in this schema have a name that mentions approval.</summary>
    /// <remarks>
    /// The structural half of the draft-only claim. "This package never approves a draft" is a
    /// statement about storage as much as about code, and a table is the thing an approval would have to
    /// live in. Counting them is checkable; reading a route for a missing verb is not.
    /// </remarks>
    public static async Task<long> CountApprovalTablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_name ILIKE '%approv%'
            """;

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
