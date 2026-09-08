namespace Deneb.Core;

/// <summary>Injectable write/flush boundary; confirmed bytes require a durable flush.</summary>
public interface IDownloadFiles
{
    FileStream OpenWrite(string path, FileMode mode);
    void Flush(FileStream file);
}
public sealed class DownloadFiles : IDownloadFiles
{
    public FileStream OpenWrite(string path, FileMode mode) => new(path, mode, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
    public void Flush(FileStream file) => file.Flush(true);
}
