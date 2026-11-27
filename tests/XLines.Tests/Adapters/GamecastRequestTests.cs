using System.Diagnostics;
using System.Net;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Tests.Adapters;

/// <summary>
/// A gamecast read is asked once and given a few seconds (<see cref="GamecastRequest"/>): the
/// feed's next poll is its retry, so a read that hangs or fails must hand the poll back rather
/// than spend half a minute on retries while the watched game waits. Everything else ESPN is
/// asked keeps the standard resilience handler. The host is built as the desk builds it, with a
/// handler in place of the network.
/// </summary>
public class GamecastRequestTests
{
    /// <summary>Never answers; counts the asks.</summary>
    private sealed class Hang : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>A 503 on the first ask, an empty board after that; counts the asks.</summary>
    private sealed class FailsOnce : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Interlocked.Increment(ref Calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"events":[]}""") });
    }

    private static ServiceProvider Host(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EspnOptIn.Switch] = "true",
            ["Ingestion:Espn:RequestDelay"] = "00:00:00"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);

        // The registration as the host makes it — resilience handler and all — on a handler in place of ESPN.
        services.AddHttpClient<EspnStatsAdapter>().ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_gamecast_read_that_hangs_is_asked_once_and_given_up_on_its_timeout()
    {
        var handler = new Hang();
        await using var host = Host(handler);
        await using var scope = host.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<IGamecastSource>();

        var clock = Stopwatch.StartNew();

        using (GamecastRequest.Begin(watched: true, TimeSpan.FromMilliseconds(400)))
            await Assert.ThrowsAsync<TimeoutException>(() => source.FetchGamecastAsync("nfl", "401873032", CancellationToken.None));

        // The standard handler alone would give each attempt ten seconds and try three more times.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"gave up after {clock.Elapsed.TotalMilliseconds} ms");
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_gamecast_read_that_fails_is_not_retried_but_the_scoreboard_still_is()
    {
        var gamecastHandler = new FailsOnce();
        await using (var host = Host(gamecastHandler))
        await using (var scope = host.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<IGamecastSource>();

            using (GamecastRequest.Begin(watched: false, TimeSpan.FromSeconds(8)))
                await Assert.ThrowsAsync<HttpRequestException>(() => source.FetchGamecastAsync("nfl", "401873032", CancellationToken.None));

            Assert.Equal(1, gamecastHandler.Calls);
        }

        // Outside a gamecast read, the same 503 is retried as before, and the retry finds the board.
        var boardHandler = new FailsOnce();
        await using (var host = Host(boardHandler))
        await using (var scope = host.CreateAsyncScope())
        {
            var stats = scope.ServiceProvider.GetRequiredService<XLines.Core.Contracts.IStatsSource>();
            await stats.FetchScheduleAsync("nfl", new DateOnly(2026, 11, 1), CancellationToken.None);

            Assert.Equal(2, boardHandler.Calls);
        }
    }

    [Fact]
    public async Task A_read_cancelled_by_its_caller_is_a_cancellation_not_a_timeout()
    {
        var handler = new Hang();
        await using var host = Host(handler);
        await using var scope = host.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<IGamecastSource>();
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        using (GamecastRequest.Begin(watched: true, TimeSpan.FromSeconds(8)))
        {
            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => source.FetchGamecastAsync("nfl", "401873032", stopping.Token));
            Assert.IsNotType<TimeoutException>(ex);
        }
    }

    [Fact]
    public void The_request_scope_is_ambient_and_ends_with_its_using()
    {
        Assert.Null(GamecastRequest.Current);

        using (GamecastRequest.Begin(watched: true, TimeSpan.FromSeconds(8)))
        {
            Assert.True(GamecastRequest.Current!.Watched);

            using (GamecastRequest.Begin(watched: false, TimeSpan.FromSeconds(8)))
                Assert.False(GamecastRequest.Current!.Watched);

            Assert.True(GamecastRequest.Current!.Watched);
        }

        Assert.Null(GamecastRequest.Current);
    }
}
