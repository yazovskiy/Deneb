using Deneb.Control;
using Deneb.Core;

namespace Deneb.App;

public sealed class RemoteEngine(ControlClient client, LocalEndpoint endpoint) : IAsyncDisposable
{
    private ControlClient client = client;
    public bool Connected => client.Connected;
    public bool GloballyPaused => client.State.GloballyPaused;
    public Problem? PersistenceError => client.State.PersistenceError;
    public Settings GetSettings() => client.State.Settings;
    public IReadOnlyList<Snapshot> Snapshots() => client.State.Jobs;
    public Task<Response> PollAsync() => client.SendAsync(new() { Command = Command.Snapshot });
    public Task<Response> SetSettingsAsync(Settings settings) => client.SendAsync(new() { Command = Command.Settings, Settings = settings });
    public Task<Response> AddAsync(DownloadInput input, string destination, string? name) => client.SendAsync(new() { Command = Command.Add, Input = input, Destination = destination, Name = name });
    public Task<Response> MoveAsync(Guid id, int direction) => client.SendAsync(new() { Command = Command.Move, Job = id, Direction = direction });
    public Task<Response> DownloadNextAsync(Guid id) => client.SendAsync(new() { Command = Command.Next, Job = id });
    public Task<Response> PauseAllAsync() => client.SendAsync(new() { Command = Command.PauseAll });
    public Task<Response> ResumeAllAsync() => client.SendAsync(new() { Command = Command.ResumeAll });
    public async Task<BatchResult> PauseManyAsync(Guid[] ids) => (await client.SendAsync(new() { Command = Command.Pause, Ids = ids })).Batch!;
    public async Task<BatchResult> ResumeManyAsync(Guid[] ids) => (await client.SendAsync(new() { Command = Command.Resume, Ids = ids })).Batch!;
    public async Task<BatchResult> RemoveManyAsync(Guid[] ids, bool deletePartial = false) => (await client.SendAsync(new() { Command = Command.Remove, Ids = ids, DeletePartial = deletePartial })).Batch!;
    public async Task<BatchResult> TrashManyAsync(Guid[] ids) => (await client.SendAsync(new() { Command = Command.Trash, Ids = ids })).Batch!;
    public async Task<BatchResult> ClearCompletedAsync() => (await client.SendAsync(new() { Command = Command.Clear })).Batch!;
    public async Task<string> GetUrlAsync(Guid id) => (await client.SendAsync(new() { Command = Command.Url, Job = id })).Url!;
    public Task<Response> RestartAsync(Guid id) => client.SendAsync(new() { Command = Command.Restart, Job = id });
    public Task<Response> ReplaceUrlAsync(Guid id, string url) => client.SendAsync(new() { Command = Command.Replace, Job = id, Url = url });
    public async Task StopAsync() { await client.SendAsync(new() { Command = Command.Stop }); await BackgroundLauncher.WaitStoppedAsync(endpoint); }
    public async Task ReconnectAsync(bool start)
    {
        await client.DisposeAsync();
        client = await BackgroundLauncher.ConnectOrStartAsync(endpoint, true, start);
    }
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
