using Chronicleaf.Application.Media;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Evidence;

/// <summary>
/// The real reference check, now that evidence bundles exist (LOCAL-MEDIA-STORAGE-002,
/// MILESTONE-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation of the seam the media package registered deliberately. The default
/// it replaced - <see cref="NoMediaReferenceChecker"/> - answered "nothing references this" because
/// no table held references yet; this package is the one that creates that table, so the honest
/// answer now has to come from it. A deployment that reaches this checker through the real
/// registrations gets the real answer, and the 409 <c>media_in_use</c> the media package already
/// answered is now a statement about the database rather than about a stub.
/// </para>
/// <para>
/// The query is a single indexed existence probe, and it is not scope-bound on purpose. A reference
/// is the same fact whichever installation is asking about it, and a scoped probe would let one
/// installation's evidence stop protecting bytes another installation's evidence depends on. The
/// question this method is asked has exactly one answer, and the answer is never a reference itself -
/// so asking cannot disclose which milestone or which bundle pointed at a row.
/// </para>
/// <para>
/// No status filter and no bundle filter: an evidence row is append-only and is never removed, so a
/// reference found here is permanent. A retired image, a corrected item and a superseded item all
/// keep their rows, which is exactly why their bytes have to survive.
/// </para>
/// </remarks>
internal sealed class PostgresEvidenceMediaReferenceChecker(ChronicleafDbContext dbContext) : IMediaReferenceChecker
{
    public async Task<bool> IsReferencedAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
        await ChronicleafSql
            .ScalarAsync<bool>(
                dbContext,
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM evidence_items
                    WHERE media_id = @p0
                )
                """,
                [new("@p0", NpgsqlDbType.Uuid, mediaId)],
                cancellationToken)
            .ConfigureAwait(false);
}
