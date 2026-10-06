using Chronicleaf.Application.Access;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Media.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Npgsql;
using Platform.Storage.Keys;

// Inside a namespace that mentions Media the bare identifier would resolve to the namespace, so the
// Core record is addressed through an alias in this file too.
using MediaRecord = Chronicleaf.Core.Media;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a real disposable PostgreSQL and a real
/// temporary MediaRoot (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Everything that is not the filesystem or the database is the production code path: the connection
/// string arrives through the same configuration key production reads, the access token hash and
/// public origin through theirs, the MediaRoot and the byte limit through
/// <c>Chronicleaf:MediaRoot</c> and <c>Chronicleaf:Media:MaximumByteLength</c>, and only the clock is
/// replaced with a fixed UTC instant. There is no in-memory storage provider, no stubbed store and no
/// EF in-memory provider anywhere in these scenarios - the claim under test is that bytes on a real
/// disk stay consistent with rows in a real database, and neither half of that can be simulated.
/// </para>
/// <para>
/// Four seams exist and each replaces exactly one registration, leaving the store, the provider, the
/// repository, the routes and the access boundary untouched:
/// <see cref="MediaReferenceScript"/> stands in for the evidence references a later package will own,
/// <see cref="MediaFaults"/> injects a real provider failure at a chosen call,
/// <see cref="MediaRepositoryFaults"/> injects a real database failure at the registration step, and
/// <see cref="SequenceMediaIdSource"/> makes the generated identity a fixed value.
/// </para>
/// </remarks>
public sealed class MediaApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string _mediaRoot;
    private readonly long? _maximumByteLength;
    private readonly MediaReferenceScript? _references;
    private readonly MediaFaults? _faults;
    private readonly bool _withRepositoryFaults;
    private readonly IMediaIdSource? _ids;
    private readonly bool _s3Mode;
    private readonly S3HostOptions? _s3;
    private readonly IS3BackendFactory? _backendFactory;
    private readonly IS3CredentialSource? _credentialSource;
    private readonly ICredentialIdSource? _credentialIds;
    private readonly string? _keyFile;

    private MediaApiHost(
        string connectionString,
        TimeProvider clock,
        string mediaRoot,
        long? maximumByteLength,
        MediaReferenceScript? references,
        MediaFaults? faults,
        bool withRepositoryFaults,
        IMediaIdSource? ids,
        bool s3Mode = false,
        S3HostOptions? s3 = null,
        IS3BackendFactory? backendFactory = null,
        IS3CredentialSource? credentialSource = null,
        ICredentialIdSource? credentialIds = null,
        string? keyFile = null)
    {
        _connectionString = connectionString;
        _clock = clock;
        _mediaRoot = mediaRoot;
        _maximumByteLength = maximumByteLength;
        _references = references;
        _faults = faults;
        _withRepositoryFaults = withRepositoryFaults;
        _ids = ids;
        _s3Mode = s3Mode;
        _s3 = s3;
        _backendFactory = backendFactory;
        _credentialSource = credentialSource;
        _credentialIds = credentialIds;
        _keyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The MediaRoot this host was configured with, so a test can look at real files.</summary>
    public string MediaRootPath => _mediaRoot;

    /// <summary>
    /// Starts a host with the production registrations and a fresh temporary MediaRoot.
    /// </summary>
    /// <param name="maximumByteLength">
    /// Tightens the configured byte limit, so a scenario can prove the limit without moving 20 MiB. The
    /// value is read through the production configuration key and the production clamping rule, so a
    /// limit this host applies is one a deployment could set too.
    /// </param>
    public static MediaApiHost Start(
        string connectionString,
        DateTimeOffset clock,
        string mediaRoot,
        long? maximumByteLength = null,
        IMediaIdSource? ids = null) =>
        new(connectionString, new FixedTimeProvider(clock), mediaRoot, maximumByteLength, null, null, false, ids);

    /// <summary>
    /// Starts a host whose reference check is driven by the test, which is how a reference into
    /// evidence is expressed before the package that owns those references exists.
    /// </summary>
    public static MediaApiHost StartWithReferences(
        string connectionString,
        DateTimeOffset clock,
        string mediaRoot,
        MediaReferenceScript references,
        IMediaIdSource? ids = null,
        long? maximumByteLength = null) =>
        new(connectionString, new FixedTimeProvider(clock), mediaRoot, maximumByteLength, references, null, false, ids);

    /// <summary>Starts a host whose storage provider reports chosen failures over the real adapter.</summary>
    public static MediaApiHost StartWithFaults(
        string connectionString,
        DateTimeOffset clock,
        string mediaRoot,
        MediaFaults faults,
        IMediaIdSource? ids = null) =>
        new(connectionString, new FixedTimeProvider(clock), mediaRoot, null, null, faults, false, ids);

    /// <summary>Starts a host whose media repository cannot commit a record.</summary>
    public static MediaApiHost StartWithRepositoryFaults(
        string connectionString,
        DateTimeOffset clock,
        string mediaRoot,
        IMediaIdSource? ids = null) =>
        new(connectionString, new FixedTimeProvider(clock), mediaRoot, null, null, null, true, ids);

    /// <summary>
    /// Starts a host in S3 storage mode (CLOUD-OBJECT-STORAGE-001/002).
    /// </summary>
    /// <remarks>
    /// The S3 options travel through the production configuration keys and the production
    /// validation, so an option this host applies is one a deployment could set too. Either the
    /// backend factory or the credential source - or both - may be replaced: replacing the factory
    /// substitutes the whole backend, while replacing only the source keeps the production factory
    /// (and therefore the real platform adapter and the real client it builds) over scripted keys.
    /// The vault key file is the production one, so a scenario can store a real sealed credential
    /// and prove the reference never reaches a row or a log.
    /// </remarks>
    public static MediaApiHost StartS3(
        string connectionString,
        DateTimeOffset clock,
        string mediaRoot,
        S3HostOptions s3,
        IS3BackendFactory? backendFactory = null,
        IS3CredentialSource? credentialSource = null,
        MediaReferenceScript? references = null,
        IMediaIdSource? ids = null,
        ICredentialIdSource? credentialIds = null,
        string? keyFile = null) =>
        new(
            connectionString,
            new FixedTimeProvider(clock),
            mediaRoot,
            null,
            references,
            null,
            false,
            ids,
            s3Mode: true,
            s3: s3,
            backendFactory: backendFactory,
            credentialSource: credentialSource,
            credentialIds: credentialIds,
            keyFile: keyFile);

    /// <summary>The real MediaRoot guard this host resolved, for the boundary assertions.</summary>
    public Infrastructure.Media.MediaRoot ResolvedRoot() =>
        Services.GetRequiredService<Infrastructure.Media.MediaRoot>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseSetting(MediaSettings.MediaRootSettingName, _mediaRoot);
        builder.UseEnvironment("Production");

        if (_s3Mode)
        {
            ArgumentNullException.ThrowIfNull(_s3);

            builder.UseSetting(MediaSettings.StorageModeSettingName, "s3");
            builder.UseSetting($"{S3MediaOptions.SectionName}:Endpoint", _s3.Endpoint);
            builder.UseSetting($"{S3MediaOptions.SectionName}:BucketName", _s3.BucketName);
            builder.UseSetting($"{S3MediaOptions.SectionName}:Region", _s3.Region);
            builder.UseSetting(
                $"{S3MediaOptions.SectionName}:ForcePathStyle",
                _s3.ForcePathStyle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting($"{S3MediaOptions.SectionName}:KeyPrefix", _s3.KeyPrefix);
            builder.UseSetting(
                $"{S3MediaOptions.SectionName}:CredentialId",
                _s3.CredentialId.ToString("D"));
        }

        if (_keyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, _keyFile);
        }

        if (_maximumByteLength is { } limit)
        {
            builder.UseSetting(MediaSettings.MaximumByteLengthSettingName, limit.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(logging =>
        {
            // A refused key and a compensated upload are logged as warnings and errors on purpose; the
            // scenarios deliberately provoke them, and a test run should not look like a failing host.
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_references is not null)
            {
                services.RemoveAll<IMediaReferenceChecker>();

                // Registered against the contract rather than the concrete script: AddSingleton would
                // otherwise infer the script's own type and register a service nothing resolves.
                services.AddSingleton<IMediaReferenceChecker>(_references);
            }

            if (_faults is not null)
            {
                // The wrapper delegates to the real adapter, so every call the store makes still goes
                // to a real filesystem and only the chosen one fails. The wrapper is created by the
                // scenario rather than here, so a scenario can arm a fault before the host exists.
                services.WrapObjectStorage(inner =>
                {
                    _faults.Inner = inner;

                    return _faults;
                });
            }

            if (_withRepositoryFaults)
            {
                services.WrapMediaRepository<MediaRepositoryFaults>();
            }

            if (_ids is not null)
            {
                services.RemoveAll<IMediaIdSource>();
                services.AddSingleton(_ids);
            }

            if (_backendFactory is not null)
            {
                // Registered against the contract rather than the concrete script: AddSingleton
                // would otherwise infer the script's own type and register a service nothing
                // resolves. The production factory is removed first, so the bundle of seams stays
                // one registration and a scenario cannot accidentally hold both.
                services.RemoveAll<IS3BackendFactory>();
                services.AddSingleton<IS3BackendFactory>(_backendFactory);
            }

            if (_credentialSource is not null)
            {
                // Keeps the production factory - and therefore the real platform adapter and the
                // real client it builds - while the keys come from the script. This is the seam a
                // MinIO scenario uses: everything above the credential is production code.
                services.RemoveAll<IS3CredentialSource>();
                services.AddSingleton<IS3CredentialSource>(_credentialSource);
            }

            if (_credentialIds is not null)
            {
                services.RemoveAll<ICredentialIdSource>();
                services.AddSingleton(_credentialIds);
            }
        });
    }
}

/// <summary>
/// The S3 options an S3-mode test host is configured with, through the production keys.
/// </summary>
/// <param name="Endpoint">The S3-compatible endpoint. A dummy host is never dialled when the backend factory is scripted.</param>
/// <param name="BucketName">The bucket this installation owns.</param>
/// <param name="Region">The signing region.</param>
/// <param name="ForcePathStyle">Path-style addressing, as MinIO and R2 require.</param>
/// <param name="KeyPrefix">The optional installation prefix.</param>
/// <param name="CredentialId">The vault reference of the sealed s3 credential document.</param>
public sealed record S3HostOptions(
    string Endpoint,
    string BucketName,
    string Region,
    bool ForcePathStyle,
    string KeyPrefix,
    Guid CredentialId)
{
    /// <summary>Options that pass production validation but dial nothing: the backend is scripted.</summary>
    public static S3HostOptions UnusedBackend(Guid? credentialId = null) =>
        new("http://127.0.0.1:9", "test-bucket", "us-east-1", true, string.Empty, credentialId ?? Guid.NewGuid());
}

/// <summary>
/// The evidence references a later package will own, expressed here as a script the test drives.
/// </summary>
/// <remarks>
/// It replaces <see cref="IMediaReferenceChecker"/> and nothing else, which is the point: the 409
/// <c>media_in_use</c> path is the store's, and this only answers the one question the store asks.
/// A media id is referenced from the moment a test says so until the test says otherwise, and the
/// record of what was asked is kept so a scenario can prove the check ran before anything was
/// written rather than after.
/// </remarks>
public sealed class MediaReferenceScript : IMediaReferenceChecker
{
    private readonly HashSet<Guid> _referenced = [];
    private readonly Lock _gate = new();

    /// <summary>The media ids this script has been asked about, in order.</summary>
    public List<Guid> Asked { get; } = [];

    /// <summary>Declares that a reference now points at this media.</summary>
    public void Reference(Guid mediaId)
    {
        lock (_gate)
        {
            _referenced.Add(mediaId);
        }
    }

    /// <summary>Removes the reference, so a later retire is allowed.</summary>
    public void Release(Guid mediaId)
    {
        lock (_gate)
        {
            _referenced.Remove(mediaId);
        }
    }

    public Task<bool> IsReferencedAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Asked.Add(mediaId);
        }

        return Task.FromResult(_referenced.Contains(mediaId));
    }
}

/// <summary>
/// A failure the test chooses to inject into the real storage provider.
/// </summary>
/// <remarks>
/// The wrapper only changes what one named call reports; every other call is forwarded to the real
/// Local adapter over a real directory, so a scenario that provokes a failure still exercises the
/// whole sequence and can then assert what is on disk. The three faults are the three points where a
/// failure has to leave the store consistent: the promotion write, the release of an object, and the
/// provider's own view of what exists.
/// </remarks>
public sealed class MediaFaults : IObjectStorage
{
    /// <summary>The real adapter this wrapper forwards to, wired by the host that built it.</summary>
    public IObjectStorage Inner { private get; set; } = null!;

    private int _uploadCalls;

    /// <summary>How many uploads this host has seen. Promotion is the second upload of one call.</summary>
    public int UploadCalls => Volatile.Read(ref _uploadCalls);

    /// <summary>When true, the promotion write of each call is reported unavailable.</summary>
    public bool FailPromotion { get; set; }

    /// <summary>
    /// When true, a release is reported unavailable, which is what an interrupted compensation looks
    /// like from the store's side.
    /// </summary>
    public bool FailRelease { get; set; }

    /// <summary>When true, the provider reports that no object exists, whatever is on disk.</summary>
    public bool HideObjects { get; set; }

    public async Task<StorageOutcome> UploadAsync(
        StorageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _uploadCalls);

        if (FailPromotion && call % 2 == 0)
        {
            return StorageOutcome.Unavailable(new StorageFailure(
                "storage.provider_unavailable",
                "The injected promotion failure.",
                true));
        }

        return await Inner.UploadAsync(request, cancellationToken);
    }

    public Task<StorageDownloadResult> DownloadAsync(
        StorageObjectKey key,
        CancellationToken cancellationToken = default) =>
        Inner.DownloadAsync(key, cancellationToken);

    public Task<StorageObjectMetadata?> GetMetadataAsync(
        StorageObjectKey key,
        CancellationToken cancellationToken = default) =>
        HideObjects ? Task.FromResult<StorageObjectMetadata?>(null) : Inner.GetMetadataAsync(key, cancellationToken);

    public Task<StorageOutcome> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
        FailRelease
            ? Task.FromResult(StorageOutcome.Unavailable(new StorageFailure(
                "storage.provider_unavailable",
                "The injected release failure.",
                true)))
            : Inner.DeleteAsync(key, cancellationToken);

    public Task<(StorageOutcome Outcome, PresignedOperation? Operation)> PresignAsync(
        PresignRequest request,
        CancellationToken cancellationToken = default) =>
        Inner.PresignAsync(request, cancellationToken);
}

/// <summary>
/// A media repository whose insert cannot commit, so a registration failure can be provoked at the
/// one step that happens after the bytes exist.
/// </summary>
/// <remarks>
/// It forwards every read and every other write to the real PostgreSQL store and fails only the
/// insert, which is the exact ordering the requirement is about: bytes staged, promoted and verified,
/// and then no row. The exception is a real <see cref="NpgsqlException"/>, not a stand-in, so the
/// store's own translation of a database failure is what the scenario observes.
/// </remarks>
public sealed class MediaRepositoryFaults(IMediaRepository inner) : IMediaRepository
{
    /// <summary>When true, the insert reports a database failure instead of committing a row.</summary>
    public bool FailInsert { get; set; } = true;

    public Task<MediaRecord?> FindAsync(Guid scopeId, Guid mediaId, CancellationToken cancellationToken = default) =>
        inner.FindAsync(scopeId, mediaId, cancellationToken);

    public Task<MediaRecord?> FindByStorageKeyAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.FindByStorageKeyAsync(storageKey, cancellationToken);

    public Task<IReadOnlyList<MediaRecord>> ListStalePendingAsync(
        DateTimeOffset stagedBefore,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ListStalePendingAsync(stagedBefore, limit, cancellationToken);

    public Task<int> TransitionStatusAsync(
        Guid scopeId,
        Guid mediaId,
        string expectedStatus,
        string newStatus,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default) =>
        inner.TransitionStatusAsync(scopeId, mediaId, expectedStatus, newStatus, changedAt, cancellationToken);

    public Task<int> UpdateProviderAsync(
        Guid scopeId,
        Guid mediaId,
        string expectedProvider,
        string newProvider,
        CancellationToken cancellationToken = default) =>
        inner.UpdateProviderAsync(scopeId, mediaId, expectedProvider, newProvider, cancellationToken);

    public Task<IReadOnlyList<MediaRecord>> ListReadyByProviderAsync(
        Guid scopeId,
        string provider,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ListReadyByProviderAsync(scopeId, provider, limit, cancellationToken);

    public Task<bool> InsertAsync(MediaRecord media, CancellationToken cancellationToken = default) =>
        FailInsert
            ? throw new NpgsqlException("The injected media registration failure.")
            : inner.InsertAsync(media, cancellationToken);
}

/// <summary>
/// A fixed sequence of media ids, so a scenario can name the key and the file it expects.
/// </summary>
/// <remarks>
/// Ids are produced in order and the sequence is exhausted loudly rather than wrapping, so a test that
/// uploads more images than it declared fails with a clear message instead of silently sharing an id
/// with another scenario.
/// </remarks>
public sealed class SequenceMediaIdSource(params Guid[] ids) : IMediaIdSource
{
    private int _next = -1;

    public Guid NextMediaId()
    {
        var index = Interlocked.Increment(ref _next);

        if (index >= ids.Length)
        {
            throw new InvalidOperationException(
                "The test declared fewer media ids than it uploaded. Declare one id per upload.");
        }

        return ids[index];
    }
}

/// <summary>
/// Wraps an existing registration in a decorator, keeping the real implementation inside.
/// </summary>
/// <remarks>
/// Written out here rather than pulled in as a package because the production registrations are
/// factories over a resolved MediaRoot and a resolved context, so a wrapper has to build the inner
/// instance from the same factory rather than from a type. Keeping the real one inside is what makes
/// an injected failure evidence about the store's sequence and not about a substitute provider.
/// </remarks>
internal static class RegistrationDecoration
{
    public static void WrapObjectStorage(
        this IServiceCollection services,
        Func<IObjectStorage, IObjectStorage> decorate) =>
        Wrap<IObjectStorage>(services, decorate);

    public static void WrapMediaRepository<TWrapper>(this IServiceCollection services)
        where TWrapper : IMediaRepository =>
        Wrap<IMediaRepository>(services, inner => (IMediaRepository)Activator.CreateInstance(
            typeof(TWrapper),
            inner)!);

    private static void Wrap<TService>(IServiceCollection services, Func<TService, TService> decorate)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var existing = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException($"No {typeof(TService).Name} is registered to wrap.");

        services.Remove(existing);
        services.AddScoped<TService>(provider => decorate((TService)Resolve(provider, existing)));
    }

    private static object Resolve(IServiceProvider provider, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return factory(provider);
        }

        return ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
    }
}

