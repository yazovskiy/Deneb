namespace Deneb.Core;

public sealed class SpeedWindow(TimeProvider clock)
{
    private readonly Queue<(long At, long Bytes)> samples = new();
    private readonly long started = clock.GetTimestamp();
    public void Add(long bytes) { lock (samples) { samples.Enqueue((clock.GetTimestamp(), bytes)); Trim(); } }
    private void Trim()
    {
        while (samples.TryPeek(out var sample) && clock.GetElapsedTime(sample.At).TotalSeconds >= 5) samples.Dequeue();
    }
    public double Speed { get { lock (samples) { Trim(); return samples.Sum(s => (double)s.Bytes) / Math.Clamp(clock.GetElapsedTime(started).TotalSeconds, 1, 5); } } }
    public TimeSpan? Eta(long remaining) => clock.GetElapsedTime(started).TotalSeconds >= 2 && Speed > 0
        ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1, Math.Max(0, remaining) / Speed)) : null;
}
