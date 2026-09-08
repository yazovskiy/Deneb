using Deneb.Core;

namespace Deneb.App;

public static class QueueView
{
    public static readonly string[] Filters = ["all", "active", "queued", "paused", "completed", "attention"];
    public static IReadOnlyList<Snapshot> Apply(IEnumerable<Snapshot> jobs, string search, string filter) => jobs.Where(j =>
        j.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && (filter switch
        {
            "all" => true,
            "active" => j.State == DownloadState.Downloading,
            "queued" => j.State == DownloadState.Queued,
            "paused" => j.State == DownloadState.Paused,
            "completed" => j.State == DownloadState.Completed,
            "attention" => j.State is DownloadState.Failed or DownloadState.NeedsDecision ||
                j.State == DownloadState.Paused && j.Error?.Code is ProblemCode.InsufficientDiskSpace or ProblemCode.DiskSpaceUnavailable,
            _ => false
        })).ToArray();

    public static Guid[] Targets(IReadOnlyList<Snapshot> visible, HashSet<Guid> marks, Guid? selected)
    {
        var ids = visible.Where(j => marks.Contains(j.Id)).Select(j => j.Id).ToArray();
        return ids.Length > 0 ? ids : selected.HasValue && visible.Any(j => j.Id == selected) ? [selected.Value] : [];
    }
    public static string SafeText(string? value) => string.Concat((value ?? "").Select(c =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : c));
}
