namespace Deneb.Core;

public enum DownloadState { Queued, Downloading, Paused, Completed, NeedsDecision, Failed }
public sealed class Settings
{
    public string Language { get; set; } = "en";
    public static string NormalizeLanguage(string? value) => value == "ru" ? "ru" : "en";
    public int ActiveFiles { get; set; } = 2;
    public int Connections { get; set; } = 4;
    public string Destination { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Deneb");
    public void Validate()
    {
        Language = NormalizeLanguage(Language);
        if (ActiveFiles is < 1 or > 16 || Connections is < 1 or > 16 || !Path.IsPathFullyQualified(Destination))
            throw new ProblemException(ProblemCode.InvalidSettings);
    }
}
public sealed class Segment
{
    public long Start { get; set; }
    public long? End { get; set; }
    public long Committed { get; set; }
    public bool Complete { get; set; }
}
public sealed class DownloadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? Target { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long? Total { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public bool Ranges { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; } // v1/v2 input only
    public Problem? Diagnostic { get; set; }
    public string? FinalHash { get; set; }
    public List<Segment> Segments { get; set; } = [];
    public string PartsDirectory => Path.Combine(Destination, $".deneb-{Id:N}");
}
public sealed class Library
{
    public int Version { get; set; } = 3;
    public bool GloballyPaused { get; set; }
    public Settings Settings { get; set; } = new();
    public List<DownloadJob> Jobs { get; set; } = [];
}
public sealed record DownloadInput(string Url, string? Name = null);
public sealed record Snapshot(Guid Id, string Name, DownloadState State, long Bytes, long? Total, double Speed, int Connections, string Source, Problem? Error, string? Target)
{
    public DownloadPhase Phase { get; init; }
    public TimeSpan? Eta { get; init; }
    public IReadOnlyList<SegmentSnapshot> Segments { get; init; } = [];
    public int Retry { get; init; }
    public TimeSpan? RetryIn { get; init; }
}
public sealed record SegmentSnapshot(int Number, long Start, long? End, long Bytes, bool Complete, DownloadPhase Phase, int Retry, TimeSpan? RetryIn);
public sealed record BatchResult(IReadOnlyList<Guid> Processed, IReadOnlyList<Guid> Skipped, IReadOnlyList<Guid> Failed);
