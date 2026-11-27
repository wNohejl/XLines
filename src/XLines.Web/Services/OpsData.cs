using XLines.Core.Contracts;
using XLines.Core.Entities;
using XLines.Data;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using XLines.Reliability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace XLines.Web.Services;

/// <summary>
/// What the Ops window reads and writes: source health, the alerts, the month's allowance and the
/// scanning plan, and the operator's keys — scanning, a fault, an evaluation, a drill, a promotion.
/// A seam so the window holds no DbContext (architecture review 2026-11-27 §1). The writes are an
/// operator's: the window refuses a visitor's press before it reaches here (ADR 0018).
/// </summary>
public interface IOpsData
{
    /// <summary>Everything one load reads. The reads share one scope's context, one after another.</summary>
    Task<OpsRead> ReadAsync(CancellationToken ct = default);

    /// <summary>Stores whether lines are scanned unasked.</summary>
    Task SetLinePollingAsync(bool automatic);

    /// <summary>Sets or clears the fault injected on a source.</summary>
    Task SetFailureModeAsync(int sourceId, string? mode);

    /// <summary>Rolls the day up and evaluates the alerts.</summary>
    Task EvaluateAsync();

    /// <summary>The league a drill would pull: the configured one with the most games still to start; null when none is.</summary>
    Task<string?> DrillSportAsync();

    /// <summary>
    /// Runs the drill on <paramref name="asked"/> (or the league <see cref="DrillSportAsync"/> finds),
    /// then rolls up and evaluates. Returns the league pulled, or null when there was none to pull
    /// and nothing ran.
    /// </summary>
    Task<string?> DrillAsync(string? asked);

    /// <summary>Promotes an alert to an incident.</summary>
    Task PromoteAsync(long alertId);
}

/// <summary>What one load of the Ops window read, handed over at once.</summary>
public sealed record OpsRead(
    IReadOnlyList<OddsProviderState> Feeds,
    DeskOddsKey? OddsKey,
    bool PollingSetHere,
    bool Unattended,
    PollPlan? Plan,
    IReadOnlyList<SportMarkets> MarketsBySport,
    StatsPort Port,
    ScoresPlan? Scores,
    AllowanceState? Allowance,
    IReadOnlyList<SourceHealth> Health,
    IReadOnlyList<Alert> Alerts,
    int OpenIncidents,
    long RowsToday,
    IReadOnlyList<Source> Injectable);

/// <summary>The window's reads and writes, a scope per call, exactly as the window made them before.</summary>
public sealed class OpsData(
    IServiceScopeFactory scopeFactory,
    IOptions<ReliabilityOptions> options,
    IOptions<IngestionOptions> ingestion) : IOpsData
{
    private LinePollingOptions LinePolling => ingestion.Value.LinePolling;

    public async Task<OpsRead> ReadAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        var pollingSetHere = await db.GetSettingAsync(AppSetting.LinePolling, ct) is not null;
        var unattended = await db.LinesRunUnattendedAsync(LinePolling.RunsUnattended, ct);

        var kpi = scope.ServiceProvider.GetRequiredService<KpiCalculator>();
        var alertEngine = scope.ServiceProvider.GetRequiredService<AlertEngine>();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();

        var feeds = scope.ServiceProvider.GetRequiredService<OddsFeedStatus>().Describe();
        var oddsKey = scope.ServiceProvider.GetService<DeskOddsKey>();

        // Recomputed here rather than read off the scheduler: it is a pure function of the
        // budget already spent, so the panel and the scheduler reach the same answer without
        // the panel needing a handle on a background service.
        var planner = scope.ServiceProvider.GetRequiredService<LinePollPlanner>();
        var oddsKeys = registry.OddsSources.Select(s => s.Key).ToList();
        var plan = await planner.PlanAsync(oddsKeys, ct);
        IReadOnlyList<SportMarkets> marketsBySport = oddsKeys.Count == 0 ? [] : planner.MarketsBySport(oddsKeys);

        var scores = await planner.ScoresPlanAsync(ct);
        var allowance = registry.FindOdds(XLines.Ingestion.Adapters.TheOddsApiAdapter.SourceKey) is not null || registry.Scores is not null
            ? await scope.ServiceProvider.GetRequiredService<CreditAllowance>().ReadAsync(ct)
            : null;

        var o = options.Value;

        var health = await kpi.GetAllHealthAsync(o.SuccessRateWindow, o.VolumeBaselineDays, ct);
        var alerts = await alertEngine.GetOpenAlertsAsync(ct);
        var openIncidents = await db.Incidents.CountAsync(i => i.Status != IncidentStatus.Resolved, ct);

        var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var rowsToday = await db.IngestionRuns
            .Where(r => r.StartedAt >= dayStart)
            .SumAsync(r => (long?)r.RowsIngested, ct) ?? 0;

        var keys = registry.OddsSources.OfType<IFailureInjectable>().Cast<IOddsSource>().Select(s => s.Key)
            .Concat(registry.StatsSources.OfType<IFailureInjectable>().Cast<IStatsSource>().Select(s => s.Key))
            .ToList();

        var injectable = await db.Sources.Where(s => keys.Contains(s.Key)).OrderBy(s => s.Name).ToListAsync(ct);

        return new OpsRead(feeds, oddsKey, pollingSetHere, unattended, plan, marketsBySport, registry.Port,
            scores, allowance, health, alerts, openIncidents, rowsToday, injectable);
    }

    public async Task SetLinePollingAsync(bool automatic)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        await db.SetSettingAsync(AppSetting.LinePolling,
            automatic ? AppSetting.LinePollingScheduled : AppSetting.LinePollingManual);
    }

    public async Task SetFailureModeAsync(int sourceId, string? mode)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<XLinesDbContext>();

        var source = await db.Sources.FirstAsync(s => s.Id == sourceId);
        source.FailureMode = string.IsNullOrEmpty(mode) ? null : mode;
        await db.SaveChangesAsync();
    }

    public async Task EvaluateAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KpiCalculator>().RollupDailyAsync();
        await scope.ServiceProvider.GetRequiredService<AlertEngine>().EvaluateAsync();
    }

    public async Task<string?> DrillSportAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return IngestionJobs.DrillSport(
            await UpcomingBySportAsync(scope.ServiceProvider),
            ingestion.Value.EffectiveSports);
    }

    public async Task<string?> DrillAsync(string? asked)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<SourceRegistry>();
        var ingest = scope.ServiceProvider.GetRequiredService<OddsIngestionService>();

        var sport = asked ?? IngestionJobs.DrillSport(
            await UpcomingBySportAsync(scope.ServiceProvider),
            ingestion.Value.EffectiveSports);

        if (sport is null)
            return null;

        foreach (var source in registry.OddsSources)
            await ingest.IngestAsync(source, sport, "odds:drill", CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<KpiCalculator>().RollupDailyAsync();
        await scope.ServiceProvider.GetRequiredService<AlertEngine>().EvaluateAsync();

        return sport;
    }

    public async Task PromoteAsync(long alertId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var incidents = scope.ServiceProvider.GetRequiredService<IncidentService>();

        await incidents.PromoteAsync(alertId);
    }

    /// <summary>
    /// Games inside the movement window, by sport slug — what the drill reads to decide which
    /// league is worth spending a credit on. The window is the ingestion one so the drill and a
    /// real pull agree about which fixtures are still priceable.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> UpcomingBySportAsync(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<XLinesDbContext>();
        var horizon = DateTimeOffset.UtcNow + ingestion.Value.MovementWindow;

        var counts = await db.Games
            .Where(g => g.StartsAt > DateTimeOffset.UtcNow && g.StartsAt <= horizon)
            .GroupBy(g => g.Sport!.Key)
            .Select(g => new { Sport = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(c => c.Sport, c => c.Count, StringComparer.OrdinalIgnoreCase);
    }
}
