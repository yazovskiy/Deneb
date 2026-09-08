using Deneb.Core;
using System.Text.Json;
using Xunit;

namespace Deneb.Tests;

public sealed class Version11Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-v11-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private Guid Add(DownloadEngine e, string url = "https://example.org/a") => e.Add(new(url), Path.Combine(root, "files"));
    [Fact]
    public async Task OrderAndManualPausesSurviveRestart()
    {
        Guid first, second, third;
        await using (var e = new DownloadEngine(root))
        {
            await e.PauseAllAsync(); first = Add(e); second = Add(e); third = Add(e);
            await e.PauseManyAsync([second]); e.DownloadNext(second); e.Move(third, -1);
            Assert.Equal(new[] { second, third, first }, e.Snapshots().Select(s => s.Id));
        }
        await using var restored = new DownloadEngine(root);
        Assert.True(restored.GloballyPaused);
        Assert.Equal(new[] { second, third, first }, restored.Snapshots().Select(s => s.Id));
        Assert.Equal(DownloadState.Paused, restored.Snapshots()[0].State);
        Assert.All(restored.Snapshots().Skip(1), s => Assert.Equal(DownloadState.Queued, s.State));
    }
    [Fact]
    public async Task BatchSkipsDecisionsAndPreservesCompletedFiles()
    {
        Directory.CreateDirectory(root); var file = Path.Combine(root, "ready"); File.WriteAllText(file, "keep");
        var jobs = new[] { new DownloadJob { State = DownloadState.NeedsDecision }, new DownloadJob { State = DownloadState.Completed, Target = file }, new DownloadJob { State = DownloadState.Failed }, new DownloadJob { State = DownloadState.Paused } };
        foreach (var j in jobs) { j.Url = "https://example.org/a"; j.Destination = root; }
        using (var store = new StateStore(root)) store.Save(new() { Jobs = jobs.ToList(), GloballyPaused = true });
        await using var e = new DownloadEngine(root);
        var result = e.ResumeMany(jobs.Select(j => j.Id)); Assert.Equal(2, result.Processed.Count); Assert.Equal(2, result.Skipped.Count);
        Assert.Single(e.ClearCompleted().Processed); Assert.True(File.Exists(file));
        var removed = await e.RemoveManyAsync(jobs.Select(j => j.Id)); Assert.Equal(3, removed.Processed.Count); Assert.Single(removed.Skipped);
        Assert.True(File.Exists(file));
    }
    [Fact]
    public void MigrationKeepsOriginalBackupAndRejectsFutureVersions()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, "state.json");
        var original = JsonSerializer.Serialize(new Library { Version = 1, Jobs = [new() { Name = "Тест", State = DownloadState.Paused }] }); File.WriteAllText(path, original);
        using (var store = new StateStore(root)) { var state = store.Load(); Assert.Equal(5, state.Version); Assert.False(state.GloballyPaused); Assert.Equal("Тест", state.Jobs[0].Name); store.Save(state); }
        Assert.Equal(original, File.ReadAllText(path + ".v1.bak"));
        File.WriteAllText(path, "{\"Version\":99}");
        using var future = new StateStore(root); Assert.Throws<ProblemException>(() => future.Load()); Assert.Equal("{\"Version\":99}", File.ReadAllText(path));
    }
    private sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    [Fact]
    public async Task SegmentRetryDoesNotHideOtherTransfersAndPauseCancelsWait()
    {
        await using var server = await TestServer.Start(20 * 1024 * 1024); server.RetryFirstSegment = true; server.DelayMs = 25;
        await using var e = new DownloadEngine(root); var id = Add(e, server.Url);
        Snapshot s = e.Snapshots()[0];
        for (var i = 0; i < 200; i++) { s = e.Snapshots()[0]; if (s.Segments.Any(p => p.RetryIn.HasValue) && s.Bytes > 0) break; await Task.Delay(20); }
        Assert.Equal(DownloadPhase.Transferring, s.Phase); Assert.Equal(1, s.Segments[0].Retry); Assert.True(s.Segments[0].RetryIn > TimeSpan.Zero);
        Assert.InRange(s.Connections, 1, 3); Assert.Contains(s.Segments.Skip(1), p => p.Bytes > 0);
        var previous = s.Segments[0].RetryIn; await Task.Delay(150); Assert.True(e.Snapshots()[0].Segments[0].RetryIn < previous);
        await e.PauseManyAsync([id]).WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(DownloadState.Paused, e.Snapshots()[0].State);
    }
    [Fact]
    public async Task CrashStateRespectsGlobalPause()
    {
        Directory.CreateDirectory(root);
        using (var store = new StateStore(root)) store.Save(new() { GloballyPaused = true, Jobs = [new() { Url = "https://example.org/a", Destination = root, State = DownloadState.Downloading }] });
        await using var e = new DownloadEngine(root); await Task.Delay(300);
        Assert.Equal(DownloadState.Queued, e.Snapshots()[0].State); Assert.Equal(DownloadPhase.GlobalPause, e.Snapshots()[0].Phase);
    }
    [Fact]
    public void WindowDecaysAndEtaSupportsDays()
    {
        var clock = new Clock(); var meter = new SpeedWindow(clock); meter.Add(100);
        Assert.Null(meter.Eta(100)); clock.Seconds = 2; Assert.Equal(50, meter.Speed);
        Assert.True(meter.Eta(10000000)!.Value.Days > 0);
        clock.Seconds = 5; Assert.Equal(0, meter.Speed); Assert.Null(meter.Eta(100));
        meter.Add(500); Assert.Equal(100, meter.Speed);
    }
    [Fact]
    public async Task PriorityDoesNotInterruptAndGlobalPauseStopsAll()
    {
        await using var server = await TestServer.Start(4 * 1024 * 1024); server.DelayMs = 20;
        await using var e = new DownloadEngine(root); e.SetSettings(new() { ActiveFiles = 1, Destination = Path.Combine(root, "files") });
        var first = Add(e, server.Url);
        for (var i = 0; i < 200 && e.Snapshots()[0].Bytes == 0; i++) await Task.Delay(20);
        Assert.True(e.Snapshots()[0].Bytes > 0);
        var second = Add(e, server.Url); var third = Add(e, server.Url); e.DownloadNext(third);
        Assert.Equal(DownloadState.Downloading, e.Snapshots().Single(s => s.Id == first).State);
        await e.PauseManyAsync([second]); await e.PauseAllAsync();
        Assert.DoesNotContain(e.Snapshots(), s => s.State == DownloadState.Downloading);
        Assert.Equal(DownloadState.Paused, e.Snapshots().Single(s => s.Id == second).State);
        e.ResumeAll(); await Task.Delay(350);
        Assert.Equal(DownloadState.Downloading, e.Snapshots().Single(s => s.Id == third).State);
        Assert.Equal(DownloadState.Paused, e.Snapshots().Single(s => s.Id == second).State);
    }
}
