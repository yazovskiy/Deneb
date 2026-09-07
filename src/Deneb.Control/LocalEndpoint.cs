using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Deneb.Core;

namespace Deneb.Control;

public sealed class LocalEndpoint
{
    public string StoreDirectory { get; }
    public string Name { get; }
    public string SocketPath { get; }
    public LocalEndpoint(string? directory)
    {
        StoreDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory ?? StateStore.DefaultDirectory));
        var identity = OperatingSystem.IsWindows() ? StoreDirectory.ToUpperInvariant() : StoreDirectory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "|" + identity)))[..24];
        Name = "deneb-" + hash;
        SocketPath = Path.Combine(Path.GetTempPath(), Name, "control.sock");
    }
    public void PrepareUnixDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.GetDirectoryName(SocketPath)!;
        if (new DirectoryInfo(directory).LinkTarget != null) throw new ProblemException(ProblemCode.Protocol);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new ProblemException(ProblemCode.Protocol);
    }
    public async Task<Stream> ConnectAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(token); return pipe; } catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }
}
