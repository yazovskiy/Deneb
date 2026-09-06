using Deneb.App;
using Xunit;

namespace Deneb.Tests;

public sealed class FileActionTests
{
    [Fact]
    public async Task PathsArePassedLiterallyAndErrorsReported()
    {
        var path = Path.Combine(Path.GetTempPath(), "Денеб пробел $; " + Guid.NewGuid()); File.WriteAllText(path, "test");
        try
        {
            string? command = null, input = null; IReadOnlyList<string> args = [];
            var actions = new MacFileActions((c, a, i) => { command = c; args = a; input = i; return Task.FromResult(0); });
            await actions.ExecuteAsync(path, "reveal"); Assert.Equal("/usr/bin/open", command); Assert.Equal(new[] { "-R", path }, args);
            await actions.ExecuteAsync(path, "open"); Assert.Equal(new[] { path }, args);
            await actions.ExecuteAsync(path, "copy"); Assert.Equal("/usr/bin/pbcopy", command); Assert.Empty(args); Assert.Equal(path, input);
            await Assert.ThrowsAsync<IOException>(() => new MacFileActions((_, _, _) => Task.FromResult(1)).ExecuteAsync(path, "open"));
            await Assert.ThrowsAsync<IOException>(() => actions.ExecuteAsync(path + "missing", "open"));
        }
        finally { File.Delete(path); }
    }
}
