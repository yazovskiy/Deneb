using System.Diagnostics;

namespace Deneb.App;

public sealed class MacFileActions(Func<string, IReadOnlyList<string>, string?, Task<int>>? runner = null)
{
    public async Task ExecuteAsync(string path, string action)
    {
        if (action != "copy" && !File.Exists(path)) throw new IOException("Файл перемещён или удалён.");
        var executable = action == "copy" ? "/usr/bin/pbcopy" : "/usr/bin/open";
        string[] arguments = action == "copy" ? [] : action == "reveal" ? ["-R", path] : [path];
        try
        {
            if (await (runner ?? RunAsync)(executable, arguments, action == "copy" ? path : null) != 0)
                throw new IOException("Системная команда не выполнена.");
        }
        catch (System.ComponentModel.Win32Exception) { throw new IOException("Не удалось запустить системную команду macOS."); }
    }
    private static async Task<int> RunAsync(string command, IReadOnlyList<string> arguments, string? input)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardInput = input != null, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить команду.");
        var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        if (input != null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
        await process.WaitForExitAsync(); await Task.WhenAll(error, output); return process.ExitCode;
    }
}
