using System.Text.Json;
using Deneb.Control;
using Deneb.Core;
using Command = Deneb.Control.Command;

namespace Deneb.App;

public sealed record CliError(string Code, int? HttpStatus = null);
public sealed record CliSegment(int Number, long Start, long? End, long Bytes, bool Complete, string Phase, int Retry, double? RetryInSeconds);
public sealed record CliJob(Guid Id, string Name, string State, string Phase, long Bytes, long? TotalBytes, double BytesPerSecond,
    double? EtaSeconds, int Connections, int ConnectionLimit, string Source, string? Target, CliError? Error, CliSegment[] Segments)
{
    public static CliJob From(Snapshot s) => new(s.Id, s.Name, s.State.ToString(), s.Phase.ToString(), s.Bytes, s.Total, s.Speed,
        s.Eta?.TotalSeconds, s.Connections, s.ConnectionLimit, s.Source, s.Target, s.Error == null ? null : new(s.Error.Code.ToString(), s.Error.Status),
        s.Segments.Select(p => new CliSegment(p.Number, p.Start, p.End, p.Bytes, p.Complete, p.Phase.ToString(), p.Retry, p.RetryIn?.TotalSeconds)).ToArray());
}
public sealed class CliResult
{
    public bool BackgroundRunning { get; set; }
    public bool GloballyPaused { get; set; }
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public CliJob[] Jobs { get; set; } = [];
    public List<Guid> Added { get; } = [];
    public Guid[] Processed { get; set; } = [];
    public Guid[] Skipped { get; set; } = [];
    public Guid[] Failed { get; set; } = [];
    public int? FailedInput { get; set; }
    public int[] NotSentInputs { get; set; } = [];
    public bool OutcomeUnknown { get; set; }
}
public sealed record CliOutput(int SchemaVersion, CliResult Result, CliError? Error);

public static class CliRunner
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task<int> RunAsync(CliOptions options, Localization language, TextReader input, TextWriter output, TextWriter errors)
    {
        var result = new CliResult(); CliError? error = null; var code = 0; string? message = null;
        var mutationSent = false;
        try
        {
            IReadOnlyList<DownloadInput> additions = [];
            if (options.Command == "add")
            {
                try { additions = await options.ReadInputsAsync(input); }
                catch (ProblemException ex) { throw new CliInputException(ex.Problem.Code == ProblemCode.SingleName ? "CliSingleName" : "CliInvalidUrl"); }
            }
            var endpoint = new LocalEndpoint(options.StateDirectory);
            ControlClient? client = null;
            try { client = await BackgroundLauncher.ConnectOrStartAsync(endpoint, false, options.Command is "start" or "add"); }
            catch (ProblemException ex) when (ex.Problem.Code == ProblemCode.BackgroundMissing && options.Command is "status" or "stop" or "list") { }
            if (client != null)
            {
                await using (client)
                {
                    language.SetLanguage(client.State.Settings.Language);
                    result.BackgroundRunning = true;
                    result.GloballyPaused = client.State.GloballyPaused;
                    result.TotalCount = client.State.Jobs.Length;
                    result.ActiveCount = client.State.Jobs.Count(j => j.State == DownloadState.Downloading);
                    if (options.Command == "add")
                    {
                        for (var i = 0; i < additions.Count; i++)
                        {
                            try
                            {
                                mutationSent = true;
                                var response = await client.SendAsync(new() { Command = Command.Add, Input = additions[i], Name = options.Name, Destination = options.Destination });
                                if (response.AddedId == null) throw new ProblemException(ProblemCode.DisconnectedControl);
                                result.Added.Add(response.AddedId.Value);
                                mutationSent = false;
                            }
                            catch
                            {
                                result.FailedInput = i + 1;
                                result.NotSentInputs = Enumerable.Range(i + 2, additions.Count - i - 1).ToArray();
                                throw;
                            }
                        }
                    }
                    else if (options.Command is "pause" or "resume" or "remove" or "stop")
                    {
                        var ids = CliOptions.ResolveIds(options.Values, client.State.Jobs);
                        var command = options.Command switch
                        {
                            "stop" => Command.Stop, "remove" => Command.Remove,
                            "pause" => ids.Length == 0 ? Command.PauseAll : Command.Pause,
                            _ => ids.Length == 0 ? Command.ResumeAll : Command.Resume
                        };
                        mutationSent = true;
                        var response = await client.SendAsync(new() { Command = command, Ids = ids, DeletePartial = options.DeletePartial });
                        mutationSent = false;
                        if (response.Batch is { } batch)
                        {
                            result.Processed = batch.Processed.ToArray(); result.Skipped = batch.Skipped.ToArray(); result.Failed = batch.Failed.ToArray();
                            if (result.Failed.Length > 0) { code = result.Processed.Length + result.Skipped.Length > 0 ? 3 : 1; error = new("BatchFailed"); message = language.Text("CliBatchFailed"); }
                        }
                        if (options.Command == "stop") { await BackgroundLauncher.WaitStoppedAsync(endpoint); result.BackgroundRunning = false; }
                    }
                    else if (options.Command == "show")
                    {
                        var id = CliOptions.ResolveIds(options.Values, client.State.Jobs).Single();
                        result.Jobs = [CliJob.From(client.State.Jobs.Single(j => j.Id == id))];
                    }
                    else if (options.Command == "list") result.Jobs = QueueView.Apply(client.State.Jobs, options.Search, options.Filter).Select(CliJob.From).ToArray();
                    result.TotalCount = client.State.Jobs.Length; result.GloballyPaused = client.State.GloballyPaused;
                    result.ActiveCount = result.BackgroundRunning ? client.State.Jobs.Count(j => j.State == DownloadState.Downloading) : 0;
                }
            }
        }
        catch (CliInputException ex) { code = 2; error = new(ex.Key); message = language.Text(ex.Key); }
        catch (Exception ex)
        {
            var problem = Problem.FromException(ex);
            result.OutcomeUnknown = mutationSent && problem.Code == ProblemCode.DisconnectedControl;
            code = result.OutcomeUnknown ? 1 : result.Added.Count > 0 ? 3 : 1;
            error = new(result.OutcomeUnknown ? "OutcomeUnknown" : problem.Code.ToString(), problem.Status);
            message = result.OutcomeUnknown ? language.Text("CliUnknown") : language.Error(problem);
        }
        if (options.Json) await output.WriteLineAsync(JsonSerializer.Serialize(new CliOutput(1, result, error), JsonOptions));
        else
        {
            await output.WriteLineAsync(language.Text(result.BackgroundRunning ? "CliRunning" : "BackgroundStopped"));
            if (result.BackgroundRunning) await output.WriteLineAsync(language.Text("BackgroundStatus", result.TotalCount, result.ActiveCount, result.GloballyPaused ? language.Text("GlobalBanner") : ""));
            foreach (var id in result.Added) await output.WriteLineAsync(language.Text("CliAdded", id));
            foreach (var job in result.Jobs)
            {
                await output.WriteLineAsync($"{job.Id}  {QueueView.SafeText(job.Name)}  {language.Phase(Enum.Parse<DownloadPhase>(job.Phase))}  {language.Size(job.Bytes)} / {(job.TotalBytes is { } total ? language.Size(total) : "—")}  {language.Rate(job.BytesPerSecond)}");
                if (options.Command == "show")
                {
                    await output.WriteLineAsync(language.Text("CliDetails", QueueView.SafeText(job.Source), QueueView.SafeText(job.Target), language.Duration(job.EtaSeconds is { } eta ? TimeSpan.FromSeconds(eta) : null), job.Connections, job.ConnectionLimit));
                    if (job.Error != null) await output.WriteLineAsync(language.Error(new Problem(Enum.Parse<ProblemCode>(job.Error.Code), job.Error.HttpStatus)));
                    foreach (var segment in job.Segments) await output.WriteLineAsync(language.Text("CliSegment", segment.Number, segment.Start, segment.End?.ToString(language.Culture) ?? "—", language.Size(segment.Bytes), language.Phase(Enum.Parse<DownloadPhase>(segment.Phase)), segment.Retry, language.Duration(segment.RetryInSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null)));
                }
            }
            if (options.Command is "pause" or "resume" or "remove") await output.WriteLineAsync(language.Text("Batch", result.Processed.Length, result.Skipped.Length, result.Failed.Length));
            foreach (var id in result.Processed) await output.WriteLineAsync(language.Text("CliProcessed", id));
            foreach (var id in result.Skipped) await output.WriteLineAsync(language.Text("CliSkipped", id));
            foreach (var id in result.Failed) await output.WriteLineAsync(language.Text("CliFailed", id));
            if (result.FailedInput.HasValue) await errors.WriteLineAsync(language.Text("CliInputFailed", result.FailedInput, string.Join(", ", result.NotSentInputs)));
            if (message != null) await errors.WriteLineAsync(QueueView.SafeText(message));
        }
        return code;
    }
}
