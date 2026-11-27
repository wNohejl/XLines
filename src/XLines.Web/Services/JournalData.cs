using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Services;
using Microsoft.EntityFrameworkCore;

namespace XLines.Web.Services;

/// <summary>
/// What the Journal window reads and writes: the entries and parlays under a filter with the lists
/// its pickers offer, and every change the window makes to the journal — save, delete, grade,
/// settle — and a game to open by id. A seam so the window holds no DbContext (architecture review
/// 2026-11-27 §1).
/// </summary>
public interface IJournalData
{
    /// <summary>
    /// One load: two strands, each on a context of its own, so the wait is the slower one's. A
    /// failure in either still propagates.
    /// </summary>
    Task<JournalRead> ReadAsync(JournalFilter filter, CancellationToken ct = default);

    Task UpdateParlayAsync(Guid id, ParlayEdit edit);

    Task SaveEntryAsync(EntryDraft draft, int? id);

    Task DeleteEntryAsync(int id);

    Task DeleteParlayAsync(Guid id);

    /// <summary>Records a result on a single entry, as the row's grade keys set it.</summary>
    Task SetResultAsync(int entryId, EntryResult result);

    Task SetParlayResultAsync(Guid id, EntryResult result, decimal? payout);

    /// <summary>Grades everything pending that can be graded.</summary>
    Task<SettlementSummary> SettleAsync();

    /// <summary>A game with both sides, to open the Game window on; null when it is gone.</summary>
    Task<Game?> GameAsync(int gameId);
}

/// <summary>Everything one load of the Journal shows: the rows under the filter, and the lists its pickers offer.</summary>
public sealed record JournalRead(
    IReadOnlyList<JournalEntry> Entries,
    IReadOnlyList<Parlay> Parlays,
    IReadOnlyList<string> Books,
    IReadOnlyList<string> Sports,
    int Pending);

/// <summary>The window's reads and writes, exactly as the window made them before.</summary>
public sealed class JournalData(IDbContextFactory<XLinesDbContext> dbFactory, IServiceScopeFactory scopeFactory) : IJournalData
{
    public async Task<JournalRead> ReadAsync(JournalFilter filter, CancellationToken ct = default)
    {
        // Two strands, each on a context of its own, so the wait is the slower one's. The
        // journal's three reads share the scope's one context and stay one after another, as do
        // the picker and the pending count on theirs. A failure in either still propagates.
        var journalRead = ReadJournalAsync(filter, ct);
        var pickerRead = ReadPickerAsync(ct);

        await Task.WhenAll(journalRead, pickerRead);

        var (entries, parlays, books) = await journalRead;
        var (sports, pending) = await pickerRead;

        return new JournalRead(entries, parlays, books, sports, pending);
    }

    private async Task<(IReadOnlyList<JournalEntry>, IReadOnlyList<Parlay>, IReadOnlyList<string>)> ReadJournalAsync(
        JournalFilter filter, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var journal = scope.ServiceProvider.GetRequiredService<JournalService>();

        var entries = await journal.EntriesAsync(filter, ct);
        var parlays = await journal.ParlaysAsync(filter, ct);
        var books = await journal.KnownBooksAsync(ct);

        return (entries, parlays, books);
    }

    private async Task<(IReadOnlyList<string>, int)> ReadPickerAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sports = await db.Sports.AsNoTracking().Where(s => s.Enabled).OrderBy(s => s.Key).Select(s => s.Key).ToListAsync(ct);

        var pending = await db.JournalEntries.CountAsync(e => e.Result == EntryResult.Pending && e.ParlayGroupId == null, ct)
                      + await db.Parlays.CountAsync(p => p.Result == EntryResult.Pending, ct);

        return (sports, pending);
    }

    public async Task UpdateParlayAsync(Guid id, ParlayEdit edit)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JournalService>().UpdateParlayAsync(id, edit);
    }

    public async Task SaveEntryAsync(EntryDraft draft, int? id)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JournalService>().SaveEntryAsync(draft, id);
    }

    public async Task DeleteEntryAsync(int id)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JournalService>().DeleteEntryAsync(id);
    }

    public async Task DeleteParlayAsync(Guid id)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JournalService>().DeleteParlayAsync(id);
    }

    public async Task SetResultAsync(int entryId, EntryResult result)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entry = await db.JournalEntries.FirstAsync(e => e.Id == entryId);

        PerformanceAnalytics.ApplyResult(entry, result);
        await db.SaveChangesAsync();
    }

    public async Task SetParlayResultAsync(Guid id, EntryResult result, decimal? payout)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JournalService>().SetParlayResultAsync(id, result, payout);
    }

    public async Task<SettlementSummary> SettleAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SettlementService>().SettleAsync();
    }

    public async Task<Game?> GameAsync(int gameId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        return await db.Games
            .Include(g => g.HomeTeam).Include(g => g.AwayTeam)
            .FirstOrDefaultAsync(g => g.Id == gameId);
    }
}
