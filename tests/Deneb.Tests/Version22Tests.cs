using System.Text.Json;
using Deneb.App;
using Deneb.Control;
using Deneb.Core;
using Xunit;

namespace Deneb.Tests;

public sealed class Version22Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-v22-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static Snapshot Job(string name, DownloadState state = DownloadState.Queued, Problem? error = null, Guid? id = null) => new(id ?? Guid.NewGuid(), name, state, 0, null, 0, 0, "example.org", error, null);

    [Theory]
    [InlineData("add")]
    [InlineData("show")]
    [InlineData("remove")]
    [InlineData("list --filter bad")]
    [InlineData("status extra")]
    [InlineData("add --stdin https://example.org")]
    [InlineData("remove 12345678 --delete-partial")]
    [InlineData("remove 12345678 --yes")]
    [InlineData("list --destination test")]
    [InlineData("pause 123")]
    [InlineData("--json")]
    [InlineData("--state-dir --json")]
    [InlineData("list --json --json")]
    [InlineData("--background")]
    public void InvalidSyntaxIsRejected(string command) => Assert.Throws<CliInputException>(() => CliOptions.Parse(command.Split(' ')));

    [Fact]
    public async Task InputsPreserveNamesAndValidateBeforeUse()
    {
        var options = CliOptions.Parse(["add", "--stdin", "--destination", "кириллица space", "--json"]);
        var input = "Row: 0 _id=1, title=Файл.mp4, uri=https://example.org/file?token=secret, status=192, bytes_so_far=999\nhttps://example.org/second";
        var parsed = await options.ReadInputsAsync(new StringReader(input));
        Assert.Equal(2, parsed.Count); Assert.Equal("Файл.mp4", parsed[0].Name);
        Assert.Equal("https://example.org/file?token=secret", parsed[0].Url);
        Assert.True(Path.IsPathFullyQualified(options.Destination!));
        await Assert.ThrowsAsync<CliInputException>(() => (options with { Name = "one" }).ReadInputsAsync(new StringReader(input)));
        await Assert.ThrowsAsync<ProblemException>(() => CliOptions.Parse(["add", "https://example.org", "bad"]).ReadInputsAsync(TextReader.Null));
    }
    [Fact]
    public void IdsAreResolvedBeforeMutationAndDeduplicated()
    {
        var one = Job("a", id: Guid.Parse("12345678-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var two = Job("b", id: Guid.Parse("12345678-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        Assert.Throws<CliInputException>(() => CliOptions.ResolveIds(["12345678"], [one, two]));
        Assert.Throws<CliInputException>(() => CliOptions.ResolveIds([one.Id.ToString(), "ffffffff"], [one, two]));
        Assert.Equal(new[] { one.Id }, CliOptions.ResolveIds([one.Id.ToString(), "12345678aaaa"], [one, two]));
    }
    [Fact]
    public void FiltersAndHiddenMarksDoNotChangeUnderlyingQueue()
    {
        var all = new[] { Job("ФИЛЬМ", DownloadState.Downloading), Job("film", DownloadState.Completed), Job("disk", DownloadState.Paused, new(ProblemCode.InsufficientDiskSpace)), Job("net", DownloadState.Failed), Job("manual", DownloadState.Paused), Job("queue") };
        Assert.Single(QueueView.Apply(all, "фильм", "active"));
        Assert.Single(QueueView.Apply(all, "FILM", "completed"));
        Assert.Equal(2, QueueView.Apply(all, "", "attention").Count);
        Assert.Equal(2, QueueView.Apply(all, "", "paused").Count);
        Assert.Single(QueueView.Apply(all, "", "queued"));
        var marks = new HashSet<Guid> { all[0].Id, all[1].Id };
        Assert.Equal(new[] { all[1].Id }, QueueView.Targets([all[1]], marks, all[1].Id));
        Assert.Equal(new[] { all[2].Id }, QueueView.Targets([all[2]], marks, all[2].Id));
        Assert.Empty(QueueView.Targets([], marks, all[0].Id)); Assert.Equal(2, marks.Count);
        Assert.Equal("ФИЛЬМ", all[0].Name);
    }
    [Fact]
    public void TerminalTextCannotInjectControlsAndJsonHasNoLegacySecrets()
    {
        Assert.Equal("name [31m path ", QueueView.SafeText("name\u001b[31m\npath\u202e"));
        var job = Job("Файл", error: new(ProblemCode.Legacy, LegacyText: "https://secret/token"));
        var json = JsonSerializer.Serialize(CliJob.From(job), CliRunner.JsonOptions);
        Assert.DoesNotContain("secret", json); Assert.Contains("Queued", json);
        Assert.Equal("Файл", Job("Файл").Name);
    }
    private async Task<(int Code, JsonDocument Json, string Error)> Run(params string[] args)
    {
        var output = new StringWriter(); var errors = new StringWriter();
        var options = CliOptions.Parse([..args, "--state-dir", root, "--json"]);
        var code = await CliRunner.RunAsync(options, new Localization("ru"), TextReader.Null, output, errors);
        return (code, JsonDocument.Parse(output.ToString()), errors.ToString());
    }
    [Fact]
    public async Task MissingBackgroundListIsReadOnlyAndInvalidAddDoesNotStartIt()
    {
        var result = await Run("list"); using var json = result.Json;
        Assert.Equal(0, result.Code); Assert.False(json.RootElement.GetProperty("result").GetProperty("backgroundRunning").GetBoolean());
        var invalid = await Run("add", "not-a-url"); invalid.Json.Dispose(); Assert.Equal(2, invalid.Code);
        Assert.False(File.Exists(Path.Combine(root, "state.json")));
    }
    [Fact]
    public async Task CliUsesExistingDaemonAlongsideUiAndPreservesPauseAndFiles()
    {
        using var cancel = new CancellationTokenSource(); var endpoint = new LocalEndpoint(root);
        var server = new ControlServer(endpoint).RunAsync(cancel.Token);
        try
        {
            await using var ui = await ControlClient.ConnectAsync(endpoint, true);
            await ui.SendAsync(new() { Command = Command.PauseAll });
            var added = await Run("add", "https://example.org/private?token=secret", "--name", "Файл", "--destination", root);
            using var addedJson = added.Json;
            Assert.Equal(0, added.Code); Assert.Equal("", added.Error);
            var id = addedJson.RootElement.GetProperty("result").GetProperty("added")[0].GetGuid();
            var shown = await Run("show", id.ToString("N")[..8]); using var shownJson = shown.Json;
            Assert.Equal(0, shown.Code); Assert.DoesNotContain("secret", shownJson.RootElement.GetRawText());
            Assert.Equal("Файл", shownJson.RootElement.GetProperty("result").GetProperty("jobs")[0].GetProperty("name").GetString());
            Assert.True(shownJson.RootElement.GetProperty("result").GetProperty("globallyPaused").GetBoolean());
            var paused = await Run("pause", id.ToString()); paused.Json.Dispose(); Assert.Equal(0, paused.Code);
            var global = await Run("resume"); global.Json.Dispose();
            await ui.SendAsync(new() { Command = Command.Snapshot }); Assert.Equal(DownloadState.Paused, ui.State.Jobs.Single().State);
            var parts = Path.Combine(root, $".deneb-{id:N}"); Directory.CreateDirectory(parts); var part = Path.Combine(parts, "preserve.part"); File.WriteAllText(part, "saved");
            var removed = await Run("remove", id.ToString()); removed.Json.Dispose(); Assert.Equal(0, removed.Code); Assert.True(File.Exists(part));
        }
        finally { cancel.Cancel(); await server; }
    }
    [Fact]
    public async Task AddReturnsOwnIdUnderConcurrentClients()
    {
        using var cancel = new CancellationTokenSource(); var endpoint = new LocalEndpoint(root);
        var server = new ControlServer(endpoint).RunAsync(cancel.Token);
        try
        {
            await using var a = await ControlClient.ConnectAsync(endpoint); await using var b = await ControlClient.ConnectAsync(endpoint);
            await a.SendAsync(new() { Command = Command.PauseAll });
            var results = await Task.WhenAll(a.SendAsync(new() { Command = Command.Add, Input = new("https://example.org/a"), Name = "a", Destination = root }), b.SendAsync(new() { Command = Command.Add, Input = new("https://example.org/b"), Name = "b", Destination = root }));
            Assert.NotNull(results[0].AddedId); Assert.NotEqual(results[0].AddedId, results[1].AddedId);
            Assert.Equal("a", results[0].State!.Jobs.Single(j => j.Id == results[0].AddedId).Name);
            Assert.Equal("b", results[1].State!.Jobs.Single(j => j.Id == results[1].AddedId).Name);
        }
        finally { cancel.Cancel(); await server; }
    }

    private async Task FakePeer(Func<Stream, CancellationToken, Task> script, CancellationToken token)
    {
        var endpoint = new LocalEndpoint(root);
        if (OperatingSystem.IsWindows())
        {
            await using var pipe = new System.IO.Pipes.NamedPipeServerStream(endpoint.Name, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token); await script(pipe, token);
        }
        else
        {
            endpoint.PrepareUnixDirectory();
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(endpoint.SocketPath)); socket.Listen(1);
            try { await using var stream = new System.Net.Sockets.NetworkStream(await socket.AcceptAsync(token), true); await script(stream, token); }
            finally { File.Delete(endpoint.SocketPath); }
        }
    }
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task AddStopsAfterErrorOrLostResponseWithoutRepeating(bool disconnect, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var id = Guid.NewGuid(); var requests = 0;
        var peer = FakePeer(async (stream, token) =>
        {
            var hello = await Framing.ReadAsync<Request>(stream, token);
            await Framing.WriteAsync(stream, new Response(hello.Id, State: new(new(), true, [], null)), token);
            var first = await Framing.ReadAsync<Request>(stream, token); requests++;
            await Framing.WriteAsync(stream, new Response(first.Id, AddedId: id), token);
            var second = await Framing.ReadAsync<Request>(stream, token); requests++;
            if (!disconnect) await Framing.WriteAsync(stream, new Response(second.Id, Error: new(ProblemCode.Persistence)), token);
        }, timeout.Token);
        var result = await Run("add", "https://example.org/1", "https://example.org/2", "https://example.org/3");
        using var json = result.Json; await peer;
        Assert.Equal(expected, result.Code); Assert.Equal(2, requests);
        var body = json.RootElement.GetProperty("result");
        Assert.Equal(id, body.GetProperty("added")[0].GetGuid());
        Assert.Equal(2, body.GetProperty("failedInput").GetInt32());
        Assert.Equal(3, body.GetProperty("notSentInputs")[0].GetInt32());
        Assert.Equal(disconnect, body.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Empty(result.Error);
    }
    [Fact]
    public async Task ConcurrentRemovalReturnsFailedIdsAndDoesNotRetarget()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var one = Job("a"); var two = Job("b");
        var peer = FakePeer(async (stream, token) =>
        {
            var hello = await Framing.ReadAsync<Request>(stream, token);
            await Framing.WriteAsync(stream, new Response(hello.Id, State: new(new(), true, [one, two], null)), token);
            var request = await Framing.ReadAsync<Request>(stream, token);
            Assert.Equal(new[] { one.Id, two.Id }, request.Ids);
            await Framing.WriteAsync(stream, new Response(request.Id, Batch: new([one.Id], [], [two.Id])), token);
        }, timeout.Token);
        var result = await Run("pause", one.Id.ToString(), two.Id.ToString()); using var json = result.Json;
        await peer; Assert.Equal(3, result.Code);
        Assert.Equal(two.Id, json.RootElement.GetProperty("result").GetProperty("failed")[0].GetGuid());
    }
    [Fact]
    public async Task FailedAddDoesNotLeaveAnUnreportedTask()
    {
        await using var engine = new DownloadEngine(root); await engine.PauseAllAsync();
        Directory.CreateDirectory(Path.Combine(root, "state.json.tmp"));
        Assert.Throws<ProblemException>(() => engine.Add(new("https://example.org/a"), root));
        Assert.Empty(engine.Snapshots());
        Directory.Delete(Path.Combine(root, "state.json.tmp"));
    }
}
