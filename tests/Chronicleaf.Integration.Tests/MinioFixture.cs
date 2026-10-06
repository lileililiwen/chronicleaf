using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// One real S3-compatible server for the cloud object storage scenarios
/// (CLOUD-OBJECT-STORAGE-001/002).
/// </summary>
/// <remarks>
/// A genuine MinIO server started from a pinned image in Docker: bucket creation, object writes,
/// presigned URL enforcement and expiry are all produced by that server, never by an in-memory
/// provider or a stub. The container is removed when the fixture is disposed, and each test
/// receives its own bucket on the shared server so object names are deterministic without
/// cross-test coupling. No real bucket, no real cloud account and no real credential are involved:
/// the keys are test-only MinIO root credentials that never leave the fixture.
/// </remarks>
public sealed class MinioFixture : IAsyncLifetime
{
    /// <summary>Pinned image tag. Change it deliberately, never to a floating tag.</summary>
    public const string Image = "minio/minio:RELEASE.2024-10-13T13-34-11Z";

    /// <summary>Test-only MinIO root user. Never a real credential.</summary>
    public const string User = "chronicleaf-minio-test";

    /// <summary>Test-only MinIO root password. Never a real credential.</summary>
    public const string Password = "chronicleaf-minio-test-password-01";

    private IContainer? _server;

    /// <summary>True once a real container is running; asserted so a stub can never pass as one.</summary>
    public bool IsRealMinio => _server is not null;

    /// <summary>The S3 endpoint of the running server, as the AWS client dials it.</summary>
    public string Endpoint
    {
        get
        {
            EnsureRunning();

            return $"http://{_server!.Hostname}:{_server.GetMappedPublicPort(9000)}";
        }
    }

    public async Task InitializeAsync()
    {
        _server = new ContainerBuilder(Image)
            .WithPortBinding(9000, true)
            .WithEnvironment("MINIO_ROOT_USER", User)
            .WithEnvironment("MINIO_ROOT_PASSWORD", Password)
            .WithCommand("server", "/data")
            .WithWaitStrategy(Wait
                .ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/minio/health/live")))
            .Build();

        await _server.StartAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }
    }

    /// <summary>Builds a real S3 client against the running server.</summary>
    public AmazonS3Client CreateClient()
    {
        EnsureRunning();

        return new AmazonS3Client(
            User,
            Password,
            new AmazonS3Config
            {
                ServiceURL = Endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                UseHttp = true,
            });
    }

    /// <summary>Creates an empty bucket on the running server and returns its name.</summary>
    public async Task<string> ProvisionBucketAsync(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        EnsureRunning();

        var bucket = $"chronicleaf-{purpose}-{Guid.NewGuid():N}"[..40].ToLowerInvariant();

        using var client = CreateClient();
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }).ConfigureAwait(false);

        return bucket;
    }

    /// <summary>Reads an object's bytes back over the real SDK, for assertions about the server.</summary>
    public async Task<byte[]> GetObjectAsync(string bucket, string key)
    {
        EnsureRunning();

        using var client = CreateClient();
        using var response = await client
            .GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key })
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer).ConfigureAwait(false);

        return buffer.ToArray();
    }

    private void EnsureRunning()
    {
        if (_server is null)
        {
            throw new InvalidOperationException("The disposable MinIO fixture is not running.");
        }
    }
}
