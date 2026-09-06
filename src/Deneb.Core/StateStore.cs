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
        catch (IOException) { throw new IOException("Это хранилище Deneb уже открыто другим процессом."); }
    }
    public Library Load()
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new();
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(candidate)) ?? throw new JsonException();
                if (library.Version is not (1 or 2)) throw new InvalidDataException("Неподдерживаемая версия хранилища Deneb.");
                library.Settings.Validate();
                if (library.Version == 1)
                {
                    if (!File.Exists(path + ".v1.bak")) File.Copy(candidate, path + ".v1.bak", false);
                    library.Version = 2;
                    library.GloballyPaused = false;
                }
                recoveredBackup = candidate.EndsWith(".bak", StringComparison.Ordinal);
                return library;
            }
            catch (Exception ex) when (ex is JsonException or FileNotFoundException) { }
        }
        throw new InvalidDataException("Основное и резервное хранилище повреждены. Файлы загрузок сохранены.");
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
