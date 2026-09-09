namespace Deneb.Core;

public sealed partial class DownloadEngine
{
    public bool GloballyPaused { get { lock (gate) return library.GloballyPaused; } }
    public void Move(Guid id, int delta)
    {
        lock (gate) { var job = Find(id); var index = library.Jobs.IndexOf(job); library.Jobs.RemoveAt(index); library.Jobs.Insert(Math.Clamp(index + delta, 0, library.Jobs.Count), job); Save(); }
    }
    public void DownloadNext(Guid id)
    {
        lock (gate) { var job = Find(id); library.Jobs.Remove(job); library.Jobs.Insert(0, job); Save(); }
    }
    public async Task PauseAllAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            library.GloballyPaused = true;
            foreach (var job in library.Jobs.Where(j => j.State == DownloadState.Downloading)) job.State = DownloadState.Queued;
            foreach (var r in running.Values) r.Cancel.Cancel();
            tasks = running.Values.Select(r => r.Task).ToArray(); Save();
        }
        await Task.WhenAll(tasks);
    }
    public void ResumeAll() { lock (gate) { library.GloballyPaused = false; Save(); } }
    public async Task<BatchResult> PauseManyAsync(IEnumerable<Guid> ids)
    {
        List<Guid> processed = [], skipped = [];
        List<Task> tasks = [];
        lock (gate)
        {
            foreach (var id in ids.Distinct())
            {
                var j = library.Jobs.FirstOrDefault(j => j.Id == id);
                if (j == null || j.State is not (DownloadState.Downloading or DownloadState.Queued)) { skipped.Add(id); continue; }
                j.State = DownloadState.Paused; processed.Add(id);
            }
            foreach (var id in processed) if (Cancel(id) is { } task) tasks.Add(task);
            Save();
        }
        await Task.WhenAll(tasks);
        lock (gate) foreach (var id in processed) if (running.TryGetValue(id, out var r) && r.Task.IsCompleted) { running.Remove(id); r.Cancel.Dispose(); }
        return new(processed, skipped, []);
    }
    public BatchResult ResumeMany(IEnumerable<Guid> ids)
    {
        List<Guid> processed = [], skipped = [];
        lock (gate)
        {
            foreach (var id in ids.Distinct())
            {
                var j = library.Jobs.FirstOrDefault(j => j.Id == id);
                if (j == null || j.State is not (DownloadState.Paused or DownloadState.Failed) || running.ContainsKey(id)) { skipped.Add(id); continue; }
                j.State = DownloadState.Queued; j.Diagnostic = null; processed.Add(id);
            }
            Save();
        }
        return new(processed, skipped, []);
    }
    public async Task<BatchResult> RemoveManyAsync(IEnumerable<Guid> ids, bool deletePartial = false)
    {
        var targets = ids.Distinct().ToArray();
        await PauseManyAsync(targets);
        List<Guid> processed = [], skipped = [], failed = [];
        foreach (var id in targets)
        {
            lock (gate) if (!library.Jobs.Any(j => j.Id == id)) { skipped.Add(id); continue; }
            lock (gate) if (deletePartial && Find(id).State == DownloadState.Completed) { skipped.Add(id); continue; }
            try { await RemoveAsync(id, deletePartial); processed.Add(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(id); }
        }
        return new(processed, skipped, failed);
    }
    // Called only after an external, confirmed file operation. Failure retains the row;
    // restarting then shows a completed entry whose file may now be absent.
    public void ForgetCompleted(Guid id, string expectedTarget)
    {
        lock (gate)
        {
            var index = library.Jobs.FindIndex(j => j.Id == id);
            if (index < 0 || library.Jobs[index].State != DownloadState.Completed || library.Jobs[index].Target != expectedTarget)
                throw new ProblemException(ProblemCode.TaskChanged);
            var job = library.Jobs[index]; library.Jobs.RemoveAt(index);
            try { store.Save(library); PersistenceError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                library.Jobs.Insert(index, job); PersistenceError = new(ProblemCode.Persistence);
                throw new ProblemException(PersistenceError);
            }
        }
    }
    public BatchResult ClearCompleted()
    {
        lock (gate) { var ids = library.Jobs.Where(j => j.State == DownloadState.Completed).Select(j => j.Id).ToArray(); library.Jobs.RemoveAll(j => ids.Contains(j.Id)); Save(); return new(ids, [], []); }
    }
}
