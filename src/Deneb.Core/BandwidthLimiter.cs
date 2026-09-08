namespace Deneb.Core;

/// <summary>One bounded token bucket, round-robin by file rather than connection.</summary>
public sealed class BandwidthLimiter : IDisposable
{
    private sealed record Waiter(int Maximum, CancellationToken Token, TaskCompletionSource<int> Completion);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Queue<Waiter>> waiting = [];
    private readonly Queue<Guid> order = [];
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private long limit, timestamp;
    private double tokens;
    private bool disposed;
    private double Capacity => Math.Max(1, limit / 10d);

    public BandwidthLimiter(TimeProvider clock, long bytesPerSecond = 0)
    {
        this.clock = clock;
        timestamp = clock.GetTimestamp();
        SetLimit(bytesPerSecond);
        timer = clock.CreateTimer(_ => { lock (gate) Pump(); }, null, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }
    public void SetLimit(long bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        lock (gate)
        {
            Refill();
            var unlimited = limit == 0;
            limit = bytesPerSecond;
            tokens = unlimited ? Capacity : Math.Min(tokens, Capacity);
            Pump();
        }
    }
    public bool IsWaiting(Guid job) { lock (gate) return waiting.TryGetValue(job, out var queue) && queue.Any(w => !w.Completion.Task.IsCompleted); }
    public async ValueTask<int> AcquireAsync(Guid job, int maximum, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => completion.TrySetCanceled(token));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!waiting.TryGetValue(job, out var queue)) { waiting[job] = queue = new(); order.Enqueue(job); }
            queue.Enqueue(new(maximum, token, completion));
            Pump();
        }
        return await completion.Task;
    }
    public void Refund(int bytes)
    {
        if (bytes <= 0) return;
        lock (gate) { Refill(); tokens = Math.Min(Capacity, tokens + bytes); Pump(); }
    }
    private void Refill()
    {
        var now = clock.GetTimestamp();
        tokens = Math.Min(Capacity, tokens + Math.Max(0, clock.GetElapsedTime(timestamp, now).TotalSeconds) * limit);
        timestamp = now;
    }
    private void Pump()
    {
        if (disposed) return;
        Refill();
        // Even when empty, discard canceled waiters so cancellation has bounded cost.
        while (order.TryPeek(out var job))
        {
            var queue = waiting[job];
            while (queue.TryPeek(out var head) && head.Completion.Task.IsCompleted) queue.Dequeue();
            if (queue.Count == 0) { order.Dequeue(); waiting.Remove(job); continue; }
            if (limit != 0 && tokens < 1) break;
            order.Dequeue();
            var waiter = queue.Dequeue();
            var count = limit == 0 ? waiter.Maximum : (int)Math.Min(waiter.Maximum, Math.Floor(tokens));
            if (waiter.Completion.TrySetResult(count) && limit != 0) tokens -= count;
            if (queue.Count == 0) waiting.Remove(job); else order.Enqueue(job);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var waiter in waiting.Values.SelectMany(q => q)) waiter.Completion.TrySetCanceled();
            waiting.Clear(); order.Clear();
        }
        timer.Dispose();
    }
}
