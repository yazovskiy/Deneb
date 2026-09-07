using Deneb.Core;

namespace Deneb.Control;

public sealed class ControlClient : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly SemaphoreSlim serial = new(1);
    public EngineState State { get; private set; } = null!;
    public bool Connected { get; private set; } = true;
    private ControlClient(Stream stream) => this.stream = stream;
    public static async Task<ControlClient> ConnectAsync(LocalEndpoint endpoint, bool interactive = false, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var client = new ControlClient(await endpoint.ConnectAsync(timeout.Token));
        try { await client.SendAsync(new() { Command = Command.Hello, Interactive = interactive }, cancellation); return client; }
        catch { await client.DisposeAsync(); throw; }
    }
    public async Task<Response> SendAsync(Request request, CancellationToken cancellation = default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if (!Connected) throw new ProblemException(ProblemCode.DisconnectedControl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Response response;
            try
            {
                await Framing.WriteAsync(stream, request, timeout.Token);
                response = await Framing.ReadAsync<Response>(stream, timeout.Token);
                if (response.Id != request.Id) throw new ProblemException(ProblemCode.Protocol);
            }
            catch
            {
                Connected = false; stream.Dispose();
                throw new ProblemException(ProblemCode.DisconnectedControl);
            }
            if (response.Error != null) throw new ProblemException(response.Error);
            if (response.State != null) State = response.State;
            return response;
        }
        finally { serial.Release(); }
    }
    public ValueTask DisposeAsync() { Connected = false; stream.Dispose(); return ValueTask.CompletedTask; }
}
