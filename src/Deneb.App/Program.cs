using System.Data;
using Deneb.Core;
using Terminal.Gui;
using Deneb.App;

if (args.Contains("--version")) { Console.WriteLine("Deneb 1.1.0"); return; }
if (args.Contains("--help")) { Console.WriteLine("deneb — интерактивный менеджер загрузок. --state-dir PATH: отдельное хранилище; --version; --help"); return; }
var stateIndex = Array.IndexOf(args, "--state-dir");
if (stateIndex >= 0 && stateIndex + 1 >= args.Length) { Console.Error.WriteLine("Укажите путь после --state-dir."); Environment.ExitCode = 2; return; }
if (Console.IsInputRedirected) { Console.Error.WriteLine("Запустите Deneb в интерактивном терминале."); Environment.ExitCode = 2; return; }
try
{
    await using var engine = new DownloadEngine(stateIndex >= 0 ? args[stateIndex + 1] : null);
    Application.Init();
    try { new DenebUi(engine).Run(); }
    finally { Application.Shutdown(); }
    Console.WriteLine("Сохраняю прогресс и останавливаю загрузки…");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex is IOException or ArgumentException or InvalidDataException ? ex.Message : $"Ошибка запуска Deneb: {ex.GetType().Name}");
    Environment.ExitCode = 1;
}

sealed class DenebUi(DownloadEngine engine)
{
    private readonly TableView table = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly Label summary = new() { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label keys = new("A добавить · Space пауза · Enter детали · F6 общая пауза · F1 помощь · Q выход")
    { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private IReadOnlyList<Snapshot> rows = [];
    private bool modal;
    private bool busy;
    private readonly HashSet<Guid> marked = [];
    private string notice = "";
    private Guid[] Targets => marked.Count > 0 ? marked.ToArray() : Selected is { } id ? [id] : [];
    private void Report(BatchResult result) => notice = $"Обработано: {result.Processed.Count}, пропущено: {result.Skipped.Count}, ошибок: {result.Failed.Count}";

    public void Run()
    {
        var window = new Window("Deneb — загрузки") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
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
                case Key.F9: Dialog(() => { if (MessageBox.Query("Очистить завершённые", "Убрать все завершённые записи? Файлы останутся на диске.", "Отмена", "Очистить") == 1) Report(engine.ClearCompleted()); }); break;
                case Key.F1: Dialog(() => MessageBox.Query("Клавиши Deneb", "A — добавить; U — заменить URL; F2 — настройки\nInsert — отметить строку; Space — пауза/продолжение выбранных\nF3/F4 — выше/ниже; F5 — скачать следующей (без снятия паузы)\nF6 — общая пауза/продолжение\nEnter — подробности; Delete — убрать выбранные\nO — открыть; F7 — Finder; F8 — скопировать путь\nF9 — очистить завершённые; Q/Ctrl+C — выход", "Закрыть")); break;
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
        rows = engine.Snapshots();
        marked.IntersectWith(rows.Select(r => r.Id));
        var data = new DataTable();
        foreach (var col in new[] { "Файл", "Состояние", "%", "Скачано / Всего", "Скорость", "Осталось", "Потоки" }) data.Columns.Add(col);
        foreach (var s in rows)
        {
            var percent = s.Total is > 0 ? $"{100.0 * s.Bytes / s.Total:0.0}" : s.State == DownloadState.Completed ? "100" : "—";
            var eta = Duration(s.Eta);
            data.Rows.Add((marked.Contains(s.Id) ? "✓ " : "") + s.Name, s.Phase, percent, $"{Size(s.Bytes)} / {(s.Total.HasValue ? Size(s.Total.Value) : "?")}", $"{Size((long)s.Speed)}/с", eta, s.Connections);
        }
        table.Table = data;
        table.Style.ColumnStyles.Clear();
        table.Style.ColumnStyles[data.Columns[0]] = new TableView.ColumnStyle { MinWidth = 24, MaxWidth = 38 };
        var index = selected.HasValue ? rows.ToList().FindIndex(r => r.Id == selected) : 0;
        if (rows.Count > 0) table.SetSelection(0, Math.Max(0, index), false);
        summary.Text = engine.PersistenceError ?? (busy ? "Выполняется операция…" : $"{(engine.GloballyPaused ? "ОБЩАЯ ПАУЗА · " : "")}Задач: {rows.Count} · Отмечено: {marked.Count} · {Size((long)rows.Sum(r => r.Speed))}/с · {notice}");
        table.SetNeedsDisplay();
    }
    private static string Size(long n) => n >= 1073741824 ? $"{n / 1073741824d:0.0} ГиБ" : n >= 1048576 ? $"{n / 1048576d:0.0} МиБ" : $"{n / 1024d:0.0} КиБ";
    private void Dialog(Action action) { modal = true; try { action(); } finally { modal = false; Refresh(); } }
    private void Error(Exception ex) => MessageBox.ErrorQuery("Ошибка", ex is ArgumentException or IOException ? ex.Message : "Операция не выполнена: " + ex.GetType().Name, "OK");
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
        var ok = new Button("Проверить"); var cancel = new Button("Отмена");
        var dialog = new Dialog("Ссылки / строки Android — по одной на строку", 95, 22, ok, cancel);
        dialog.Add(input, new Label("Папка:") { X = 1, Y = Pos.AnchorEnd(5) }, folder, new Label("Имя*:") { X = 1, Y = Pos.AnchorEnd(3) }, name);
        input.SetFocus();
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            try
            {
                var parsed = InputParser.Parse(input.Text.ToString() ?? "");
                var custom = name.Text.ToString();
                if (parsed.Count > 1 && !string.IsNullOrWhiteSpace(custom)) throw new ArgumentException("Своё имя можно указать только для одной ссылки.");
                var dest = folder.Text.ToString() ?? "";
                if (!Path.IsPathFullyQualified(dest)) throw new ArgumentException("Укажите абсолютный путь к папке.");
                var preview = string.Join("\n\n", parsed.Select(p => $"{(string.IsNullOrWhiteSpace(custom) ? p.Name ?? "Автоматическое имя" : custom)}\n{p.Url}"));
                if (MessageBox.Query("Добавить в очередь?", preview, "Добавить", "Назад") != 0) return;
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
    private static string Duration(TimeSpan? time) => time is { } t ? (t.Days > 0 ? $"{t.Days} д " : "") + t.ToString(@"hh\:mm\:ss") : "—";
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
            var close = new Button("Закрыть"); var restart = new Button("Начать заново");
            var open = new Button("Открыть файл"); var reveal = new Button("Показать в Finder"); var copy = new Button("Скопировать путь");
            var dialog = new Dialog("Подробности загрузки", 110, 25, close, restart, open, reveal, copy);
            var info = new Label { X = 1, Y = 1, Width = Dim.Fill(1), Height = 8 };
            var segments = new TableView { X = 1, Y = 10, Width = Dim.Fill(1), Height = Dim.Fill(2), FullRowSelect = true };
            dialog.Add(info, segments); close.Clicked += () => Application.RequestStop();
            restart.Clicked += () => { if (MessageBox.Query("Начать заново?", "Старые части будут сохранены отдельно.", "Отмена", "Начать") == 1) { Work(() => engine.RestartAsync(id)); Application.RequestStop(); } };
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
                info.Text = $"{s.Name}\n{s.Phase} · {Size(s.Bytes)} / {(s.Total.HasValue ? Size(s.Total.Value) : "?")}\nСкорость: {Size((long)s.Speed)}/с · Осталось: {Duration(s.Eta)} · Соединений: {s.Connections}\nИсточник: {s.Source}\nФайл: {s.Target}\nПроверка сервера: повтор {s.Retry}/10 · ожидание {Duration(s.RetryIn)}\n{s.Error}";
                restart.Enabled = s.State == DownloadState.NeedsDecision; open.Enabled = reveal.Enabled = copy.Enabled = s.State == DownloadState.Completed;
                var data = new DataTable(); foreach (var c in new[] { "№", "Диапазон", "Скачано / Всего", "%", "Состояние", "Повтор", "Через" }) data.Columns.Add(c);
                foreach (var p in s.Segments) { var total = p.End - p.Start + 1; data.Rows.Add(p.Number, $"{p.Start}–{p.End}", $"{Size(p.Bytes)} / {(total.HasValue ? Size(total.Value) : "?")}", total > 0 ? $"{100d * p.Bytes / total:0.0}" : "—", p.Phase, $"{p.Retry}/10", Duration(p.RetryIn)); }
                var row = segments.SelectedRow; var column = segments.SelectedColumn; var offset = segments.RowOffset; var horizontal = segments.ColumnOffset; segments.Table = data;
                if (data.Rows.Count > 0) segments.SetSelection(Math.Max(0, column), Math.Clamp(row, 0, data.Rows.Count - 1), false); segments.RowOffset = offset; segments.ColumnOffset = horizontal;
            }
            Update(); var timer = Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(250), _ => { Update(); return true; });
            try { Application.Run(dialog); } finally { Application.MainLoop.RemoveTimeout(timer); }
        });
    }
    private void Replace()
    {
        if (Selected is not { } id || rows.First(r => r.Id == id).State == DownloadState.Completed) return;
        Dialog(() =>
        {
            var field = new TextField(engine.GetUrl(id)) { X = 1, Y = 1, Width = Dim.Fill(1) };
            var ok = new Button("Заменить"); var cancel = new Button("Отмена");
            var dialog = new Dialog("Новый URL того же файла", 90, 7, ok, cancel);
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
            var choice = MessageBox.Query("Убрать задачи", $"Задач: {ids.Length}. Готовые файлы останутся на диске.", "Отмена", "Сохранить части", "Удалить части");
            if (choice == 1) Work(async () => Report(await engine.RemoveManyAsync(ids)));
            if (choice == 2 && MessageBox.Query("Удаление", "Безвозвратно удалить незавершённые части этой задачи?", "Отмена", "Удалить") == 1)
                Work(async () => Report(await engine.RemoveManyAsync(ids, true)));
        });
    }
    private void Settings() => Dialog(() =>
    {
        var settings = engine.GetSettings();
        var files = new TextField(settings.ActiveFiles.ToString()) { X = 26, Y = 1, Width = 6 };
        var connections = new TextField(settings.Connections.ToString()) { X = 26, Y = 3, Width = 6 };
        var folder = new TextField(settings.Destination) { X = 1, Y = 6, Width = Dim.Fill(1) };
        var ok = new Button("Сохранить"); var cancel = new Button("Отмена");
        var dialog = new Dialog("Настройки (потоки — для новых задач)", 85, 12, ok, cancel);
        dialog.Add(new Label("Одновременных файлов:") { X = 1, Y = 1 }, files, new Label("Соединений на файл:") { X = 1, Y = 3 }, connections, new Label("Папка загрузок:") { X = 1, Y = 5 }, folder);
        files.SetFocus();
        cancel.Clicked += () => Application.RequestStop();
        ok.Clicked += () =>
        {
            try
            {
                if (!int.TryParse(files.Text.ToString(), out var f) || !int.TryParse(connections.Text.ToString(), out var c)) throw new ArgumentException("Введите целые числа от 1 до 16.");
                engine.SetSettings(new() { ActiveFiles = f, Connections = c, Destination = folder.Text.ToString() ?? "" }); Application.RequestStop();
            }
            catch (Exception ex) { Error(ex); }
        };
        Application.Run(dialog);
    });
}
