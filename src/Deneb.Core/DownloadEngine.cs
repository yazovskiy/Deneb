using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Deneb.Core;

public sealed partial class DownloadEngine : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly StateStore store;
    private readonly Library library;
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Running> running = [];
    private readonly Task scheduler;
    private bool stopping;
    private readonly TimeProvider clock;
    public string? PersistenceError { get; private set; }
    private sealed class Running
    {
        public CancellationTokenSource Cancel { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public long Bytes;
        public int Connections;
        public SpeedWindow Meter { get; init; } = null!;
        public string Phase = "Проверка сервера";
        public Progress Probe { get; } = new();
        public Dictionary<int, Progress> Segments { get; } = [];
    }
    private sealed class Progress
    {
        public long Bytes;
        public string Phase = "Передача";
        public int Retry;
        public long? RetryAt;
        public TimeSpan Delay;
        public string? Error;
    }
    private sealed record Remote(long? Size, bool Ranges, string? ETag, DateTimeOffset? Modified, string? Name);
    private sealed class DecisionException(string message) : Exception(message);
    private sealed class RetryException(string message, TimeSpan? delay = null) : Exception(message)
    { public TimeSpan? Delay { get; } = delay; }

    public DownloadEngine(string? stateDirectory = null, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        store = new(stateDirectory ?? StateStore.DefaultDirectory);
        try { library = store.Load(); }
        catch { store.Dispose(); throw; }
        client = new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 256
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Deneb/1.1");
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
        foreach (var job in library.Jobs)
        {
            if (job.State == DownloadState.Downloading) job.State = DownloadState.Queued;
            if (job.State == DownloadState.Completed) continue;
            // Recover a crash between atomic publication and the final state checkpoint.
            if (job.FinalHash != null && job.Target != null && File.Exists(job.Target))
            {
                using var published = File.OpenRead(job.Target);
                if (Convert.ToHexString(SHA256.HashData(published)) == job.FinalHash)
                { job.State = DownloadState.Completed; job.Error = null; continue; }
            }
            for (var i = 0; i < job.Segments.Count; i++)
            {
                var part = Part(job, i);
                var segment = job.Segments[i];
                var actual = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (actual < segment.Committed)
                {
                    segment.Committed = actual;
                    segment.Complete = false;
                    job.State = DownloadState.NeedsDecision;
                    job.Error = "Часть сохранённых данных отсутствует. Начните заново.";
                }
                // Bytes beyond the last durable checkpoint were not committed.
                if (actual > segment.Committed)
                {
                    using var stream = new FileStream(part, FileMode.Open, FileAccess.Write);
                    stream.SetLength(segment.Committed);
                    stream.Flush(true);
                }
            }
        }
        scheduler = Task.Run(ScheduleAsync);
    }

    public Settings GetSettings()
    {
        lock (gate) return new() { ActiveFiles = library.Settings.ActiveFiles, Connections = library.Settings.Connections, Destination = library.Settings.Destination };
    }
    public void SetSettings(Settings settings)
    {
        settings.Validate();
        lock (gate) { library.Settings = settings; Save(); }
    }
    public Guid Add(DownloadInput input, string? destination = null, string? name = null)
    {
        InputParser.ValidateUrl(input.Url);
        lock (gate)
        {
            var folder = destination ?? library.Settings.Destination;
            if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("Папка должна быть абсолютным путём.");
            var job = new DownloadJob { Url = input.Url, Name = name ?? input.Name ?? "", Destination = folder };
            library.Jobs.Add(job);
            Save();
            return job.Id;
        }
    }
    public string GetUrl(Guid id) { lock (gate) return Find(id).Url; }
    public IReadOnlyList<Snapshot> Snapshots()
    {
        lock (gate) return library.Jobs.Select(j =>
        {
            running.TryGetValue(j.Id, out var r);
            var bytes = r == null ? j.Segments.Sum(s => s.Committed) : Interlocked.Read(ref r.Bytes);
            var speed = j.State == DownloadState.Downloading ? r?.Meter.Speed ?? 0 : 0;
            return new Snapshot(j.Id, string.IsNullOrWhiteSpace(j.Name) ? "Получение имени…" : j.Name, j.State, bytes, j.Total, speed,
                r == null ? 0 : Volatile.Read(ref r.Connections), InputParser.ValidateUrl(j.Url).Host, j.Error ?? r?.Segments.Values.Select(p => p.Error).FirstOrDefault(e => e != null) ?? r?.Probe.Error, j.Target)
            {
                Phase = j.State switch { DownloadState.Queued => library.GloballyPaused ? "Общая пауза" : "Очередь", DownloadState.Paused => "Ручная пауза", DownloadState.Completed => "Завершение", DownloadState.Failed => "Ошибка", DownloadState.NeedsDecision => "Требуется решение", _ => r?.Phase == "Проверка сервера" && r.Probe.RetryAt.HasValue ? "Ожидание повтора проверки" : r?.Phase == "Передача" && r.Segments.Values.Any(p => p.RetryAt.HasValue) && !r.Segments.Values.Any(p => p.Phase == "Передача") ? "Ожидание повтора" : r?.Phase ?? "Передача" },
                Eta = r?.Phase == "Передача" && j.State == DownloadState.Downloading && j.Total.HasValue ? r.Meter.Eta(j.Total.Value - bytes) : null,
                Retry = r?.Probe.Retry ?? 0,
                RetryIn = RetryRemaining(r?.Probe),
                Segments = j.Segments.Select((s, i) => { var p = r?.Segments.GetValueOrDefault(i); return new SegmentSnapshot(i + 1, s.Start, s.End, p?.Bytes ?? s.Committed, s.Complete, s.Complete ? "Готово" : j.State != DownloadState.Downloading ? "Остановлен" : p?.Phase ?? "Ожидание", p?.Retry ?? 0, RetryRemaining(p)); }).ToArray()
            };
        }).ToArray();
    }
    public async Task PauseAsync(Guid id)
    {
        Task? task;
        lock (gate)
        {
            var j = Find(id);
            if (j.State == DownloadState.Completed) return;
            j.State = DownloadState.Paused;
            task = Cancel(id);
            Save();
        }
        if (task != null)
        {
            await task;
            lock (gate)
            {
                if (running.Remove(id, out var finished)) finished.Cancel.Dispose();
            }
        }
    }
    public void Resume(Guid id)
    {
        lock (gate)
        {
            var j = Find(id);
            if (j.State is not (DownloadState.Paused or DownloadState.Failed) || running.ContainsKey(id)) return;
            j.State = DownloadState.Queued; j.Error = null; Save();
        }
    }
    public async Task ReplaceUrlAsync(Guid id, string url)
    {
        InputParser.ValidateUrl(url);
        await PauseAsync(id);
        lock (gate) { var j = Find(id); j.Url = url; j.Error = null; j.State = DownloadState.Queued; Save(); }
    }
    public async Task RestartAsync(Guid id)
    {
        await PauseAsync(id);
        lock (gate)
        {
            var j = Find(id);
            if (j.State == DownloadState.Completed) return;
            // Keep old bytes recoverable, even after explicit restart.
            if (Directory.Exists(j.PartsDirectory)) Directory.Move(j.PartsDirectory, j.PartsDirectory + ".saved-" + Guid.NewGuid().ToString("N"));
            j.Segments.Clear(); j.ETag = null; j.LastModified = null; j.Total = null; j.FinalHash = null;
            j.State = DownloadState.Queued; j.Error = null; Save();
        }
    }
    public async Task RemoveAsync(Guid id, bool deletePartial = false)
    {
        await PauseAsync(id);
        lock (gate)
        {
            var j = Find(id);
            if (deletePartial && Directory.Exists(j.PartsDirectory)) Directory.Delete(j.PartsDirectory, true);
            library.Jobs.Remove(j); Save();
        }
    }
    private DownloadJob Find(Guid id) => library.Jobs.First(j => j.Id == id);
    private Task? Cancel(Guid id)
    {
        if (!running.TryGetValue(id, out var r)) return null;
        r.Cancel.Cancel(); return r.Task;
    }
    private static string Part(DownloadJob j, int i) => Path.Combine(j.PartsDirectory, $"{i:D3}.part");
    private void Save()
    {
        try { store.Save(library); PersistenceError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PersistenceError = "Не удалось сохранить состояние: проверьте место и права доступа."; }
    }
    private async Task ScheduleAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                lock (gate)
                {
                    foreach (var pair in running.Where(p => p.Value.Task.IsCompleted).ToArray())
                    { pair.Value.Cancel.Dispose(); running.Remove(pair.Key); }
                    if (!stopping && !library.GloballyPaused && PersistenceError == null)
                    {
                        foreach (var j in library.Jobs.Where(j => j.State == DownloadState.Queued && !running.ContainsKey(j.Id)).Take(Math.Max(0, library.Settings.ActiveFiles - running.Count)).ToArray())
                        {
                            var r = new Running { Meter = new(clock), Bytes = j.Segments.Sum(s => s.Committed) };
                            running.Add(j.Id, r); j.State = DownloadState.Downloading;
                            r.Task = Task.Run(() => RunAsync(j, r));
                        }
                    }
                }
                await Task.Delay(200, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunAsync(DownloadJob job, Running run)
    {
        var token = run.Cancel.Token;
        try
        {
            if (job.Segments.Count > 0 && job.Segments.All(s => s.Complete))
            {
                await AssembleAsync(job, token);
                lock (gate) { job.State = DownloadState.Completed; job.Error = null; Save(); }
                try { Directory.Delete(job.PartsDirectory, true); } catch (IOException) { }
                return;
            }
            var remote = await RetryAsync(async () => { Interlocked.Increment(ref run.Connections); try { return await ProbeAsync(job.Url, token); } finally { Interlocked.Decrement(ref run.Connections); } }, token, run.Probe);
            lock (gate)
            {
                if (job.Segments.Any(s => s.Committed > 0))
                {
                    var identity = job.ETag != null ? job.ETag == remote.ETag
                        : job.LastModified != null && job.LastModified == remote.Modified && job.Total != null && job.Total == remote.Size;
                    if (!identity || job.Total != remote.Size) throw new DecisionException("Нельзя подтвердить прежний файл. Замените ссылку или начните заново.");
                    if (!remote.Ranges) throw new DecisionException("Сервер не поддерживает докачку. Можно начать заново.");
                }
                job.Total = remote.Size; job.Ranges = remote.Ranges; job.ETag = remote.ETag; job.LastModified = remote.Modified;
                if (job.Target == null)
                {
                    job.Name = InputParser.SafeName(string.IsNullOrWhiteSpace(job.Name) ? remote.Name : job.Name, job.Id);
                    job.Target = UniqueTarget(job);
                }
                Directory.CreateDirectory(job.PartsDirectory);
                if (job.Segments.Count == 0)
                {
                    var count = remote.Ranges && remote.Size >= 16 * 1024 * 1024 && (remote.ETag != null || remote.Modified != null)
                        ? library.Settings.Connections : 1;
                    var chunk = remote.Size.HasValue ? (remote.Size.Value + count - 1) / count : 0;
                    for (var i = 0; i < count; i++) job.Segments.Add(new() { Start = i * chunk, End = remote.Size.HasValue ? Math.Min(remote.Size.Value, (i + 1) * chunk) - 1 : null });
                }
                Save();
                run.Phase = "Передача";
                for (var i = 0; i < job.Segments.Count; i++) run.Segments[i] = new() { Bytes = job.Segments[i].Committed };
            }
            using var siblings = CancellationTokenSource.CreateLinkedTokenSource(token);
            Exception? failure = null;
            var transfers = job.Segments.Select(async (s, i) =>
            {
                try { if (!s.Complete) await TransferAsync(job, i, run, siblings.Token); }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref failure, ex, null);
                    siblings.Cancel(); throw;
                }
            }).ToArray();
            try { await Task.WhenAll(transfers); }
            catch { if (failure != null) throw failure; throw; }
            token.ThrowIfCancellationRequested();
            await AssembleAsync(job, token);
            lock (gate) { job.State = DownloadState.Completed; job.Error = null; Save(); }
            try { Directory.Delete(job.PartsDirectory, true); } catch (IOException) { /* Completed output is already durable. */ }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (DecisionException ex) { lock (gate) { job.State = DownloadState.NeedsDecision; job.Error = ex.Message; } }
        catch (Exception ex)
        {
            lock (gate)
            {
                job.State = DownloadState.Failed;
                job.Error = ex switch
                {
                    IOException or UnauthorizedAccessException => "Ошибка диска: проверьте свободное место и права доступа.",
                    HttpRequestException or RetryException or OperationCanceledException => "Сетевая ошибка после повторных попыток. Можно продолжить позже.",
                    _ => "Ошибка загрузки: " + ex.GetType().Name
                };
            }
        }
        finally { lock (gate) Save(); }
    }
    private string UniqueTarget(DownloadJob j)
    {
        var name = j.Name; var index = 1;
        while (File.Exists(Path.Combine(j.Destination, name)) || Directory.Exists(Path.Combine(j.Destination, name)) || library.Jobs.Any(x => x.Id != j.Id && string.Equals(x.Target, Path.Combine(j.Destination, name), StringComparison.OrdinalIgnoreCase)))
            name = $"{Path.GetFileNameWithoutExtension(j.Name)} ({index++}){Path.GetExtension(j.Name)}";
        j.Name = name;
        return Path.Combine(j.Destination, name);
    }
    private async Task<Remote> ProbeAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new(0, 0);
        using var response = await SendAsync(request, token);
        CheckStatus(response);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && range?.Length == 0)
            return new(0, false, StrongTag(response), response.Content.Headers.LastModified, Name(response, url));
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (range?.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is null or <= 0)
                throw new DecisionException("Сервер вернул неверный Content-Range.");
            return new(range.Length, true, StrongTag(response), response.Content.Headers.LastModified, Name(response, url));
        }
        if (response.StatusCode != HttpStatusCode.OK) throw new DecisionException($"HTTP {(int)response.StatusCode}: загрузка недоступна.");
        return new(response.Content.Headers.ContentLength, false, StrongTag(response), response.Content.Headers.LastModified, Name(response, url));
    }
    private static string? StrongTag(HttpResponseMessage response) => response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
    private static string? Name(HttpResponseMessage response, string url) => response.Content.Headers.ContentDisposition?.FileNameStar
        ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Uri.UnescapeDataString(new Uri(url).AbsolutePath.Split('/').Last());
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }
    private static void CheckStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403) throw new DecisionException($"HTTP {status}: доступ отклонён. Проверьте или замените ссылку.");
        if (status is 408 or 429 || status >= 500)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            throw new RetryException($"HTTP {status}", delay);
        }
        if (status >= 400 && status != 416) throw new DecisionException($"HTTP {status}: файл недоступен.");
    }
    private TimeSpan? RetryRemaining(Progress? p) => p?.RetryAt is { } at ? TimeSpan.FromSeconds(Math.Max(0, (p.Delay - clock.GetElapsedTime(at)).TotalSeconds)) : null;
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token, Progress progress)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { lock (gate) { progress.Phase = "Передача"; progress.RetryAt = null; } return await action(); }
            catch (Exception ex) when (!token.IsCancellationRequested && attempt < 10 && ex is HttpRequestException or RetryException or OperationCanceledException)
            {
                var delay = ex is RetryException { Delay: { } d } ? d : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
                lock (gate) { progress.Retry = attempt + 1; progress.Phase = "Ожидание повтора"; progress.Delay = delay; progress.RetryAt = clock.GetTimestamp(); progress.Error = ex is RetryException ? ex.Message : ex is OperationCanceledException ? "Истекло время ожидания ответа." : "Сетевая ошибка соединения."; }
                await Task.Delay(delay, clock, token);
            }
        }
    }
    private async Task TransferAsync(DownloadJob job, int index, Running run, CancellationToken token)
    {
        var segment = job.Segments[index];
        try
        {
            await RetryAsync(async () =>
            {
                Interlocked.Increment(ref run.Connections);
                try
                {
                    if (segment.End != null && segment.Committed == segment.End - segment.Start + 1)
                    {
                        if (!File.Exists(Part(job, index))) { using var empty = File.Create(Part(job, index)); empty.Flush(true); }
                        lock (gate) { segment.Complete = true; Save(); }
                        return true;
                    }
                    var offset = segment.Committed;
                    if (offset > 0 && (!job.Ranges || (job.ETag == null && job.LastModified == null)))
                        throw new DecisionException("Без валидатора и поддержки Range безопасная докачка невозможна. Начните заново.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
                    if (job.Ranges)
                    {
                        request.Headers.Range = new(segment.Start + offset, segment.End);
                        if (job.ETag != null) request.Headers.IfRange = new(EntityTagHeaderValue.Parse(job.ETag));
                        else if (job.LastModified.HasValue) request.Headers.IfRange = new(job.LastModified.Value);
                    }
                    using var response = await SendAsync(request, token);
                    CheckStatus(response);
                    if (job.Ranges)
                    {
                        var cr = response.Content.Headers.ContentRange;
                        if (response.StatusCode != HttpStatusCode.PartialContent || cr?.Unit != "bytes" || cr.From != segment.Start + offset || cr.To != segment.End || cr.Length != job.Total)
                            throw new DecisionException("Сервер изменил файл или диапазон. Данные не были дописаны; требуется решение.");
                        if (job.ETag != null && StrongTag(response) is { } tag && tag != job.ETag)
                            throw new DecisionException("ETag файла изменился. Требуется начать заново.");
                    }
                    else if (response.StatusCode != HttpStatusCode.OK) throw new DecisionException("Неожиданный ответ сервера.");
                    if (job.ETag != null && StrongTag(response) is { } responseTag && responseTag != job.ETag)
                        throw new DecisionException("Файл изменился между запросами. Начните заново.");
                    if (job.ETag == null && job.LastModified.HasValue && response.Content.Headers.LastModified is { } modified && modified != job.LastModified)
                        throw new DecisionException("Дата изменения файла изменилась. Начните заново.");
                    await using var output = new FileStream(Part(job, index), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                    output.SetLength(offset); output.Position = offset;
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    var buffer = new byte[65536];
                    var written = offset;
                    var checkpoint = DateTime.UtcNow;
                    try
                    {
                        while (true)
                        {
                            int read;
                            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                idle.CancelAfter(TimeSpan.FromSeconds(30));
                                try { read = await input.ReadAsync(buffer, idle.Token); }
                                catch (IOException) { throw new RetryException("Соединение оборвалось."); }
                            }
                            if (read == 0) break;
                            if (segment.End.HasValue && written + read > segment.End.Value - segment.Start + 1)
                                throw new DecisionException("Сервер прислал больше данных, чем заявлено.");
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                            written += read; Interlocked.Add(ref run.Bytes, read); run.Meter.Add(read);
                            lock (gate) run.Segments[index].Bytes = written;
                            if ((DateTime.UtcNow - checkpoint).TotalSeconds >= 2)
                            {
                                output.Flush(true);
                                lock (gate) { segment.Committed = written; Save(); }
                                checkpoint = DateTime.UtcNow;
                            }
                        }
                        if (segment.End.HasValue && written != segment.End.Value - segment.Start + 1)
                            throw new RetryException("Неполный ответ сервера.");
                        output.Flush(true);
                        lock (gate) { segment.Committed = written; segment.Complete = true; Save(); }
                        return true;
                    }
                    finally
                    {
                        // FileStream.Length reflects completed writes, including cancellation boundaries.
                        output.Flush(true);
                        lock (gate) { segment.Committed = output.Length; Save(); }
                    }
                }
                finally { Interlocked.Decrement(ref run.Connections); }
            }, token, run.Segments[index]);
        }
        finally { lock (gate) run.Segments[index].Phase = segment.Complete ? "Готово" : "Остановлен"; }
    }
    private async Task AssembleAsync(DownloadJob job, CancellationToken token)
    {
        lock (gate) if (running.TryGetValue(job.Id, out var active)) active.Phase = "Сборка";
        var assembled = Path.Combine(job.PartsDirectory, "assembled.tmp");
        await using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            for (var i = 0; i < job.Segments.Count; i++)
            {
                await using var input = File.OpenRead(Part(job, i));
                if (input.Length != job.Segments[i].Committed) throw new DecisionException("Размер сегмента не совпадает с сохранённым состоянием.");
                await input.CopyToAsync(output, token);
            }
            if (job.Total.HasValue && output.Length != job.Total) throw new DecisionException("Итоговый размер файла не совпадает.");
            output.Flush(true);
            lock (gate) { job.Total ??= output.Length; Save(); }
        }
        string hash;
        lock (gate) if (running.TryGetValue(job.Id, out var active)) active.Phase = "Проверка файла";
        using (var input = File.OpenRead(assembled)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        lock (gate)
        {
            if (File.Exists(job.Target)) job.Target = UniqueTarget(job);
            job.FinalHash = hash;
            Save();
            if (PersistenceError != null) throw new IOException("Не удалось сохранить метаданные готового файла.");
            File.Move(assembled, job.Target!); // Never overwrite an existing destination.
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (gate)
        {
            stopping = true;
            foreach (var r in running.Values) r.Cancel.Cancel();
            tasks = running.Values.Select(r => r.Task).ToArray();
        }
        await Task.WhenAll(tasks);
        lifetime.Cancel(); await scheduler;
        lock (gate) { foreach (var j in library.Jobs.Where(j => j.State == DownloadState.Downloading)) j.State = DownloadState.Queued; Save(); }
        foreach (var r in running.Values) r.Cancel.Dispose();
        client.Dispose(); lifetime.Dispose(); store.Dispose();
    }
}
