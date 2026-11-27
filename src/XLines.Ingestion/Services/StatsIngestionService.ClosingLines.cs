using System.Text.Json;
using System.Text.Json.Nodes;
using XLines.Core.Analytics;
using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Adapters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace XLines.Ingestion.Services;

/// <summary>
/// Closing lines from a stats provider: the refill that asks for the closes finals are owed, what is
/// owed and set aside, and the writer both the refill and a box-score walk store them through (ADR 0011).
/// </summary>
public partial class StatsIngestionService
{
    /// <summary>
    /// Finished games in one sport without the close they are owed from any source (a fight's
    /// moneyline, any other game's total — <see cref="OwedMarket"/>), asked of a provider
    /// that can state a close per game — the backport for a season the box-score walk stored
    /// without lines. What it finds is written exactly as the walk writes it, under the stats
    /// provider's own source and only where that source holds nothing for the side yet.
    ///
    /// <para>
    /// A game the provider answers without the close it is owed is set aside
    /// (<see cref="ClosingLineMiss"/>): not asked again for two weeks, then four, eight, and at
    /// most every ninety days, because ESPN does fill a close in after the fact — but not for a
    /// fight card from two seasons ago, which every press used to ask about again. Where it has
    /// none, there is none: nothing is fabricated.
    /// </para>
    ///
    /// <para>
    /// The run row says what the press did in <see cref="IngestionRun.Detail"/> — "NFL: 2 asked,
    /// 0 answered; 2 set aside until Nov 23." — and keeps <see cref="IngestionRun.Error"/> for what
    /// went wrong. A write that throws marks the run Failed with its error rather than leaving it
    /// Running.
    /// </para>
    /// </summary>
    /// <param name="now">The moment the press is taken at: what a set-aside game's retry is measured from.</param>
    public async Task<IngestionOutcome> RefillClosingLinesAsync(
        IClosingLineSource source, string sportKey, string jobKey, int limit, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, DateTimeOffset? now = null)
    {
        var at = now ?? clock.GetUtcNow();

        var sourceRow = await RunRecord.SourceRowAsync(db, source.Key, ct);

        var run = new IngestionRun
        {
            SourceId = sourceRow.Id,
            JobKey = jobKey,
            StartedAt = clock.GetUtcNow(),
            Status = RunStatus.Running
        };

        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // A backfill range asks only for its own games; the scheduled job asks for every final.
        // Either way a game set aside by this source is left until its retry date.
        var owedQuery = ClosingLinesOwed(sportKey, sourceRow.Id, source.Key, at);
        if (from is { } since)
            owedQuery = owedQuery.Where(g => g.StartsAt >= since);
        if (to is { } until)
            owedQuery = owedQuery.Where(g => g.StartsAt < until);

        var owed = await owedQuery
            .Include(g => g.HomeTeam)
            .Include(g => g.AwayTeam)
            .OrderByDescending(g => g.StartsAt)
            .Take(limit)
            .ToListAsync(ct);

        var lines = new List<CanonicalOdds>();
        var gameMap = new Dictionary<string, Game>();
        var asked = new List<Game>();
        var requests = 0;
        var answered = 0;
        var failures = 0;

        // A fight's odds live under its card's event, not its own id (ADR 0021).
        var owedIds = owed.Select(g => g.Id).ToList();
        var cards = await db.Bouts
            .Where(b => owedIds.Contains(b.GameId))
            .ToDictionaryAsync(b => b.GameId, b => b.EventId, ct);

        foreach (var game in owed)
        {
            ct.ThrowIfCancellationRequested();

            if (!game.ExternalIds.TryGetValue(source.Key, out var eventId)
                || game.HomeTeam is null || game.AwayTeam is null)
                continue;

            try
            {
                requests++;
                var found = await source.FetchClosingLinesAsync(sportKey,
                    new ClosingLineRequest(eventId, game.HomeTeam.Name, game.AwayTeam.Name, game.StartsAt,
                        cards.GetValueOrDefault(game.Id)), ct);

                gameMap[eventId] = game;
                lines.AddRange(found);
                asked.Add(game);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One game ESPN cannot answer for must not cost the rest of the season. A request
                // that threw is not set aside: the next press asks it again.
                failures++;
                logger.LogWarning(ex, "{Source}: closing odds unavailable for {GameId}", source.Key, eventId);
            }
        }

        var written = 0;
        DateTimeOffset[] setAside = [];

        try
        {
            // Answered means the close the game is owed was written: a reply with a spread and no
            // total, or a moneyline the writer could not place on the bout, leaves the game owed,
            // and a game left owed is set aside so the next press does not ask it again.
            var persisted = await PersistClosingLinesWithClosedAsync(sourceRow, lines, gameMap, ct);
            written = persisted.Written;
            answered = asked.Count(g => persisted.Closed.Contains(g.Id));
            setAside = await SetAsideAsync(sourceRow.Id, asked.Where(g => !persisted.Closed.Contains(g.Id)).ToList(), at, ct);

            run.Status = failures == 0 || written > 0 ? RunStatus.Success : RunStatus.Failed;
            run.Error = failures > 0 ? $"{failures} of {requests} games had no answer from the provider." : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What was not written is not kept half-written: the closes and misses in flight are
            // dropped, and the run says why rather than sitting at Running for ever.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity != run
                         && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
                entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;

            logger.LogError(ex, "{Source}/{Sport}: closing lines could not be written", source.Key, sportKey);

            written = 0;
            setAside = [];
            run.Status = RunStatus.Failed;
            run.Error = RunRecord.Describe(ex);
        }

        run.RequestsMade = requests;
        run.RowsIngested = written;
        run.FinishedAt = clock.GetUtcNow();
        run.Detail = owed.Count == 0
            ? await NothingToAskAsync(sportKey, sourceRow.Id, at, from, to, ct)
            : RefillDetail(sportKey, requests, answered, setAside);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("{Source}/{Sport}: {Detail} {Rows} lines written",
            source.Key, sportKey, run.Detail, written);

        return new IngestionOutcome(run.Id, run.Status, written, run.Error);
    }

    /// <summary>
    /// A press that asked nothing: "NFL: nothing owed." when no final lacks its close, and when
    /// every one that does is set aside, how many and until when — so a quiet run is not read as
    /// a league with nothing missing.
    /// </summary>
    private async Task<string> NothingToAskAsync(
        string sportKey, int sourceId, DateTimeOffset now, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var owed = ClosingLinesOwed(sportKey);
        if (from is { } since)
            owed = owed.Where(g => g.StartsAt >= since);
        if (to is { } until)
            owed = owed.Where(g => g.StartsAt < until);

        var retries = await db.ClosingLineMisses
            .Where(m => m.SourceId == sourceId && m.RetryAt > now && owed.Any(g => g.Id == m.GameId))
            .Select(m => m.RetryAt)
            .ToListAsync(ct);

        return retries.Count == 0
            ? $"{sportKey.ToUpperInvariant()}: nothing owed."
            : WithSetAside($"{sportKey.ToUpperInvariant()}: nothing to ask", retries);
    }

    /// <summary>The first wait after a miss; each further miss doubles it, up to <see cref="MaxMissWait"/>.</summary>
    public static readonly TimeSpan FirstMissWait = TimeSpan.FromDays(14);

    public static readonly TimeSpan MaxMissWait = TimeSpan.FromDays(90);

    /// <summary>How long a game is left after its <paramref name="attempts"/>th miss: 14, 28, 56, then 90 days.</summary>
    public static TimeSpan MissWait(int attempts)
    {
        var days = FirstMissWait.TotalDays * Math.Pow(2, Math.Clamp(attempts, 1, 16) - 1);
        return TimeSpan.FromDays(Math.Min(days, MaxMissWait.TotalDays));
    }

    /// <summary>
    /// Remembers each game asked and left without its close, so the next press does not ask it
    /// again before its retry date. Returns the retry dates written.
    /// </summary>
    private async Task<DateTimeOffset[]> SetAsideAsync(
        int sourceId, IReadOnlyList<Game> games, DateTimeOffset now, CancellationToken ct)
    {
        if (games.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return [];
        }

        var ids = games.Select(g => g.Id).ToList();
        var held = await db.ClosingLineMisses
            .Where(m => m.SourceId == sourceId && ids.Contains(m.GameId))
            .ToDictionaryAsync(m => m.GameId, ct);

        var retries = new List<DateTimeOffset>(games.Count);

        foreach (var game in games)
        {
            if (!held.TryGetValue(game.Id, out var miss))
            {
                miss = new ClosingLineMiss { GameId = game.Id, SourceId = sourceId };
                db.ClosingLineMisses.Add(miss);
                held[game.Id] = miss;
            }

            miss.Attempts++;
            miss.LastTriedAt = now;
            miss.RetryAt = now + MissWait(miss.Attempts);
            retries.Add(miss.RetryAt);
        }

        await db.SaveChangesAsync(ct);
        return [.. retries];
    }

    /// <summary>"NFL: 2 asked, 0 answered; 2 set aside until Nov 23."</summary>
    internal static string RefillDetail(string sportKey, int asked, int answered, IReadOnlyCollection<DateTimeOffset> setAside)
        => WithSetAside($"{sportKey.ToUpperInvariant()}: {asked:N0} asked, {answered:N0} answered", setAside);

    /// <summary>The sentence, and how many games are set aside until when, if any are.</summary>
    private static string WithSetAside(string sentence, IReadOnlyCollection<DateTimeOffset> setAside)
    {
        if (setAside.Count == 0)
            return sentence + ".";

        var first = LeagueClock.DateOf(setAside.Min());
        var last = LeagueClock.DateOf(setAside.Max());
        var until = first == last
            ? Day(first)
            : $"between {Day(first)} and {Day(last)}";

        return $"{sentence}; {setAside.Count:N0} set aside until {until}.";

        static string Day(DateOnly day) => day.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The close a game is owed: a fight's moneyline, because a fight has no total to close at
    /// (ADR 0021); any other game's total, because it is the market a stats provider's walk is
    /// likeliest to have missed and the one every game has.
    /// </summary>
    public static string OwedMarket(string sportKey)
        => FightSports.IsFight(sportKey) ? Markets.Moneyline : Markets.Total;

    /// <summary>
    /// Finals in one sport that no source has given the close they are owed
    /// (<see cref="OwedMarket"/>) — a game with that close from somewhere has had its close read.
    /// </summary>
    public IQueryable<Game> ClosingLinesOwed(string sportKey)
    {
        var market = OwedMarket(sportKey);

        return db.Games.Where(g => g.Sport!.Key == sportKey
                                   && g.Status == GameStatus.Final
                                   && !db.ClosingLines.Any(c => c.GameId == g.Id && c.Market == market));
    }

    /// <summary>
    /// The finals a press will ask <paramref name="sourceId"/> about at <paramref name="now"/>: those
    /// owed a close that the source knows by its own event id, less the ones it answered without
    /// one whose retry date is still ahead. A game with no id for the source cannot be asked, so it
    /// is neither asked nor set aside; counting it would quote games no press reaches. What the
    /// menu quotes is this count, so the quote is what a press asks.
    /// </summary>
    public IQueryable<Game> ClosingLinesOwed(string sportKey, int sourceId, string sourceKey, DateTimeOffset now)
        => ClosingLinesOwed(sportKey)
            .Where(g => EF.Functions.JsonExists(g.ExternalIds, sourceKey))
            .Where(g => !db.ClosingLineMisses.Any(m => m.GameId == g.Id && m.SourceId == sourceId && m.RetryAt > now));

    /// <summary>
    /// How many finals in one sport a press of the refill will ask <paramref name="sourceKey"/>
    /// about, at most <paramref name="limit"/>: zero for a source not registered.
    /// </summary>
    public async Task<int> ClosingLinesToAskAsync(
        string sportKey, string sourceKey, DateTimeOffset now, int limit, CancellationToken ct)
    {
        var sourceId = await db.Sources.Where(s => s.Key == sourceKey).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);

        return sourceId is { } id
            ? Math.Min(await ClosingLinesOwed(sportKey, id, sourceKey, now).CountAsync(ct), limit)
            : 0;
    }

    /// <summary>
    /// How many finals in one sport owed a close <paramref name="sourceKey"/> has set aside until a
    /// retry date still ahead — counted from the misses themselves, so a final the source cannot
    /// be asked about at all (no id for it) is not reported as waiting on a retry it will never get.
    /// </summary>
    public async Task<int> ClosingLinesSetAsideAsync(
        string sportKey, string sourceKey, DateTimeOffset now, CancellationToken ct)
    {
        var sourceId = await db.Sources.Where(s => s.Key == sourceKey).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);

        return sourceId is { } id
            ? await ClosingLinesOwed(sportKey)
                .CountAsync(g => db.ClosingLineMisses.Any(m => m.GameId == g.Id && m.SourceId == id && m.RetryAt > now), ct)
            : 0;
    }

    /// <summary>
    /// Stores what the line closed at, for games that have already finished.
    ///
    /// <para>
    /// These are a <i>reference</i>, never the market. They are written under the stats
    /// provider's own source id, which is what keeps them distinguishable downstream — a reader
    /// wanting the market can require rows from an odds provider and fall back to these only
    /// when a fixture has none. Nothing here can contaminate a book consensus, because nothing
    /// here claims to be one (ADR 0011).
    /// </para>
    ///
    /// <para>
    /// Written once and left alone. A closing line is a fact about a moment that has passed, so
    /// a later walk over the same day must not rewrite it — and re-walking days is the ordinary
    /// case, not the exception. The open the provider states beside it (<see cref="CanonicalOdds.Opening"/>)
    /// rides on the same row and is the one thing a later walk may add to it: a close stored before
    /// opens were read gets its open from the next walk that states one, for the same book only.
    /// </para>
    /// </summary>
    private async Task<int> PersistClosingLinesAsync(
        Source sourceRow,
        IReadOnlyList<CanonicalOdds> lines,
        Dictionary<string, Game> gameMap,
        CancellationToken ct)
        => (await PersistClosingLinesWithClosedAsync(sourceRow, lines, gameMap, ct)).Written;

    /// <summary>
    /// As <see cref="PersistClosingLinesAsync"/>, and which games it actually gave the close they
    /// were owed. The refill decides "answered" from this, not from the reply: a moneyline ESPN
    /// states for a bout whose fighters it names differently is a reply the writer drops, and a
    /// game counted answered on the reply alone was never set aside and was asked again every press.
    /// </summary>
    private async Task<(int Written, HashSet<int> Closed)> PersistClosingLinesWithClosedAsync(
        Source sourceRow,
        IReadOnlyList<CanonicalOdds> lines,
        Dictionary<string, Game> gameMap,
        CancellationToken ct)
    {
        if (lines.Count == 0)
            return (0, []);

        var gameIds = gameMap.Values.Select(g => g.Id).ToHashSet();

        // A fight's close names its fighters as the provider bills them; a fighter whose name is
        // shared is held under a qualified one ("Bruno Silva (Flyweight)"), and a side the board
        // cannot pair is a close nobody reads. So a fight's sides are written as its own (ADR 0021).
        var fighters = await db.Games
            .Where(g => gameIds.Contains(g.Id) && FightSports.Keys.Contains(g.Sport!.Key))
            .Select(g => new { g.Id, Home = g.HomeTeam!.Name, Away = g.AwayTeam!.Name })
            .ToDictionaryAsync(g => g.Id, g => (g.Home, g.Away), ct);

        var held = (await db.ClosingLines
                .Where(c => c.SourceId == sourceRow.Id && gameIds.Contains(c.GameId))
                .Select(c => new { c.GameId, c.Market, c.Outcome })
                .ToListAsync(ct))
            .Select(c => (c.GameId, c.Market, c.Outcome))
            .ToHashSet();

        // The rows already held that a line in hand could give an open to: a close stored before
        // opens were read, or by a read whose provider block had none. Loaded only when some line
        // carries an open, so a pass with none sends exactly the queries it always did.
        Dictionary<(int, string, string), ClosingLine>? openable = null;

        if (lines.Any(l => l.Opening is { IsEmpty: false }))
        {
            openable = (await db.ClosingLines
                    .Where(c => c.SourceId == sourceRow.Id && gameIds.Contains(c.GameId))
                    .ToListAsync(ct))
                .GroupBy(c => (c.GameId, c.Market, c.Outcome))
                .ToDictionary(g => g.Key, g => g.First());
        }

        var written = 0;
        var closed = new HashSet<int>();

        foreach (var line in lines)
        {
            if (!gameMap.TryGetValue(line.SourceGameId, out var game))
                continue;

            var outcome = line.Outcome;

            if (line.Market != Markets.Total && fighters.TryGetValue(game.Id, out var corners))
            {
                if (FighterNames.Same(outcome, corners.Home) && !FighterNames.Same(outcome, corners.Away))
                    outcome = corners.Home;
                else if (FighterNames.Same(outcome, corners.Away) && !FighterNames.Same(outcome, corners.Home))
                    outcome = corners.Away;
            }

            if (!held.Add((game.Id, line.Market, outcome)))
            {
                // The close stands as first written. Its open is filled or corrected from a later
                // read of the same book — never erased by one that states none, and never taken
                // from another book, whose open is not this close's open.
                if (openable is not null
                    && openable.TryGetValue((game.Id, line.Market, outcome), out var stored)
                    && ApplyOpening(stored, line))
                    written++;

                continue;
            }

            var close = new ClosingLine
            {
                GameId = game.Id,
                SourceId = sourceRow.Id,
                Book = line.Book,
                Market = line.Market,
                Outcome = outcome,
                Line = line.Line,
                PriceAmerican = line.PriceAmerican,
                CapturedAt = line.CapturedAt
            };

            ApplyOpening(close, line);
            db.ClosingLines.Add(close);
            openable?.TryAdd((game.Id, line.Market, outcome), close);

            written++;

            if (line.Market == (fighters.ContainsKey(game.Id) ? Markets.Moneyline : Markets.Total))
                closed.Add(game.Id);
        }

        // A game that now has the close it was owed is no longer set aside by anyone: the memory
        // of having asked goes with the debt.
        if (closed.Count > 0)
        {
            db.ClosingLineMisses.RemoveRange(await db.ClosingLineMisses
                .Where(m => closed.Contains(m.GameId))
                .ToListAsync(ct));
        }

        if (written > 0)
            logger.LogInformation("{Source}: {Count} closing lines recorded", sourceRow.Key, written);

        return (written, closed);
    }

    /// <summary>
    /// A line's open onto a close row of the same book: each half the line states replaces the one
    /// held, a half it leaves out stays. A row of another book is left alone.
    /// </summary>
    /// <returns>True when the row's open changed.</returns>
    internal static bool ApplyOpening(ClosingLine row, CanonicalOdds line)
    {
        if (line.Opening is not { IsEmpty: false } opening
            || !string.Equals(row.Book, line.Book, StringComparison.OrdinalIgnoreCase))
            return false;

        var changed = false;

        if (opening.Line is { } openLine && row.OpenLine != openLine)
        {
            row.OpenLine = openLine;
            changed = true;
        }

        if (opening.PriceAmerican is { } openPrice && row.OpenPriceAmerican != openPrice)
        {
            row.OpenPriceAmerican = openPrice;
            changed = true;
        }

        return changed;
    }
}
