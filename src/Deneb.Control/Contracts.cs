using System.Buffers.Binary;
using System.Text.Json;
using Deneb.Core;

namespace Deneb.Control;

public enum Command { Hello, Snapshot, Add, Settings, Url, Replace, Restart, Pause, Resume, Remove, Move, Next, PauseAll, ResumeAll, Clear, Stop }
public sealed record Request
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Protocol { get; init; } = 1;
    public string Version { get; init; } = "2.0.0";
    public Command Command { get; init; }
    public bool Interactive { get; init; }
    public Guid Job { get; init; }
    public Guid[] Ids { get; init; } = [];
    public DownloadInput? Input { get; init; }
    public string? Destination { get; init; }
    public string? Name { get; init; }
    public string? Url { get; init; }
    public Settings? Settings { get; init; }
    public bool DeletePartial { get; init; }
    public int Direction { get; init; }
}
public sealed record EngineState(Settings Settings, bool GloballyPaused, Snapshot[] Jobs, Problem? PersistenceError)
{
    public int ProcessId { get; init; } = Environment.ProcessId;
}
public sealed record Response(Guid Id, Problem? Error = null, EngineState? State = null, BatchResult? Batch = null, string? Url = null);

public static class Framing
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxBytes) throw new ProblemException(ProblemCode.Protocol);
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxBytes) throw new ProblemException(ProblemCode.Protocol);
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        try { return JsonSerializer.Deserialize<T>(bytes) ?? throw new ProblemException(ProblemCode.Protocol); }
        catch (JsonException) { throw new ProblemException(ProblemCode.Protocol); }
    }
}
