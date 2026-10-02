using System.Net;
using System.Text.Json;
using Chronicleaf.Api.Common;
using Chronicleaf.Application.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The error envelope owned by the persistence package: <c>{code,message,traceId,fieldErrors?}</c>
/// with the status mapping the design names, and messages that never leak provider bodies,
/// tokens or host paths.
/// </summary>
/// <remarks>
/// These assertions run against a real HTTP server that mounts the production
/// <see cref="ApiErrorMiddleware"/>. The probe endpoints exist only to raise the same failures
/// feature code raises; the middleware under test is the shipped one, not a copy.
/// </remarks>
public sealed class ErrorEnvelopeCommunityPersistenceTests
{
    /// <summary>A secret that must never appear in a response body.</summary>
    private const string Secret = ErrorProbeHost.Secret;

    /// <summary>An absolute host path that must never appear in a response body.</summary>
    private const string HostPath = ErrorProbeHost.HostPath;

    public static TheoryData<string, int, string> StatusMappings => new()
    {
        { "/probe/malformed-json", 400, ApiError.MalformedJson },
        { "/probe/validation", 422, ApiError.ValidationFailed },
        { "/probe/if-match", 428, ApiError.IfMatchRequired },
        { "/probe/revision", 409, ApiError.RevisionConflict },
        { "/probe/oversized", 413, ApiError.PayloadTooLarge },
        { "/probe/unavailable", 503, ApiError.DatabaseUnavailable },
        { "/probe/timeout", 504, ApiError.DependencyTimeout },
    };

    [Theory]
    [MemberData(nameof(StatusMappings))]
    public async Task Failure_is_written_as_the_common_envelope_with_its_contracted_status(
        string path,
        int expectedStatus,
        string expectedCode)
    {
        await using var host = await ErrorProbeHost.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.PostAsync(path, Json("{}"));

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;

        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        // The envelope is camelCase and carries no provider detail.
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(HostPath, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Semantic_validation_reports_field_errors()
    {
        await using var host = await ErrorProbeHost.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.PostAsync("/probe/validation", Json("{}"));

        Assert.Equal(422, (int)response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fieldErrors = document.RootElement.GetProperty("fieldErrors");

        Assert.Equal("revision must be a positive integer", fieldErrors.GetProperty("revision")[0].GetString());
    }

    [Fact]
    public async Task Malformed_json_body_is_reported_as_malformed_json()
    {
        await using var host = await ErrorProbeHost.StartAsync();
        using var client = host.GetTestClient();

        using var content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/probe/body", content);

        Assert.Equal(400, (int)response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ApiError.MalformedJson, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unhandled_failure_returns_an_opaque_internal_error()
    {
        await using var host = await ErrorProbeHost.StartAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync("/probe/unexpected");

        Assert.Equal(500, (int)response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(ApiError.InternalError, document.RootElement.GetProperty("code").GetString());

        // The dependency's own text stays in the log; the client only sees an opaque code.
        Assert.DoesNotContain(Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(HostPath, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
    }

    private static StringContent Json(string value) =>
        new(value, System.Text.Encoding.UTF8, "application/json");
}

/// <summary>
/// Mounts the production error middleware on a real server and exposes one probe per failure the
/// package maps. The probes raise the same exception types feature code raises; nothing about the
/// middleware, the envelope or the status mapping is reimplemented here.
/// </summary>
internal sealed class ErrorProbeHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private ErrorProbeHost(WebApplication application) => _application = application;

    public static async Task<ErrorProbeHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var application = builder.Build();
        application.UseMiddleware<ApiErrorMiddleware>();

        // The runtime deserializing its own request body: the JSON exception raised here travels
        // through the shipped middleware, which owns the 400 `malformed_json` envelope.
        application.MapPost("/probe/body", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            _ = JsonSerializer.Deserialize<ProbeBody>(body);
            return Results.NoContent();
        });
        application.MapPost("/probe/malformed-json", Probe(() => throw new MalformedJsonException()));
        application.MapPost("/probe/validation", Probe(() => throw new ValidationFailedException("revision", "revision must be a positive integer")));
        application.MapPost("/probe/if-match", Probe(() => throw new IfMatchRequiredException("installation")));
        application.MapPost("/probe/revision", Probe(() => throw new RevisionConflictException(7)));
        application.MapPost("/probe/oversized", Probe(() => throw new PayloadTooLargeException(1024)));
        application.MapPost("/probe/unavailable", Probe(() => throw new DependencyUnavailableException("PostgreSQL")));
        application.MapPost("/probe/timeout", Probe(() => throw new DependencyTimeoutException("PostgreSQL", TimeSpan.FromSeconds(2))));
        application.MapGet(
            "/probe/unexpected",
            Probe(() => throw new InvalidOperationException(
                $"connect failed for {ErrorProbeHost.Secret} while reading {ErrorProbeHost.HostPath}")));

        await application.StartAsync();
        return new ErrorProbeHost(application);
    }

    /// <summary>Dependency detail that must never reach a client.</summary>
    internal const string Secret = "postgresql://chronicleaf:hunter2@db.internal:5432/chronicleaf";

    /// <summary>An absolute host path that must never reach a client.</summary>
    internal const string HostPath = "/srv/chronicleaf/persistence/migrations";

    /// <summary>Adapts a throwing body to the delegate shape the endpoint router expects.</summary>
    private static RequestDelegate Probe(Action raise) => _ => { raise(); return Task.CompletedTask; };

    /// <summary>The only shape a probe body is ever deserialized into.</summary>
    private sealed record ProbeBody(string? Value);

    public HttpClient GetTestClient() => _application.GetTestClient();

    public ValueTask DisposeAsync() => _application.DisposeAsync();
}
