using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deneb.App;
using Deneb.Core;
using Xunit;

namespace Deneb.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deneb-languages-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void ResourcesAreCompleteAndHaveMatchingPlaceholders()
    {
        var baseline = Localization.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var russian = Localization.Resources.GetResourceSet(CultureInfo.GetCultureInfo("ru"), true, false)!;
        var en = baseline.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        var ru = russian.Cast<DictionaryEntry>().ToDictionary(p => (string)p.Key, p => (string)p.Value!);
        Assert.Equal(en.Keys.Order(), ru.Keys.Order());
        foreach (var key in en.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key])); Assert.False(string.IsNullOrWhiteSpace(ru[key]));
            Assert.Equal(Regex.Matches(en[key], @"\{\d+(?:[^}]*)\}").Select(m => m.Value).Order(), Regex.Matches(ru[key], @"\{\d+(?:[^}]*)\}").Select(m => m.Value).Order());
            foreach (var language in new[] { "en", "ru" }) Assert.NotEmpty(new Localization(language).Text(key, Enumerable.Repeat<object?>(1, 12).ToArray()));
        }
        foreach (var code in Enum.GetValues<ProblemCode>()) Assert.Contains("Error_" + code, en.Keys);
        foreach (var phase in Enum.GetValues<DownloadPhase>()) Assert.Contains("Phase_" + phase, en.Keys);
        foreach (var constraint in Enum.GetValues<ConnectionConstraint>()) Assert.Contains("Constraint_" + constraint, en.Keys);
        Assert.Throws<MissingManifestResourceException>(() => new Localization().Text("MissingKey"));
        Assert.Equal(en["WindowTitle"], Localization.Resources.GetString("WindowTitle", CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void FormattingAndErrorsDoNotDependOnAmbientCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var en = new Localization(); var ru = new Localization("ru");
            Assert.Equal("1.5 MiB", en.Size(1572864)); Assert.Equal("1,5 МиБ", ru.Size(1572864));
            Assert.Equal("1.5 MiB/s", en.Rate(1572864)); Assert.Equal("1,5 МиБ/с", ru.Rate(1572864));
            Assert.Equal("2 d 03:04:05", en.Duration(new TimeSpan(2, 3, 4, 5))); Assert.Equal("2 д 03:04:05", ru.Duration(new TimeSpan(2, 3, 4, 5)));
            Assert.Equal("—", en.Duration(null)); Assert.Equal("en", new Localization("de").Language);
            Assert.Contains("403", en.Error(new Problem(ProblemCode.HttpDenied, 403)));
            Assert.DoesNotContain("secret", en.Error(new IOException("https://example.org/?token=secret")));
            Assert.Contains("previous version", en.Error(new Problem(ProblemCode.Legacy, LegacyText: "Старое сообщение")));
            var snapshot = new Snapshot(Guid.NewGuid(), "Звезда.mp4", DownloadState.Paused, 0, null, 0, 0, "example.org", null, "/tmp/Звезда.mp4");
            Assert.Equal(snapshot.Name, en.Name(snapshot)); Assert.Equal(snapshot.Name, ru.Name(snapshot));
            en.SetLanguage("ru"); Assert.Equal(ru.Phase(DownloadPhase.Probing), en.Phase(DownloadPhase.Probing));
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MigrationKeepsDataAndConvertsKnownErrors(int version)
    {
        Directory.CreateDirectory(root);
        var job = new DownloadJob
        {
            Name = "Тест.bin",
            Url = "https://example.org/file",
            Destination = root,
            State = DownloadState.NeedsDecision,
            ETag = "\"original\"",
            Total = 42,
            Error = "HTTP 403: доступ отклонён. Проверьте или замените ссылку.",
            Segments = [new() { Start = 0, End = 41, Committed = 12 }]
        };
        var library = new Library { Version = version, Settings = new() { Language = "ru" }, GloballyPaused = true, Jobs = [job, new() { Error = "Неизвестная старая ошибка", State = DownloadState.Paused }] };
        var json = JsonSerializer.Serialize(library); var path = Path.Combine(root, "state.json"); File.WriteAllText(path, json);
        Assert.Equal("en", StateStore.ReadLanguage(root)); Assert.False(File.Exists(path + $".v{version}.bak"));
        using var store = new StateStore(root); var loaded = store.Load();
        Assert.Equal(4, loaded.Version); Assert.Equal("en", loaded.Settings.Language); Assert.Equal(version == 2, loaded.GloballyPaused);
        Assert.Equal(job.Id, loaded.Jobs[0].Id); Assert.Equal(job.ETag, loaded.Jobs[0].ETag); Assert.Equal(12, loaded.Jobs[0].Segments[0].Committed);
        Assert.Equal(ProblemCode.HttpDenied, loaded.Jobs[0].Diagnostic!.Code); Assert.Equal(403, loaded.Jobs[0].Diagnostic!.Status); Assert.Null(loaded.Jobs[0].Error);
        Assert.Equal(ProblemCode.Legacy, loaded.Jobs[1].Diagnostic!.Code); Assert.Equal("Неизвестная старая ошибка", loaded.Jobs[1].Diagnostic!.LegacyText);
        store.Save(loaded); Assert.Equal(json, File.ReadAllText(path + $".v{version}.bak"));
        File.WriteAllText(path, json); _ = store.Load(); Assert.Equal(json, File.ReadAllText(path + $".v{version}.bak"));
    }

    [Fact]
    public async Task LanguageIsSavedAndUnknownValuesFallBackToEnglish()
    {
        Assert.Equal("en", StateStore.ReadLanguage(root)); Assert.False(Directory.Exists(root));
        await using (var engine = new DownloadEngine(root))
        {
            Assert.Equal("en", engine.GetSettings().Language);
            var settings = engine.GetSettings(); settings.Language = "ru"; engine.SetSettings(settings);
            settings.Language = "en"; Assert.Equal("ru", engine.GetSettings().Language);
            Assert.Equal("ru", StateStore.ReadLanguage(root)); // read-only access while the store is locked
        }
        var path = Path.Combine(root, "state.json"); var original = File.ReadAllText(path); var files = Directory.GetFiles(root).Order().ToArray();
        Assert.Equal("ru", StateStore.ReadLanguage(root)); Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(files, Directory.GetFiles(root).Order());
        await using (var engine = new DownloadEngine(root))
        {
            Assert.Equal("ru", engine.GetSettings().Language);
            var settings = engine.GetSettings(); settings.Language = "es"; engine.SetSettings(settings); Assert.Equal("en", engine.GetSettings().Language);
        }
    }

    [Theory]
    [InlineData("ETag файла изменился. Требуется начать заново.", ProblemCode.TagChanged)]
    [InlineData("HTTP 503", ProblemCode.HttpRetry)]
    [InlineData("HTTP 404: загрузка недоступна.", ProblemCode.HttpUnavailable)]
    [InlineData("Ошибка загрузки: InvalidOperationException", ProblemCode.Unexpected)]
    public void LegacyDiagnosticsMapToStableCodes(string text, ProblemCode expected) => Assert.Equal(expected, LegacyProblems.Convert(text)!.Code);
}
