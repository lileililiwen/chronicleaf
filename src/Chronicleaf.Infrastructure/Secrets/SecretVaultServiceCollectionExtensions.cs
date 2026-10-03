using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Secrets;

/// <summary>
/// Registers the credential vault (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The registrations are the same shape as every other package's: a scoped store and a scoped vault
/// over the delivered <c>ChronicleafDbContext</c>, the delivered unit of work and audit writer, and
/// the delivered operation handler registry picking up the resolve handler. Nothing here migrates and
/// nothing here substitutes a provider: a database that has not been migrated reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here fails when the key file is missing.</b> Registration is not a start-up gate for
/// the key, on purpose: a vault that cannot be configured yet must not take the whole application down
/// with it. The application keeps serving liveness, readiness and everything that is not a
/// credential, and every credential operation refuses with
/// <c>credential_vault_unavailable</c> until the operator's file is there. That is the fail-closed
/// behaviour the design asks for - no plaintext, no sentinel, no silent success - without turning an
/// operator's missing mount into a site that will not come up. An operator who wants a hard gate can
/// take it, and readiness says the vault is not configured, but this build does not enforce it.
/// </para>
/// <para>
/// There is no <c>Platform.*</c> reference here: the package is sealed to the delivered
/// infrastructure it already runs on.
/// </para>
/// </remarks>
public static class SecretVaultServiceCollectionExtensions
{
    /// <summary>
    /// Registers the vault, its store, its service and the resolve handler.
    /// </summary>
    /// <param name="services">The container being composed.</param>
    /// <param name="configuration">
    /// The application configuration, read for <c>Chronicleaf:EncryptionKeyFile</c>. It is read once
    /// here because a path is an operator's choice that does not change between requests; the key
    /// itself is read from the file per operation.
    /// </param>
    public static IServiceCollection AddCredentialVault(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(SecretVaultSettings.FromConfiguration(configuration));
        services.AddScoped<ISecretKeyProvider, FileSecretKeyProvider>();
        services.AddSingleton<ICredentialIdSource, NewCredentialIdSource>();
        services.AddScoped<ICredentialStore, PostgresCredentialStore>();
        services.AddScoped<ISecretVault, PostgresSecretVault>();
        services.AddScoped<ICredentialVaultService, CredentialVaultService>();

        // The queued-job seam. It is registered as a delivered operation handler, so it is dispatched
        // by the delivered worker and its failures are recorded by the delivered conditional commit.
        services.AddScoped<IOperationHandler, CredentialResolveOperationHandler>();

        return services;
    }
}
