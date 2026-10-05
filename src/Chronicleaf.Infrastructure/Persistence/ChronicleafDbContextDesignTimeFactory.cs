using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// The design-time shape of <see cref="ChronicleafDbContext"/>, for <c>dotnet ef</c> only.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a scaffolding aid and never a runtime path.</b> The context the application uses is
/// built by <c>AddChronicleafPersistence</c> from the operator's connection string, and the container
/// never migrates: an operator runs the explicit <c>dotnet ef database update</c> against a database
/// they chose. This factory exists only so the tool can construct the same model without booting the
/// whole composition root, which is what makes scaffolding a migration possible at all from this
/// project alone.
/// </para>
/// <para>
/// <b>The connection string defaults to a placeholder for model-only work.</b> Scaffolding and
/// <c>script</c> read the model, not a server - but <c>database update</c> dials whatever this context
/// carries, so when <c>ConnectionStrings__Chronicleaf</c> names a database the factory uses it. That is
/// what lets an operator (and scripts/e2e.sh) migrate the database they chose, and there is still no
/// credential in this file: the value arrives through the environment, as it does at runtime.
/// </para>
/// <para>
/// It is internal on purpose: nothing outside the assembly can reach it, and the only caller is the
/// design-time tooling that looks for it by convention.
/// </para>
/// </remarks>
internal sealed class ChronicleafDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ChronicleafDbContext>
{
    /// <summary>The provider-agnostic name of the server this product's single relational store is.</summary>
    private const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public ChronicleafDbContext CreateDbContext(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // `database update` dials whatever connection string this context carries, so a fixed
        // placeholder breaks every caller that migrates a database it chose - notably scripts/e2e.sh,
        // which points at a disposable PostgreSQL through `ConnectionStrings__Chronicleaf`. The
        // operator's value therefore wins when it is present; the placeholder is only for model-only
        // operations (`scaffold`, `script`) that never open a connection.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Chronicleaf");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ChronicleafDb");
        }

        connectionString ??= "Host=localhost;Database=chronicleaf_design_time;Username=chronicleaf;Password=unused";

        var options = new DbContextOptionsBuilder<ChronicleafDbContext>()
            .UseNpgsql(
                connectionString,
                postgres => postgres.MigrationsAssembly(typeof(ChronicleafDbContext).Assembly.FullName))
            .Options;

        return new ChronicleafDbContext(options);
    }
}
