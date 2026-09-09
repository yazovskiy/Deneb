using Deneb.Core;

namespace Deneb.App;

public sealed class CliInputException(string key = "CliInvalid") : Exception(key)
{
    public string Key { get; } = key;
}

public sealed record CliOptions
{
    public string? Command { get; init; }
    public string? StateDirectory { get; init; }
    public bool Json { get; init; }
    public bool Help { get; init; }
    public bool Version { get; init; }
    public bool Stdin { get; init; }
    public bool DeletePartial { get; init; }
    public bool Trash { get; init; }
    public string? HelpTopic { get; init; }
    public string? Destination { get; init; }
    public string? Name { get; init; }
    public string Search { get; init; } = "";
    public string Filter { get; init; } = "all";
    public string[] Values { get; init; } = [];

    public static CliOptions Parse(string[] args)
    {
        var flags = new HashSet<string>(); var options = new Dictionary<string, string>(); var values = new List<string>();
        string? command = null; var literal = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = !literal && args[i] == "-h" ? "--help" : args[i];
            if (!literal && arg == "--") { literal = true; continue; }
            if (!literal && arg.StartsWith('-'))
            {
                if (arg is "--state-dir" or "--destination" or "--name" or "--search" or "--filter")
                {
                    if (options.ContainsKey(arg) || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal)) throw new CliInputException();
                    options.Add(arg, args[i]);
                }
                else if (arg is "--json" or "--help" or "--version" or "--stdin" or "--delete-partial" or "--yes" or "--trash")
                { if (!flags.Add(arg)) throw new CliInputException(); }
                else throw new CliInputException();
            }
            else if (command == null) command = arg;
            else values.Add(arg);
        }
        if (command == "help")
        {
            if (values.Count > 1) throw new CliInputException();
            command = values.FirstOrDefault(); values.Clear(); flags.Add("--help");
        }
        if (command != null && !CliHelp.Commands.Contains(command)) throw new CliInputException();
        if (flags.Contains("--version") && (command != null || flags.Count > 1 || values.Count > 0)) throw new CliInputException();
        if (flags.Contains("--help") && flags.Contains("--json")) throw new CliInputException();
        if (options.Keys.Any(k => k is "--destination" or "--name") && command != "add" || flags.Contains("--stdin") && command != "add") throw new CliInputException();
        if (options.Keys.Any(k => k is "--search" or "--filter") && command != "list") throw new CliInputException();
        if (flags.Any(f => f is "--delete-partial" or "--yes" or "--trash") && command != "remove") throw new CliInputException();
        if (flags.Contains("--delete-partial") && flags.Contains("--trash")) throw new CliInputException("CliDeleteConfirm");
        if ((flags.Contains("--delete-partial") || flags.Contains("--trash")) != flags.Contains("--yes")) throw new CliInputException("CliDeleteConfirm");
        if (!QueueView.Filters.Contains(options.GetValueOrDefault("--filter", "all"))) throw new CliInputException();
        if (!flags.Contains("--help") && !flags.Contains("--version"))
        {
            if (command == null && (flags.Count > 0 || values.Count > 0)) throw new CliInputException();
            if (command is "start" or "status" or "stop" or "list" && values.Count > 0) throw new CliInputException();
            if (command == "show" && values.Count != 1 || command == "remove" && values.Count == 0) throw new CliInputException();
            if (command == "add" && (flags.Contains("--stdin") ? values.Count != 0 : values.Count == 0)) throw new CliInputException();
            if (command is "show" or "pause" or "resume" or "remove") foreach (var id in values) ValidateId(id);
        }
        try
        {
            return new() { Command = command, StateDirectory = options.TryGetValue("--state-dir", out var state) ? Path.GetFullPath(state) : null,
                Json = flags.Contains("--json"), Help = flags.Contains("--help"), HelpTopic = flags.Contains("--help") ? command : null,
                Trash = flags.Contains("--trash"), Version = flags.Contains("--version"), Stdin = flags.Contains("--stdin"),
                DeletePartial = flags.Contains("--delete-partial"), Destination = options.TryGetValue("--destination", out var path) ? Path.GetFullPath(path) : null,
                Name = options.GetValueOrDefault("--name"), Search = options.GetValueOrDefault("--search", ""), Filter = options.GetValueOrDefault("--filter", "all"), Values = values.ToArray() };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new CliInputException(); }
    }
    private static string ValidateId(string value)
    {
        if (Guid.TryParseExact(value, "D", out var id)) return id.ToString("N");
        if (value.Length is < 8 or > 32 || !value.All(Uri.IsHexDigit)) throw new CliInputException("CliId");
        return value;
    }
    public static Guid[] ResolveIds(IEnumerable<string> values, IReadOnlyList<Snapshot> jobs) => values.Select(value =>
    {
        var prefix = ValidateId(value);
        var matches = jobs.Where(j => j.Id.ToString("N").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new CliInputException("CliId");
        return matches[0].Id;
    }).Distinct().ToArray();

    public async Task<IReadOnlyList<DownloadInput>> ReadInputsAsync(TextReader input)
    {
        IReadOnlyList<DownloadInput> result;
        if (Stdin) result = InputParser.Parse(await input.ReadToEndAsync());
        else { foreach (var url in Values) InputParser.ValidateUrl(url); result = Values.Select(url => new DownloadInput(url)).ToArray(); }
        if (Name != null && result.Count != 1) throw new CliInputException("CliSingleName");
        return result;
    }
}
