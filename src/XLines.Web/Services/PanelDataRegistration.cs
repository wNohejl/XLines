namespace XLines.Web.Services;

/// <summary>
/// The panels' data services (architecture review 2026-11-27 §1): every query and write a window
/// used to hold in its <c>@code</c>, one service per window, so no <c>.razor</c> file holds a
/// DbContext. Scoped, as the windows they serve are: each reads the reader's tier from the circuit.
/// </summary>
public static class PanelDataRegistration
{
    public static IServiceCollection AddXLinesPanelData(this IServiceCollection services)
    {
        services.AddScoped<IRunsData, RunsData>();
        services.AddScoped<IHistoryData, HistoryData>();
        services.AddScoped<IOpsData, OpsData>();
        services.AddScoped<IPlayerData, PlayerData>();
        services.AddScoped<ITeamData, TeamData>();
        services.AddScoped<IPlayersData, PlayersData>();
        services.AddScoped<IMarketMovementData, MarketMovementData>();
        services.AddScoped<IBoardData, BoardData>();
        services.AddScoped<IJournalData, JournalData>();
        return services;
    }
}
