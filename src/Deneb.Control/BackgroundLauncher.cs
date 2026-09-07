using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Deneb.Core;

namespace Deneb.Control;

public static partial class BackgroundLauncher
{
    [LibraryImport("libc", SetLastError = true)] private static partial int setsid();
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)] private static partial int open(string path, int flags);
    [LibraryImport("libc", SetLastError = true)] private static partial int dup2(int oldfd, int newfd);
    [LibraryImport("libc", SetLastError = true)] private static partial int close(int fd);
    public static void Detach()
    {
        if (OperatingSystem.IsWindows()) return;
        if (setsid() < 0) throw new ProblemException(ProblemCode.SystemLaunch);
        var descriptor = open("/dev/null", 2);
        if (descriptor < 0) throw new ProblemException(ProblemCode.SystemLaunch);
        try { for (var fd = 0; fd < 3; fd++) if (dup2(descriptor, fd) < 0) throw new ProblemException(ProblemCode.SystemLaunch); }
        finally { if (descriptor > 2) close(descriptor); }
    }
    public static bool IsAbsent(Exception ex) => ex is IOException or SocketException or OperationCanceledException;
    public static async Task<ControlClient> ConnectOrStartAsync(LocalEndpoint endpoint, bool interactive, bool start)
    {
        try { return await ControlClient.ConnectAsync(endpoint, interactive); }
        catch (Exception ex) when (IsAbsent(ex)) { if (!start) throw new ProblemException(ProblemCode.BackgroundMissing); }
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        info.ArgumentList.Add("--background"); info.ArgumentList.Add("--state-dir"); info.ArgumentList.Add(endpoint.StoreDirectory);
        using var process = Process.Start(info) ?? throw new ProblemException(ProblemCode.SystemLaunch);
        process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        var end = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(end) < TimeSpan.FromSeconds(10))
        {
            try { return await ControlClient.ConnectAsync(endpoint, interactive); }
            catch (Exception ex) when (IsAbsent(ex)) { }
            if (process.HasExited && process.ExitCode != 20 + (int)ProblemCode.StoreLocked)
            {
                var code = process.ExitCode - 20;
                throw new ProblemException(Enum.IsDefined(typeof(ProblemCode), code) ? (ProblemCode)code : ProblemCode.SystemLaunch);
            }
            await Task.Delay(100);
        }
        throw new ProblemException(ProblemCode.BackgroundTimeout);
    }
    public static async Task WaitStoppedAsync(LocalEndpoint endpoint)
    {
        var end = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(end) < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var lease = new FileStream(Path.Combine(endpoint.StoreDirectory, "instance.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (IOException) { await Task.Delay(100); }
        }
        throw new ProblemException(ProblemCode.BackgroundTimeout);
    }
}
