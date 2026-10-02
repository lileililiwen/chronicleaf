using System.Text.Json;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// Writes the Project and Milestone audit trail into the delivered <c>audit_events</c> table
/// (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// The row is staged, not saved: it is committed by the same unit of work as the change it records,
/// so an audit row exists if and only if that change was committed. The metadata is a fixed,
/// secret-free shape - the action, the revision and a short label from this package's own
/// vocabulary. A title, a note, a version, a URL and a project name are deliberately absent: the
/// trail is readable more widely than the records it describes, so it must not become a second copy
/// of them.
/// <para>
/// The stamped instant is the injected clock's, not a property of the record. A milestone carries
/// the moment the owner says it happened, which is their claim about their project and not a
/// database fact, and the two are kept apart on purpose.
/// </para>
/// </remarks>
public sealed class PostgresAuditWriter(ChronicleafDbContext dbContext, TimeProvider clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    public void Record(DomainAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        dbContext.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            audit.ActorId,
            audit.Action,
            audit.TargetId,
            clock.GetUtcNow(),
            JsonSerializer.Serialize(
                new MilestoneAuditMetadata(audit.Action, audit.Revision, audit.Detail),
                Wire)));
    }

    /// <summary>The fixed, secret-free metadata shape written as the delivered <c>safe_metadata</c>.</summary>
    private sealed record MilestoneAuditMetadata(string Action, long Revision, string Detail);
}
