using System.Data;
using Deneb.Core;
using Deneb.Control;
using Command = Deneb.Control.Command;
using Terminal.Gui;
using Deneb.App;

CliOptions options;
var background = args.Length == 3 && args[0] == "--background" && args[1] == "--state-dir";
try { options = CliOptions.Parse(background ? args[1..] : args); }
catch (CliInputException ex)
{
    var index = Array.IndexOf(args, "--state-dir");
    var lang = new Localization(StateStore.ReadLanguage(index >= 0 && index + 1 < args.Length ? args[index + 1] : null));
    if (args.Contains("--json")) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new CliOutput(1, new(), new(ex.Key)), CliRunner.JsonOptions));
    else Console.Error.WriteLine(lang.Text(ex.Key));
    Environment.ExitCode = 2; return;
}
var stateDirectory = options.StateDirectory;
if (background)
{
    try
    {
        BackgroundLauncher.Detach();
        using var stop = new CancellationTokenSource();
        using var termination = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        await new ControlServer(new LocalEndpoint(stateDirectory)).RunAsync(stop.Token);
    }
    catch (Exception ex) { Environment.ExitCode = 20 + (int)Problem.FromException(ex).Code; }
    return;
}
var localization = new Localization(StateStore.ReadLanguage(stateDirectory));
if (options.Version) { Console.WriteLine("Deneb 2.2.0"); return; }
if (options.Help) { Console.WriteLine(localization.Text("CliHelp")); return; }
try
{
    var endpoint = new LocalEndpoint(stateDirectory);
    if (options.Command != null)
    {
        Environment.ExitCode = await CliRunner.RunAsync(options, localization, Console.In, Console.Out, Console.Error);
        return;
    }
    if (Console.IsInputRedirected) { Console.Error.WriteLine(localization.Text("InteractiveRequired")); Environment.ExitCode = 2; return; }
    await using var engine = new RemoteEngine(await BackgroundLauncher.ConnectOrStartAsync(endpoint, true, true), endpoint);
    localization.SetLanguage(engine.GetSettings().Language);
    Application.Init();
    try { new DenebUi(engine, localization).Run(); }
    finally { Application.Shutdown(); }
    Console.WriteLine(localization.Text("Shutdown"));
}
catch (Exception ex)
{
    Console.Error.WriteLine(localization.Text("StartupError", localization.Error(ex)));
    Environment.ExitCode = 1;
}

sealed class DenebUi(RemoteEngine engine, Localization localization)
{
    private readonly TableView table = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly Label summary = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label keys = new()
    { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private IReadOnlyList<Snapshot> rows = [];
    private string search = "";
    private string filter = "all";
    private bool modal;
    private bool busy;
    private bool polling;
    private bool closed;
    private readonly HashSet<Guid> marked = [];
    private BatchResult? notice;
    private Window? window;
    private string T(string key, params object?[] values) => localization.Text(key, values);
    private Guid[] Targets => QueueView.Targets(rows, marked, Selected);
    private void Report(BatchResult result) => notice = result;

    public void Run()
    {
        window = new Window(T("WindowTitle")) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(table, summary, keys);
        table.Style.ExpandLastColumn = false;
        Application.Top.Add(window);
        table.CellActivated += _ => Details();
        table.KeyPress += e =>
        {
            if (modal || busy) return;
            var key = e.KeyEvent.Key;
            if (!engine.Connected && key is not (Key.q or Key.Q or Key.F1 or Key.F11 or Key.F12) && key != (Key.CtrlMask | Key.c)) return;
            switch (key)
            {
                case (Key)'/': Search(); break;
                case Key.CtrlMask | Key.l: search = ""; filter = "all"; Refresh(); break;
                case Key.a: case Key.A: Add(); break;
                case Key.Space: Toggle(); break;
                case Key.u: case Key.U: Replace(); break;
                case Key.DeleteChar: case Key.Delete: Remove(); break;
                case Key.F2: Settings(); break;
                case Key.InsertChar: if (Selected is { } mark && !marked.Remove(mark)) marked.Add(mark); Refresh(); break;
                case Key.F3: if (Selected is { } up) Work(() => engine.MoveAsync(up, -1)); break;
                case Key.F4: if (Selected is { } down) Work(() => engine.MoveAsync(down, 1)); break;
                case Key.F5: if (Selected is { } next) Work(() => engine.DownloadNextAsync(next)); break;
                case Key.F6: if (engine.GloballyPaused) Work(() => engine.ResumeAllAsync()); else Work(() => engine.PauseAllAsync()); break;
                case Key.o: case Key.O: FileAction("open"); break;
                case Key.F7: FileAction("reveal"); break;
                case Key.F8: FileAction("copy"); break;
                case Key.F9: Dialog(() => { if (MessageBox.Query(T("ClearTitle"), T("ClearPrompt"), T("Cancel"), T("Clear")) == 1) Work(async () => Report(await engine.ClearCompletedAsync())); }); break;
                case Key.F10: Dialog(() => { if (MessageBox.Query(T("StopTitle"), T("StopPrompt"), T("Cancel"), T("Stop")) == 1) Work(async () => { await engine.StopAsync(); Application.MainLoop.Invoke(() => Application.RequestStop()); }); }); break;
                case Key.F11: if (!engine.Connected) Work(() => engine.ReconnectAsync(false)); break;
                case Key.F12: if (!engine.Connected) Work(() => engine.ReconnectAsync(true)); break;
                case Key.F1: Dialog(() => MessageBox.Query(T("HelpTitle"), T("Help"), T("Close"))); break;
                case Key.q: case Key.Q: case Key.CtrlMask | Key.c: Application.RequestStop(); break;
                default: return;
            }
            e.Handled = true;
        };
        Console.CancelKeyPress += OnCancel;
        var previousRootKey = Application.RootKeyEvent;
        Application.RootKeyEvent = key =>
        {
            if (!modal && !busy && key.Key is (Key.CtrlMask | Key.l) or (Key.CtrlMask | Key.L))
            { search = ""; filter = "all"; notice = null; Refresh(); return true; }
            return previousRootKey?.Invoke(key) ?? false;
        };
        var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => { Poll(); Refresh(); return true; });
        Refresh();
        try { Application.Run(); }
        finally { closed = true; Application.RootKeyEvent = previousRootKey; Application.MainLoop.RemoveTimeout(timer); Console.CancelKeyPress -= OnCancel; }
    }
    private void Poll()
    {
        if (polling || busy || !engine.Connected) return;
        polling = true;
        _ = Task.Run(async () =>
        {
            try { await engine.PollAsync(); }
            catch (ProblemException) { }
            finally { if (!closed) Application.MainLoop.Invoke(() => { polling = false; Refresh(); }); }
        });
    }
    private static void OnCancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; Application.MainLoop.Invoke(() => Application.RequestStop()); }
    private Guid? Selected => table.SelectedRow >= 0 && table.SelectedRow < rows.Count ? rows[table.SelectedRow].Id : null;
    private void Refresh()
    {
        if (modal) return;
        var selected = Selected;
        var previousIndex = table.SelectedRow;
        var rowOffset = table.RowOffset; var columnOffset = table.ColumnOffset;
        keys.Text = T("Keys"); if (window != null) window.Title = T("WindowTitle");
        var all = engine.Snapshots();
        marked.IntersectWith(all.Select(r => r.Id));
        rows = QueueView.Apply(all, search, filter);
        var data = new DataTable();
        foreach (var col in new[] { T("File"), T("State"), "%", T("Volume"), T("Speed"), T("Remaining"), T("Connections") }) data.Columns.Add(col);
        foreach (var s in rows)
        {
            var percent = s.Total is > 0 ? localization.Number(100.0 * s.Bytes / s.Total.Value) : s.State == DownloadState.Completed ? "100" : "—";
            var eta = Duration(s.Eta);
            data.Rows.Add((marked.Contains(s.Id) ? "✓ " : "") + localization.Name(s), localization.Phase(s.Phase), percent, $"{Size(s.Bytes)} / {(s.Total.HasValue ? Size(s.Total.Value) : "?")}", localization.Rate(s.Speed), eta, T("ConnectionCount", s.Connections, s.ConnectionLimit));
        }
        table.Table = data;
        table.Style.ColumnStyles.Clear();
        table.Style.ColumnStyles[data.Columns[0]] = new TableView.ColumnStyle { MinWidth = 24, MaxWidth = 38 };
        var index = selected.HasValue ? rows.ToList().FindIndex(r => r.Id == selected) : 0;
        if (rows.Count > 0) table.SetSelection(0, index >= 0 ? index : Math.Clamp(previousIndex, 0, rows.Count - 1), false);
        table.RowOffset = Math.Clamp(rowOffset, 0, Math.Max(0, rows.Count - 1)); table.ColumnOffset = columnOffset;
        summary.Text = !engine.Connected ? T("DisconnectedBanner") : engine.PersistenceError != null ? localization.Error(engine.PersistenceError) : busy ? T("Busy") : T("Summary", engine.GloballyPaused ? T("GlobalBanner") : "", rows.Count, marked.Count, localization.Rate(rows.Sum(r => r.Speed)), notice == null ? "" : T("Batch", notice.Processed.Count, notice.Skipped.Count, notice.Failed.Count));
        if (engine.Connected)
        {
            var visibleMarks = rows.Count(r => marked.Contains(r.Id));
            summary.Text = T("QueueSummary", rows.Count, all.Count, visibleMarks, marked.Count - visibleMarks, T("Filter_" + filter), localization.Rate(all.Sum(r => r.Speed))) + " | " + localization.Bandwidth(engine.GetSettings().BandwidthLimitBytesPerSecond) + (engine.GloballyPaused ? " " + T("GlobalBanner") : "");
            if (engine.PersistenceError != null) summary.Text = localization.Error(engine.PersistenceError);
            else if (busy) summary.Text = T("Busy");
            else if (notice != null) summary.Text += " " + T("Batch", notice.Processed.Count, notice.Skipped.Count, notice.Failed.Count);
        }
        table.SetNeedsDisplay();
    }
    private string Size(long n) => localization.Size(n);
    private void Search() => Dialog(() =>
    {
        var text = new TextField(search) { X = 1, Y = 1, Width = Dim.Fill(1) };
        var choices = new RadioGroup(QueueView.Filters.Select(f => (NStack.ustring)T("Filter_" + f)).ToArray()) { X = 1, Y = 3, SelectedItem = Array.IndexOf(QueueView.Filters, filter) };
        var apply = new Button(T("Ok"), true); var cancel = new Button(T("Cancel"));
        var dialog = new Dialog(T("SearchTitle"), 64, 15, apply, cancel);
        dialog.Add(text, choices);
        text.SetFocus();
        apply.Clicked += () => { search = text.Text.ToString() ?? ""; filter = QueueView.Filters[choices.SelectedItem]; notice = null; Application.RequestStop(); };
        cancel.Clicked += () => Application.RequestStop();
        Application.Run(dialog);
    });
    private void Dialog(Action action) { modal = true; try { action(); } catch (Exception ex) { Error(ex); } finally { modal = false; Refresh(); } }
    private void Error(Exception ex) => MessageBox.ErrorQuery(T("ErrorTitle"), localization.Error(ex), T("Ok"));
    private void Work(Func<Task> action)
    {
        busy = true;
        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try { await action(); } catch (Exception ex) { error = ex; }
            if (!closed) Application.MainLoop.Invoke(() => { busy = false; if (error != null) Dialog(() => Error(error)); Refresh(); });
        });
    }
    private void Add() => Dialog(() =>
    {
        var input = new TextView { X = 1, Y = 1, Width = Dim.Fill(1), Height = Dim.Fill(6), WordWrap = false, AllowsTab = false };
        var folder = new TextField(engine.GetSettings().Destination) { X = 10, Y = Pos.AnchorEnd(5), Width = Dim.Fill(1) };
        var name = new TextField("") { X = 10, Y = Pos.AnchorEnd(3), Width = Dim.Fill(1) };
        var ok = new Button(T("Check")); var cancel = new Button(T("Cancel"));
        var dialog = new Dialog(T("AddTitle"), 95, 22, ok, cancel);
        dialog.Add(input, new Label(T("FolderLabel")) { X = 1, Y = Pos.AnchorEnd(5) }, folder, new Label(T("NameLabel")) { X = 1, Y = Pos.AnchorEnd(3) }, name);
        input.SetFocus();
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            try
            {
                var parsed = InputParser.Parse(input.Text.ToString() ?? "");
                var custom = name.Text.ToString();
                if (parsed.Count > 1 && !string.IsNullOrWhiteSpace(custom)) throw new ProblemException(ProblemCode.SingleName);
                var dest = folder.Text.ToString() ?? "";
                if (!Path.IsPathFullyQualified(dest)) throw new ProblemException(ProblemCode.AbsolutePath);
                var preview = string.Join("\n\n", parsed.Select(p => $"{(string.IsNullOrWhiteSpace(custom) ? p.Name ?? T("AutomaticName") : custom)}\n{p.Url}"));
                if (MessageBox.Query(T("AddConfirm"), preview, T("Add"), T("Back")) != 0) return;
                Work(async () => { foreach (var item in parsed) await engine.AddAsync(item, dest, string.IsNullOrWhiteSpace(custom) ? null : custom); });
                Application.RequestStop();
            }
            catch (Exception ex) { Error(ex); }
        };
        Application.Run(dialog);
    });
    private void Toggle()
    {
        var ids = Targets;
        if (ids.Length == 0) return;
        if (rows.Any(s => ids.Contains(s.Id) && s.State is DownloadState.Downloading or DownloadState.Queued)) Work(async () => Report(await engine.PauseManyAsync(ids)));
        else Work(async () => Report(await engine.ResumeManyAsync(ids)));
    }
    private string Duration(TimeSpan? time) => localization.Duration(time);
    private void FileAction(string action)
    {
        if (Selected is not { } id) return;
        var s = engine.Snapshots().FirstOrDefault(s => s.Id == id);
        if (s is { State: DownloadState.Completed, Target: not null }) Work(() => new MacFileActions().ExecuteAsync(s.Target, action));
    }
    private void Details()
    {
        if (modal || busy) return;
        if (Selected is not { } id) return;
        Dialog(() =>
        {
            var close = new Button(T("Close")); var restart = new Button(T("Restart"));
            var open = new Button(T("OpenFile")); var reveal = new Button(T("Reveal")); var copy = new Button(T("CopyPath"));
            var dialog = new Dialog(T("DetailsTitle"), 110, 25, close, restart, open, reveal, copy);
            var info = new TextView { X = 1, Y = 1, Width = Dim.Fill(1), Height = 8, ReadOnly = true, WordWrap = true, AllowsTab = false };
            var segments = new TableView { X = 1, Y = 10, Width = Dim.Fill(1), Height = Dim.Fill(2), FullRowSelect = true };
            dialog.Add(info, segments); close.Clicked += () => Application.RequestStop();
            restart.Clicked += () => { if (MessageBox.Query(T("RestartTitle"), T("RestartPrompt"), T("Cancel"), T("Start")) == 1) { Work(() => engine.RestartAsync(id)); Application.RequestStop(); } };
            async void Act(string action)
            {
                var s = engine.Snapshots().FirstOrDefault(s => s.Id == id);
                if (s is not { State: DownloadState.Completed, Target: not null }) return;
                try { await new MacFileActions().ExecuteAsync(s.Target, action); }
                catch (Exception ex) { Application.MainLoop.Invoke(() => Error(ex)); }
            }
            open.Clicked += () => Act("open"); reveal.Clicked += () => Act("reveal"); copy.Clicked += () => Act("copy");
            void Update()
            {
                var s = engine.Snapshots().FirstOrDefault(s => s.Id == id); if (s == null) return;
                var top = info.TopRow; var left = info.LeftColumn; var cursor = info.CursorPosition;
                var detailText = T("ConnectionDetail", s.Connections, s.ConnectionLimit, s.ApplyingConnections ? T("ApplyingConnections") : T("Constraint_" + s.ConnectionConstraint), localization.Error(s.ConnectionError)) + "\n" + T("Details", localization.Name(s), localization.Phase(s.Phase), Size(s.Bytes), s.Total.HasValue ? Size(s.Total.Value) : T("Unknown"), localization.Rate(s.Speed), Duration(s.Eta), s.Connections, s.Source, s.Target, s.Retry, Duration(s.RetryIn), localization.Error(s.Error));
                detailText += "\n" + localization.Disk(s.DiskSpace);
                if (s.WaitingForBandwidth) detailText += "\n" + T("BandwidthWaiting");
                if (s.Phase == DownloadPhase.DiskPause) detailText += "\n" + T("DiskResumeHint");
                if (info.Text.ToString() != detailText) { info.Text = detailText; info.CursorPosition = cursor; info.TopRow = top; info.LeftColumn = left; }
                restart.Enabled = s.State == DownloadState.NeedsDecision; open.Enabled = reveal.Enabled = copy.Enabled = s.State == DownloadState.Completed && OperatingSystem.IsMacOS();
                var data = new DataTable(); foreach (var c in new[] { T("Number"), T("Range"), T("Volume"), "%", T("State"), T("Retry"), T("RetryIn") }) data.Columns.Add(c);
                foreach (var p in s.Segments) { var total = p.End - p.Start + 1; data.Rows.Add(p.Number.ToString(localization.Culture), $"{p.Start.ToString(localization.Culture)}–{p.End?.ToString(localization.Culture)}", $"{Size(p.Bytes)} / {(total.HasValue ? Size(total.Value) : "?")}", total > 0 ? localization.Number(100d * p.Bytes / total.Value) : "—", localization.Phase(p.Phase), T("RetryFormat", p.Retry), Duration(p.RetryIn)); }
                var row = segments.SelectedRow; var column = segments.SelectedColumn; var offset = segments.RowOffset; var horizontal = segments.ColumnOffset; segments.Table = data;
                if (data.Rows.Count > 0) segments.SetSelection(Math.Max(0, column), Math.Clamp(row, 0, data.Rows.Count - 1), false); segments.RowOffset = offset; segments.ColumnOffset = horizontal;
            }
            Update(); info.SetFocus(); var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => { Update(); return true; });
            try { Application.Run(dialog); } finally { Application.MainLoop.RemoveTimeout(timer); }
        });
    }
    private void Replace()
    {
        if (Selected is not { } id || rows.First(r => r.Id == id).State == DownloadState.Completed) return;
        Work(async () => { var url = await engine.GetUrlAsync(id); Application.MainLoop.Invoke(() => ReplaceDialog(id, url)); });
    }
    private void ReplaceDialog(Guid id, string currentUrl)
    {
        Dialog(() =>
        {
            var field = new TextField(currentUrl) { X = 1, Y = 1, Width = Dim.Fill(1) };
            var ok = new Button(T("Replace")); var cancel = new Button(T("Cancel"));
            var dialog = new Dialog(T("ReplaceTitle"), 90, 7, ok, cancel);
            dialog.Add(field); cancel.Clicked += () => Application.RequestStop();
            field.SetFocus();
            ok.Clicked += () =>
            {
                try { var url = field.Text.ToString() ?? ""; InputParser.ValidateUrl(url); Work(() => engine.ReplaceUrlAsync(id, url)); Application.RequestStop(); }
                catch (Exception ex) { Error(ex); }
            };
            Application.Run(dialog);
        });
    }
    private void Remove()
    {
        var ids = Targets; if (ids.Length == 0) return;
        Dialog(() =>
        {
            var choice = MessageBox.Query(T("RemoveTitle"), T("RemovePrompt", ids.Length), T("Cancel"), T("KeepParts"), T("DeleteParts"));
            if (choice == 1) Work(async () => Report(await engine.RemoveManyAsync(ids)));
            if (choice == 2 && MessageBox.Query(T("DeleteTitle"), T("DeletePrompt"), T("Cancel"), T("Delete")) == 1)
                Work(async () => Report(await engine.RemoveManyAsync(ids, true)));
        });
    }
    private void Settings() => Dialog(() =>
    {
        var settings = engine.GetSettings();
        var files = new TextField(settings.ActiveFiles.ToString(localization.Culture)) { X = 43, Y = 1, Width = 6 };
        var connections = new TextField(settings.Connections.ToString(localization.Culture)) { X = 43, Y = 3, Width = 6 };
        var folder = new TextField(settings.Destination) { X = 1, Y = 6, Width = Dim.Fill(1) };
        var ok = new Button(T("Save")); var cancel = new Button(T("Cancel"));
        var dialog = new Dialog(T("SettingsTitle"), 96, 21, ok, cancel);
        var rate = new TextField((settings.BandwidthLimitBytesPerSecond / 1024m).ToString("0.##########", localization.Culture)) { X = 43, Y = 11, Width = 20 };
        var rateUnit = new RadioGroup(new NStack.ustring[] { localization.Text("Rate", T("KiB")), localization.Text("Rate", T("MiB")) }) { X = 67, Y = 11 };
        var language = new RadioGroup(new NStack.ustring[] { T("English"), T("Russian") }) { X = 27, Y = 8, SelectedItem = settings.Language == "ru" ? 1 : 0 };
        dialog.Add(new Label(T("ActiveFilesLabel")) { X = 1, Y = 1 }, files, new Label(T("ConnectionsLabel")) { X = 1, Y = 3 }, connections, new Label(T("DestinationLabel")) { X = 1, Y = 5 }, folder);
        dialog.Add(new Label(T("LanguageLabel")) { X = 1, Y = 8 }, language);
        dialog.Add(new Label(T("BandwidthLabel")) { X = 1, Y = 11 }, rate, rateUnit, new Label(T("BandwidthHint")) { X = 1, Y = 14 });
        files.SetFocus();
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            try
            {
                if (!int.TryParse(files.Text.ToString(), System.Globalization.NumberStyles.Integer, localization.Culture, out var f) || !int.TryParse(connections.Text.ToString(), System.Globalization.NumberStyles.Integer, localization.Culture, out var c)) throw new ProblemException(ProblemCode.IntegerSettings);
                var updated = new Settings { ActiveFiles = f, Connections = c, Destination = folder.Text.ToString() ?? "", Language = language.SelectedItem == 1 ? "ru" : "en", BandwidthLimitBytesPerSecond = localization.ParseBandwidth(rate.Text.ToString() ?? "", rateUnit.SelectedItem == 1) };
                updated.Validate();
                Work(async () => { await engine.SetSettingsAsync(updated); Application.MainLoop.Invoke(() => { localization.SetLanguage(engine.GetSettings().Language); Refresh(); }); });
                Application.RequestStop();
            }
            catch (Exception ex) { Error(ex); }
        };
        Application.Run(dialog);
    });
}
