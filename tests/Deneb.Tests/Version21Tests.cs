using System.Security.Cryptography;
using System.Text.Json;
using Deneb.App;
using Deneb.Core;
using Xunit;

namespace Deneb.Tests;

public sealed class Version21Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-v21-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<TimerCallback> callbacks = [];
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ticks;
        public void Advance(int milliseconds) { ticks += milliseconds; foreach (var callback in callbacks.ToArray()) callback(null); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { TimerCallback action = _ => callback(state); callbacks.Add(action); return new Timer(() => callbacks.Remove(action)); }
        private sealed class Timer(Action remove) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => remove();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Disk : IDiskSpaceService
    {
        public long Free = 10L * 1024 * 1024 * 1024;
        public bool Fails;
        public bool Separate;
        public DiskVolume Measure(string path) => Fails ? throw new ProblemException(ProblemCode.DiskSpaceUnavailable) : new(Separate ? path : "test-volume", Interlocked.Read(ref Free));
    }
    private async Task<Snapshot> Wait(DownloadEngine engine, Guid id, Func<Snapshot, bool> condition)
    {
        for (var i = 0; i < 1500; i++)
        {
            var s = engine.Snapshots().Single(s => s.Id == id);
            if (condition(s)) return s;
            Assert.True(s.State is not (DownloadState.Failed or DownloadState.NeedsDecision), s.Error?.ToString());
            await Task.Delay(20);
        }
        throw new TimeoutException(JsonSerializer.Serialize(engine.Snapshots()));
    }
    [Fact]
    public async Task TokenBucketIsBoundedAndRotatesBetweenFiles()
    {
        var clock = new Clock(); using var limiter = new BandwidthLimiter(clock, 1000);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Equal(100, await limiter.AcquireAsync(a, 1000, default));
        var a1 = limiter.AcquireAsync(a, 1000, default).AsTask();
        var a2 = limiter.AcquireAsync(a, 1000, default).AsTask();
        var b1 = limiter.AcquireAsync(b, 1000, default).AsTask();
        clock.Advance(100); Assert.Equal(100, await a1); Assert.False(a2.IsCompleted);
        clock.Advance(100); Assert.Equal(100, await b1); Assert.False(a2.IsCompleted);
        clock.Advance(10000); Assert.Equal(100, await a2);
        var pending = limiter.AcquireAsync(a, 1, default).AsTask(); Assert.False(pending.IsCompleted);
        clock.Advance(1); Assert.Equal(1, await pending);
    }
    [Fact]
    public async Task LimitChangesCancelWaitsAndRefundShortReads()
    {
        var clock = new Clock(); using var limiter = new BandwidthLimiter(clock, 100);
        var id = Guid.NewGuid(); Assert.Equal(10, await limiter.AcquireAsync(id, 50, default));
        limiter.Refund(6); Assert.Equal(6, await limiter.AcquireAsync(id, 50, default));
        using var cancel = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(id, 50, cancel.Token).AsTask(); Assert.True(limiter.IsWaiting(id));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var next = limiter.AcquireAsync(id, 50, default).AsTask();
        limiter.SetLimit(0); Assert.Equal(50, await next);
        limiter.SetLimit(10); Assert.Equal(1, await limiter.AcquireAsync(id, 50, default));
        var slowed = limiter.AcquireAsync(id, 50, default).AsTask(); clock.Advance(99); Assert.False(slowed.IsCompleted);
        clock.Advance(1); Assert.Equal(1, await slowed);
    }
    [Fact]
    public void ReservationsCountAssemblyAndShareOneSafetyMargin()
    {
        var disk = new Disk { Free = DiskReservations.SafetyBytes + 350 };
        var reservations = new DiskReservations(disk, new Clock()); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        reservations.Add(a, root, 100, 50, false);
        Assert.Equal(150, reservations.Snapshot(a)!.RequiredBytes);
        reservations.Add(b, root, 100, 0, false);
        Assert.True(reservations.Fits(b)); Assert.Equal(150, reservations.Snapshot(b)!.ReservedByOthersBytes);
        reservations.ReserveWrite(a, 50); reservations.CompleteWrite(a, 50, 50);
        Assert.Equal(100, reservations.Snapshot(a)!.RequiredBytes); Assert.True(reservations.Fits(a));
        reservations.BeginAssembly(a); reservations.ReserveWrite(a, 100); reservations.CompleteWrite(a, 100, 100);
        Assert.Equal(0, reservations.Snapshot(a)!.RequiredBytes);
        reservations.Remove(a); Assert.Equal(0, reservations.Snapshot(b)!.ReservedByOthersBytes);
    }
    [Fact]
    public void ExternalUseStopsQueueTailAndSeparateVolumesStayIndependent()
    {
        var disk = new Disk { Free = DiskReservations.SafetyBytes + 400 };
        var reservations = new DiskReservations(disk, new Clock()); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        reservations.Add(a, "a", 100, 0, false); reservations.Add(b, "b", 100, 0, false);
        disk.Free -= 100;
        Assert.Equal(b, Assert.Single(reservations.Refresh([a, b], true)).Id);
        Assert.Equal(200, reservations.Snapshot(b)!.RequiredBytes); Assert.True(reservations.Fits(a));
        var separate = new DiskReservations(new Disk { Separate = true, Free = DiskReservations.SafetyBytes + 200 }, new Clock());
        separate.Add(a, "a", 100, 0, false); separate.Add(b, "b", 100, 0, false);
        Assert.True(separate.Fits(a)); Assert.True(separate.Fits(b));
    }
    [Fact]
    public void UnknownSizeReservesGrowthAndMeasurementErrorsAreDistinct()
    {
        var disk = new Disk { Free = DiskReservations.SafetyBytes + 200 };
        var reservations = new DiskReservations(disk, new Clock()); var id = Guid.NewGuid();
        reservations.Add(id, root, null, 0, false); reservations.ReserveWrite(id, 100);
        Assert.Equal(200, reservations.Snapshot(id)!.RequiredBytes);
        reservations.CompleteWrite(id, 100, 100);
        Assert.True(reservations.Snapshot(id)!.UnknownSize);
        var error = Assert.Throws<ProblemException>(() => reservations.ReserveWrite(id, 1));
        Assert.Equal(ProblemCode.InsufficientDiskSpace, error.Problem.Code);
        disk.Fails = true;
        Assert.Equal(ProblemCode.DiskSpaceUnavailable, Assert.Single(reservations.Refresh([id], true)).Reason);
    }
    [Fact]
    public async Task LowDiskStaysPausedThroughRestartAndRequiresManualResume()
    {
        await using var server = await TestServer.Start(1024 * 1024);
        var disk = new Disk { Free = DiskReservations.SafetyBytes + 1 }; Guid id;
        await using (var engine = new DownloadEngine(root, diskSpaceService: disk))
        {
            id = engine.Add(new(server.Url), root);
            var paused = await Wait(engine, id, s => s.State == DownloadState.Paused);
            Assert.Equal(ProblemCode.InsufficientDiskSpace, paused.Error!.Code);
            Assert.Equal(DownloadPhase.DiskPause, paused.Phase); Assert.Equal(0, paused.Bytes);
            Assert.Empty(server.Requests);
        }
        disk.Free = 10L * 1024 * 1024 * 1024;
        await using var restored = new DownloadEngine(root, diskSpaceService: disk);
        await restored.PauseAllAsync(); restored.ResumeAll(); await Task.Delay(300);
        Assert.Equal(DownloadState.Paused, restored.Snapshots()[0].State);
        restored.Resume(id);
        var done = await Wait(restored, id, s => s.State == DownloadState.Completed);
        Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(File.ReadAllBytes(done.Target!)));
    }
    [Fact]
    public async Task LiveDiskLossPreservesPartsAndResumesWithSameHash()
    {
        await using var server = await TestServer.Start(20 * 1024 * 1024); server.DelayMs = 20;
        var disk = new Disk();
        await using var engine = new DownloadEngine(root, diskSpaceService: disk);
        var id = engine.Add(new(server.Url), root);
        await Wait(engine, id, s => s.Bytes > 1048576);
        Interlocked.Exchange(ref disk.Free, DiskReservations.SafetyBytes);
        await Wait(engine, id, s => s.State == DownloadState.Paused && s.Connections == 0);
        await engine.PauseAsync(id); // wait for the durable shutdown boundary
        var bytes = engine.Snapshots()[0].Bytes; Assert.True(bytes > 0);
        disk.Free = 10L * 1024 * 1024 * 1024;
        engine.Resume(id);
        var done = await Wait(engine, id, s => s.State == DownloadState.Completed);
        Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(File.ReadAllBytes(done.Target!)));
    }
    [Fact]
    public async Task CompletedPartsWaitForAssemblySpaceWithoutRedownloading()
    {
        Directory.CreateDirectory(root);
        var data = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        var segment = new Segment { Start = 0, End = data.Length - 1, Committed = data.Length, Complete = true };
        var job = new DownloadJob { Url = "https://example.org/not-requested", Name = "ready.bin", Target = Path.Combine(root, "ready.bin"), Destination = root, Total = data.Length, Segments = [segment] };
        Directory.CreateDirectory(job.PartsDirectory);
        File.WriteAllBytes(Path.Combine(job.PartsDirectory, segment.FileName), data);
        File.WriteAllText(Path.Combine(job.PartsDirectory, "assembled.tmp"), "unconfirmed scratch");
        using (var store = new StateStore(root)) store.Save(new() { Jobs = [job] });
        var disk = new Disk { Free = DiskReservations.SafetyBytes + data.Length - 1 };
        await using var engine = new DownloadEngine(root, diskSpaceService: disk);
        var paused = await Wait(engine, job.Id, s => s.State == DownloadState.Paused);
        Assert.Equal(data.Length, paused.DiskSpace!.RequiredBytes);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(job.PartsDirectory, segment.FileName)));
        await engine.PauseAsync(job.Id);
        disk.Free += 100; engine.Resume(job.Id);
        var done = await Wait(engine, job.Id, s => s.State == DownloadState.Completed);
        Assert.Equal(data, File.ReadAllBytes(done.Target!));
    }
    [Fact]
    public async Task MeasurementFailurePausesAndBlockedJobDoesNotOccupySlot()
    {
        await using var server = await TestServer.Start(1024 * 1024);
        await using var empty = await TestServer.Start(0);
        var disk = new Disk { Fails = true };
        await using var engine = new DownloadEngine(root, diskSpaceService: disk);
        var settings = engine.GetSettings(); settings.ActiveFiles = 1; engine.SetSettings(settings);
        var id = engine.Add(new(server.Url), root);
        var paused = await Wait(engine, id, s => s.State == DownloadState.Paused);
        Assert.Equal(ProblemCode.DiskSpaceUnavailable, paused.Error!.Code);
        disk.Fails = false; disk.Free = DiskReservations.SafetyBytes + 1;
        var next = engine.Add(new(empty.Url), root);
        await Wait(engine, next, s => s.State == DownloadState.Completed);
        Assert.Equal(DownloadState.Paused, engine.Snapshots().Single(s => s.Id == id).State);
    }
    [Fact]
    public async Task UnknownSizeAndGlobalRateLimitPreserveHash()
    {
        await using var server = await TestServer.Start(128 * 1024); server.UnknownLength = true;
        await using var engine = new DownloadEngine(root, diskSpaceService: new Disk());
        var settings = engine.GetSettings(); settings.BandwidthLimitBytesPerSecond = 128 * 1024; engine.SetSettings(settings);
        var ids = new[] { engine.Add(new(server.Url), root), engine.Add(new(server.Url), root) };
        await Wait(engine, ids[0], s => s.WaitingForBandwidth);
        foreach (var id in ids)
        {
            var done = await Wait(engine, id, s => s.State == DownloadState.Completed);
            Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(File.ReadAllBytes(done.Target!)));
        }
    }
    [Fact]
    public async Task LiveLimitWithConnectionChangesAndPauseDoesNotSpendRetries()
    {
        await using var server = await TestServer.Start(32 * 1024 * 1024); server.DelayMs = 5;
        await using var engine = new DownloadEngine(root, diskSpaceService: new Disk());
        var id = engine.Add(new(server.Url), root);
        await Wait(engine, id, s => s.Bytes > 65536);
        var settings = engine.GetSettings(); settings.BandwidthLimitBytesPerSecond = 1024; settings.Connections = 10; engine.SetSettings(settings);
        await Wait(engine, id, s => s.WaitingForBandwidth && s.Connections == 10);
        settings.Connections = 2; engine.SetSettings(settings);
        await Wait(engine, id, s => s.Connections == 2 && !s.ApplyingConnections);
        await engine.PauseAsync(id).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.All(engine.Snapshots()[0].Segments, s => Assert.Equal(0, s.Retry));
        settings.BandwidthLimitBytesPerSecond = 8 * 1024 * 1024; engine.SetSettings(settings); engine.Resume(id);
        await Wait(engine, id, s => s.Bytes > 1024 * 1024);
        settings.BandwidthLimitBytesPerSecond = 0; engine.SetSettings(settings);
        var done = await Wait(engine, id, s => s.State == DownloadState.Completed);
        Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(File.ReadAllBytes(done.Target!)));
    }
    private sealed class BrokenFlush : IDownloadFiles
    {
        public FileStream OpenWrite(string path, FileMode mode) => new DownloadFiles().OpenWrite(path, mode);
        public void Flush(FileStream file) => throw new IOException("test only");
    }
    [Fact]
    public async Task FailedFlushNeverAdvancesCommittedMetadata()
    {
        await using var server = await TestServer.Start(128 * 1024);
        await using var engine = new DownloadEngine(root, diskSpaceService: new Disk(), downloadFiles: new BrokenFlush());
        var id = engine.Add(new(server.Url), root);
        await Wait(engine, id, s => s.State == DownloadState.Failed);
        await engine.PauseAsync(id);
        var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(Path.Combine(root, "state.json")))!;
        Assert.All(library.Jobs[0].Segments, s => { Assert.Equal(0, s.Committed); Assert.False(s.Complete); });
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void MigrationKeepsOriginalBackupAndV4PartIdentity(int version)
    {
        Directory.CreateDirectory(root);
        var segment = new Segment { Start = 0, End = 9, Committed = 3 };
        var job = new DownloadJob { State = DownloadState.Paused, Destination = root, Total = 10, Segments = [segment], ETag = "\"v1\"" };
        var json = JsonSerializer.Serialize(new Library { Version = version, GloballyPaused = true, Settings = new() { Language = "ru" }, Jobs = [job] });
        var path = Path.Combine(root, "state.json"); File.WriteAllText(path, json);
        using var store = new StateStore(root); var library = store.Load();
        Assert.Equal(5, library.Version); Assert.Equal(0, library.Settings.BandwidthLimitBytesPerSecond);
        Assert.Equal(version >= 3 ? "ru" : "en", library.Settings.Language);
        if (version == 4) { Assert.Equal(segment.Id, library.Jobs[0].Segments[0].Id); Assert.Equal(segment.FileName, library.Jobs[0].Segments[0].FileName); }
        store.Save(library); Assert.Equal(json, File.ReadAllText(path + $".v{version}.bak"));
        Assert.DoesNotContain("DiskSpace", File.ReadAllText(path));
    }
    [Theory]
    [InlineData("en", "1.5")] [InlineData("ru", "1,5")]
    public void BandwidthFormattingAndValidation(string language, string text)
    {
        var localization = new Localization(language);
        Assert.Equal(1536, localization.ParseBandwidth(text, false)); Assert.Equal(1572864, localization.ParseBandwidth(text, true));
        Assert.Equal(0, localization.ParseBandwidth("0", false));
        foreach (var bad in new[] { "-1", "nan", "99999999999999999999999999999999999999", language == "ru" ? "0,00001" : "0.00001" })
            Assert.Throws<ProblemException>(() => localization.ParseBandwidth(bad, false));
        Assert.NotEmpty(localization.Bandwidth(0)); Assert.NotEmpty(localization.Bandwidth(1024));
    }
    [Fact]
    public async Task SavedLimitAndLanguageSurviveRestartAndFailedSettingsStayUnchanged()
    {
        await using (var engine = new DownloadEngine(root))
        {
            var settings = engine.GetSettings(); settings.BandwidthLimitBytesPerSecond = 123456; settings.Language = "ru"; engine.SetSettings(settings);
            Directory.CreateDirectory(Path.Combine(root, "state.json.tmp"));
            settings.BandwidthLimitBytesPerSecond = 1;
            Assert.Throws<ProblemException>(() => engine.SetSettings(settings));
            Assert.Equal(123456, engine.GetSettings().BandwidthLimitBytesPerSecond);
            Directory.Delete(Path.Combine(root, "state.json.tmp"));
        }
        Assert.Equal("ru", StateStore.ReadLanguage(root));
        await using var restored = new DownloadEngine(root); Assert.Equal(123456, restored.GetSettings().BandwidthLimitBytesPerSecond);
    }
    [Fact]
    public void NativeDiskMeasurementResolvesDirectoryLinks()
    {
        Directory.CreateDirectory(root);
        var actual = new DiskSpaceService().Measure(root); Assert.True(actual.AvailableBytes >= 0); Assert.NotEmpty(actual.Id);
        if (OperatingSystem.IsWindows()) return; // Windows link creation may require additional privileges.
        var link = Path.Combine(root, "link"); Directory.CreateSymbolicLink(link, Path.GetTempPath());
        Assert.Equal(actual.Id, new DiskSpaceService().Measure(Path.Combine(link, "not-created")).Id);
    }
}
