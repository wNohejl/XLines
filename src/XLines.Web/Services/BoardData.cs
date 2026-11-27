using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Data.CrossReference;
using XLines.Ingestion.Services;
using XLines.Web.Components.Snippets;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Board reads: its rows, today's moves, the alert counts, the league names, which stats
/// source fills it and the books the desk polls. A seam so the window holds no DbContext
/// (architecture review 2026-11-27 §1).
/// </summary>
public interface IBoardData
{
    /// <summary>
    /// The board's reads, together: the rows, today's moves, the alert counts and the league names
    /// each have a context of their own and none needs another's answer, so the wait is the
    /// slowest read's rather than the sum.
    /// </summary>
    /// <param name="sport">The league's key, or null for every league.</param>
    Task<BoardRead> ReadAsync(string? sport, TimeSpan window, TimeSpan lookback, CancellationToken ct = default);

    /// <summary>Open alerts and the critical ones among them, each counted on a context of its own so they go out together.</summary>
    Task<AlertCounts> AlertCountsAsync(CancellationToken ct = default);
}

/// <summary>Open alerts, and how many of them are critical.</summary>
public sealed record AlertCounts(int Open, int Critical);

/// <summary>One load of the Board.</summary>
/// <param name="Port">Which stats source fills the board (ADR 0026); null with no ingestion services.</param>
/// <param name="Names">The enabled leagues' names by key, for the league filter.</param>
/// <param name="Polled">The books the desk polls (PolledBooks); empty with no odds provider live.</param>
public sealed record BoardRead(
    IReadOnlyList<BoardRow> Rows,
    StatsPort? Port,
    long Moves,
    AlertCounts Alerts,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyList<string> Polled,
    IReadOnlyList<string> PaidOnly);

/// <summary>The Board's reads, exactly as the window made them before.</summary>
public sealed class BoardData(
    IDbContextFactory<XLinesDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    IDataTier tier) : IBoardData
{
    public async Task<BoardRead> ReadAsync(string? sport, TimeSpan window, TimeSpan lookback, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var board = scope.ServiceProvider.GetRequiredService<BoardService>();

        var rowsRead = board.GetAsync(sport, window, lookback, ct);
        var movesRead = MovesTodayAsync(ct);
        var alertsRead = AlertCountsAsync(ct);
        var namesRead = SportNamesAsync(ct);

        // A failure in any of them still reaches the caller, so the window says the board did
        // not load exactly as it did when they ran one after another.
        await Task.WhenAll(rowsRead, movesRead, alertsRead, namesRead);

        var rows = (await rowsRead).ToList();

        // Which stats source fills the board (ADR 0026), for the empty state's sentence and key.
        var port = scope.ServiceProvider.GetService<SourceRegistry>()?.Port;

        // The books the desk polls (PolledBooks), so an unpriced row and the Priced tile
        // account for them; empty with no odds provider live, and both read as before.
        var feeds = PolledBooks.Feeds(scope.ServiceProvider);
        var polled = PolledBooks.Of(feeds);
        var paidOnly = MainCoverage.PaidOnlyOf(feeds);

        return new BoardRead(rows, port, await movesRead, await alertsRead, await namesRead, polled, paidOnly);
    }

    /// <summary>Price moves written today, counted over the scans this reader may see (ADR 0024), as the rows are priced.</summary>
    private async Task<long> MovesTodayAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dayStart = LeagueClock.StartOf(LeagueClock.Today());
        return await db.OddsSnapshots.FromVisible(await db.VisibleSourceIdsAsync(tier, ct))
            .LongCountAsync(s => s.CapturedAt >= dayStart, ct);
    }

    public async Task<AlertCounts> AlertCountsAsync(CancellationToken ct = default)
    {
        var open = CountAlertsAsync(critical: false, ct);
        var critical = CountAlertsAsync(critical: true, ct);
        await Task.WhenAll(open, critical);

        return new AlertCounts(await open, await critical);
    }

    private async Task<int> CountAlertsAsync(bool critical, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return critical
            ? await db.Alerts.CountAsync(a => a.ResolvedAt == null && a.Severity == AlertSeverity.Critical, ct)
            : await db.Alerts.CountAsync(a => a.ResolvedAt == null, ct);
    }

    /// <summary>The enabled leagues' names by key, for the league filter.</summary>
    private async Task<Dictionary<string, string>> SportNamesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await db.Sports
            .Where(s => s.Enabled)
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Name, StringComparer.OrdinalIgnoreCase, ct);
    }
}
