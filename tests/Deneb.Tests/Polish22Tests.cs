using System.Text.Json;
using Deneb.App;
using Deneb.Control;
using Deneb.Core;
using Xunit;

namespace Deneb.Tests;

public sealed class Polish22Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-polish-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class FakeTrash(Action<string> move, bool supported = true) : ITrashService
    {
        public bool Supported => supported;
        public int Calls { get; private set; }
        public string Move(string path) { Calls++; move(path); return ""; }
    }
    private DownloadJob Completed(string name = "готовый.bin")
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name); File.WriteAllText(path, "fixture");
        return new() { Name = name, Url = "https://example.org/fixture", Destination = root, Target = path, State = DownloadState.Completed };
    }
    private void Seed(params DownloadJob[] jobs)
    {
        using var store = new StateStore(Path.Combine(root, "state"));
        store.Save(new() { Jobs = jobs.ToList(), GloballyPaused = true });
    }
    [Theory]
    [InlineData("help", null)]
    [InlineData("-h", null)]
    [InlineData("help add", "add")]
    [InlineData("add --help", "add")]
    [InlineData("remove -h", "remove")]
    public void HelpFormsHaveOneLocalizedSource(string args, string? topic)
    {
        var options = CliOptions.Parse(args.Split(' ')); Assert.True(options.Help); Assert.Equal(topic, options.HelpTopic);
        foreach (var language in new[] { "en", "ru" })
        {
            var text = CliHelp.Format(new(language), topic); Assert.Contains("deneb", text);
            Assert.Contains(language == "en" ? "Usage:" : "Использование:", text);
        }
        foreach (var command in CliHelp.Commands) Assert.Contains("--state-dir", CliHelp.Format(new(), command));
    }
    [Theory]
    [InlineData("help unknown")]
    [InlineData("help add extra")]
    [InlineData("remove 12345678 --trash")]
    [InlineData("remove 12345678 --trash --delete-partial --yes")]
    [InlineData("list --trash --yes")]
    public void InvalidHelpAndTrashFlagsAreRejected(string args) => Assert.Throws<CliInputException>(() => CliOptions.Parse(args.Split(' ')));

    [Theory]
    [InlineData(78)]
    [InlineData(118)]
    [InlineData(158)]
    public void NamesUseHalfTheTableAndTwoDisplayLines(int width)
    {
        Assert.True(QueueLayout.NameWidth(width) >= width / 2);
        var name = string.Concat(Enumerable.Repeat("Длинное имя 字😀 ", 80));
        var preview = QueueLayout.Preview(name, width);
        Assert.EndsWith("…", preview); Assert.Equal(2, preview.Split('\n').Length);
        foreach (var line in preview.Split('\n')) Assert.True(((NStack.ustring)line).ConsoleWidth <= width);
        Assert.Equal("Файл.bin", QueueLayout.Preview("Файл.bin", width));
        Assert.DoesNotContain('\u001b', QueueLayout.Preview("a\u001bb", width));
    }
    [Fact]
    public async Task TrashProcessesOnlyCompletedAndPreservesFailedEntries()
    {
        var a = Completed("a"); var b = Completed("b"); var c = Completed("c"); c.State = DownloadState.Paused;
        Seed(a, b, c);
        await using var engine = new DownloadEngine(Path.Combine(root, "state"));
        var service = new FakeTrash(path => { if (path == b.Target) throw new UnauthorizedAccessException("private path"); File.Move(path, path + ".fixture-trash"); });
        var result = TrashOperations.Execute(engine, [a.Id, b.Id, c.Id], service);
        Assert.Equal(new[] { a.Id }, result.Processed); Assert.Equal(new[] { b.Id }, result.Failed); Assert.Equal(new[] { c.Id }, result.Skipped);
        Assert.False(result.Errors.Single().FileMoved); Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.Equal(2, engine.Snapshots().Count); Assert.True(File.Exists(b.Target));
    }
    [Fact]
    public async Task MissingAndLinkedFilesAreNeverSentToAdapter()
    {
        var missing = Completed("missing"); File.Delete(missing.Target!);
        var link = Completed("link"); File.Delete(link.Target!); File.CreateSymbolicLink(link.Target!, missing.Target!);
        var folder = Completed("folder"); File.Delete(folder.Target!); Directory.CreateDirectory(folder.Target!);
        Seed(missing, link, folder);
        await using var engine = new DownloadEngine(Path.Combine(root, "state"));
        var service = new FakeTrash(_ => throw new Exception("must not be called"));
        var result = TrashOperations.Execute(engine, [missing.Id, link.Id, folder.Id], service);
        Assert.Equal(0, service.Calls); Assert.Equal(3, result.Failed.Count);
        Assert.Equal(ProblemCode.MissingFile, result.Errors[0].Error.Code);
        Assert.Equal(ProblemCode.UnsafeFileType, result.Errors[1].Error.Code);
        Assert.Equal(ProblemCode.UnsafeFileType, result.Errors[2].Error.Code);
    }
    [Fact]
    public async Task UnsupportedPlatformRetainsFileAndEntry()
    {
        var job = Completed(); Seed(job);
        await using var engine = new DownloadEngine(Path.Combine(root, "state"));
        var service = new FakeTrash(_ => { }, false);
        var result = TrashOperations.Execute(engine, [job.Id], service);
        Assert.Equal(ProblemCode.UnsupportedPlatform, result.Errors.Single().Error.Code);
        Assert.Equal(0, service.Calls); Assert.True(File.Exists(job.Target)); Assert.Single(engine.Snapshots());
    }
    [Fact]
    public async Task MetadataFailureAfterTrashRetainsCompletedRowAndDoesNotRepeat()
    {
        var job = Completed(); Seed(job); var state = Path.Combine(root, "state");
        var service = new FakeTrash(path => { File.Move(path, path + ".fixture-trash"); Directory.CreateDirectory(Path.Combine(state, "state.json.tmp")); });
        await using (var engine = new DownloadEngine(state))
        {
            var result = TrashOperations.Execute(engine, [job.Id], service);
            Assert.Equal(ProblemCode.Persistence, result.Errors.Single().Error.Code); Assert.True(result.Errors.Single().FileMoved);
            Assert.Equal(DownloadState.Completed, engine.Snapshots().Single().State);
            var again = TrashOperations.Execute(engine, [job.Id], service); Assert.Equal(ProblemCode.MissingFile, again.Errors.Single().Error.Code);
            Assert.Equal(1, service.Calls); Directory.Delete(Path.Combine(state, "state.json.tmp"));
        }
        await using var restored = new DownloadEngine(state);
        Assert.Equal(DownloadState.Completed, restored.Snapshots().Single().State);
    }
    [Fact]
    public async Task CrashWindowBeforeForgetLeavesCompletedMissingFile()
    {
        var job = Completed(); Seed(job);
        // Reproduce durable state at a crash after the external move but before ForgetCompleted.
        File.Move(job.Target!, job.Target + ".fixture-trash");
        await using var restored = new DownloadEngine(Path.Combine(root, "state"));
        Assert.Equal(DownloadState.Completed, restored.Snapshots().Single().State);
        var service = new FakeTrash(_ => { });
        Assert.Equal(ProblemCode.MissingFile, TrashOperations.Execute(restored, [job.Id], service).Errors.Single().Error.Code);
        Assert.Equal(0, service.Calls);
    }
    [Fact]
    public async Task ChangedTargetCannotBeForgottenAndPartialRemovalSkipsCompleted()
    {
        var job = Completed(); Seed(job);
        await using var engine = new DownloadEngine(Path.Combine(root, "state"));
        Assert.Throws<ProblemException>(() => engine.ForgetCompleted(job.Id, job.Target + ".other"));
        var result = await engine.RemoveManyAsync([job.Id], true);
        Assert.Equal(new[] { job.Id }, result.Skipped); Assert.True(File.Exists(job.Target)); Assert.Single(engine.Snapshots());
        await engine.RemoveManyAsync([job.Id]); Assert.Empty(engine.Snapshots()); Assert.True(File.Exists(job.Target));
    }
    [Fact]
    public async Task ConfirmationDoesNotExpandWhenAnotherDownloadCompletes()
    {
        var a = Completed("confirmed"); var b = Completed("was-active"); Seed(a, b);
        await using var engine = new DownloadEngine(Path.Combine(root, "state"));
        var completed = engine.Snapshots().ToArray();
        var atConfirmation = completed.Select(j => j.Id == b.Id ? j with { State = DownloadState.Downloading } : j).ToArray();
        var confirmed = QueueView.TrashTargets(atConfirmation, [a.Id, b.Id]);
        Assert.Equal(new[] { a.Id }, confirmed);
        var service = new FakeTrash(path => File.Move(path, path + ".fixture-trash"));
        var result = TrashOperations.Execute(engine, confirmed, service);
        Assert.Equal(new[] { a.Id }, result.Processed);
        Assert.True(File.Exists(b.Target)); Assert.Equal(b.Id, engine.Snapshots().Single().Id);
    }
    [Fact]
    public async Task TrashIsCoordinatedByDaemonAndJsonContainsPerIdFailures()
    {
        var good = Completed("good"); var bad = Completed("bad"); Seed(good, bad);
        using var cancel = new CancellationTokenSource(); var state = Path.Combine(root, "state");
        var endpoint = new LocalEndpoint(state);
        var service = new FakeTrash(path => { if (path == bad.Target) throw new ProblemException(ProblemCode.TrashFailed); File.Move(path, path + ".fixture-trash"); });
        var server = new ControlServer(endpoint, service).RunAsync(cancel.Token);
        try
        {
            await using var ui = await ControlClient.ConnectAsync(endpoint, true);
            var output = new StringWriter(); var errors = new StringWriter();
            var options = CliOptions.Parse(["remove", good.Id.ToString(), bad.Id.ToString(), "--trash", "--yes", "--json", "--state-dir", state]);
            var code = await CliRunner.RunAsync(options, new Localization(), new StringReader(""), output, errors);
            Assert.Equal(3, code); Assert.Equal("", errors.ToString());
            using var json = JsonDocument.Parse(output.ToString());
            var result = json.RootElement.GetProperty("result");
            Assert.Equal(good.Id, result.GetProperty("processed")[0].GetGuid());
            Assert.Equal("TrashFailed", result.GetProperty("failures")[0].GetProperty("error").GetProperty("code").GetString());
            await ui.SendAsync(new() { Command = Command.Snapshot });
            Assert.Equal(bad.Id, ui.State.Jobs.Single().Id); Assert.Equal(2, service.Calls);
        }
        finally { cancel.Cancel(); await server; }
    }
    [Fact]
    public void NativeMacTrashUsesTemporaryFixtureAndRestoresIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var job = Completed("Корзина пробел ; '$.txt"); var original = File.ReadAllBytes(job.Target!);
        var moved = new MacTrashService().Move(job.Target!);
        Assert.False(File.Exists(job.Target)); Assert.False(string.IsNullOrEmpty(moved));
        try { Assert.Equal(original, File.ReadAllBytes(moved)); }
        finally { if (File.Exists(moved)) File.Move(moved, job.Target!); }
    }
}
