using System.Text.Json;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.EntityFrameworkCore;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Writes the installation row, its audit evidence and its outbox event as one set of changes
/// inside the caller's unit of work, and reads the committed state back.
/// </summary>
public sealed class InstallationStore(ChronicleafDbContext dbContext, TimeProvider clock)
    : IInstallationStore
{
    /// <summary>Action recorded in the audit log for the initial installation.</summary>
    public const string ProvisionedAction = "installation.provisioned";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public Task<Installation> ProvisionAsync(
        Guid installationId,
        int schemaVersion,
        string action,
        Guid? actorId,
        OutboxPayload outboxPayload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.GetUtcNow();

        var installation = new Installation(installationId, schemaVersion, now);
        var audit = new AuditEvent(
            Guid.NewGuid(),
            actorId,
            action,
            installationId,
            now,
            JsonSerializer.Serialize(
                new AuditMetadata(outboxPayload.Kind, outboxPayload.Revision, outboxPayload.SchemaVersion),
                Wire));

        var outbox = new OutboxEvent(
            Guid.NewGuid(),
            outboxPayload.Kind,
            outboxPayload.AggregateId,
            outboxPayload.Revision,
            outboxPayload.ToJson(),
            now);

        // The rows are staged only. They reach PostgreSQL when the caller's unit of work
        // commits, so an installation can never exist without its audit and outbox rows.
        dbContext.Installations.Add(installation);
        dbContext.AuditEvents.Add(audit);
        dbContext.Outbox.Add(outbox);

        return Task.FromResult(installation);
    }

    public Task<Installation?> FindAsync(Guid installationId, CancellationToken cancellationToken = default) =>
        dbContext.Installations
            .AsNoTracking()
            .SingleOrDefaultAsync(installation => installation.Id == installationId, cancellationToken);

    public async Task<IReadOnlyList<Installation>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Installations
            .AsNoTracking()
            .OrderBy(installation => installation.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    private sealed record AuditMetadata(string Kind, long Revision, int SchemaVersion);
}

/// <summary>Read side of the append-only audit log.</summary>
public sealed class AuditLog(ChronicleafDbContext dbContext) : IAuditLog
{
    public async Task<IReadOnlyList<AuditEvent>> ListForTargetAsync(
        Guid targetId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = ListQueryPolicy.Clamp(limit);
        return await dbContext.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.TargetId == targetId)
            .OrderBy(auditEvent => auditEvent.OccurredAt)
            .Take(bounded)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Read side of the durable outbox.</summary>
public sealed class OutboxStore(ChronicleafDbContext dbContext) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxEvent>> ListPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var bounded = ListQueryPolicy.Clamp(limit);
        return await dbContext.Outbox
            .AsNoTracking()
            .Where(outboxEvent => outboxEvent.DispatchedAt == null)
            .OrderBy(outboxEvent => outboxEvent.CreatedAt)
            .ThenBy(outboxEvent => outboxEvent.Id)
            .Take(bounded)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> CountAsync(CancellationToken cancellationToken = default) =>
        dbContext.Outbox.LongCountAsync(cancellationToken);
}
