using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Secrets;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Platform.Storage.Keys;
using Platform.Storage.S3;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// Builds one leased S3 backend per call over the adopted platform adapter
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// The shape mirrors the provider adapters' own key sources: the credential crosses one seam the
/// backend declares (<see cref="IS3CredentialSource"/>: keys, briefly) and only the composition
/// root knows the answer comes from a sealed, scope-bound vault document. The lease is opened here,
/// copied once into the client's own credentials, and returned before the first request - so the
/// vault's buffer is zeroed the moment the copy exists and the client's copies live only as long as
/// the returned backend, which the caller disposes.
/// </para>
/// <para>
/// The client is application-owned, as the platform adapter requires: the endpoint, bucket, region
/// and path-style come from the operator's configuration, and the key pair from the vault. No
/// ambient credential chain is consulted, so a key the operator did not store cannot authenticate
/// anything. Every failure to configure, open or parse the credential is 503 with a message that
/// names no value, no key and no endpoint fragment beyond what the operator configured.
/// </para>
/// </remarks>
public sealed class S3BackendFactory(
    S3MediaOptions options,
    IS3CredentialSource credentials,
    MediaSettings settings,
    ILogger<S3BackendFactory> logger) : IS3BackendFactory
{
    public async Task<OwnedS3Backend> CreateAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            throw new MediaStorageUnavailableException(
                "The S3 object storage backend is not configured for this installation.");
        }

        using var lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);

        var (accessKey, secretKey) = ReadKeyPair(lease);

        AmazonS3Client client;
        try
        {
            client = new AmazonS3Client(
                accessKey,
                secretKey,
                new AmazonS3Config
                {
                    ServiceURL = options.Endpoint,
                    ForcePathStyle = options.ForcePathStyle,
                    AuthenticationRegion = options.Region,
                    Timeout = TimeSpan.FromSeconds(30),

                    // The endpoint declares its own scheme: a plain-http MinIO or R2 endpoint
                    // must not be dialled as TLS, and an https endpoint must not be downgraded.
                    // The SDK defaults to HTTPS, so only an explicit http endpoint opts out.
                    UseHttp = options.Endpoint.StartsWith(
                        "http://",
                        StringComparison.OrdinalIgnoreCase),
                });
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            throw new MediaStorageUnavailableException("The S3 object storage backend is not configured.");
        }

        var storage = new S3Storage(
            client,
            new S3StorageOptions
            {
                BucketName = options.BucketName,
                KeyPrefix = options.KeyPrefix,
                Limits = new StorageOptions
                {
                    MaximumObjectBytes = settings.MaximumByteLength,

                    // The platform enforces the design's five-minute presign bound too, so a longer
                    // lifetime cannot arrive through a path that forgot the service-level check.
                    MaximumPresignLifetime = S3MediaOptions.MaxPresignLifetime,
                },
            });

        // The lease is returned here, before the first request: the vault's buffers are zeroed while
        // the client's own copies live exactly as long as the backend the caller disposes.
        IObjectStorage backend = storage;

        if (options.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            // The adopted adapter mints presigned URLs in its own https scheme, which is right for
            // AWS and R2 but wrong for a plain-http MinIO endpoint: an https URL against an http
            // port fails before MinIO reads a byte. The rewrite below restores the endpoint's own
            // scheme when the minted authority is otherwise identical, which preserves the
            // signature - SigV4 signs the host header and the query, never the scheme - and leaves
            // every https endpoint untouched.
            backend = new HttpPresignBackend(backend, endpoint.Host, endpoint.Port);
        }

        return new OwnedS3Backend(
            backend,
            async () =>
            {
                client.Dispose();
                await ValueTask.CompletedTask;
            });
    }

    private async Task<S3CredentialLease> AcquireAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CredentialMissingException
            or SecretVaultUnavailableException
            or CredentialUndecryptableException)
        {
            logger.LogWarning(exception, "The S3 object-storage credential could not be opened.");

            throw new MediaStorageUnavailableException("The S3 object-storage credential is not available.");
        }
    }

    /// <summary>
    /// Reads the sealed key-pair document: one JSON object with exactly <c>accessKeyId</c> and
    /// <c>secretAccessKey</c>.
    /// </summary>
    /// <remarks>
    /// Strict on purpose. A document with a missing half, an empty half, whitespace where a key
    /// cannot have any, or members this backend does not understand (a session token, a region, a
    /// comment) is refused rather than half-used: temporary credentials and extra fields are shapes
    /// this package does not support, and silently ignoring them would be a claim about what was
    /// used that is not true. Nothing parsed here is ever logged.
    /// </remarks>
    private static (string AccessKey, string SecretKey) ReadKeyPair(S3CredentialLease lease)
    {
        if (!string.Equals(lease.Provider, S3MediaOptions.CredentialProvider, StringComparison.Ordinal))
        {
            throw new MediaStorageUnavailableException("The S3 object-storage credential is not available.");
        }

        var document = lease.Document.ToArray();

        try
        {
            using var parsed = JsonDocument.Parse(
                new ReadOnlyMemory<byte>(document),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });

            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new MediaStorageUnavailableException("The S3 object-storage credential is not available.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            string? accessKey = null;
            string? secretKey = null;

            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                names.Add(property.Name);

                if (string.Equals(property.Name, "accessKeyId", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    accessKey = property.Value.GetString();
                }
                else if (string.Equals(property.Name, "secretAccessKey", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    secretKey = property.Value.GetString();
                }
            }

            if (!names.SetEquals(["accessKeyId", "secretAccessKey"])
                || !IsKeyMaterial(accessKey)
                || !IsKeyMaterial(secretKey))
            {
                throw new MediaStorageUnavailableException("The S3 object-storage credential is not available.");
            }

            return (accessKey!, secretKey!);
        }
        catch (JsonException)
        {
            throw new MediaStorageUnavailableException("The S3 object-storage credential is not available.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(document);
        }
    }

    private static bool IsKeyMaterial(string? value) =>
        value is { Length: > 0 and <= S3MediaOptions.MaxFieldLength }
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    /// <summary>
    /// Restores a presigned URL to the plain-http endpoint's own scheme when the minted authority
    /// is otherwise identical (CLOUD-OBJECT-STORAGE-001).
    /// </summary>
    /// <remarks>
    /// A delegating wrapper rather than a second implementation: every call but the scheme of a
    /// minted URL reaches the platform's own adapter untouched. The rewrite is exact-match only -
    /// same host, same port, https minted - so an URL the adapter minted for anywhere else passes
    /// through byte-identical, and an http endpoint whose adapter already minted http needs no
    /// rewrite at all.
    /// </remarks>
    private sealed class HttpPresignBackend(IObjectStorage inner, string host, int port) : IObjectStorage
    {
        public Task<StorageOutcome> UploadAsync(StorageUploadRequest request, CancellationToken cancellationToken = default) =>
            inner.UploadAsync(request, cancellationToken);

        public Task<StorageDownloadResult> DownloadAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
            inner.DownloadAsync(key, cancellationToken);

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
            inner.GetMetadataAsync(key, cancellationToken);

        public Task<StorageOutcome> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(key, cancellationToken);

        public async Task<(StorageOutcome Outcome, PresignedOperation? Operation)> PresignAsync(
            PresignRequest request,
            CancellationToken cancellationToken = default)
        {
            var (outcome, operation) = await inner.PresignAsync(request, cancellationToken).ConfigureAwait(false);

            if (outcome.Status != StorageOutcomeStatus.Succeeded || operation is null)
            {
                return (outcome, operation);
            }

            var url = operation.Url;

            if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase)
                || url.Port != port)
            {
                return (outcome, operation);
            }

            var http = new UriBuilder(url) { Scheme = Uri.UriSchemeHttp, Port = url.IsDefaultPort ? -1 : url.Port };

            return (outcome, operation with { Url = http.Uri });
        }
    }
}
