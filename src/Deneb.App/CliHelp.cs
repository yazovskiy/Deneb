namespace Deneb.App;

public static class CliHelp
{
    public static readonly string[] Commands = ["add", "list", "show", "pause", "resume", "remove", "start", "status", "stop"];
    public static string Format(Localization language, string? command = null) => command == null
        ? language.Text("CliHelp")
        : language.Text("CliCommand_" + command) + "\n\n" + language.Text("CliCommonHelp");
}
