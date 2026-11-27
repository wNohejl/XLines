using System.Diagnostics;
using System.Net;
using XLines.Ingestion;
using XLines.Ingestion.Adapters;
using XLines.Ingestion.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XLines.Tests.Adapters;

/// <summary>
/// ESPN's patience, held once for the whole process (<see cref="EspnPacer"/>): every ESPN request
/// waits its turn at one gate, so the gap holds between the feed, the scoreboard pass, a backfill
/// and a box write alike, and a watched game's summary goes ahead of anything else waiting. The
/// clock is the real one, so the gaps asserted are generous; the order is exact.
/// </summary>
public class EspnPacerTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(150);

    /// <summary>A little under the gap: timers fire late, never early, but the stopwatch rounds.</summary>
    private static readonly TimeSpan AtLeast = TimeSpan.FromMilliseconds(135);

    [Fact]
    public async Task The_gap_holds_between_turns_whoever_asks()
    {
        var pacer = new EspnPacer(Gap);
        var clock = Stopwatch.StartNew();
        var granted = new List<TimeSpan>();

        // Two callers at once, three turns each, as the feed and a backfill would ask.
        async Task Caller()
        {
            for (var i = 0; i < 3; i++)
            {
                await pacer.WaitTurnAsync(priority: false, CancellationToken.None);
                lock (granted) granted.Add(clock.Elapsed);
            }
        }

        await Task.WhenAll(Caller(), Caller());

        Assert.Equal(6, granted.Count);
        var order = granted.Order().ToList();

        for (var i = 1; i < order.Count; i++)
            Assert.True(order[i] - order[i - 1] >= AtLeast, $"turn {i} came {(order[i] - order[i - 1]).TotalMilliseconds} ms after the last");
    }

    [Fact]
    public async Task A_priority_turn_goes_ahead_of_every_ordinary_one_waiting()
    {
        var pacer = new EspnPacer(TimeSpan.FromMilliseconds(250));
        var order = new List<string>();

        // The one in flight: the gate is taken now, and everything after it waits.
        await pacer.WaitTurnAsync(priority: false, CancellationToken.None);

        Task Ask(string name, bool priority)
            => pacer.WaitTurnAsync(priority, CancellationToken.None).ContinueWith(_ => { lock (order) order.Add(name); });

        // A backfill's two requests queue first; the watched game's summary arrives last.
        var backfill1 = Ask("backfill 1", priority: false);
        var backfill2 = Ask("backfill 2", priority: false);
        var watched = Ask("watched", priority: true);

        await Task.WhenAll(backfill1, backfill2, watched);

        Assert.Equal(["watched", "backfill 1", "backfill 2"], order);
    }

    [Fact]
    public async Task A_priority_turn_waits_no_longer_than_the_one_gap_already_running()
    {
        var pacer = new EspnPacer(Gap);

        await pacer.WaitTurnAsync(priority: false, CancellationToken.None);

        var queued = Enumerable.Range(0, 5).Select(_ => pacer.WaitTurnAsync(priority: false, CancellationToken.None)).ToList();
        var clock = Stopwatch.StartNew();
        await pacer.WaitTurnAsync(priority: true, CancellationToken.None);

        Assert.True(clock.Elapsed < Gap * 2, $"the watched turn waited {clock.Elapsed.TotalMilliseconds} ms behind five queued");
        await Task.WhenAll(queued);
    }

    [Fact]
    public async Task A_cancelled_wait_ends_at_once_and_gives_its_turn_to_the_next()
    {
        var pacer = new EspnPacer(TimeSpan.FromMilliseconds(300));
        await pacer.WaitTurnAsync(priority: false, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cancelled = pacer.WaitTurnAsync(priority: false, cts.Token);
        var next = pacer.WaitTurnAsync(priority: false, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(150));

        // The next is served on the first gap, not the second: the cancelled wait took no turn.
        await next;
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"the next turn came after {clock.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task An_already_cancelled_wait_is_refused()
    {
        var pacer = new EspnPacer(Gap);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pacer.WaitTurnAsync(priority: false, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task No_gap_is_no_wait()
    {
        var pacer = new EspnPacer(TimeSpan.Zero);

        for (var i = 0; i < 3; i++)
            Assert.True(pacer.WaitTurnAsync(priority: false, CancellationToken.None).IsCompletedSuccessfully);

        await Task.CompletedTask;
    }

    /// <summary>Answers every request at once with an empty board, counting them.</summary>
    private sealed class Counting : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"events":[]}""") });
        }
    }

    [Fact]
    public async Task The_host_holds_one_pacer_and_every_port_in_every_scope_waits_at_it()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EspnOptIn.Switch] = "true",
            ["Ingestion:Espn:RequestDelay"] = "00:00:00.300"
        }).Build();

        var handler = new Counting();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddXLinesIngestion(configuration);
        services.AddHttpClient<EspnStatsAdapter>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<EspnPacer>(), provider.GetRequiredService<EspnPacer>());
        Assert.Equal(TimeSpan.FromMilliseconds(300), provider.GetRequiredService<EspnPacer>().Delay);

        // Two scopes, two adapters, as the feed's poll and the scheduler's pass hold: one gap between them.
        await using var feed = provider.CreateAsyncScope();
        await using var scheduler = provider.CreateAsyncScope();
        var a = feed.ServiceProvider.GetRequiredService<XLines.Core.Contracts.IStatsSource>();
        var b = scheduler.ServiceProvider.GetRequiredService<XLines.Core.Contracts.IStatsSource>();
        Assert.NotSame(a, b);

        var clock = Stopwatch.StartNew();
        await a.FetchScheduleAsync("nfl", new DateOnly(2026, 11, 1), CancellationToken.None);
        await b.FetchScheduleAsync("nfl", new DateOnly(2026, 11, 1), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(280), $"two ports' requests came {clock.Elapsed.TotalMilliseconds} ms apart");
    }
}
