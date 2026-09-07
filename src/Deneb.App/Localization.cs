using System.Globalization;
using System.Resources;
using Deneb.Core;

namespace Deneb.App;

public sealed class Localization
{
    public static ResourceManager Resources { get; } = new("Deneb.App.Resources.Strings", typeof(Localization).Assembly);
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public string Language => Culture.TwoLetterISOLanguageName;
    public Localization(string language = "en") => SetLanguage(language);
    public void SetLanguage(string language) => Culture = CultureInfo.GetCultureInfo(Settings.NormalizeLanguage(language) == "ru" ? "ru-RU" : "en-US");
    public string Text(string key, params object?[] values)
    {
        var template = Resources.GetString(key, Culture) ?? Resources.GetString(key, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException(key);
        return values.Length == 0 ? template : string.Format(Culture, template, values);
    }
    public string Error(Problem? problem) => problem == null ? "" : Text("Error_" + (Enum.IsDefined(problem.Code) ? problem.Code : ProblemCode.Unexpected),
        problem.Code == ProblemCode.Legacy ? problem.LegacyText : problem.Status);
    public string Error(Exception exception) => Error(Problem.FromException(exception));
    public string Phase(DownloadPhase phase) => Text("Phase_" + phase);
    public string Number(double number, string format = "0.0") => number.ToString(format, Culture);
    public string Size(long bytes) => bytes >= 1073741824 ? Text("SizeFormat", bytes / 1073741824d, Text("GiB"))
        : bytes >= 1048576 ? Text("SizeFormat", bytes / 1048576d, Text("MiB")) : Text("SizeFormat", bytes / 1024d, Text("KiB"));
    public string Rate(double bytes) => Text("Rate", Size((long)bytes));
    public string Duration(TimeSpan? time) => time is { } t
        ? (t.Days > 0 ? Text("Days", t.Days) : "") + t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : Text("Dash");
    public string Name(Snapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.Name) ? Text("GettingName") : snapshot.Name;
}
