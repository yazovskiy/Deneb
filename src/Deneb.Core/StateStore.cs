using System.Text.Json;

namespace Deneb.Core;

public sealed class StateStore : IDisposable
{
    private readonly string path;
    private readonly FileStream lease;
    private bool recoveredBackup;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Deneb")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Deneb");
    public StateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        path = Path.Combine(directory, "state.json");
        try { lease = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ProblemException(ProblemCode.StoreLocked); }
    }
    public Library Load()
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new();
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(candidate)) ?? throw new JsonException();
                if (library.Version is not (1 or 2 or 3)) throw new ProblemException(ProblemCode.UnsupportedStore);
                library.Settings.Validate();
                if (library.Version < 3)
                {
                    var backup = path + $".v{library.Version}.bak";
                    if (!File.Exists(backup)) File.Copy(candidate, backup, false);
                    if (library.Version == 1) library.GloballyPaused = false;
                    library.Settings.Language = "en";
                    foreach (var job in library.Jobs)
                    {
                        job.Diagnostic = LegacyProblems.Convert(job.Error);
                        job.Error = null;
                    }
                    library.Version = 3;
                }
                recoveredBackup = candidate.EndsWith(".bak", StringComparison.Ordinal);
                return library;
            }
            catch (Exception ex) when (ex is JsonException or FileNotFoundException) { }
        }
        throw new ProblemException(ProblemCode.CorruptStore);
    }
    // CLI help must not acquire a lease, create directories, migrate or save anything.
    public static string ReadLanguage(string? directory = null)
    {
        var state = Path.Combine(directory ?? DefaultDirectory, "state.json");
        foreach (var candidate in new[] { state, state + ".bak" })
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                var root = document.RootElement;
                if (!root.TryGetProperty("Version", out var version) || version.GetInt32() != 3) return "en";
                if (root.TryGetProperty("Settings", out var settings) && settings.TryGetProperty("Language", out var language))
                    return Settings.NormalizeLanguage(language.GetString());
                return "en";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        }
        return "en";
    }
    public void Save(Library library)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(library, Json);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (File.Exists(path) && !recoveredBackup) File.Copy(path, path + ".bak", true);
        File.Move(path + ".tmp", path, true);
        recoveredBackup = false;
    }
    public void Dispose() => lease.Dispose();
}
