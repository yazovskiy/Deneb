using System.Globalization;

namespace Deneb.App;

public static class QueueLayout
{
    public static int NameWidth(int available) => Math.Max(24, (available + 1) / 2);
    public static string Preview(string name, int width)
    {
        width = Math.Max(1, width);
        var elements = StringInfo.GetTextElementEnumerator(QueueView.SafeText(name));
        var lines = new[] { new System.Text.StringBuilder(), new System.Text.StringBuilder() };
        var row = 0; var used = 0;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement(); var size = ((NStack.ustring)element).ConsoleWidth;
            if (used + size > width) { row++; used = 0; }
            if (row >= 2)
            {
                var last = lines[1].ToString();
                var offsets = StringInfo.ParseCombiningCharacters(last);
                if (offsets.Length > 0) last = last[..offsets[^1]];
                return lines[0] + "\n" + last + "…";
            }
            lines[row].Append(element); used += size;
        }
        return lines[0] + (lines[1].Length > 0 ? "\n" + lines[1] : "");
    }
}
