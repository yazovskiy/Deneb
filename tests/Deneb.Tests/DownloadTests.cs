using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Deneb.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Deneb.Tests;

public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication app;
    public byte[] Data { get; }
    public bool Ranges = true;
    public bool NoValidators;
    public bool LastModifiedOnly;
    public bool UnknownLength;
    public bool BadRange;
    public bool IgnoreTransferRange;
    public bool Return416;
    public string ETag = "\"v1\"";
    public int Denied;
    public int Failures;
    public int FailureCode = 503;
    public int AbortOnce;
    public bool RetryFirstSegment;
    public int DelayMs;
    public int Peak;
    private int active;
    public ConcurrentBag<(long Start, long End)> Requests { get; } = [];
    public string Url => app.Urls.Single() + "/file";
    private TestServer(WebApplication app, byte[] data) { this.app = app; Data = data; }
    public static async Task<TestServer> Start(int size = 1024 * 1024)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var data = new byte[size]; new Random(42).NextBytes(data);
        var server = new TestServer(app, data);
        app.MapGet("/file", server.Handle);
        app.MapGet("/redirect", context => { context.Response.Redirect("/file"); return Task.CompletedTask; });
        await app.StartAsync(); return server;
    }
    private async Task Handle(HttpContext ctx)
    {
        if (Denied != 0) { ctx.Response.StatusCode = Denied; return; }
        if (Interlocked.Decrement(ref Failures) >= 0) { ctx.Response.StatusCode = FailureCode; ctx.Response.Headers.RetryAfter = "0"; return; }
        var range = ctx.Request.Headers.Range.ToString();
        var probe = range == "bytes=0-0";
        if (RetryFirstSegment && !probe && range.StartsWith("bytes=0-"))
        { ctx.Response.StatusCode = 429; ctx.Response.Headers.RetryAfter = "4"; return; }
        long start = 0, end = Data.Length - 1;
        if (!NoValidators) { if (!LastModifiedOnly) ctx.Response.Headers.ETag = ETag; ctx.Response.Headers.LastModified = "Wed, 01 Jan 2025 00:00:00 GMT"; }
        ctx.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''%D1%84%D0%B0%D0%B9%D0%BB.bin";
        if ((Return416 && !probe) || Data.Length == 0)
        { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = $"bytes */{Data.Length}"; return; }
        if (Ranges && !UnknownLength && range.Length > 0 && !(IgnoreTransferRange && !probe))
        {
            var parts = range[6..].Split('-'); start = long.Parse(parts[0]); end = long.Parse(parts[1]);
            if (start >= Data.Length) { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = $"bytes */{Data.Length}"; return; }
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.ContentRange = $"bytes {(BadRange ? start + 1 : start)}-{end}/{Data.Length}";
        }
        if (!UnknownLength) ctx.Response.ContentLength = end - start + 1;
        if (!probe) Requests.Add((start, end));
        var current = Interlocked.Increment(ref active);
        int previous;
        do { previous = Peak; } while (current > previous && Interlocked.CompareExchange(ref Peak, current, previous) != previous);
        try
        {
            for (long pos = start; pos <= end;)
            {
                var length = (int)Math.Min(65536, end - pos + 1);
                await ctx.Response.Body.WriteAsync(Data.AsMemory((int)pos, length), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                pos += length;
                if (!probe && Interlocked.Exchange(ref AbortOnce, 0) == 1) { ctx.Abort(); return; }
                if (!probe && DelayMs > 0) await Task.Delay(DelayMs, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Decrement(ref active); }
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}

public sealed class DownloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-tests-" + Guid.NewGuid().ToString("N"));
    private string StateDir => Path.Combine(root, "state");
    private string Destination => Path.Combine(root, "downloads");
    private DownloadEngine Engine() => new(StateDir);
    private Guid Add(DownloadEngine e, string url, string? name = null) => e.Add(new(url, name), Destination);
    private static async Task<Snapshot> Wait(DownloadEngine e, Guid id, Func<Snapshot, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            var snapshot = e.Snapshots().Single(s => s.Id == id);
            if (condition(snapshot)) return snapshot;
            if (snapshot.State is DownloadState.Failed or DownloadState.NeedsDecision) throw new Exception(snapshot.Error?.ToString());
            await Task.Delay(25, timeout.Token);
        }
    }
    private static Task<Snapshot> Finished(DownloadEngine e, Guid id) => Wait(e, id, s => s.State == DownloadState.Completed);
    [Fact]
    public void AndroidRowsAndMarkdown()
    {
        var rows = InputParser.Parse("Row: 2 \\_id=7, title=Джек Ричер 1080p.mp4, uri=[https://example.org/a?x=1==](https://example.org/a?x=1==), status=192, bytes_so_far=6512374\nhttps://example.org/b");
        Assert.Equal(2, rows.Count); Assert.Equal("Джек Ричер 1080p.mp4", rows[0].Name);
        Assert.Equal("https://example.org/a?x=1==", rows[0].Url);
        Assert.Throws<ProblemException>(() => InputParser.Parse("ftp://example.org/a"));
        Assert.Equal("evil_.mp4", InputParser.SafeName("../../evil?.mp4", Guid.NewGuid()));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(InputParser.SafeName(new string('Я', 200) + ".mp4", Guid.NewGuid())) <= 200);
    }
    [Fact]
    public async Task SegmentedHashMatches()
    {
        await using var server = await TestServer.Start(20 * 1024 * 1024); server.DelayMs = 2;
        await using var e = Engine(); var id = Add(e, server.Url);
        var done = await Finished(e, id);
        Assert.Equal(SHA256.HashData(server.Data), SHA256.HashData(await File.ReadAllBytesAsync(done.Target!)));
        Assert.True(server.Peak >= 2); Assert.Equal(4, server.Requests.Count);
        Assert.Equal("файл.bin", done.Name);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialAndUnknownSize(bool unknown)
    {
        await using var server = await TestServer.Start(); server.Ranges = false; server.UnknownLength = unknown;
        await using var e = Engine(); var done = await Finished(e, Add(e, server.Url));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!)); Assert.Equal(server.Data.Length, done.Total);
    }
    [Fact]
    public async Task EmptyFile()
    {
        await using var server = await TestServer.Start(0); await using var e = Engine();
        var done = await Finished(e, Add(e, server.Url)); Assert.Equal(0, new FileInfo(done.Target!).Length);
    }
    [Fact]
    public async Task PauseResumeKeepsBytes()
    {
        await using var server = await TestServer.Start(5 * 1024 * 1024); server.DelayMs = 8;
        await using var e = Engine(); var id = Add(e, server.Url);
        await Wait(e, id, s => s.Bytes > 200000); await e.PauseAsync(id);
        var bytes = e.Snapshots().Single().Bytes; Assert.True(bytes > 0);
        e.Resume(id); var done = await Finished(e, id);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!)); Assert.Contains(server.Requests, r => r.Start > 0);
    }
    [Fact]
    public async Task RestartAutoResumesButPreservesManualPause()
    {
        await using var server = await TestServer.Start(5 * 1024 * 1024); server.DelayMs = 12;
        Guid auto, paused;
        await using (var e = Engine())
        {
            auto = Add(e, server.Url, "auto.bin"); paused = Add(e, server.Url, "paused.bin");
            await Wait(e, auto, s => s.Bytes > 100000); await e.PauseAsync(paused);
        }
        await using (var e = Engine())
        {
            var done = await Finished(e, auto); Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!));
            Assert.Equal(DownloadState.Paused, e.Snapshots().Single(s => s.Id == paused).State);
        }
    }
    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(408)]
    public async Task RetriesTransientStatus(int status)
    {
        await using var server = await TestServer.Start(); server.Failures = 2; server.FailureCode = status;
        await using var e = Engine(); await Finished(e, Add(e, server.Url));
    }
    [Fact]
    public async Task InterruptedBodyIsResumed()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024); server.AbortOnce = 1;
        await using var e = Engine(); var done = await Finished(e, Add(e, server.Url));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!));
    }
    [Theory]
    [InlineData("403")]
    [InlineData("bad-range")]
    [InlineData("ignore-range")]
    [InlineData("416")]
    public async Task InvalidResponsesNeedDecision(string mode)
    {
        await using var server = await TestServer.Start();
        server.Denied = mode == "403" ? 403 : 0; server.BadRange = mode == "bad-range";
        server.IgnoreTransferRange = mode == "ignore-range"; server.Return416 = mode == "416";
        await using var e = Engine(); var result = await Wait(e, Add(e, server.Url), s => s.State == DownloadState.NeedsDecision);
        Assert.NotNull(result.Error); Assert.False(File.Exists(result.Target));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedReplacementPreservesOldBytes(bool noValidators)
    {
        await using var server = await TestServer.Start(5 * 1024 * 1024); server.DelayMs = 10; server.NoValidators = noValidators;
        await using var e = Engine(); var id = Add(e, server.Url);
        await Wait(e, id, s => s.Bytes > 100000); await e.PauseAsync(id);
        var before = e.Snapshots().Single().Bytes; server.ETag = "\"changed\"";
        await e.ReplaceUrlAsync(id, server.Url + "?new=1");
        var result = await Wait(e, id, s => s.State == DownloadState.NeedsDecision);
        Assert.Equal(before, result.Bytes); Assert.False(File.Exists(result.Target));
        await e.RestartAsync(id); var done = await Finished(e, id);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!));
        Assert.NotEmpty(Directory.GetDirectories(Destination, "*.saved-*"));
    }
    [Fact]
    public async Task ValidReplacementResumes()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024); server.DelayMs = 10;
        await using var e = Engine(); var id = Add(e, server.Url);
        await Wait(e, id, s => s.Bytes > 100000); await e.PauseAsync(id);
        await e.ReplaceUrlAsync(id, server.Url + "?fresh=1"); await Finished(e, id);
        Assert.Contains(server.Requests, r => r.Start > 0);
    }
    [Fact]
    public async Task RedirectAndNameConflict()
    {
        await using var server = await TestServer.Start();
        Directory.CreateDirectory(Destination); await File.WriteAllTextAsync(Path.Combine(Destination, "same.bin"), "old");
        await using var e = Engine(); var done = await Finished(e, Add(e, server.Url.Replace("/file", "/redirect"), "same.bin"));
        Assert.Equal("same (1).bin", done.Name); Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(Destination, "same.bin")));
    }
    [Fact]
    public async Task DiskErrorIsNotNetworkError()
    {
        await using var server = await TestServer.Start(); Directory.CreateDirectory(root); await File.WriteAllTextAsync(Destination, "not a directory");
        await using var e = Engine(); var failed = await Wait(e, Add(e, server.Url), s => s.State == DownloadState.Failed);
        Assert.Equal(ProblemCode.Disk, failed.Error?.Code);
    }
    [Fact]
    public void StoreLockAndBackupRecovery()
    {
        using (var store = new StateStore(StateDir))
        {
            store.Save(new()); store.Save(new());
            Assert.Throws<ProblemException>(() => new StateStore(StateDir));
        }
        File.WriteAllText(Path.Combine(StateDir, "state.json"), "broken");
        using var recovered = new StateStore(StateDir); Assert.Equal(5, recovered.Load().Version);
        recovered.Save(new()); Assert.NotNull(JsonSerializer.Deserialize<Library>(File.ReadAllText(Path.Combine(StateDir, "state.json.bak"))));
    }
    [Fact]
    public async Task CrashCheckpointTruncatesUncommittedTailAndResumes()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024);
        var job = new DownloadJob
        {
            Url = server.Url,
            Name = "crash.bin",
            Destination = Destination,
            Target = Path.Combine(Destination, "crash.bin"),
            State = DownloadState.Downloading,
            Total = server.Data.Length,
            ETag = server.ETag,
            Ranges = true,
            Segments = [new() { Start = 0, End = server.Data.Length - 1, Committed = 65536 }]
        };
        Directory.CreateDirectory(job.PartsDirectory);
        await File.WriteAllBytesAsync(Path.Combine(job.PartsDirectory, job.Segments[0].FileName), server.Data[..131072]);
        using (var store = new StateStore(StateDir)) store.Save(new() { Jobs = [job] });
        await using var e = Engine(); var done = await Finished(e, job.Id);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!));
        Assert.Contains(server.Requests, r => r.Start == 65536);
    }
    [Fact]
    public async Task MissingCommittedBytesRequireDecision()
    {
        await using var server = await TestServer.Start();
        var job = new DownloadJob
        {
            Url = server.Url,
            Destination = Destination,
            State = DownloadState.Downloading,
            Segments = [new() { Committed = 100, End = server.Data.Length - 1 }]
        };
        using (var store = new StateStore(StateDir)) store.Save(new() { Jobs = [job] });
        await using var e = Engine();
        Assert.Equal(DownloadState.NeedsDecision, e.Snapshots().Single().State);
        Assert.Empty(server.Requests);
    }
    [Fact]
    public async Task CrashAfterPublishingDoesNotDuplicateOutput()
    {
        await using var server = await TestServer.Start(); Directory.CreateDirectory(Destination);
        var target = Path.Combine(Destination, "published.bin"); await File.WriteAllBytesAsync(target, server.Data);
        var job = new DownloadJob
        {
            Url = server.Url,
            Destination = Destination,
            Target = target,
            Name = "published.bin",
            State = DownloadState.Downloading,
            Total = server.Data.Length,
            FinalHash = Convert.ToHexString(SHA256.HashData(server.Data)),
            Segments = [new() { Committed = server.Data.Length, End = server.Data.Length - 1, Complete = true }]
        };
        using (var store = new StateStore(StateDir)) store.Save(new() { Jobs = [job] });
        await using var e = Engine(); Assert.Equal(DownloadState.Completed, e.Snapshots().Single().State);
        Assert.Single(Directory.GetFiles(Destination)); Assert.Empty(server.Requests);
    }
    [Fact]
    public async Task LastModifiedValidatorAllowsResume()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024); server.LastModifiedOnly = true; server.DelayMs = 10;
        await using var e = Engine(); var id = Add(e, server.Url);
        await Wait(e, id, s => s.Bytes > 100000); await e.PauseAsync(id); e.Resume(id);
        var done = await Finished(e, id); Assert.Equal(server.Data, await File.ReadAllBytesAsync(done.Target!));
    }
    [Fact]
    public async Task RemoveRetainsPartialByDefault()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024); server.DelayMs = 10;
        await using var e = Engine(); var id = Add(e, server.Url);
        await Wait(e, id, s => s.Bytes > 100000); await e.RemoveAsync(id);
        Assert.Empty(e.Snapshots()); Assert.NotEmpty(Directory.GetFiles(Destination, "*.part", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task QueueHonorsFileLimit()
    {
        await using var server = await TestServer.Start(3 * 1024 * 1024); server.DelayMs = 8;
        await using var e = Engine(); e.SetSettings(new() { ActiveFiles = 1, Connections = 4, Destination = Destination });
        var first = Add(e, server.Url); var second = Add(e, server.Url);
        await Wait(e, first, s => s.Bytes > 100000);
        Assert.Equal(DownloadState.Queued, e.Snapshots().Single(s => s.Id == second).State);
        await Finished(e, first); await Finished(e, second);
    }
    private sealed class TimeoutOnceHandler : DelegatingHandler
    {
        private bool failed;
        public TimeoutOnceHandler() : base(new SocketsHttpHandler()) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!failed) { failed = true; throw new TaskCanceledException("simulated timeout"); }
            return base.SendAsync(request, cancellationToken);
        }
    }
    [Fact]
    public async Task HeaderTimeoutRetries()
    {
        await using var server = await TestServer.Start();
        await using var e = new DownloadEngine(StateDir, new TimeoutOnceHandler());
        await Finished(e, Add(e, server.Url));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
