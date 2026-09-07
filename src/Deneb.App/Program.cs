using System.Data;
using Deneb.Core;
using Terminal.Gui;
using Deneb.App;

var stateIndex = Array.IndexOf(args, "--state-dir");
var stateDirectory = stateIndex >= 0 && stateIndex + 1 < args.Length ? args[stateIndex + 1] : null;
var localization = new Localization(StateStore.ReadLanguage(stateDirectory));
if (args.Contains("--version")) { Console.WriteLine("Deneb 1.2.0"); return; }
if (stateIndex >= 0 && stateDirectory == null) { Console.Error.WriteLine(localization.Text("MissingStatePath")); Environment.ExitCode = 2; return; }
if (args.Contains("--help")) { Console.WriteLine(localization.Text("CliHelp")); return; }
if (Console.IsInputRedirected) { Console.Error.WriteLine(localization.Text("InteractiveRequired")); Environment.ExitCode = 2; return; }
try
{
    await using var engine = new DownloadEngine(stateDirectory);
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

sealed class DenebUi(DownloadEngine engine, Localization localization)
{
    private readonly TableView table = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly Label summary = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label keys = new()
    { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private IReadOnlyList<Snapshot> rows = [];
    private bool modal;
    private bool busy;
    private readonly HashSet<Guid> marked = [];
    private BatchResult? notice;
    private Window? window;
    private string T(string key, params object?[] values) => localization.Text(key, values);
    private Guid[] Targets => marked.Count > 0 ? marked.ToArray() : Selected is { } id ? [id] : [];
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
            switch (key)
            {
                case Key.a: case Key.A: Add(); break;
                case Key.Space: Toggle(); break;
                case Key.u: case Key.U: Replace(); break;
                case Key.DeleteChar: case Key.Delete: Remove(); break;
                case Key.F2: Settings(); break;
                case Key.InsertChar: if (Selected is { } mark && !marked.Remove(mark)) marked.Add(mark); Refresh(); break;
                case Key.F3: if (Selected is { } up) engine.Move(up, -1); Refresh(); break;
                case Key.F4: if (Selected is { } down) engine.Move(down, 1); Refresh(); break;
                case Key.F5: if (Selected is { } next) engine.DownloadNext(next); Refresh(); break;
                case Key.F6: if (engine.GloballyPaused) engine.ResumeAll(); else Work(engine.PauseAllAsync); break;
                case Key.o: case Key.O: FileAction("open"); break;
                case Key.F7: FileAction("reveal"); break;
                case Key.F8: FileAction("copy"); break;
                case Key.F9: Dialog(() => { if (MessageBox.Query(T("ClearTitle"), T("ClearPrompt"), T("Cancel"), T("Clear")) == 1) Report(engine.ClearCompleted()); }); break;
                case Key.F1: Dialog(() => MessageBox.Query(T("HelpTitle"), T("Help"), T("Close"))); break;
                case Key.q: case Key.Q: case Key.CtrlMask | Key.c: Application.RequestStop(); break;
                default: return;
            }
            e.Handled = true;
        };
        Console.CancelKeyPress += OnCancel;
        var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => { Refresh(); return true; });
        Refresh();
        try { Application.Run(); }
        finally { Application.MainLoop.RemoveTimeout(timer); Console.CancelKeyPress -= OnCancel; }
    }
    private static void OnCancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; Application.MainLoop.Invoke(() => Application.RequestStop()); }
    private Guid? Selected => table.SelectedRow >= 0 && table.SelectedRow < rows.Count ? rows[table.SelectedRow].Id : null;
    private void Refresh()
    {
        if (modal) return;
        var selected = Selected;
        var rowOffset = table.RowOffset; var columnOffset = table.ColumnOffset;
        keys.Text = T("Keys"); if (window != null) window.Title = T("WindowTitle");
        rows = engine.Snapshots();
        marked.IntersectWith(rows.Select(r => r.Id));
        var data = new DataTable();
        foreach (var col in new[] { T("File"), T("State"), "%", T("Volume"), T("Speed"), T("Remaining"), T("Connections") }) data.Columns.Add(col);
        foreach (var s in rows)
        {
            var percent = s.Total is > 0 ? localization.Number(100.0 * s.Bytes / s.Total.Value) : s.State == DownloadState.Completed ? "100" : "—";
            var eta = Duration(s.Eta);
            data.Rows.Add((marked.Contains(s.Id) ? "✓ " : "") + localization.Name(s), localization.Phase(s.Phase), percent, $"{Size(s.Bytes)} / {(s.Total.HasValue ? Size(s.Total.Value) : "?")}", localization.Rate(s.Speed), eta, s.Connections.ToString(localization.Culture));
        }
        table.Table = data;
        table.Style.ColumnStyles.Clear();
        table.Style.ColumnStyles[data.Columns[0]] = new TableView.ColumnStyle { MinWidth = 24, MaxWidth = 38 };
        var index = selected.HasValue ? rows.ToList().FindIndex(r => r.Id == selected) : 0;
        if (rows.Count > 0) table.SetSelection(0, Math.Max(0, index), false);
        table.RowOffset = rowOffset; table.ColumnOffset = columnOffset;
        summary.Text = engine.PersistenceError != null ? localization.Error(engine.PersistenceError) : busy ? T("Busy") : T("Summary", engine.GloballyPaused ? T("GlobalBanner") : "", rows.Count, marked.Count, localization.Rate(rows.Sum(r => r.Speed)), notice == null ? "" : T("Batch", notice.Processed.Count, notice.Skipped.Count, notice.Failed.Count));
        table.SetNeedsDisplay();
    }
    private string Size(long n) => localization.Size(n);
    private void Dialog(Action action) { modal = true; try { action(); } finally { modal = false; Refresh(); } }
    private void Error(Exception ex) => MessageBox.ErrorQuery(T("ErrorTitle"), localization.Error(ex), T("Ok"));
    private void Work(Func<Task> action)
    {
        busy = true;
        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try { await action(); } catch (Exception ex) { error = ex; }
            Application.MainLoop.Invoke(() => { busy = false; if (error != null) Dialog(() => Error(error)); Refresh(); });
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
                foreach (var item in parsed) engine.Add(item, dest, string.IsNullOrWhiteSpace(custom) ? null : custom);
                Application.RequestStop();
            }
            catch (Exception ex) { Error(ex); }
        };
        Application.Run(dialog);
    });
    private void Toggle()
    {
        var ids = Targets;
        if (rows.Any(s => ids.Contains(s.Id) && s.State is DownloadState.Downloading or DownloadState.Queued)) Work(async () => Report(await engine.PauseManyAsync(ids)));
        else Report(engine.ResumeMany(ids));
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
                var detailText = T("Details", localization.Name(s), localization.Phase(s.Phase), Size(s.Bytes), s.Total.HasValue ? Size(s.Total.Value) : T("Unknown"), localization.Rate(s.Speed), Duration(s.Eta), s.Connections, s.Source, s.Target, s.Retry, Duration(s.RetryIn), localization.Error(s.Error));
                if (info.Text.ToString() != detailText) { info.Text = detailText; info.CursorPosition = cursor; info.TopRow = top; info.LeftColumn = left; }
                restart.Enabled = s.State == DownloadState.NeedsDecision; open.Enabled = reveal.Enabled = copy.Enabled = s.State == DownloadState.Completed;
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
        Dialog(() =>
        {
            var field = new TextField(engine.GetUrl(id)) { X = 1, Y = 1, Width = Dim.Fill(1) };
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
        var files = new TextField(settings.ActiveFiles.ToString(localization.Culture)) { X = 26, Y = 1, Width = 6 };
        var connections = new TextField(settings.Connections.ToString(localization.Culture)) { X = 26, Y = 3, Width = 6 };
        var folder = new TextField(settings.Destination) { X = 1, Y = 6, Width = Dim.Fill(1) };
        var ok = new Button(T("Save")); var cancel = new Button(T("Cancel"));
        var dialog = new Dialog(T("SettingsTitle"), 90, 16, ok, cancel);
        var language = new RadioGroup(new NStack.ustring[] { T("English"), T("Russian") }) { X = 27, Y = 8, SelectedItem = settings.Language == "ru" ? 1 : 0 };
        dialog.Add(new Label(T("ActiveFilesLabel")) { X = 1, Y = 1 }, files, new Label(T("ConnectionsLabel")) { X = 1, Y = 3 }, connections, new Label(T("DestinationLabel")) { X = 1, Y = 5 }, folder);
        dialog.Add(new Label(T("LanguageLabel")) { X = 1, Y = 8 }, language);
        files.SetFocus();
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            try
            {
                if (!int.TryParse(files.Text.ToString(), System.Globalization.NumberStyles.Integer, localization.Culture, out var f) || !int.TryParse(connections.Text.ToString(), System.Globalization.NumberStyles.Integer, localization.Culture, out var c)) throw new ProblemException(ProblemCode.IntegerSettings);
                engine.SetSettings(new() { ActiveFiles = f, Connections = c, Destination = folder.Text.ToString() ?? "", Language = language.SelectedItem == 1 ? "ru" : "en" });
                localization.SetLanguage(engine.GetSettings().Language); Application.RequestStop();
            }
            catch (Exception ex) { Error(ex); }
        };
        Application.Run(dialog);
    });
}
