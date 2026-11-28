using System.Text.RegularExpressions;
using XLines.Core.Entities;
using XLines.Web.Services;
using XLines.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Web.Tests;

/// <summary>
/// Every window reads through a data service (architecture review 2026-11-27 §1): no <c>.razor</c>
/// file holds a DbContext, each service is registered once, scoped, and a failed read reaches the
/// window that asked. Plain C#, no render. The Web tests have no Postgres fixture, so the queries
/// themselves are not run here.
/// </summary>
public class PanelDataTests
{
    [Fact]
    public void No_razor_file_in_the_web_host_holds_a_db_context()
    {
        var components = Path.Combine(Repo.Root(), "src", "XLines.Web");
        // A window's code may sit in its code-behind partials beside the .razor (OpsPanel.razor.cs,
        // BoardPanel.Rows.cs), so every C# file under Components is read with the markup.
        var holders = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(components, "Components"), "*.cs", SearchOption.AllDirectories))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "IDbContextFactory|XLinesDbContext"))
            .Select(f => Path.GetRelativePath(components, f))
            .ToList();

        Assert.Empty(holders);
    }

    public static TheoryData<Type, Type> Services() => new()
    {
        { typeof(IRunsData), typeof(RunsData) },
        { typeof(IHistoryData), typeof(HistoryData) },
        { typeof(IOpsData), typeof(OpsData) },
        { typeof(IPlayerData), typeof(PlayerData) },
        { typeof(ITeamData), typeof(TeamData) },
        { typeof(IPlayersData), typeof(PlayersData) },
        { typeof(IMarketMovementData), typeof(MarketMovementData) },
        { typeof(IBoardData), typeof(BoardData) },
        { typeof(IJournalData), typeof(JournalData) },
    };

    [Theory]
    [MemberData(nameof(Services))]
    public void Each_service_is_registered_once_and_scoped_to_the_circuit(Type service, Type implementation)
    {
        var registered = new ServiceCollection().AddXLinesPanelData()
            .Where(d => d.ServiceType == service)
            .ToList();

        var only = Assert.Single(registered);
        Assert.Equal(implementation, only.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, only.Lifetime);
    }

    [Fact]
    public async Task A_runs_read_that_cannot_reach_the_database_fails_to_the_window()
    {
        var runs = new RunsData(new UnreachableFactory());

        await Assert.ThrowsAsync<NotSupportedException>(
            () => runs.ReadAsync(withSources: true, sourceId: 1, status: RunStatus.Failed, limit: 200));
    }

    [Fact]
    public async Task A_market_read_that_cannot_reach_the_database_fails_to_the_card()
    {
        var market = new MarketMovementData(new UnreachableFactory(), XLines.Core.Contracts.FullDataTier.Instance);

        await Assert.ThrowsAsync<NotSupportedException>(() => market.SnapshotsAsync(7, "h2h"));
    }

    [Theory]
    [InlineData(2025, 2025)] // asked for, and played in: kept
    [InlineData(2019, 2026)] // asked for, never played in: the newest
    [InlineData(null, 2026)] // nothing asked: the newest
    public void A_subject_s_season_is_the_one_asked_while_played_in_else_the_newest(int? asked, int expected)
        => Assert.Equal(expected, SubjectSeasons.Pick([2026, 2025, 2024], asked));

    [Fact]
    public void A_subject_with_no_season_has_none()
        => Assert.Null(SubjectSeasons.Pick([], 2025));
}
