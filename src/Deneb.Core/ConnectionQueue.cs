namespace Deneb.Core;

public sealed partial class DownloadEngine
{
    private sealed record Worker(CancellationTokenSource Cancel, Task Task);
    private int EffectiveLimit(DownloadJob job) => CanSplit(job) ? library.Settings.Connections : 1;
    private static bool CanSplit(DownloadJob job) => job.Ranges && job.Total >= 16 * 1024 * 1024 && (job.ETag != null || job.LastModified != null);

    private async Task AcquireRequestAsync(DownloadJob job, Running run, Progress progress, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (run.Connections < EffectiveLimit(job))
                { run.Connections++; progress.RequestActive = true; progress.Phase = DownloadPhase.Transferring; return; }
                progress.Phase = DownloadPhase.Waiting;
            }
            await Task.Delay(25, token);
        }
    }

    private async Task TransferQueueAsync(DownloadJob job, Running run, CancellationToken token)
    {
        var workers = new Dictionary<int, Worker>();
        async Task StopWorker(int index)
        {
            if (!workers.Remove(index, out var worker)) return;
            worker.Cancel.Cancel();
            try { await worker.Task; }
            catch (OperationCanceledException) when (worker.Cancel.IsCancellationRequested) { }
            finally { worker.Cancel.Dispose(); }
        }
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var pair in workers.Where(p => p.Value.Task.IsCompleted).ToArray())
                { await pair.Value.Task; pair.Value.Cancel.Dispose(); workers.Remove(pair.Key); }
                int desired; int[] excess;
                lock (gate)
                {
                    if (job.Segments.All(p => p.Complete)) return;
                    desired = EffectiveLimit(job);
                    run.Applying = run.AppliedLimit != library.Settings.Connections;
                    excess = workers.Keys.Where(i => run.Segments[i].RequestActive)
                        .OrderByDescending(i => (job.Segments[i].End ?? long.MaxValue) - job.Segments[i].Start - run.Segments[i].Bytes)
                        .ThenByDescending(i => job.Segments[i].Start)
                        .Take(Math.Max(0, run.Connections - desired)).ToArray();
                }
                foreach (var i in excess) await StopWorker(i);
                while (true)
                {
                    int split;
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        split = !CanSplit(job) || job.Segments.Count(p => !p.Complete) >= EffectiveLimit(job) ? -1 :
                            Enumerable.Range(0, job.Segments.Count)
                                .Where(i => !job.Segments[i].Complete && run.Segments[i].Retry == 0 &&
                                    job.Segments[i].End - job.Segments[i].Start + 1 - run.Segments[i].Bytes >= 2 * 1024 * 1024)
                                .OrderByDescending(i => job.Segments[i].End - job.Segments[i].Start - run.Segments[i].Bytes)
                                .FirstOrDefault(-1);
                    }
                    if (split < 0) break;
                    await StopWorker(split);
                    lock (gate)
                    {
                        token.ThrowIfCancellationRequested();
                        var part = job.Segments[split];
                        var remaining = part.End!.Value - part.Start + 1 - part.Committed;
                        if (part.Complete || remaining < 2 * 1024 * 1024 ||
                            job.Segments.Count(p => !p.Complete) >= EffectiveLimit(job)) break;
                        // Persist the old map and durable bytes first. No existing file is moved or shortened.
                        Save();
                        if (PersistenceError != null) { job.ConnectionDiagnostic = run.ConnectionError = PersistenceError; throw new ProblemException(ProblemCode.Persistence); }
                        var oldEnd = part.End;
                        var added = new Segment { Start = part.Start + part.Committed + remaining / 2, End = oldEnd };
                        part.End = added.Start - 1; job.Segments.Add(added);
                        try
                        {
                            StateStore.ValidateSegments(job);
                            store.Save(library); PersistenceError = null;
                        }
                        catch
                        {
                            job.Segments.RemoveAt(job.Segments.Count - 1); part.End = oldEnd;
                            job.ConnectionDiagnostic = run.ConnectionError = new(ProblemCode.Persistence);
                            throw;
                        }
                        run.Segments[job.Segments.Count - 1] = new() { Phase = DownloadPhase.Waiting };
                    }
                }
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    run.AppliedLimit = library.Settings.Connections; run.Applying = false; job.ConnectionDiagnostic = null;
                    for (var i = 0; i < job.Segments.Count; i++)
                    {
                        if (job.Segments[i].Complete || workers.ContainsKey(i)) continue;
                        var index = i;
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                        workers[i] = new(cancel, Task.Run(() => TransferAsync(job, index, run, cancel.Token)));
                    }
                }
                await Task.Delay(50, token);
            }
        }
        finally
        {
            foreach (var worker in workers.Values) worker.Cancel.Cancel();
            foreach (var worker in workers.Values)
            {
                try { await worker.Task; } catch { /* The initiating failure is preserved. */ }
                worker.Cancel.Dispose();
            }
            lock (gate) run.Applying = false;
        }
    }
}
