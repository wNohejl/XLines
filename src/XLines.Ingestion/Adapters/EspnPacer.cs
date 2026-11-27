namespace XLines.Ingestion.Adapters;

/// <summary>
/// ESPN's patience, held once for the whole process: every ESPN request waits its turn here, and
/// turns are <see cref="Delay"/> apart whoever asks.
///
/// <para>
/// The adapter used to keep the gap itself, from its own last request. But the adapter is scoped,
/// so the feed's poll, the scheduler's scoreboard pass, a backfill and a box write each held their
/// own clock, and four of them running together reached ESPN four times as fast as the setting
/// says. One singleton gate makes the setting true of the host.
/// </para>
///
/// <para>
/// Two queues. A watched game's read (<see cref="GamecastRequest.Watched"/>) takes the next turn
/// ahead of anything ordinary still waiting, so a running backfill or scoreboard pass costs the
/// game someone has open one gap at most — the turn already given out — and never the queue behind
/// it. Within a queue, first come first served. The gap is measured from turn to turn, not from a
/// response, so a slow response has already paid it; a request is never held for another's body.
/// </para>
/// </summary>
public sealed class EspnPacer
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _priority = new();
    private readonly Queue<TaskCompletionSource> _ordinary = new();

    /// <summary>When the next turn may be given out.</summary>
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    /// <summary>Whether the loop that hands out queued turns is running.</summary>
    private bool _pumping;

    public EspnPacer(TimeSpan delay, TimeProvider? time = null)
    {
        Delay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The least time between two turns. Zero gives every turn at once.</summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// Completes when this request may go. A cancelled wait ends at once and takes no turn from
    /// the requests behind it.
    /// </summary>
    public Task WaitTurnAsync(bool priority, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        if (Delay == TimeSpan.Zero)
            return Task.CompletedTask;

        TaskCompletionSource waiter;

        lock (_gate)
        {
            var now = _time.GetUtcNow();

            // Nobody waiting and the gap already paid: this turn is the caller's, now.
            if (!_pumping && now >= _next)
            {
                _next = now + Delay;
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (priority ? _priority : _ordinary).Enqueue(waiter);

            if (!_pumping)
            {
                _pumping = true;
                _ = PumpAsync();
            }
        }

        if (!ct.CanBeCanceled)
            return waiter.Task;

        // The queue skips a waiter already cancelled, so cancelling here is all it takes.
        var registration = ct.Register(() => waiter.TrySetCanceled(ct));
        waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return waiter.Task;
    }

    /// <summary>
    /// Hands out queued turns, one a gap, watched first, until both queues are empty. The queues
    /// are read again at every turn, so a watched read that arrived during the gap is the next.
    /// </summary>
    private async Task PumpAsync()
    {
        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                var now = _time.GetUtcNow();
                wait = _next - now;

                if (wait <= TimeSpan.Zero)
                {
                    if (!TryGiveTurn(_priority) && !TryGiveTurn(_ordinary))
                    {
                        _pumping = false;
                        return;
                    }

                    _next = now + Delay;
                    continue;
                }
            }

            await Task.Delay(wait, _time).ConfigureAwait(false);
        }
    }

    /// <summary>Gives the turn to the first waiter in the queue still waiting; false when none is.</summary>
    private static bool TryGiveTurn(Queue<TaskCompletionSource> queue)
    {
        while (queue.TryDequeue(out var waiter))
        {
            if (waiter.TrySetResult())
                return true;
        }

        return false;
    }
}
