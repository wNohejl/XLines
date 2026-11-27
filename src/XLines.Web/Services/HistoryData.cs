using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Services;
using XLines.Reliability;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the History window reads: each source's eligibility, the coverage the backfill holds, the
/// latest days walked, the seasons table (when it is due) and the registry's names for the keys the
/// backfill reports in. A seam so the window holds no DbContext (architecture review 2026-11-27 §1).
/// </summary>
public interface IHistoryData
{
    /// <param name="readSeasons">False when the seasons table is not due; <see cref="HistoryRead.Seasons"/> is then null.</param>
    Task<HistoryRead> ReadAsync(bool readSeasons, CancellationToken ct = default);
}

/// <summary>One read of the History window.</summary>
public sealed record HistoryRead(
    IReadOnlyList<SourceEligibility> Eligibility,
    BackfillCoverage Coverage,
    IReadOnlyList<BackfillCheckpoint> Recent,
    IReadOnlyList<SeasonCoverage>? Seasons,
    IReadOnlyDictionary<string, string> SourceNames);

/// <summary>The window's reads, exactly as the window made them before.</summary>
public sealed class HistoryData(HistoryBackfillService backfill, IDbContextFactory<XLinesDbContext> dbFactory) : IHistoryData
{
    public async Task<HistoryRead> ReadAsync(bool readSeasons, CancellationToken ct = default)
    {
        // Eligibility is read again inside GetCoverageAsync for its target; this copy is the
        // list the Sources section shows, and HistoryBackfillService returns it no other way.
        var eligibility = await backfill.GetEligibilityAsync(ct);
        var coverage = await backfill.GetCoverageAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.BackfillCheckpoints.AsNoTracking()
            .Include(c => c.Sport)
            .OrderByDescending(c => c.CompletedAt)
            .Take(40)
            .ToListAsync(ct);

        var seasons = readSeasons ? await new DataQuality(db).SeasonsAsync(ct) : null;

        // Names for the keys the backfill reports in. Read with the rest so a source renamed
        // in the registry is renamed here on the next change, not on the next restart.
        var names = await db.Sources.AsNoTracking()
            .Select(s => new { s.Key, s.Name })
            .ToListAsync(ct);

        var sourceNames = names
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        return new HistoryRead(eligibility, coverage, recent, seasons, sourceNames);
    }
}
