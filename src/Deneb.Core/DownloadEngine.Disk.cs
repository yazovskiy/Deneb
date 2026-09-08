namespace Deneb.Core;

public sealed partial class DownloadEngine
{
    private void AdmitDisk(DownloadJob job, bool assembly)
    {
        lock (gate)
        {
            running[job.Id].Cancel.Token.ThrowIfCancellationRequested();
            disks.Add(job.Id, job.Destination, job.Total, job.Segments.Sum(s => s.Committed), assembly);
            RefreshDiskSpace(true);
            running[job.Id].Cancel.Token.ThrowIfCancellationRequested();
            job.DiskSpace = disks.Snapshot(job.Id);
        }
    }
    private void RefreshDiskSpace(bool force = false)
    {
        foreach (var (id, reason) in disks.Refresh(library.Jobs.Select(j => j.Id).ToArray(), force)) StopForDisk(id, reason);
    }
    private void StopForDisk(Guid id, ProblemCode reason)
    {
        var job = Find(id);
        if (job.State != DownloadState.Downloading) return;
        job.DiskSpace = disks.Snapshot(id) ?? job.DiskSpace;
        job.State = DownloadState.Paused;
        job.Diagnostic = new(reason);
        disks.Stop(id);
        Cancel(id);
        Save();
    }
    private async Task WriteWithDiskBudgetAsync(DownloadJob job, FileStream output, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            RefreshDiskSpace();
            token.ThrowIfCancellationRequested();
            disks.ReserveWrite(job.Id, bytes.Length);
        }
        var position = output.Position;
        try { await output.WriteAsync(bytes, token); }
        finally
        {
            lock (gate) disks.CompleteWrite(job.Id, bytes.Length, (int)Math.Clamp(output.Position - position, 0, bytes.Length));
        }
    }
}
