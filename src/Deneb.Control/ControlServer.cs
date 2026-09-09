using System.IO.Pipes;
using System.Net.Sockets;
using Deneb.Core;

namespace Deneb.Control;

public sealed class ControlServer(LocalEndpoint endpoint, ITrashService? trashService = null)
{
    private readonly SemaphoreSlim mutations = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private int interactive;
    public async Task RunAsync(CancellationToken cancellation = default)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        var token = combined.Token;
        await using var engine = new DownloadEngine(endpoint.StoreDirectory);
        var clients = new List<Task>();
        Socket? listener = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                endpoint.PrepareUnixDirectory();
                // Only the holder of the engine/store lease may remove a stale endpoint.
                if (File.Exists(endpoint.SocketPath)) File.Delete(endpoint.SocketPath);
                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(endpoint.SocketPath));
                File.SetUnixFileMode(endpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(16);
            }
            while (!token.IsCancellationRequested)
            {
                clients.RemoveAll(t => t.IsCompleted);
                if (clients.Count >= 16) { await Task.WhenAny(clients).WaitAsync(token); continue; }
                Stream stream;
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeServerStream(endpoint.Name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(token); stream = pipe; } catch { pipe.Dispose(); throw; }
                }
                else stream = new NetworkStream(await listener!.AcceptAsync(token), true);
                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeAsync(stream, engine, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            shutdown.Cancel(); listener?.Dispose();
            await Task.WhenAll(clients);
            if (!OperatingSystem.IsWindows() && File.Exists(endpoint.SocketPath)) File.Delete(endpoint.SocketPath);
        }
    }
    private EngineState State(DownloadEngine engine) => new(engine.GetSettings(), engine.GloballyPaused, engine.Snapshots().ToArray(), engine.PersistenceError);
    private async Task ServeAsync(Stream stream, DownloadEngine engine, CancellationToken token)
    {
        using (stream)
        {
            var ownsUi = false; var greeted = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var request = await Framing.ReadAsync<Request>(stream, timeout.Token);
                    if (request.Protocol != 4 || request.Version != "2.2.0")
                    { await Framing.WriteAsync(stream, new Response(request.Id, new(ProblemCode.Incompatible)), timeout.Token); return; }
                    if (!greeted)
                    {
                        if (request.Command != Command.Hello) throw new ProblemException(ProblemCode.Protocol);
                        if (request.Interactive)
                        {
                            if (Interlocked.CompareExchange(ref interactive, 1, 0) != 0)
                            { await Framing.WriteAsync(stream, new Response(request.Id, new(ProblemCode.InterfaceOpen)), timeout.Token); return; }
                            ownsUi = true;
                        }
                        greeted = true;
                        await Framing.WriteAsync(stream, new Response(request.Id, State: State(engine)), timeout.Token);
                        continue;
                    }
                    if (request.Command == Command.Snapshot)
                    { await Framing.WriteAsync(stream, new Response(request.Id, State: State(engine)), timeout.Token); continue; }
                    Response response;
                    await mutations.WaitAsync(token);
                    try
                    {
                        response = await ExecuteAsync(request, engine);
                    }
                    catch (Exception ex) { response = new(request.Id, Problem.FromException(ex)); }
                    finally { mutations.Release(); }
                    if (request.Command == Command.Stop && response.Error == null)
                    {
                        try { await Framing.WriteAsync(stream, response, timeout.Token); }
                        finally { shutdown.Cancel(); }
                        return;
                    }
                    await Framing.WriteAsync(stream, response, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ProblemException) { }
            finally { if (ownsUi) Interlocked.Exchange(ref interactive, 0); }
        }
    }
    private async Task<Response> ExecuteAsync(Request r, DownloadEngine e)
    {
        BatchResult? batch = null; string? url = null; Guid? addedId = null;
        switch (r.Command)
        {
            case Command.Add: addedId = e.Add(r.Input ?? throw new ProblemException(ProblemCode.Protocol), r.Destination, r.Name); break;
            case Command.Settings: e.SetSettings(r.Settings ?? throw new ProblemException(ProblemCode.Protocol)); break;
            case Command.Url: url = e.GetUrl(r.Job); break;
            case Command.Replace: await e.ReplaceUrlAsync(r.Job, r.Url ?? ""); break;
            case Command.Restart: await e.RestartAsync(r.Job); break;
            case Command.Pause: batch = await e.PauseManyAsync(r.Ids); break;
            case Command.Resume: batch = e.ResumeMany(r.Ids); break;
            case Command.Remove: batch = await e.RemoveManyAsync(r.Ids, r.DeletePartial); break;
            case Command.Trash: batch = TrashOperations.Execute(e, r.Ids, trashService ?? new MacTrashService()); break;
            case Command.Move: if (r.Direction is not (-1 or 1)) throw new ProblemException(ProblemCode.Protocol); e.Move(r.Job, r.Direction); break;
            case Command.Next: e.DownloadNext(r.Job); break;
            case Command.PauseAll: await e.PauseAllAsync(); break;
            case Command.ResumeAll: e.ResumeAll(); break;
            case Command.Clear: batch = e.ClearCompleted(); break;
            case Command.Stop: break;
            default: throw new ProblemException(ProblemCode.Protocol);
        }
        return new(r.Id, State: State(e), Batch: batch, Url: url, AddedId: addedId);
    }
}
