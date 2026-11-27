using System.Net;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Tests.Reliability;

/// <summary>
/// A metered provider's call is never re-sent automatically: The Odds API bills a call it answered
/// whether or not it then failed, and the cost is read from the final response alone, so a retry
/// could be billed twice and recorded once. A free provider (ESPN) keeps the standard retry.
/// Through the registration the hosts use, with a stub primary handler in place of the network.
/// </summary>
public class MeteredClientRetryTests
{
    private sealed class Stub(Func<int, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _sent;

        public int Sent => Volatile.Read(ref _sent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => answer(Interlocked.Increment(ref _sent), ct);
    }

    private static HttpClient Client<T>(Stub stub, bool espn = true)
        where T : class
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ingestion:Sports:0"] = "mlb",
            ["Ingestion:Espn:Enabled"] = espn ? "true" : "false",
            ["Ingestion:MlbStatsApi:Enabled"] = "false",
            ["Ingestion:Gamecast:Enabled"] = "false",
            ["Ingestion:TheOddsApi:Enabled"] = "true",
            ["Ingestion:TheOddsApi:ApiKey"] = "test-key"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        services.AddHttpClient<T>().ConfigurePrimaryHttpMessageHandler(() => stub);

        return services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(typeof(T).Name);
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode code)
        => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("[]") });

    [Fact]
    public async Task A_metered_call_answered_503_is_sent_exactly_once()
    {
        var stub = new Stub((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        var client = Client<TheOddsApiAdapter>(stub);

        using var response = await client.GetAsync("https://api.the-odds-api.com/v4/sports/baseball_mlb/odds");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, stub.Sent);
    }

    [Fact]
    public async Task A_metered_schedule_call_that_fails_on_the_wire_is_sent_exactly_once()
    {
        var stub = new Stub((_, _) => throw new HttpRequestException("connection reset"));
        var client = Client<TheOddsApiScheduleAdapter>(stub, espn: false);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.the-odds-api.com/v4/sports/baseball_mlb/scores"));
        Assert.Equal(1, stub.Sent);
    }

    [Fact]
    public async Task A_metered_call_that_times_out_is_sent_exactly_once()
    {
        // Never answers: the standard handler's attempt timeout (10 s) is what ends it.
        var stub = new Stub(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = Client<TheOddsApiAdapter>(stub);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://api.the-odds-api.com/v4/sports/baseball_mlb/odds"));
        Assert.Equal(1, stub.Sent);
    }

    [Fact]
    public async Task A_free_providers_call_answered_503_is_retried()
    {
        var stub = new Stub((n, _) => Status(n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var client = Client<EspnStatsAdapter>(stub);

        using var response = await client.GetAsync("https://site.api.espn.com/apis/site/v2/sports/baseball/mlb/scoreboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Sent);
    }
}
