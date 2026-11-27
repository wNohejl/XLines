using XLines.Data.Changes;
using XLines.Data.Gamecast;
using XLines.Desk.Windowing;
using XLines.Web.Services;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// The snapshot every Gamecast window on a game shares (<see cref="GamecastSnapshots"/>): one load
/// per game and tier, never another tier's rows, and evicted by the same notices the windows hear.
/// </summary>
public sealed class GamecastPanelCacheTests : IDisposable
{
    private const int Game = 4123;
    private const int Other = 4124;
    private const string Operator = "all";
    private const string Visitor = "v:";

    private readonly DeskSignals _signals = new();
    private readonly GamecastSnapshots _cache;

    public GamecastPanelCacheTests() => _cache = new GamecastSnapshots(_signals);

    public void Dispose() => _cache.Dispose();

    private int _loads;

    /// <summary>A load that counts itself and answers rows marked with what it was asked for.</summary>
    private Func<Task<GamecastRows>> Load(string text, TaskCompletionSource? gate = null) => async () =>
    {
        Interlocked.Increment(ref _loads);
        if (gate is not null)
            await gate.Task;

        return new GamecastRows([FakeGamecastData.Play(1, 1, text)], [], FakeGamecastData.Epoch);
    };

    private void Publish(params string[] tokens) => _signals.Publish(tokens.ToHashSet());

    [Fact]
    public async Task A_second_open_of_a_final_reads_no_plays_or_drives()
    {
        var first = await _cache.GetAsync(Game, Operator, settled: true, Load("first"));
        var second = await _cache.GetAsync(Game, Operator, settled: true, Load("second"));

        Assert.Equal(1, _loads);
        Assert.Same(first, second);
        Assert.Equal("first", second.Plays[0].Text);
    }

    [Fact]
    public async Task A_plays_notice_evicts_only_that_game()
    {
        await _cache.GetAsync(Game, Operator, settled: true, Load("game"));
        await _cache.GetAsync(Game, Visitor, settled: true, Load("game, visitor"));
        await _cache.GetAsync(Other, Operator, settled: true, Load("other"));
        Assert.Equal(3, _loads);

        Publish(DataTopics.Address(DataTopics.Plays, Game.ToString()));

        // Both of the game's tiers load again; the other game is still held.
        Assert.Equal("game again", (await _cache.GetAsync(Game, Operator, settled: true, Load("game again"))).Plays[0].Text);
        await _cache.GetAsync(Game, Visitor, settled: true, Load("game, visitor again"));
        Assert.Equal("other", (await _cache.GetAsync(Other, Operator, settled: true, Load("other again"))).Plays[0].Text);
        Assert.Equal(5, _loads);
    }

    [Fact]
    public async Task A_games_notice_evicts_that_game_and_another_topic_evicts_nothing()
    {
        await _cache.GetAsync(Game, Operator, settled: true, Load("game"));
        await _cache.GetAsync(Other, Operator, settled: true, Load("other"));

        Publish(DataTopics.Address(DataTopics.Odds, Game.ToString()), DataTopics.Journal);
        Assert.Equal(2, _cache.Count);

        Publish(DataTopics.Address(DataTopics.Games, Other.ToString()));
        Assert.Equal(1, _cache.Count);
        Assert.Equal("game", (await _cache.GetAsync(Game, Operator, settled: true, Load("again"))).Plays[0].Text);
    }

    /// <summary>A bare topic cannot say which game moved — the fallback poll, the listener's reconnect — so everything goes.</summary>
    [Theory]
    [InlineData(DataTopics.Plays)]
    [InlineData(DataTopics.Games)]
    public async Task A_bare_topic_or_the_reconnects_publish_all_evicts_everything(string topic)
    {
        await _cache.GetAsync(Game, Operator, settled: true, Load("game"));
        await _cache.GetAsync(Other, Visitor, settled: true, Load("other"));

        Publish(topic);
        Assert.Equal(0, _cache.Count);

        await _cache.GetAsync(Game, Operator, settled: true, Load("game"));
        _signals.Publish(DataTopics.All);
        Assert.Equal(0, _cache.Count);
    }

    [Fact]
    public async Task One_tier_is_never_handed_another_tiers_snapshot()
    {
        var operatorRows = await _cache.GetAsync(Game, Operator, settled: true, Load("operator's"));
        var visitorRows = await _cache.GetAsync(Game, Visitor, settled: true, () => Task.FromResult(GamecastRows.Empty));

        Assert.Equal("operator's", operatorRows.Plays[0].Text);
        Assert.Empty(visitorRows.Plays);
        Assert.Empty((await _cache.GetAsync(Game, Visitor, settled: true, Load("should not load"))).Plays);
        Assert.Equal("operator's", (await _cache.GetAsync(Game, Operator, settled: true, Load("should not load"))).Plays[0].Text);
    }

    [Fact]
    public async Task Concurrent_opens_of_one_game_share_one_load()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var opens = Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => _cache.GetAsync(Game, Operator, settled: false, Load($"load {i}", gate))))
            .ToList();

        // Every open is asking before the one load answers.
        await Task.Delay(50);
        gate.SetResult();
        var rows = await Task.WhenAll(opens);

        Assert.Equal(1, _loads);
        Assert.All(rows, r => Assert.Same(rows[0], r));
    }

    [Fact]
    public async Task A_load_that_fails_is_not_kept()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _cache.GetAsync(Game, Operator, settled: true, () => Task.FromException<GamecastRows>(new InvalidOperationException("database restarting"))));

        Assert.Equal("second", (await _cache.GetAsync(Game, Operator, settled: true, Load("second"))).Plays[0].Text);
    }

    /// <summary>A live game's snapshot is held only seconds; a final's for hours.</summary>
    [Fact]
    public void A_live_snapshot_is_short_lived_and_a_finals_is_long()
    {
        Assert.True(GamecastSnapshots.LiveFor <= TimeSpan.FromMinutes(1));
        Assert.True(GamecastSnapshots.SettledFor >= TimeSpan.FromHours(1));
        Assert.True(GamecastSnapshots.Capacity > 0);
    }
}
