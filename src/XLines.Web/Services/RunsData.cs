using XLines.Core.Entities;
using XLines.Data;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Runs window reads: the latest runs, optionally of one source and one state, and the
/// sources its picker names. A seam so the window holds no DbContext (architecture review
/// 2026-11-27 §1).
/// </summary>
public interface IRunsData
{
    /// <param name="withSources">False once the picker has its sources, so they are not read again.</param>
    /// <param name="limit">How many of the latest runs to read.</param>
    Task<RunsRead> ReadAsync(bool withSources, int? sourceId, RunStatus? status, int limit, CancellationToken ct = default);
}

/// <summary>One read of the Runs window. <see cref="Sources"/> is null when it was not asked for.</summary>
public sealed record RunsRead(IReadOnlyList<Source>? Sources, IReadOnlyList<IngestionRun> Runs);

/// <summary>The window's reads, a context per call, exactly as the window made them before.</summary>
public sealed class RunsData(IDbContextFactory<XLinesDbContext> dbFactory) : IRunsData
{
    public async Task<RunsRead> ReadAsync(bool withSources, int? sourceId, RunStatus? status, int limit,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sources = withSources
            ? await db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)
            : null;

        var query = db.IngestionRuns.AsNoTracking().Include(r => r.Source).AsQueryable();

        if (sourceId is { } id)
            query = query.Where(r => r.SourceId == id);

        if (status is { } state)
            query = query.Where(r => r.Status == state);

        var runs = await query.OrderByDescending(r => r.StartedAt).Take(limit).ToListAsync(ct);

        return new RunsRead(sources, runs);
    }
}
