using XLines.Core.Analytics;
using XLines.Core.Entities;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace XLines.Ingestion.Services.Jobs;

/// <summary>Play-by-play owed, <see cref="IngestionJobs.EspnGamecastBackport"/>: one batch of the gamecast backport a run.</summary>
internal sealed class EspnGamecastBackportJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    TimeProvider clock) : IIngestionJob
{
    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == IngestionJobs.EspnGamecastBackport;

    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        if (!menu.Espn)
            return JobSupport.None;

        // Plays for the finals from before the feed: two requests a game, a batch a press.
        var gamecasts = menu.Services.GetService<Adapters.IGamecastSource>() is not null;
        var batch = Math.Max(0, _options.Gamecast.BackportBatch);

        if (menu.Services.GetService<GamecastBackport>() is not { } backport)
            return JobSupport.None;

        return
        [
            new IngestionJob(
                Key: IngestionJobs.EspnGamecastBackport,
                Label: "Play-by-play owed",
                Description: "Plays, drives and win, spread and total probabilities for finished games that have none, "
                             + $"newest first, {batch} games a press at two requests each. A game ESPN has no plays for "
                             + "is set aside rather than asked for again.",
                EstimatedRequests: gamecasts ? Math.Min(await backport.OwedCountAsync(clock.GetUtcNow(), ct), batch) * 2 : 0,
                Available: gamecasts && batch > 0,
                Unavailable: !gamecasts ? "No gamecast source registered."
                    : batch > 0 ? null : "Switched off (Gamecast:BackportBatch).",
                Group: JobGroups.CatchUp)
        ];
    }

    /// <summary>
    /// One batch of the gamecast backport. One batch, not the whole debt, so the scheduler's tick
    /// is never held for hours; the outcome says whether more is owed, which is what
    /// <c>--until-done</c> on the Worker repeats on.
    /// </summary>
    public async Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var backport = scope.ServiceProvider.GetRequiredService<GamecastBackport>();

        var outcome = await backport.RunAsync(clock.GetUtcNow(), ct);

        // A game with no plays is set aside, not failed; only a batch that came away with none
        // because every request threw is.
        return new JobOutcome(IngestionJobs.EspnGamecastBackport, outcome.Status != RunStatus.Failed, outcome.Rows,
            outcome.Status == RunStatus.Failed ? outcome.Misses : 0, outcome.Detail,
            More: outcome.RunId != 0 && outcome.StillOwed > 0);
    }
}

/// <summary>Fighter profiles owed, <see cref="IngestionJobs.EspnFighters"/> (ADR 0021).</summary>
internal sealed class EspnFightersJob(
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options) : IIngestionJob
{
    /// <summary>Fighters one press describes: two full cards' worth, a few minutes at ESPN's pace.</summary>
    private const int FightersPerRun = 60;

    private readonly IngestionOptions _options = options.Value;

    public bool Matches(string key) => key == IngestionJobs.EspnFighters;

    public async ValueTask<IReadOnlyList<IngestionJob>> DescribeAsync(JobMenu menu, CancellationToken ct)
    {
        // Only a desk that holds a fought sport has fighters to describe.
        if (!menu.Espn || !menu.Sports.Any(FightSports.IsFight))
            return JobSupport.None;

        var owedFighters = 0;
        var fighters = menu.Services.GetRequiredService<FighterProfileService>();

        foreach (var sport in menu.Sports.Where(FightSports.IsFight))
            owedFighters += await fighters.OwedCountAsync(sport, ct);

        return
        [
            new IngestionJob(
                Key: IngestionJobs.EspnFighters,
                Label: "Fighter profiles owed",
                Description: "Bio, career rates and every recorded fight with ESPN's stat sheet, for fighters on a card "
                             + $"ahead or who have fought since theirs was taken — {IngestionJobs.RequestsPerFighter} requests a fighter, "
                             + $"{FightersPerRun} fighters a press.",
                EstimatedRequests: Math.Min(owedFighters, FightersPerRun) * IngestionJobs.RequestsPerFighter,
                Available: menu.Stats > 0,
                Unavailable: menu.Stats > 0 ? null : "No stats source registered.",
                Group: JobGroups.CatchUp)
        ];
    }

    public Task<JobOutcome> RunAsync(JobRun run, CancellationToken ct) => RunFightersAsync(FightersPerRun, ct);

    /// <summary>
    /// Profiles for the fighters owed one, in every configured fought sport (ADR 0021). A fighter
    /// ESPN cannot describe is retried on the next press; the rest are not held up by it.
    /// </summary>
    private async Task<JobOutcome> RunFightersAsync(int limit, CancellationToken ct)
    {
        var rows = 0;
        var failures = 0;
        var stillOwed = 0;

        foreach (var sportKey in _options.EffectiveSports.Where(FightSports.IsFight))
        {
            if (ct.IsCancellationRequested)
                break;

            await using var scope = scopeFactory.CreateAsyncScope();
            var fighters = scope.ServiceProvider.GetRequiredService<FighterProfileService>();

            var outcome = await fighters.RefreshAsync(sportKey, IngestionJobs.EspnFighters, limit, ct);
            rows += outcome.RowsIngested;

            if (outcome.Status == RunStatus.Failed)
                failures++;

            stillOwed += await fighters.OwedCountAsync(sportKey, ct);
        }

        return new JobOutcome(IngestionJobs.EspnFighters, failures == 0, rows, failures,
            $"{rows:N0} fighter {(rows == 1 ? "profile" : "profiles")} taken"
            + (stillOwed > 0 ? $"; {stillOwed:N0} still owed." : "."));
    }
}
