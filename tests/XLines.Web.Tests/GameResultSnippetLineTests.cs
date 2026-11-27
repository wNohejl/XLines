using Bunit;
using XLines.Core.Entities;
using XLines.Data.CrossReference;
using XLines.Web.Components.Snippets;

namespace XLines.Web.Tests;

/// <summary>
/// The Game result snippet's closing spread, read from the numeric column at its scale, printed
/// "-3.00" and "+2.50" from a copy of the line format the panels had already fixed. It prints as
/// every other window does now (<see cref="XLines.Web.Components.OddsText"/>), and a game with no
/// closing spread still shows a dash.
/// </summary>
public class GameResultSnippetLineTests : DeskTestContext
{
    private static TeamGameLogRow Row(decimal? spread) => new(
        GameId: 1, StartsAt: new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), Opponent: "Away", Home: true,
        ScoreFor: 24, ScoreAgainst: 20, Status: GameStatus.Final, ClosingSpread: spread, AtsResult: null,
        ClosingTotal: null, TotalResult: null);

    private string ClosingSpread(decimal? spread)
    {
        var cut = RenderComponent<GameResultSnippet>(p => p.Add(s => s.Row, Row(spread)));

        return cut.FindAll(".fact")
            .Single(f => f.QuerySelector(".fact__label")!.TextContent.Trim() == "Closing spread")
            .QuerySelector(".fact__value")!.TextContent.Trim();
    }

    [Theory]
    [InlineData("-3.00", "-3")]
    [InlineData("2.50", "+2.5")]
    [InlineData("0.00", "0")]
    public void A_scaled_closing_spread_prints_as_a_book_prints_it(string spread, string expected)
        => Assert.Equal(expected, ClosingSpread(decimal.Parse(spread, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void No_closing_spread_is_a_dash()
        => Assert.Equal("—", ClosingSpread(null));
}
