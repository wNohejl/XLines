using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Game window's market-movement card reads: one market's stored prices on one game, to
/// the reader's tier. A seam so the card holds no DbContext (architecture review 2026-11-27 §1).
/// </summary>
public interface IMarketMovementData
{
    /// <summary>Every stored price on one market of one game, both sides, oldest first.</summary>
    Task<IReadOnlyList<OddsSnapshot>> SnapshotsAsync(int gameId, string market, CancellationToken ct = default);
}

/// <summary>The card's read, exactly as the card made it before.</summary>
public sealed class MarketMovementData(IDbContextFactory<XLinesDbContext> dbFactory, IDataTier tier) : IMarketMovementData
{
    public async Task<IReadOnlyList<OddsSnapshot>> SnapshotsAsync(int gameId, string market, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Scans keep to the tier (ADR 0024): an evaluation feed's moves are the operator's.
        return await db.OddsSnapshots.FromVisible(await db.VisibleSourceIdsAsync(tier, ct)).AsNoTracking()
            .Where(s => s.GameId == gameId && s.Market == market)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);
    }
}
