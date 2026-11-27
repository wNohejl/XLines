using System.Text.Json;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using XLines.Ingestion.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace XLines.Tests.Adapters;

/// <summary>
/// What a configured book list will not buy is said before a pull, not discovered after one
/// (ADR 0033): a book The Odds API sells only on paid plans, and a list too long to send by name.
/// And the ten the desk ships with are ten free books.
/// </summary>
public class TheOddsApiBooksTests
{
    private static readonly string[] Shipped =
        ["pinnacle", "draftkings", "fanduel", "betmgm", "betrivers", "espnbet", "hardrockbet", "bovada", "betonlineag", "ballybet"];

    [Fact]
    public void Caesars_and_Fanatics_are_named_as_needing_a_paid_plan()
    {
        var notes = TheOddsApiBooks.Notes(["pinnacle", "williamhill_us", "draftkings", "fanatics"]);

        Assert.Equal(
            "Caesars and Fanatics need a paid plan at The Odds API; on the free plan they return no prices, and still take a slot of the ten.",
            Assert.Single(notes));
    }

    [Theory]
    [InlineData("williamhill_us")]
    [InlineData("fanatics")]
    [InlineData("bet365_au")]
    [InlineData("courtside")]
    [InlineData("rebet")]
    [InlineData("bet99_ca_on")]
    [InlineData("dabble_au")]
    public void Every_book_the_provider_marks_paid_only_is_known(string key)
        => Assert.True(TheOddsApiBooks.PaidOnly.ContainsKey(key));

    [Fact]
    public void Past_ten_the_status_says_the_list_is_ignored_and_which_books_are_lost()
    {
        var eleven = Shipped.Append("lowvig").ToArray();

        var note = Assert.Single(TheOddsApiBooks.Notes(eleven));

        Assert.Contains("11 books are configured", note);
        Assert.Contains("regions=us", note);
        Assert.Contains("pinnacle, espnbet, hardrockbet and ballybet are not in that region", note);
        Assert.Equal(["pinnacle", "espnbet", "hardrockbet", "ballybet"], TheOddsApiBooks.LostPastTen(eleven));
    }

    [Fact]
    public void The_shipped_ten_are_ten_free_books_with_nothing_to_say()
    {
        foreach (var host in new[] { "XLines.Web", "XLines.Worker" })
        {
            var books = Configured(host);

            Assert.Equal(Shipped, books);
            Assert.DoesNotContain("lowvig", books);
            Assert.Empty(TheOddsApiBooks.Notes(books));
        }
    }

    [Fact]
    public void A_paid_only_book_still_counts_in_the_price_and_past_ten_the_price_does_not_double()
    {
        // Ten with Caesars in a slot: one region unit, the market count. Eleven: the named list is
        // dropped for regions=us, also one unit — the status says what that loses (above).
        Assert.Equal(3, Adapter([.. Shipped[..9], "williamhill_us"]).CreditsPerScan("nfl"));
        Assert.Equal(3, Adapter([.. Shipped, "williamhill_us"]).CreditsPerScan("nfl"));
    }

    [Fact]
    public void The_feed_status_carries_the_note_in_the_reason_Ops_draws()
    {
        var options = new IngestionOptions();
        options.TheOddsApi.Enabled = true;
        options.TheOddsApi.ApiKey = "k";
        options.TheOddsApi.Bookmakers = ["pinnacle", "draftkings", "williamhill_us", "fanatics"];

        var adapter = Adapter(options.TheOddsApi.Bookmakers);
        var status = new OddsFeedStatus(Options.Create(options), new SourceRegistry([adapter], []));

        var feed = Assert.Single(status.Describe(), s => s.Key == TheOddsApiAdapter.SourceKey);

        Assert.True(feed.IsLive);
        Assert.Contains("Caesars and Fanatics need a paid plan at The Odds API", feed.Reason);
        Assert.Single(feed.Notes!);
    }

    [Fact]
    public void The_second_source_is_listed_only_where_its_key_is_held()
    {
        var options = new IngestionOptions();
        Assert.DoesNotContain(new OddsFeedStatus(Options.Create(options), new SourceRegistry([], [])).Describe(),
            s => s.Key == SportsGameOddsAdapter.SourceKey);

        options.SportsGameOdds.ApiKey = "k";
        var sgo = new SportsGameOddsAdapter(new HttpClient(), Options.Create(options), NullLogger<SportsGameOddsAdapter>.Instance, TimeProvider.System);
        var feed = Assert.Single(new OddsFeedStatus(Options.Create(options), new SourceRegistry([], [], sgo)).Describe());

        Assert.Equal(SportsGameOddsAdapter.SourceKey, feed.Key);
        Assert.True(feed.IsLive);
        Assert.Equal(["williamhill_us"], feed.Books);
        Assert.Contains("on request only", feed.Reason);
    }

    private static TheOddsApiAdapter Adapter(string[] books)
    {
        var options = new IngestionOptions();
        options.TheOddsApi.ApiKey = "k";
        options.TheOddsApi.Enabled = true;
        options.TheOddsApi.Bookmakers = books;
        options.TheOddsApi.Markets = ["moneyline", "spread", "total"];
        return new TheOddsApiAdapter(new HttpClient(), Options.Create(options), NullLogger<TheOddsApiAdapter>.Instance, TimeProvider.System);
    }

    private static string[] Configured(string host)
    {
        var path = Path.Combine(Root(), "src", host, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        return doc.RootElement.GetProperty("Ingestion").GetProperty("TheOddsApi").GetProperty("Bookmakers")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XLines.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("XLines.slnx not found above the test assembly.");
    }
}
