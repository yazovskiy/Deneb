using System.Text.RegularExpressions;
using System.Text;

namespace Deneb.Core;

public static partial class InputParser
{
    public static IReadOnlyList<DownloadInput> Parse(string text)
    {
        var result = new List<DownloadInput>();
        text = Regex.Replace(text, @"\\([_.*\[\]()\-])", "$1");
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim().TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? name = null;
            var title = Regex.Match(line, @"title=(.*?),\s*uri=", RegexOptions.Singleline);
            if (title.Success) name = title.Groups[1].Value.Trim();
            var source = title.Success ? line[(title.Index + title.Length)..] : line;
            var markdown = Regex.Match(source, @"\[(https?://[^\]]+)\]\((https?://[^\s]+)\)");
            var url = markdown.Success ? markdown.Groups[2].Value : Regex.Match(source, @"https?://[^\s\]]+").Value;
            if (title.Success && !markdown.Success) url = Regex.Replace(url, @",$", "");
            ValidateUrl(url);
            result.Add(new(url, name));
        }
        if (result.Count == 0) throw new ArgumentException("Добавьте хотя бы одну HTTP/HTTPS-ссылку.");
        return result;
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Нужна корректная HTTP/HTTPS-ссылка без логина и пароля в адресе.");
        return uri;
    }

    public static string SafeName(string? value, Guid id)
    {
        var name = (value ?? "").Replace('\\', '/').Split('/').Last();
        name = Regex.Replace(name, "[\\x00-\\x1f<>:\"|?*]", "_").Trim().Trim('.');
        if (Encoding.UTF8.GetByteCount(name) > 200)
        {
            var extension = Path.GetExtension(name);
            if (Encoding.UTF8.GetByteCount(extension) > 24) extension = "";
            var prefix = new StringBuilder();
            var bytes = 0;
            foreach (var rune in Path.GetFileNameWithoutExtension(name).EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 176) break;
                prefix.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
            }
            name = prefix + extension;
        }
        return string.IsNullOrWhiteSpace(name) ? $"download-{id:N}" : name;
    }
}
