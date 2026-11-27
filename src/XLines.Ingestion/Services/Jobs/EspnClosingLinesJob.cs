using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OwedCloses = XLines.Ingestion.Services.IngestionJobs.OwedCloses;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Closing lines owed, <see cref="IngestionJobs.EspnClosingLines"/>, asked of ESPN one game at a time.</summary>
internal sealed class EspnClosingLinesJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    SettleJob settle,
    TimeProvider clock) : IIngestionJob
{
    /// <summary>How many owed games one press asks about, per sport: a season, not a decade.</summary>
    private const int ClosingLinesPerRun = 600;

    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == IngestionJobs.EspnClosingLines;

    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
        => !menu.Espn
            ? JobSupport.None
            : [(await ClosingLinesJobAsync(menu.Services, menu.Sports, menu.Closers, clock.GetUtcNow(), ct)) with { Group = JobGroups.CatchUp }];

    /// <summary>
    /// Every configured sport's finals owed a close (<see cref="StatsIngestionService.OwedMarket"/>),
    /// asked of each stats source that can state one — less the games a source has set aside
    /// until their retry date. Each sport's run row says what it asked; the roll-up keeps "without
    /// a close" apart from "asked", because 523 owed and 3 asked is the difference between a
    /// two-minute press and a one-second one. Settles afterwards: an entry waiting on its close for
    /// CLV gets it now rather than at the next settle.
    /// </summary>
    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        var rows = 0;
        var failures = 0;
        var asked = 0;

        OwedCloses before;
        await using (var scope = scopeFactory.CreateAsyncScope())
            before = await OwedClosesAsync(scope.ServiceProvider, _options.EffectiveSports, clock.GetUtcNow(), ct);

        foreach (var sportKey in _options.EffectiveSports)
        {
            if (ct.IsCancellationRequested)
                break;

            await using var scope = scopeFactory.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
            var stats = scope.ServiceProvider.GetRequiredService<StatsIngestionService>();
            var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

            foreach (var source in registry.StatsSources.OfType<Core.Contracts.IClosingLineSource>())
            {
                var outcome = await stats.RefillClosingLinesAsync(source, sportKey, IngestionJobs.EspnClosingLines, ClosingLinesPerRun, ct);
                rows += outcome.RowsIngested;
                asked += await db.IngestionRuns.Where(r => r.Id == outcome.RunId).Select(r => r.RequestsMade).FirstOrDefaultAsync(ct);

                if (outcome.Status == RunStatus.Failed)
                    failures++;
            }
        }

        if (rows > 0)
            await settle.RunAsync(new JobRun(IngestionJobs.Settle), ct);

        OwedCloses after;
        await using (var scope = scopeFactory.CreateAsyncScope())
            after = await OwedClosesAsync(scope.ServiceProvider, _options.EffectiveSports, clock.GetUtcNow(), ct);

        return new JobOutcome(IngestionJobs.EspnClosingLines, failures == 0, rows, failures,
            Sentence(rows, asked, before.WithoutClose - after.WithoutClose, after));
    }

    /// <summary>
    /// "0 closing lines written; 0 of 3 games asked got a close; 520 set aside until their retry date."
    /// </summary>
    internal static string Sentence(int rows, int asked, int closed, OwedCloses after)
    {
        if (asked == 0 && after.WithoutClose == 0)
            return $"{rows:N0} closing lines written; no game is owed a close.";

        return $"{rows:N0} closing lines written; {Math.Max(0, closed):N0} of {asked:N0} games asked got a close"
               + (after.SetAside > 0 ? $"; {after.SetAside:N0} set aside until their retry date" : "")
               + (after.ToAsk > 0 ? $"; {after.ToAsk:N0} still to ask" : "")
               + ".";
    }

    /// <summary>
    /// The closing-line refill as the menu offers it: the estimate counts only the games a press
    /// will ask, and the description says how many are without a close and how many of those
    /// that is — "523 without a close · 3 to ask".
    /// </summary>
    private static async Task<IngestionJob> ClosingLinesJobAsync(
        IServiceProvider provider, IReadOnlyList<string> sports, int closers, DateTimeOffset now, CancellationToken ct)
    {
        var owed = closers > 0 ? await OwedClosesAsync(provider, sports, now, ct) : new OwedCloses(0, 0);

        return new IngestionJob(
            Key: IngestionJobs.EspnClosingLines,
            Label: "Closing lines owed",
            Description: "ESPN's closing moneyline, spread and total for finished games without the close they are owed "
                         + "(a fight's moneyline, a game's total), one request per game. Where ESPN has no line, none is "
                         + "written, and the game is set aside — two weeks the first time, longer each time after — before it is asked again."
                         + (closers > 0 ? $" {owed.WithoutClose:N0} without a close · {owed.ToAsk:N0} to ask." : ""),
            EstimatedRequests: owed.ToAsk,
            Available: closers > 0,
            Unavailable: closers > 0 ? null : "No stats source states closing lines.");
    }

    /// <summary>
    /// Finals without the close they are owed, and of those the ones a press would ask each
    /// closing-line source about now — not set aside, and within a run's ceiling.
    /// </summary>
    private static async Task<OwedCloses> OwedClosesAsync(IServiceProvider provider, IReadOnlyList<string> sports, DateTimeOffset now, CancellationToken ct)
    {
        var stats = provider.GetRequiredService<StatsIngestionService>();
        var closers = provider.GetRequiredService<SourceRegistry>().StatsSources
            .OfType<Core.Contracts.IClosingLineSource>()
            .Select(s => s.Key)
            .ToList();
        var without = 0;
        var toAsk = 0;
        var setAside = 0;

        foreach (var sport in sports)
        {
            without += await stats.ClosingLinesOwed(sport).CountAsync(ct);

            foreach (var closer in closers)
            {
                toAsk += await stats.ClosingLinesToAskAsync(sport, closer, now, ClosingLinesPerRun, ct);
                setAside += await stats.ClosingLinesSetAsideAsync(sport, closer, now, ct);
            }
        }

        // Set aside is counted from the misses, not derived: "owed less askable" also took in
        // finals the source has no id for, which wait on no retry, and a run's ceiling, which is
        // not a setting-aside — 700 owed and none set aside is 600 to ask now and 100 after.
        return new OwedCloses(without, toAsk, setAside);
    }
}
