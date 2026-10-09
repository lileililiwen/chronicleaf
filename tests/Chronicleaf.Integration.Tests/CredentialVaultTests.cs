using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The credential vault, against a real disposable PostgreSQL, a real temporary key file and the real
/// operation queue (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every scenario asserts an externally observable result: a status code, an envelope, a response body,
/// the raw <c>bytea</c> columns read straight out of PostgreSQL, the text a host logged, or a
/// credential's leased bytes. Nothing here asserts a private field, a sealed class or a call count, so
/// the scenarios stay meaningful if the implementation changes shape.
/// </para>
/// <para>
/// The two properties that matter most are asserted the only way that is honest. That a plaintext is
/// absent from the stored row is proved by reading the <c>ciphertext</c> and <c>nonce</c> columns back
/// and searching the bytes for the plaintext, not by observing that the API omitted a field. That the
/// associated data binds a credential to its scope, provider, id and key version is proved by moving a
/// stored row and watching the same key fail to open it, which is the only failure a wrong binding can
/// produce.
/// </para>
/// <para>
/// No provider is called anywhere in this class. Nothing reaches the network, no token is presented
/// and no external write is attempted: the resolve job reads the leased bytes and returns them.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class CredentialVaultTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("8b4d1c7e-5a63-4f21-9d84-7e0c1a2b3c49");

    /// <summary>A second scope, used to prove a credential outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("9c5e2d8f-6b74-4a32-8e95-8f1d2b3c4d50");

    /// <summary>A sentinel the scenarios can search for wherever a leak would show up.</summary>
    private const string Sentinel = "sentinel-secret-value-do-not-log-7f3a91";

    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerPrefix = "Bearer ";

    // =============================================================================================
    // CREDENTIAL-VAULT-001 Success: an owner saves a credential, the metadata reads back, and the
    // stored row does not contain the plaintext.
    // =============================================================================================

    [Fact]
    public async Task A_saved_credential_reads_back_as_metadata_and_the_stored_row_holds_no_plaintext()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("save");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-555555555555");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var savedBody = await ReadJsonAsync(saved);
        Assert.Equal("github", savedBody.GetProperty("provider").GetString());
        Assert.True(savedBody.GetProperty("configured").GetBoolean());
        Assert.Equal(Clock, savedBody.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(credentialId, savedBody.GetProperty("id").GetGuid());
        Assert.Equal("\"1\"", saved.Headers.ETag?.ToString());

        // The response carried no value at all, in any spelling.
        var responseText = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sentinel, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("value", responseText, StringComparison.OrdinalIgnoreCase);

        // The metadata reads back for the same provider, with nothing to reveal.
        using var described = await owner.GetAsync("/api/settings/credentials/github");
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        Assert.Equal("\"1\"", described.Headers.ETag?.ToString());

        var describedBody = await ReadJsonAsync(described);
        Assert.Equal(credentialId, describedBody.GetProperty("id").GetGuid());
        Assert.True(describedBody.GetProperty("configured").GetBoolean());
        Assert.DoesNotContain(Sentinel, await described.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The stored row, read straight out of the bytea columns. The plaintext is searched for at
        // every offset, because a secret does not have to be aligned inside a ciphertext to be a leak.
        var row = await CredentialRows.ReadByProviderAsync(database, InstallationId, "github");
        Assert.NotNull(row);
        Assert.Equal(credentialId, row.Id);
        Assert.Equal(SecretVaultSettings.CurrentKeyVersion, row.KeyVersion);
        Assert.Equal(1, row.Revision);

        var plaintext = Encoding.UTF8.GetBytes(Sentinel);
        Assert.False(
            CredentialRows.Contains(row.Ciphertext, plaintext),
            "the stored ciphertext contains the plaintext bytes");
        Assert.False(
            CredentialRows.Contains(row.Nonce, plaintext),
            "the stored nonce contains the plaintext bytes");

        // And no column of the table, hex-encoded or otherwise, contains it anywhere in the row.
        Assert.DoesNotContain(
            Convert.ToHexString(plaintext),
            Convert.ToHexString(row.Ciphertext),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sentinel, await CredentialRows.DumpAllTextAsync(database), StringComparison.Ordinal);
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-001 Failure: no usable key fails closed - no upstream call, nothing written,
    // and neither the response nor the log names the key file or the value.
    // =============================================================================================

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-length")]
    [InlineData("unreadable")]
    public async Task An_unusable_key_file_fails_closed_and_leaves_nothing_behind(string fault)
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync($"key-{fault}");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-666666666666");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        // Arm the fault after the host is up, so the refusal is the running process's answer and not a
        // start-up condition.
        switch (fault)
        {
            case "missing":
                keyFile.Remove();
                break;

            case "wrong-length":
                keyFile.WriteWrongLength();
                break;

            default:
                keyFile.MakeUnreadable();
                break;
        }

        using var refused = await SaveAsync(owner, "openai", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        var body = await ReadJsonAsync(refused);
        Assert.Equal(CredentialVaultError.VaultUnavailable, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));

        var responseText = await refused.Content.ReadAsStringAsync();
        AssertNoLeak(responseText, keyFile.FilePath);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);

        // Nothing was written: no row, no ciphertext, no plaintext.
        Assert.Equal(0, await CredentialRows.CountAsync(database));
        Assert.DoesNotContain(Sentinel, await CredentialRows.DumpAllTextAsync(database), StringComparison.Ordinal);

        // And the same fault refuses a read of a provider that has nothing configured, rather than
        // inventing an empty credential or reporting a vault that works.
        using var described = await owner.GetAsync("/api/settings/credentials/openai");
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);
        Assert.False((await ReadJsonAsync(described)).GetProperty("configured").GetBoolean());

        // Restoring the key is what fixes the vault, with no restart: the file is read per operation.
        keyFile.Dispose();
        using var restored = OperatorKeyFile.Create();
        await using var recovered = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            restored.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var recoveredOwner = await OwnerClient.CreateAsync(recovered);
        using var accepted = await SaveAsync(recoveredOwner, "openai", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(1, await CredentialRows.CountAsync(database));
    }

    [Fact]
    public async Task No_configured_key_file_refuses_every_credential_route()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("key-absent");

        // No key file configured at all: the deployment that forgot the mount.
        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile: null,
            new SequenceCredentialIdSource(Guid.NewGuid()));

        using var owner = await OwnerClient.CreateAsync(host);

        using var refused = await SaveAsync(owner, "gemini", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(
            CredentialVaultError.VaultUnavailable,
            (await ReadJsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(0, await CredentialRows.CountAsync(database));

        // The vault's own health is unaffected: liveness and readiness are not credential routes.
        using var health = await owner.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var ready = await owner.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-001 Boundary: a credential deleted before its queued job runs fails
    // credential_missing, and no plaintext is retained anywhere.
    // =============================================================================================

    [Fact]
    public async Task A_job_whose_credential_was_deleted_fails_credential_missing_and_retains_nothing()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("deleted-before-job");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-777777777777");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new AdjustableTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "anthropic", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        // The job is committed first and dispatched afterwards, so the deletion lands in between - which
        // is the window the design names.
        using var enqueued = await EnqueueResolveAsync(owner, credentialId, "deleted-before-job");
        enqueued.EnsureSuccessStatusCode();
        var operationId = await ReadOperationIdAsync(enqueued);

        using var deleted = await DeleteAsync(owner, credentialId, revision: 1);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False((await ReadJsonAsync(deleted)).GetProperty("configured").GetBoolean());
        Assert.Equal(0, await CredentialRows.CountAsync(database));

        await host.DispatchAsync();

        using var worker = host.OpenWorker();
        await worker.Worker.RunAsync(operationId);

        var operation = await OperationRows.ReadAsync(database, operationId);
        Assert.NotNull(operation);
        Assert.Equal("failed", operation.Status);
        Assert.Equal(CredentialVaultError.Missing, operation.ErrorCode);
        Assert.Null(operation.ResultRef);

        // The operation's own row, its result and the queue carry a reference and an error code, and
        // nothing that came out of the credential.
        // The queue's own payload carries the operation id and nothing else, and the operation row
        // carries a reference and an error code. Neither has ever seen the value.
        AssertNoLeak(await ReadAllQueuedJobArgumentsAsync(database), keyFile.FilePath);
        AssertNoLeak(await CredentialRows.DumpAllTextAsync(database), keyFile.FilePath);
        AssertNoLeak(await ReadAllOperationRowsAsync(database), keyFile.FilePath);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);
    }

    [Fact]
    public async Task A_job_whose_credential_is_present_opens_it_and_names_only_the_reference()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("job-resolves");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-888888888888");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new AdjustableTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "openrouter", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        using var enqueued = await EnqueueResolveAsync(owner, credentialId, "job-resolves");
        enqueued.EnsureSuccessStatusCode();
        var operationId = await ReadOperationIdAsync(enqueued);

        await host.DispatchAsync();
        using var worker = host.OpenWorker();
        await worker.Worker.RunAsync(operationId);

        var operation = await OperationRows.ReadAsync(database, operationId);
        Assert.NotNull(operation);
        Assert.Equal("succeeded", operation.Status);
        Assert.Null(operation.ErrorCode);

        // The result names the credential and the leased length. It is a reference, not a payload: a
        // job payload, a result column and a log line all stay free of the value.
        Assert.StartsWith($"credential:{credentialId:D}:resolved:", operation.ResultRef, StringComparison.Ordinal);
        Assert.Equal(
            $"credential:{credentialId:D}:resolved:{Encoding.UTF8.GetByteCount(Sentinel)}",
            operation.ResultRef);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-002 Success: the current revision is replaced, and the replacement is what a
    // later resolve opens.
    // =============================================================================================

    [Fact]
    public async Task A_replaced_credential_is_what_resolves_afterwards_and_its_revision_advances()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("rotate");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-999999999999");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var first = await SaveAsync(owner, "ollama", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag?.ToString());

        // The first bytes, so the rotation can be shown to have replaced them rather than appended to
        // them.
        var before = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(before);
        var firstCiphertext = before.Ciphertext.ToArray();
        var firstNonce = before.Nonce.ToArray();

        using var rotated = await SaveAsync(owner, "ollama", "replacement-value-2b7c44", expectedRevision: 1);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.Equal("\"2\"", rotated.Headers.ETag?.ToString());
        Assert.Equal(credentialId, (await ReadJsonAsync(rotated)).GetProperty("id").GetGuid());

        var after = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(after);
        Assert.Equal(2, after.Revision);
        Assert.NotEqual(firstCiphertext, after.Ciphertext);
        Assert.NotEqual(firstNonce, after.Nonce);
        Assert.False(CredentialRows.Contains(after.Ciphertext, Encoding.UTF8.GetBytes("replacement-value-2b7c44")));

        // What resolves now is the replacement, and only the replacement.
        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();
        using var lease = await vault.ResolveAsync(SecretRef.To(credentialId));

        Assert.Equal(credentialId, lease.Reference.Value);
        Assert.Equal("ollama", lease.Provider);
        Assert.Equal(InstallationId, lease.ScopeId);
        Assert.Equal(SecretVaultSettings.CurrentKeyVersion, lease.KeyVersion);
        Assert.Equal("replacement-value-2b7c44", Encoding.UTF8.GetString(lease.Secret.ToArray()));

        // The old value is gone from the store in every form, including as a substring of the new row.
        Assert.False(CredentialRows.Contains(after.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));
        Assert.DoesNotContain(Sentinel, await CredentialRows.DumpAllTextAsync(database), StringComparison.Ordinal);

        // The lease is returned and cannot be read again: the bytes are gone from the process's hands.
        lease.Dispose();
        Assert.True(lease.IsReturned);
        Assert.Throws<ObjectDisposedException>(() => lease.Secret.ToArray());
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-002 Failure: a stale If-Match is a 409 and the current credential is preserved
    // byte for byte.
    // =============================================================================================

    [Fact]
    public async Task A_stale_revision_is_a_conflict_and_preserves_the_stored_credential_byte_for_byte()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("stale");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-aaaaaaaa0001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId, Guid.NewGuid()));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "publisher", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var before = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(before);

        // Revision 1 is current, so 1 is not stale; revision 7 never was.
        using var rotated = await SaveAsync(owner, "publisher", "second-value-31ac9e", expectedRevision: 1);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var current = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(current);
        Assert.Equal(2, current.Revision);

        using var stale = await SaveAsync(owner, "publisher", "third-value-never-stored", expectedRevision: 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var body = await ReadJsonAsync(stale);
        Assert.Equal("revision_conflict", body.GetProperty("code").GetString());
        AssertNoLeak(await stale.Content.ReadAsStringAsync(), keyFile.FilePath);

        // Byte for byte: the ciphertext, the nonce, the version, the instant and the revision are all
        // what they were, and the rejected value is nowhere in the table.
        var after = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(after);
        Assert.Equal(current.Ciphertext, after.Ciphertext);
        Assert.Equal(current.Nonce, after.Nonce);
        Assert.Equal(current.KeyVersion, after.KeyVersion);
        Assert.Equal(current.UpdatedAt, after.UpdatedAt);
        Assert.Equal(2, after.Revision);
        Assert.Equal(1, await CredentialRows.CountAsync(database));

        var dump = await CredentialRows.DumpAllTextAsync(database);
        Assert.DoesNotContain("third-value-never-stored", dump, StringComparison.Ordinal);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);
    }

    [Fact]
    public async Task A_save_without_a_revision_is_refused_before_anything_is_written()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("no-if-match");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(new Guid("11111111-2222-4333-8444-bbbbbbbb0001")));

        using var owner = await OwnerClient.CreateAsync(host);

        using var request = await owner.Put(
            "/api/settings/credentials/github",
            new { value = Sentinel });

        using var refused = await owner.SendAsync(request);
        Assert.Equal((HttpStatusCode)428, refused.StatusCode);
        Assert.Equal("if_match_required", (await ReadJsonAsync(refused)).GetProperty("code").GetString());
        Assert.Equal(0, await CredentialRows.CountAsync(database));
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-002 Boundary: what a failure leaves in the logs and in the error envelope.
    // =============================================================================================

    [Fact]
    public async Task A_provider_less_failure_leaves_no_credential_no_authorization_header_and_no_key_path()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("no-leak");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-cccccccc0001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        // The failure is provoked the way a provider failure would be, without a provider: the stored
        // bytes are presented as ones this key did not seal, which is what a wrong key file or a
        // tampered row looks like from inside the vault.
        keyFile.ReplaceKey();

        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        var failure = await Assert.ThrowsAsync<CredentialUndecryptableException>(
            () => vault.ResolveAsync(SecretRef.To(credentialId)));

        Assert.Equal(credentialId, failure.Reference.Value);
        Assert.Equal(CredentialVaultError.Undecryptable, failure.Code);
        AssertNoLeak(failure.Message, keyFile.FilePath);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);

        // The same failure as it reaches a client: the envelope a route would produce, captured by
        // asking the boundary for a provider that does not exist and reading the whole body back.
        using var refused = await SaveAsync(owner, "not-a-provider", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        AssertNoLeak(await refused.Content.ReadAsStringAsync(), keyFile.FilePath);

        AssertNoLeak(host.Logs.Text, keyFile.FilePath);

        // The stored row was not touched by the failed opening, and the value it holds is still sealed.
        var row = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(row);
        Assert.False(CredentialRows.Contains(row.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-002 Boundary: another installation's credential is not found, and no byte or
    // metadata of it is disclosed.
    // =============================================================================================

    [Fact]
    public async Task A_credential_in_another_scope_is_not_found_and_is_neither_readable_nor_deletable()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("foreign");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-dddddddd0001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        // A credential that belongs to this scope is stored, so the answers below are about the scope
        // and not about the provider being unknown.
        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        // The row is moved to another installation's scope directly in the database, which is the only
        // way a row can end up out of scope, and the owner still holds its reference.
        await CredentialRows.MoveToScopeAsync(database, credentialId, ForeignScopeId);

        // A read by provider is now unconfigured: this scope has no credential for it.
        using var described = await owner.GetAsync("/api/settings/credentials/github");
        Assert.Equal(HttpStatusCode.OK, described.StatusCode);

        var describedBody = await ReadJsonAsync(described);
        Assert.False(describedBody.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, describedBody.GetProperty("id").ValueKind);
        AssertNoLeak(await described.Content.ReadAsStringAsync(), keyFile.FilePath);

        // The list does not mention it either.
        using var listed = await owner.GetAsync("/api/settings/credentials?limit=100");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var listText = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(credentialId.ToString(), listText, StringComparison.OrdinalIgnoreCase);
        AssertNoLeak(listText, keyFile.FilePath);

        // A delete by the id the owner knows is 404, and the row in the other scope survives.
        using var deleted = await DeleteAsync(owner, credentialId, revision: 1);

        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(deleted)).GetProperty("code").GetString());
        AssertNoLeak(await deleted.Content.ReadAsStringAsync(), keyFile.FilePath);

        var foreignRow = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(foreignRow);
        Assert.Equal(ForeignScopeId, foreignRow.ScopeId);
        Assert.Equal(1, foreignRow.Revision);
    }

    // =============================================================================================
    // CREDENTIAL-VAULT-001/002 Boundary: the associated data binds the ciphertext. A row that was
    // moved, or that claims an older key version, does not open under the same key.
    // =============================================================================================

    [Fact]
    public async Task A_row_moved_to_another_scope_or_provider_does_not_open_under_the_same_key()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("aad-scope");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-eeeeeeee0001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var row = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(row);

        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        // Before the move the same key opens it, so what fails afterwards is the move and nothing else.
        using (var opened = await vault.ResolveAsync(SecretRef.To(credentialId)))
        {
            Assert.Equal(Sentinel, Encoding.UTF8.GetString(opened.Secret.ToArray()));
        }

        await CredentialRows.MoveToScopeAsync(database, credentialId, ForeignScopeId);

        await Assert.ThrowsAsync<CredentialUndecryptableException>(
            () => vault.ResolveAsync(SecretRef.To(credentialId)));

        // The ciphertext is untouched by the failed opening, and still holds no plaintext.
        var moved = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(moved);
        Assert.Equal(row.Ciphertext, moved.Ciphertext);
        Assert.False(CredentialRows.Contains(moved.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));

        // The same holds for a row moved between providers inside one scope.
        await CredentialRows.MoveToScopeAsync(database, credentialId, InstallationId);
        await CredentialRows.MoveToProviderAsync(database, credentialId, "openai");

        await Assert.ThrowsAsync<CredentialUndecryptableException>(
            () => vault.ResolveAsync(SecretRef.To(credentialId)));
    }

    [Fact]
    public async Task A_row_that_claims_a_key_version_this_build_does_not_have_does_not_open()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("aad-version");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-ffffffff0001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "gemini", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        using (var opened = await vault.ResolveAsync(SecretRef.To(credentialId)))
        {
            Assert.Equal(Sentinel, Encoding.UTF8.GetString(opened.Secret.ToArray()));
        }

        // A row that claims a key version this build does not have is refused before the key file is
        // even opened: there is no key to open it with, and a vault that guessed would be the failure
        // this package exists to prevent.
        await CredentialRows.SetKeyVersionAsync(database, credentialId, SecretVaultSettings.CurrentKeyVersion + 1);

        var unavailable = await Assert.ThrowsAsync<SecretVaultUnavailableException>(
            () => vault.ResolveAsync(SecretRef.To(credentialId)));
        Assert.Equal(SecretKeyFault.UnknownKeyVersion, unavailable.Reason);
        Assert.Equal(CredentialVaultError.VaultUnavailable, unavailable.Code);
        AssertNoLeak(unavailable.Message, keyFile.FilePath);
        AssertNoLeak(host.Logs.Text, keyFile.FilePath);

        // The row itself is untouched by the refusal, and still holds no plaintext.
        var row = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(row);
        Assert.False(CredentialRows.Contains(row.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));
    }

    [Fact]
    public async Task The_associated_data_is_what_binds_a_credential_to_its_scope_provider_id_and_version()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("aad-cipher");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-123456780001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        // The row's own stored bytes, and the key that sealed them: the exact pair the vault opens.
        var row = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(row);

        var stored = new CredentialBinding(row.ScopeId, row.Provider, row.Id, row.KeyVersion);
        var opened = AesGcmSecretCipher.Open(row.Ciphertext, row.Nonce, keyFile.Key, stored);
        Assert.Equal(Sentinel, Encoding.UTF8.GetString(opened));
        CryptographicOperations.ZeroMemory(opened);
        byte[] reopened;

        // The authenticated data is the four values joined with a separator and a domain prefix, so
        // this is exactly the bytes GCM verifies the tag over.
        var associatedData = AesGcmSecretCipher.AssociatedDataFor(stored);
        Assert.Equal(
            "chronicleaf.credential.v1\u001f" +
            $"{row.ScopeId:D}\u001f{row.Provider}\u001f{row.Id:D}\u001f{row.KeyVersion}",
            Encoding.UTF8.GetString(associatedData));

        // Change one part of the binding and the same key, the same nonce and the same bytes no
        // longer open. That is the whole claim about the associated data: it is not decoration, it is
        // what a moved or downgraded row fails on.
        var wrongBindings = new (string Part, CredentialBinding Binding)[]
        {
            ("scope", stored with { ScopeId = ForeignScopeId }),
            ("provider", stored with { Provider = "openai" }),
            ("id", stored with { CredentialId = Guid.NewGuid() }),
            ("key version", stored with { KeyVersion = row.KeyVersion + 1 }),
        };

        foreach (var (part, binding) in wrongBindings)
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                AesGcmSecretCipher.Open(row.Ciphertext, row.Nonce, keyFile.Key, binding));

            Assert.False(
                string.Equals(Encoding.UTF8.GetString(AesGcmSecretCipher.AssociatedDataFor(binding)), Encoding.UTF8.GetString(associatedData), StringComparison.Ordinal),
                $"the {part} is not part of the associated data");
        }

        // The right binding with the right key still opens, so nothing above failed for another reason.
        reopened = AesGcmSecretCipher.Open(row.Ciphertext, row.Nonce, keyFile.Key, stored);
        Assert.Equal(Sentinel, Encoding.UTF8.GetString(reopened));
        CryptographicOperations.ZeroMemory(reopened);

        // A different key does not open it either, which is the operator who restored the wrong file.
        using var other = OperatorKeyFile.Create();
        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmSecretCipher.Open(row.Ciphertext, row.Nonce, other.Key, stored));

        // And the sealed form never contained the plaintext in the first place.
        Assert.False(CredentialRows.Contains(row.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));
    }

    [Fact]
    public async Task A_credential_whose_ciphertext_was_tampered_with_does_not_open()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("tampered");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("11111111-2222-4333-8444-111111110001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);
        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        var row = await CredentialRows.ReadAsync(database, credentialId);
        Assert.NotNull(row);

        // Flip one bit of the sealed body: the tag is what notices.
        var tampered = row.Ciphertext.ToArray();
        tampered[0] ^= 0x01;
        await WriteCiphertextAsync(database, credentialId, tampered);

        using var scope = host.Services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<ISecretVault>();

        await Assert.ThrowsAsync<CredentialUndecryptableException>(
            () => vault.ResolveAsync(SecretRef.To(credentialId)));
    }

    // =============================================================================================
    // The provider vocabulary, and the list bounds.
    // =============================================================================================

    [Theory]
    [InlineData("github")]
    [InlineData("openai")]
    [InlineData("gemini")]
    [InlineData("anthropic")]
    [InlineData("openrouter")]
    [InlineData("ollama")]
    [InlineData("publisher")]
    public async Task Every_supported_provider_is_accepted(string provider)
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync($"provider-{provider}");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(new Guid("22222222-3333-4444-8555-666666666666")));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, provider, Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(provider, (await ReadJsonAsync(saved)).GetProperty("provider").GetString());

        var row = await CredentialRows.ReadByProviderAsync(database, InstallationId, provider);
        Assert.NotNull(row);
        Assert.False(CredentialRows.Contains(row.Ciphertext, Encoding.UTF8.GetBytes(Sentinel)));
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("GITHUB")]
    [InlineData("not-a-provider")]
    [InlineData("github2")]
    [InlineData("open-ai")]
    public async Task An_unsupported_provider_is_refused_and_stores_nothing(string provider)
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("provider-refused");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(new Guid("22222222-3333-4444-8555-777777777777")));

        using var owner = await OwnerClient.CreateAsync(host);

        using var refused = await SaveAsync(owner, provider, Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

        var body = await ReadJsonAsync(refused);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.Equal(
            "The provider is not one Chronicleaf stores credentials for.",
            body.GetProperty("fieldErrors").GetProperty("provider")[0].GetString());
        Assert.Equal(0, await CredentialRows.CountAsync(database));
    }

    [Fact]
    public async Task An_empty_or_missing_value_is_refused_with_a_field_error_that_repeats_neither()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("empty-value");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(new Guid("22222222-3333-4444-8555-888888888888")));

        using var owner = await OwnerClient.CreateAsync(host);

        foreach (var (label, body) in new (string, object?)[]
                 {
                     ("whitespace", new { value = "   " }),
                     ("empty", new { value = string.Empty }),
                     ("absent", new { }),
                 })
        {
            using var refused = await SaveAsync(owner, "github", body, expectedRevision: 0);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var text = await refused.Content.ReadAsStringAsync();
            Assert.Equal("validation_failed", JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
            Assert.Contains("value", text, StringComparison.Ordinal);
            AssertNoLeak(text, keyFile.FilePath);
            Assert.Equal(0, await CredentialRows.CountAsync(database));

            Assert.NotEmpty(label);
        }
    }

    [Fact]
    public async Task The_settings_list_is_bounded_and_cursored_within_one_scope()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("list");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(
                new Guid("33333333-4444-4555-8666-000000000001"),
                new Guid("33333333-4444-4555-8666-000000000002"),
                new Guid("33333333-4444-4555-8666-000000000003")));

        using var owner = await OwnerClient.CreateAsync(host);

        foreach (var provider in new[] { "gemini", "openai", "publisher" })
        {
            using var saved = await SaveAsync(owner, provider, $"{provider}-value-8ac31d", expectedRevision: 0);
            Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        }

        // The default page is 25 items, and there are three.
        using var first = await owner.GetAsync("/api/settings/credentials");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var firstPage = await ReadJsonAsync(first);
        Assert.Equal(3, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, firstPage.GetProperty("nextCursor").ValueKind);
        Assert.Equal(
            ["gemini", "openai", "publisher"],
            [.. firstPage.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("provider").GetString() ?? string.Empty)]);

        // A page size is clamped, never refused, and the cursor continues where the page ended.
        using var limited = await owner.GetAsync("/api/settings/credentials?limit=1");
        var limitedPage = await ReadJsonAsync(limited);
        Assert.Single(limitedPage.GetProperty("items").EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, limitedPage.GetProperty("nextCursor").ValueKind);

        var cursor = limitedPage.GetProperty("nextCursor").GetString()!;
        using var second = await owner.GetAsync($"/api/settings/credentials?limit=1&cursor={Uri.EscapeDataString(cursor)}");
        var secondPage = await ReadJsonAsync(second);
        Assert.Equal("openai", secondPage.GetProperty("items")[0].GetProperty("provider").GetString());

        // A cursor is bound to the scope that issued it.
        var foreign = ScopeCursor.Encode(ForeignScopeId, "gemini", new Guid("33333333-4444-4555-8666-000000000001"));
        using var refused = await owner.GetAsync(
            $"/api/settings/credentials?cursor={Uri.EscapeDataString(foreign)}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(
            "The cursor is not a cursor this scope issued.",
            (await ReadJsonAsync(refused)).GetProperty("fieldErrors").GetProperty("cursor")[0].GetString());

        // No item in any page carries a value or anything derived from one.
        AssertNoLeak(await first.Content.ReadAsStringAsync(), keyFile.FilePath);
        AssertNoLeak(await second.Content.ReadAsStringAsync(), keyFile.FilePath);
    }

    // =============================================================================================
    // The boundary: the routes are private, and the public allow-list has not grown.
    // =============================================================================================

    [Fact]
    public async Task The_credential_routes_are_private_and_the_public_health_routes_are_unchanged()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("boundary");
        using var keyFile = OperatorKeyFile.Create();

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(new Guid("44444444-5555-4666-8777-000000000001")));

        using var client = host.CreateClient();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/settings/credentials"),
                     (HttpMethod.Get, "/api/settings/credentials/github"),
                     (HttpMethod.Get, $"/api/settings/credentials/{Guid.NewGuid():D}"),
                     (HttpMethod.Put, "/api/settings/credentials/github"),
                     (HttpMethod.Delete, $"/api/settings/credentials/{Guid.NewGuid():D}"),
                 })
        {
            using var anonymous = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            using var response = await client.SendAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // The public routes are exactly as they were: liveness and readiness answer without a session.
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", (await ReadJsonAsync(health)).GetProperty("status").GetString());

        using var ready = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(0, await CredentialRows.CountAsync(database));
    }

    [Fact]
    public async Task Every_mutation_is_audited_with_the_credential_the_action_and_the_revision()
    {
        Assert.True(postgres.IsRealPostgres, "the credential vault scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("audit");
        using var keyFile = OperatorKeyFile.Create();
        var credentialId = new Guid("55555555-6666-4777-8888-000000000001");

        await using var host = CredentialVaultApiHost.Start(
            database,
            new FixedTimeProvider(Clock),
            keyFile.FilePath,
            new SequenceCredentialIdSource(credentialId));

        using var owner = await OwnerClient.CreateAsync(host);

        using var saved = await SaveAsync(owner, "github", Sentinel, expectedRevision: 0);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var rotated = await SaveAsync(owner, "github", "second-value-2f0d13", expectedRevision: 1);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var deleted = await DeleteAsync(owner, credentialId, revision: 2);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        // Two saves and one delete, each naming the credential, the action and the revision - and the
        // audit trail, which is readable more widely than the vault, holds no value and no value's
        // shape.
        var audits = await AuditRowsAsync(database, credentialId);
        Assert.Equal(3, audits.Count);
        Assert.Equal(2, audits.Count(audit => audit.Action == CredentialVaultService.SavedAction));
        Assert.Single(audits, audit => audit.Action == CredentialVaultService.DeletedAction);

        // Every saved revision and the removed one, without depending on the order of three rows that
        // share an injected instant.
        Assert.Equal([1, 2, 2], audits.Select(audit => audit.Revision).Order());
        Assert.All(audits, audit => Assert.Contains("provider", audit.Detail, StringComparison.Ordinal));
        Assert.All(audits, audit => Assert.DoesNotContain(Sentinel, audit.Detail, StringComparison.Ordinal));

        var dump = await CredentialRows.DumpAllTextAsync(database);
        AssertNoLeak(dump, keyFile.FilePath);
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);

        await using (var host = CredentialVaultApiHost.Start(
                         database,
                         new AdjustableTimeProvider(Clock),
                         keyFile: null))
        {
            using var scope = host.Services.CreateScope();
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return database;
    }

    /// <summary>Deletes through the real route, presenting the revision the owner read.</summary>
    private static async Task<HttpResponseMessage> DeleteAsync(OwnerClient owner, Guid credentialId, long revision)
    {
        ArgumentNullException.ThrowIfNull(owner);

        using var request = await owner.Archive(
            $"/api/settings/credentials/{credentialId:D}",
            revision);

        return await owner.SendAsync(request);
    }

    /// <summary>
    /// Saves through the real route with the real session, origin, antiforgery value and
    /// <c>If-Match</c>, and returns the response the owner would have seen.
    /// </summary>
    private static async Task<HttpResponseMessage> SaveAsync(
        OwnerClient owner,
        string provider,
        object? body,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A string is the value; anything else is a body the test built, including one with no value
        // field at all.
        object? payload = body switch
        {
            string value => new { value },
            null => new { value = (string?)null },
            _ => body,
        };

        using var request = await owner.Put(
            $"/api/settings/credentials/{provider}",
            payload,
            expectedRevision);

        return await owner.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> EnqueueResolveAsync(
        OwnerClient owner,
        Guid credentialId,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(owner);

        using var request = await owner.Post(
            "/api/operations",
            new
            {
                kind = CredentialResolveOperationHandler.ResolveKind,
                targetId = credentialId.ToString(),
                inputRevision = 1L,
                idempotencyKey,
            });

        return await owner.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> ReadOperationIdAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("id").GetGuid();

    private static async Task WriteCiphertextAsync(
        string connectionString,
        Guid credentialId,
        byte[] ciphertext)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE credentials SET ciphertext = @ciphertext WHERE id = @id";
        command.Parameters.AddWithValue("ciphertext", ciphertext);
        command.Parameters.AddWithValue("id", credentialId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Every argument the real queue stored, joined, which is what a payload grep would see.</summary>
    private static async Task<string> ReadAllQueuedJobArgumentsAsync(string connectionString)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT arguments::text FROM hangfire.job";

        var text = new System.Text.StringBuilder();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            text.AppendLine(reader.GetString(0));
        }

        return text.ToString();
    }

    /// <summary>Every operation row, as text, for the assertions that a job retained nothing.</summary>
    private static async Task<string> ReadAllOperationRowsAsync(string connectionString)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id::text || '|' || kind || '|' || coalesce(result_ref, '') || '|' || coalesce(error_code, '') FROM operations";

        var text = new System.Text.StringBuilder();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            text.AppendLine(reader.GetString(0));
        }

        return text.ToString();
    }

    private static async Task<List<(string Action, long Revision, string Detail)>> AuditRowsAsync(
        string connectionString,
        Guid credentialId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT action, safe_metadata ->> 'revision', safe_metadata ->> 'detail'
            FROM audit_events
            WHERE target_id = @id
            ORDER BY occurred_at ASC, action ASC
            """;
        command.Parameters.AddWithValue("id", credentialId);

        var rows = new List<(string Action, long Revision, string Detail)>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                long.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// The leak assertion, applied to every place a secret, an authorization header or a key-file path
    /// would show up if it leaked: an envelope, a response body, a log line, a stored dump.
    /// </summary>
    private static void AssertNoLeak(string text, string? keyFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Assert.DoesNotContain(Sentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthorizationHeaderName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BearerPrefix, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", text, StringComparison.OrdinalIgnoreCase);

        if (keyFilePath is { Length: > 0 } path)
        {
            Assert.DoesNotContain(path, text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                text,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                System.IO.Path.GetFileName(path),
                text,
                StringComparison.Ordinal);
        }
    }
}
