using System.Security.Cryptography;
using System.Text.Json;
using Deneb.Core;
using Deneb.Control;
using Xunit;

namespace Deneb.Tests;

public sealed class Version20Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-v20-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private async Task<Snapshot> Wait(DownloadEngine engine, Guid id, Func<Snapshot, bool> predicate)
    {
        for (var i = 0; i < 1000; i++)
        {
            var snapshot = engine.Snapshots().Single(s => s.Id == id);
            Assert.True(snapshot.State is not (DownloadState.Failed or DownloadState.NeedsDecision), snapshot.Error?.ToString());
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(20);
        }
        throw new TimeoutException(JsonSerializer.Serialize(engine.Snapshots()));
    }
    [Fact]
    public async Task ConnectionsChangeLiveAndPreserveHashAcrossRestart()
    {
        await using var server = await TestServer.Start(64 * 1024 * 1024); server.DelayMs = 25;
        Guid id; long checkpoint;
        await using (var engine = new DownloadEngine(root))
        {
            id = engine.Add(new(server.Url), root);
            await Wait(engine, id, s => s.Connections == 4 && s.Bytes > 1048576);
            var settings = engine.GetSettings(); settings.Connections = 10; engine.SetSettings(settings);
            var expanded = await Wait(engine, id, s => s.Connections == 10);
            Assert.True(expanded.Segments.Count >= 10);
            settings.Connections = 2; engine.SetSettings(settings);
            await Wait(engine, id, s => s.Connections == 2 && !s.ApplyingConnections);
            await Task.Delay(200);
            Assert.True(engine.Snapshots()[0].Connections <= 2);
            await engine.PauseAsync(id);
            checkpoint = engine.Snapshots()[0].Bytes;
            Assert.True(checkpoint >= expanded.Bytes);
            settings.Connections = 4; engine.SetSettings(settings);
            Assert.Equal(DownloadState.Paused, engine.Snapshots()[0].State);
        }
        await using (var engine = new DownloadEngine(root))
        {
            Assert.Equal(checkpoint, engine.Snapshots()[0].Bytes);
            engine.Resume(id);
            var done = await Wait(engine, id, s => s.State == DownloadState.Completed);
            Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(await File.ReadAllBytesAsync(done.Target!)));
        }
    }
    [Fact]
    public void Version3MigrationPreservesPartFilesAndRussian()
    {
        Directory.CreateDirectory(root);
        var job = new DownloadJob
        {
            Destination = root,
            Total = 9,
            State = DownloadState.Paused,
            Segments = [new() { Start = 0, End = 8, Committed = 3 }]
        };
        Directory.CreateDirectory(job.PartsDirectory);
        File.WriteAllBytes(Path.Combine(job.PartsDirectory, "000.part"), [1, 2, 3]);
        var json = JsonSerializer.Serialize(new Library { Version = 3, Settings = new() { Language = "ru" }, Jobs = [job], GloballyPaused = true });
        File.WriteAllText(Path.Combine(root, "state.json"), json);
        using var store = new StateStore(root);
        var loaded = store.Load(); store.Save(loaded);
        Assert.Equal(4, loaded.Version); Assert.Equal("ru", loaded.Settings.Language);
        Assert.True(loaded.GloballyPaused); Assert.Equal("000.part", loaded.Jobs[0].Segments[0].FileName);
        Assert.Equal(json, File.ReadAllText(Path.Combine(root, "state.json.v3.bak")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(job.PartsDirectory, "000.part")));
    }
    [Theory]
    [InlineData("../escape.part")]
    [InlineData("..\\escape.part")]
    [InlineData("/tmp/escape.part")]
    public void InvalidPartNamesAreRejected(string file)
    {
        var job = new DownloadJob { Total = 4, Segments = [new() { Start = 0, End = 3, FileName = file }] };
        Assert.Throws<ProblemException>(() => StateStore.ValidateSegments(job));
    }
    [Fact]
    public async Task IpcLeaseCommandsAndDisconnectDoNotStopEngine()
    {
        var endpoint = new LocalEndpoint(root);
        var server = new ControlServer(endpoint);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        try
        {
            await using var first = await ControlClient.ConnectAsync(endpoint, true);
            var error = await Assert.ThrowsAsync<ProblemException>(() => ControlClient.ConnectAsync(endpoint, true));
            Assert.Equal(ProblemCode.InterfaceOpen, error.Problem.Code);
            await using var cli = await ControlClient.ConnectAsync(endpoint);
            await cli.SendAsync(new() { Command = Command.PauseAll });
            var settings = cli.State.Settings; settings.Language = "ru"; settings.Connections = 10;
            await cli.SendAsync(new() { Command = Command.Settings, Settings = settings });
            await first.SendAsync(new() { Command = Command.Snapshot });
            Assert.True(first.State.GloballyPaused); Assert.Equal(10, first.State.Settings.Connections);
            await first.DisposeAsync(); await Task.Delay(100);
            await using var replacement = await ControlClient.ConnectAsync(endpoint, true);
            Assert.Equal("ru", replacement.State.Settings.Language);
            await cli.SendAsync(new() { Command = Command.Stop });
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Cancel(); await running; }
    }
    [Fact]
    public async Task ProtocolRejectsWrongVersionAndOversizedFrame()
    {
        var endpoint = new LocalEndpoint(root); using var stop = new CancellationTokenSource();
        var server = new ControlServer(endpoint).RunAsync(stop.Token);
        try
        {
            using var stream = await endpoint.ConnectAsync(default);
            var request = new Request { Protocol = 99, Command = Command.Hello };
            await Framing.WriteAsync(stream, request, default);
            var response = await Framing.ReadAsync<Response>(stream, default);
            Assert.Equal(ProblemCode.Incompatible, response.Error!.Code);
            using var frame = new MemoryStream([255, 255, 255, 127]);
            await Assert.ThrowsAsync<ProblemException>(() => Framing.ReadAsync<Request>(frame, default));
        }
        finally { stop.Cancel(); await server; }
    }
    [Fact]
    public async Task SettingsFailureDoesNotChangeTheEffectiveLimit()
    {
        await using var engine = new DownloadEngine(root);
        await engine.PauseAllAsync();
        Directory.CreateDirectory(Path.Combine(root, "state.json.tmp"));
        var settings = engine.GetSettings(); settings.Connections = 10;
        var error = Assert.Throws<ProblemException>(() => engine.SetSettings(settings));
        Assert.Equal(ProblemCode.Persistence, error.Problem.Code);
        Assert.Equal(4, engine.GetSettings().Connections);
        Directory.Delete(Path.Combine(root, "state.json.tmp"));
    }
    [Fact]
    public async Task RetryBudgetAndDelaySurviveLiveChanges()
    {
        await using var server = await TestServer.Start(32 * 1024 * 1024);
        server.DelayMs = 20; server.RetryFirstSegment = true;
        await using var engine = new DownloadEngine(root);
        var id = engine.Add(new(server.Url), root);
        var waiting = await Wait(engine, id, s => s.Segments.Any(p => p.Start == 0 && p.Retry == 1));
        var settings = engine.GetSettings(); settings.Connections = 1; engine.SetSettings(settings);
        await Task.Delay(200);
        settings.Connections = 10; engine.SetSettings(settings);
        await Task.Delay(200);
        var first = engine.Snapshots()[0].Segments.Single(p => p.Start == 0);
        Assert.Equal(1, first.Retry);
        Assert.True(first.RetryIn < waiting.Segments.Single(p => p.Start == 0).RetryIn);
        server.RetryFirstSegment = false;
        var done = await Wait(engine, id, s => s.State == DownloadState.Completed);
        Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(await File.ReadAllBytesAsync(done.Target!)));
    }
    [Fact]
    public async Task NoRangesRemainSingleAndPauseWinsReconfiguration()
    {
        await using var server = await TestServer.Start(20 * 1024 * 1024); server.Ranges = false; server.DelayMs = 10;
        await using var engine = new DownloadEngine(root);
        var id = engine.Add(new(server.Url), root);
        await Wait(engine, id, s => s.Bytes > 65536);
        var settings = engine.GetSettings(); settings.Connections = 10; engine.SetSettings(settings);
        await Task.Delay(100);
        Assert.Equal(1, engine.Snapshots()[0].Connections);
        Assert.Equal(ConnectionConstraint.Server, engine.Snapshots()[0].ConnectionConstraint);
        await engine.PauseAllAsync();
        Assert.True(engine.GloballyPaused);
        Assert.Equal(0, engine.Snapshots()[0].Connections);
        Assert.NotEqual(DownloadState.Failed, engine.Snapshots()[0].State);
    }
    [Fact]
    public void SplitMapCanRecoverOnEitherSideOfAtomicPublication()
    {
        Directory.CreateDirectory(root);
        var first = new Segment { Start = 0, End = 15, Committed = 4 };
        var job = new DownloadJob { Url = "https://example.org/file", Destination = root, Total = 16, Segments = [first], State = DownloadState.Paused };
        Directory.CreateDirectory(job.PartsDirectory);
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(job.PartsDirectory, first.FileName), bytes);
        using var store = new StateStore(root);
        var library = new Library { Jobs = [job] };
        store.Save(library);
        var original = File.ReadAllText(Path.Combine(root, "state.json"));
        // An incomplete temporary map is never used for recovery.
        File.WriteAllText(Path.Combine(root, "state.json.tmp"), "{");
        Assert.Single(store.Load().Jobs[0].Segments);
        first.End = 9; job.Segments.Add(new() { Start = 10, End = 15 });
        store.Save(library);
        var loaded = store.Load(); Assert.Equal(2, loaded.Jobs[0].Segments.Count);
        Assert.Equal(first.Id, loaded.Jobs[0].Segments[0].Id);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(job.PartsDirectory, first.FileName)));
        Assert.Equal(original, File.ReadAllText(Path.Combine(root, "state.json.bak")));
        StateStore.ValidateSegments(loaded.Jobs[0]);
    }
}
