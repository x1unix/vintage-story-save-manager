using System.Data;
using Terminal.Gui;

namespace VintageStorySaveManager;

internal sealed class TuiApplication
{
    private readonly SaveRepository _repository;

    public TuiApplication(SaveRepository repository) => _repository = repository;

    public void Run()
    {
        var configuration = new ConfigurationStore();
        var settings = configuration.Load();
        Application.Init();
        try
        {
            var knownTheme = ThemeManager.Instance.ContainsKey(settings.Theme);
            ThemePreview.SetTheme(knownTheme ? settings.Theme : "Default");

            using var window = new Window
            {
                Title = "Vintage Story Save Manager (Esc to quit)",
                Arrangement = ViewArrangement.Fixed,
                ShadowStyle = ShadowStyle.None
            };
            var tabs = CreateTabs();
            var theme = DialogButton("_Theme (F2)");
            theme.X = Pos.AnchorEnd(14);
            theme.Y = Pos.AnchorEnd(1);
            theme.Accepting += (_, _) => Guard(() => ThemePreview.Show(window, configuration));
            window.KeyDown += (_, key) =>
            {
                if (key != Key.F2) return;
                Guard(() => ThemePreview.Show(window, configuration));
                key.Handled = true;
            };
            window.Add(tabs, new Label
            {
                Text = "[/] tabs | Tab focus | Arrows/hjkl move | Esc quit",
                Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(15)
            }, theme);
            if (!knownTheme)
            {
                MessageBox.Query("Unknown theme",
                    $"Theme '{settings.Theme}' in '{configuration.FilePath}' is not available. Using Default. Select a theme with F2 to save a replacement.", "OK");
            }
            Application.Run(window);
        }
        finally
        {
            Application.Shutdown();
        }
    }

    internal TabView CreateTabs()
    {
        var tabs = new TabView { Width = Dim.Fill(), Height = Dim.Fill(1) };
        var screens = new Dictionary<Tab, EntryScreen>();
        void AddScreen(string title, bool backups, string actionText, Action<SaveEntry> action)
        {
            var screen = new EntryScreen(
                backups ? _repository.ListBackups : _repository.ListSaves,
                actionText, action, backups, Guard);
            if (backups)
            {
                screen.AddAction("_Delete", entry =>
                {
                    if (Confirm("Delete backup", $"Permanently delete {entry.FileName}?", "Delete"))
                        _repository.DeleteBackup(entry);
                });
                screen.AddNoteAction();
            }
            var tab = new Tab { DisplayText = title, View = screen };
            screens.Add(tab, screen);
            tabs.AddTab(tab, andSelect: false);
        }

        AddScreen("Backup save", false, "_Back up", BackupWorkflow);
        AddScreen("Restore backup", true, "_Restore", RestoreWorkflow);
        AddScreen("Rename world", false, "_Rename", RenameWorkflow);
        AddScreen("Repair save", false, "_Select", RepairWorkflow);
        tabs.SelectedTabChanged += (_, args) => Guard(() => screens[args.NewTab].Refresh());
        tabs.KeyDown += (_, key) =>
        {
            if (key == new Key('[') || key == new Key(']'))
            {
                tabs.SwitchTabBy(key == new Key('[') ? -1 : 1);
                key.Handled = true;
            }
        };
        return tabs;
    }

    private void BackupWorkflow(SaveEntry selected)
    {
        EnsureReadable(selected);
        var note = PromptBackupNote(selected);
        if (note is null)
        {
            return;
        }

        var result = _repository.Backup(selected, note);
        MessageBox.Query("Backup complete", $"Created:\n{result.Path}", "OK");
    }

    private void RestoreWorkflow(SaveEntry backup)
    {
        EnsureReadable(backup);
        var saves = _repository.ListSaves();
        var destinationOptions = saves
            .Select(save => new DestinationOption(save))
            .Append(new DestinationOption(null))
            .ToArray();
        var destination = SelectDestination(destinationOptions);
        if (destination is null)
        {
            return;
        }

        RestoreRequest request;
        if (destination.Save is not null)
        {
            var choice = MessageBox.Query(
                "Replace existing save",
                $"Replace {destination.Save.FileName} with {backup.FileName}?\n\nCreate a safety backup first?",
                "Cancel",
                "Replace only",
                "Back up & replace");
            if (choice == 0)
            {
                return;
            }

            request = new RestoreRequest(
                backup,
                destination.Save.Path,
                destination.Save.Location,
                WorldName: null,
                BackupExisting: choice == 2);
        }
        else
        {
            var values = PromptNewSave(backup);
            if (values is null)
            {
                return;
            }

            var destinationPath = _repository.NewSavePath(backup.Location, values.Value.FileName);
            request = new RestoreRequest(
                backup,
                destinationPath,
                backup.Location,
                values.Value.WorldName,
                BackupExisting: false);
        }

        var restored = _repository.Restore(request);
        MessageBox.Query("Restore complete", $"Restored:\n{restored.Path}", "OK");
    }

    private void RenameWorkflow(SaveEntry selected)
    {
        EnsureReadable(selected);
        var newName = PromptText("Rename world", "World name:", selected.WorldName, "Rename");
        if (newName is null)
        {
            return;
        }

        SaveRepository.ValidateWorldName(newName);
        if (!Confirm("Rename world", $"Rename '{selected.WorldName}' to '{newName}'?", "Rename"))
        {
            return;
        }

        var result = _repository.RenameWorld(selected, newName);
        MessageBox.Query("Rename complete", $"World renamed to '{result.WorldName}'.", "OK");
    }

    private void RepairWorkflow(SaveEntry source)
    {
        var mode = MessageBox.Query("Repair save",
            "Close this world in Vintage Story before continuing.\nScan for damaged world records, or choose a backup to use for repair.\nRepairs are written to a new save file.",
            "Cancel", "Scan only", "Choose backup");
        if (mode < 1)
        {
            return;
        }

        var backup = mode == 2 ? SelectEntry("Select backup of the same world", _repository.ListBackups(), "_Select") : null;
        if (mode == 2 && backup is null)
        {
            return;
        }

        using var session = RunRepairTask("Scanning save", token => _repository.PrepareRepair(source, backup, token));
        var report = session.Report;
        if (!report.CanRepair)
        {
            MessageBox.Query("Scan complete", report.Summary + "\n\n" +
                (report.DamagedRecords == 0 ? "No damaged records detected." : "Choose a compatible backup with valid replacements for every damaged record.") +
                "\nNo save was created.\nThese checks cannot detect missing rows or all damage inside mod data.", "OK");
            return;
        }

        var filename = PromptText("Save repaired copy", "New filename (without .vcdbs):",
            Path.GetFileNameWithoutExtension(source.FileName) + "-repaired", "Continue");
        if (filename is null)
        {
            return;
        }

        var destination = _repository.NewSavePath(source.Location, filename);
        if (!Confirm("Create repaired save", report.Summary +
            $"\n\nBackup: {backup!.FileName} ({backup.Played})\nSource: {source.FileName} ({source.Played})\nNew file: {Path.GetFileName(destination)}\n\n" + RepairReport.Limitations,
            "Create copy"))
        {
            return;
        }

        var result = RunRepairTask("Writing and validating repair", token => _repository.CompleteRepair(session, filename, token));
        MessageBox.Query("Repair complete", $"Created:\n{result.Path}\n\nRestored {report.DamagedRecords} records.\nWorld name: {result.WorldName}\nTest this copy in Vintage Story.", "OK");
    }

    private static T RunRepairTask<T>(string title, Func<CancellationToken, T> work)
    {
        using var cancellation = new CancellationTokenSource();
        using var dialog = FormDialog(title, 70, 8);
        var status = new Label { Text = "Checking database and game data. This may take a while.", X = 1, Y = 1 };
        dialog.Add(status);
        var cancel = DialogButton("_Cancel");
        dialog.AddButton(cancel);
        void Cancel()
        {
            cancellation.Cancel();
            status.Text = "Cancelling after the current database operation...";
        }

        cancel.Accepting += (_, _) => Cancel();
        var completed = false;
        dialog.Closing += (_, args) =>
        {
            if (!completed)
            {
                args.Cancel = true;
                Cancel();
            }
        };
        Task<T>? task = null;
        dialog.Ready += (_, _) =>
        {
            task = Task.Run(() => work(cancellation.Token));
            _ = task.ContinueWith(_ => Application.Invoke(() =>
            {
                completed = true;
                Application.RequestStop(dialog);
            }), TaskScheduler.Default);
        };
        Application.Run(dialog);
        return (task ?? throw new UserFacingException("The repair task did not start.")).GetAwaiter().GetResult();
    }

    private SaveEntry? SelectEntry(string title, IReadOnlyList<SaveEntry> entries, string actionText)
    {
        if (entries.Count == 0)
        {
            MessageBox.Query("Nothing found", "No matching save files were found.", "OK");
            return null;
        }

        SaveEntry? selected = null;
        using var dialog = SelectionDialog(title);
        var table = EntryTable(entries);
        dialog.Add(table);
        FocusTableWhenReady(dialog, table);
        var cancel = DialogButton("_Cancel");
        var accept = DialogButton(actionText, isDefault: true);
        dialog.AddButton(cancel);
        if (entries.Any(entry => entry.IsBackup))
        {
            dialog.AddButton(NoteButton(entries, table));
        }
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            selected = Selected(entries, table);
            Application.RequestStop(dialog);
        };
        table.CellActivated += (_, _) =>
        {
            selected = Selected(entries, table);
            Application.RequestStop(dialog);
        };
        Application.Run(dialog);
        return selected;
    }

    private DestinationOption? SelectDestination(IReadOnlyList<DestinationOption> options)
    {
        DestinationOption? selected = null;
        using var dialog = SelectionDialog("Select restore destination");
        var table = DestinationTable(options);
        dialog.Add(table);
        FocusTableWhenReady(dialog, table);
        var cancel = DialogButton("_Cancel");
        var accept = DialogButton("_Select", isDefault: true);
        dialog.AddButton(cancel);
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            selected = options[table.SelectedRow];
            Application.RequestStop(dialog);
        };
        table.CellActivated += (_, _) =>
        {
            selected = options[table.SelectedRow];
            Application.RequestStop(dialog);
        };
        Application.Run(dialog);
        return selected;
    }

    private (string FileName, string WorldName)? PromptNewSave(SaveEntry backup)
    {
        (string FileName, string WorldName)? result = null;
        using var dialog = FormDialog("Restore as new save", 70, 11);
        var fileLabel = new Label { Text = "Filename (without .vcdbs):", X = 1, Y = 1 };
        var file = new TextField
        {
            Text = Path.GetFileNameWithoutExtension(StripBackupTimestamp(backup.FileName)),
            X = 1,
            Y = 2,
            Width = Dim.Fill(1)
        };
        var worldLabel = new Label { Text = "World name:", X = 1, Y = 4 };
        var world = new TextField { Text = backup.WorldName, X = 1, Y = 5, Width = Dim.Fill(1) };
        var cancel = DialogButton("_Cancel");
        var restore = DialogButton("_Restore", isDefault: true);
        dialog.AddButton(cancel);
        dialog.AddButton(restore);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        restore.Accepting += (_, _) =>
        {
            try
            {
                var fileName = SaveRepository.ValidateFileName(file.Text);
                SaveRepository.ValidateWorldName(world.Text);
                result = (fileName, world.Text.Trim());
                Application.RequestStop(dialog);
            }
            catch (UserFacingException exception)
            {
                MessageBox.ErrorQuery("Invalid value", exception.Message, "OK");
            }

        };
        dialog.Add(fileLabel, file, worldLabel, world);
        dialog.Ready += (_, _) => file.SetFocus();
        Application.Run(dialog);
        return result;
    }

    private static string? PromptBackupNote(SaveEntry source)
    {
        string? result = null;
        using var dialog = FormDialog("Create backup", 70, 11);
        var summary = new Label { Text = $"Back up {source.FileName}\nWorld: {source.WorldName}", X = 1, Y = 1 };
        var label = new Label { Text = "Note (optional):", X = 1, Y = 4 };
        var field = new TextField { X = 1, Y = 5, Width = Dim.Fill(1) };
        var cancel = DialogButton("_Cancel");
        var accept = DialogButton("_Back up", isDefault: true);
        dialog.AddButton(cancel);
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            result = field.Text;
            Application.RequestStop(dialog);
        };
        dialog.Add(summary, label, field);
        dialog.Ready += (_, _) => field.SetFocus();
        Application.Run(dialog);
        return result;
    }

    internal static Button NoteButton(IReadOnlyList<SaveEntry> entries, TableView table)
    {
        var button = DialogButton("View _note");
        button.Accepting += (_, _) =>
        {
            var entry = Selected(entries, table);
            if (entry is null) return;
            using var dialog = SelectionDialog($"Note: {entry.FileName}");
            var text = new TextView
            {
                Text = BackupNotes.Display(entry.NoteError ?? entry.Note ?? "No note.", multiline: true),
                ReadOnly = true,
                WordWrap = true,
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = Dim.Fill(2)
            };
            var close = DialogButton("_Close", isDefault: true);
            close.Accepting += (_, _) => Application.RequestStop(dialog);
            dialog.Add(text);
            dialog.AddButton(close);
            dialog.Ready += (_, _) => text.SetFocus();
            Application.Run(dialog);
        };
        return button;
    }

    private string? PromptText(string title, string labelText, string initial, string actionText)
    {
        string? result = null;
        using var dialog = FormDialog(title, 70, 8);
        var label = new Label { Text = labelText, X = 1, Y = 1 };
        var field = new TextField { Text = initial, X = 1, Y = 2, Width = Dim.Fill(1) };
        var cancel = DialogButton("_Cancel");
        var accept = DialogButton($"_{actionText}", isDefault: true);
        dialog.AddButton(cancel);
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            result = field.Text;
            Application.RequestStop(dialog);
        };
        dialog.Add(label, field);
        Application.Run(dialog);
        return result;
    }

    internal static TableView EntryTable(IEnumerable<SaveEntry> entries)
    {
        var items = entries.ToArray();
        var backups = items.Any(entry => entry.IsBackup);
        var table = CreateTable(EntryData(items, backups));
        if (backups)
        {
            ConfigureBackupColumns(table, ((DataTableSource)table.Table).DataTable);
        }
        return table;
    }

    internal static void UpdateEntryTable(TableView table, IReadOnlyList<SaveEntry> entries, bool backups)
    {
        var data = EntryData(entries, backups);
        table.Table = new DataTableSource(data);
        if (backups) ConfigureBackupColumns(table, data);
        table.Update();
    }

    private static DataTable EntryData(IEnumerable<SaveEntry> items, bool backups)
    {
        var data = SaveTableSchema(includeNote: backups);
        foreach (var entry in items)
        {
            var values = new List<object> { entry.FileName, entry.WorldName, entry.Played, entry.Created };
            if (backups) values.Add(entry.NoteDisplay);
            values.Add(entry.Location.Name);
            data.Rows.Add(values.ToArray());
        }

        return data;
    }

    private static void ConfigureBackupColumns(TableView table, DataTable data)
    {
        const int noteColumn = 4;
        var fixedWidth = 0;
        for (var column = 0; column < data.Columns.Count; column++)
        {
            if (column == noteColumn) continue;
            var width = Math.Min(100, Math.Max(
                data.Columns[column].ColumnName.GetColumns(),
                data.Rows.Cast<DataRow>().Select(row => (row[column].ToString() ?? string.Empty).GetColumns()).DefaultIfEmpty(0).Max()));
            var style = table.Style.GetOrCreateColumnStyle(column);
            style.MinWidth = style.MaxWidth = width;
            fixedWidth += width;
        }

        table.Style.ExpandLastColumn = false;
        table.MaxCellWidth = int.MaxValue;
        var noteStyle = table.Style.GetOrCreateColumnStyle(noteColumn);
        void ResizeNote()
        {
            // Each column has a separator, plus the leading table edge.
            var width = Math.Max("Note".Length, table.Viewport.Width - fixedWidth - data.Columns.Count - 1);
            noteStyle.MinWidth = noteStyle.MaxWidth = width;
        }
        ResizeNote();
    }

    private static TableView DestinationTable(IEnumerable<DestinationOption> options)
    {
        var data = SaveTableSchema();
        foreach (var option in options)
        {
            var entry = option.Save;
            data.Rows.Add(
                entry?.FileName ?? "<Save as new file>",
                entry?.WorldName ?? string.Empty,
                entry?.Played ?? string.Empty,
                entry?.Created ?? string.Empty,
                entry?.Location.Name ?? string.Empty);
        }

        return CreateTable(data);
    }

    private static DataTable SaveTableSchema(bool includeNote = false)
    {
        var data = new DataTable();
        data.Columns.Add("Filename");
        data.Columns.Add("World");
        data.Columns.Add("Played");
        data.Columns.Add("Created");
        if (includeNote) data.Columns.Add("Note");
        data.Columns.Add("Installation");
        return data;
    }

    private static TableView CreateTable(DataTable data)
    {
        var table = new TableView(new DataTableSource(data))
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2),
            BorderStyle = LineStyle.Single,
            ShadowStyle = ShadowStyle.None,
            FullRowSelect = true,
            MultiSelect = false,
            CollectionNavigator = null
        };
        table.Style.ShowHeaders = true;
        table.Style.AlwaysShowHeaders = true;
        table.Style.ShowVerticalHeaderLines = true;
        table.Style.ShowVerticalCellLines = true;
        table.ViewportChanged += (_, _) =>
        {
            var current = ((DataTableSource)table.Table).DataTable;
            if (current.Columns.Contains("Note")) ConfigureBackupColumns(table, current);
        };
        AddVimTableNavigation(table);
        return table;
    }

    private static Dialog SelectionDialog(string title)
    {
        var dialog = new Dialog
        {
            Title = title,
            Width = Dim.Percent(95),
            Height = Dim.Percent(85),
            Arrangement = ViewArrangement.Fixed,
            ShadowStyle = ShadowStyle.None
        };
        AddVimFocusBindings(dialog);
        return dialog;
    }

    private static Dialog FormDialog(string title, int width, int height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        Arrangement = ViewArrangement.Fixed,
        ShadowStyle = ShadowStyle.None
    };

    internal static Button DialogButton(string text, bool isDefault = false)
    {
        var button = new Button
        {
            Text = text,
            IsDefault = isDefault,
            ShadowStyle = ShadowStyle.None
        };
        // Each button owns its action; do not forward acceptance to the default button.
        button.Accepting += (_, args) => args.Cancel = true;
        return button;
    }

    private static void FocusTableWhenReady(Dialog dialog, TableView table) =>
        dialog.Ready += (_, _) => table.SetFocus();

    private static void AddVimTableNavigation(TableView table)
    {
        table.KeyBindings.Add(Key.H, table, Command.Left);
        table.KeyBindings.Add(Key.J, table, Command.Down);
        table.KeyBindings.Add(Key.K, table, Command.Up);
        table.KeyBindings.Add(Key.L, table, Command.Right);

        table.KeyDown += (_, key) =>
        {
            var offset = key switch
            {
                _ when key == Key.H => (-1, 0),
                _ when key == Key.J => (0, 1),
                _ when key == Key.K => (0, -1),
                _ when key == Key.L => (1, 0),
                _ => ((int X, int Y)?)null
            };

            if (offset is not null)
            {
                table.ChangeSelectionByOffset(offset.Value.X, offset.Value.Y, false);
                table.EnsureSelectedCellIsVisible();
                table.SetNeedsDraw();
                key.Handled = true;
            }
        };
    }

    private static void AddVimFocusBindings(View view)
    {
        view.KeyDown += (_, key) =>
        {
            NavigationDirection? direction = key switch
            {
                _ when key == Key.H || key == Key.K => NavigationDirection.Backward,
                _ when key == Key.J || key == Key.L => NavigationDirection.Forward,
                _ => null
            };

            if (direction is not null)
            {
                key.Handled = view.AdvanceFocus(direction.Value, TabBehavior.TabStop);
            }
        };
    }

    private static SaveEntry? Selected(IReadOnlyList<SaveEntry> entries, TableView table) =>
        entries.Count == 0 ? null : entries[table.SelectedRow];

    private bool Confirm(string title, string message, string action) =>
        MessageBox.Query(title, message, "Cancel", action) == 1;

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            MessageBox.Query("Cancelled", "Repair cancelled. No repaired save was published.", "OK");
        }
        catch (Exception exception)
        {
            MessageBox.ErrorQuery("Error", exception.Message, "OK");
        }
    }

    private static void EnsureReadable(SaveEntry entry)
    {
        if (entry.Error is not null)
        {
            throw new UserFacingException($"Cannot use '{entry.FileName}': {entry.Error}");
        }
    }

    private static string StripBackupTimestamp(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var modern = System.Text.RegularExpressions.Regex.Match(name, @"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-(.+)$");
        if (modern.Success)
        {
            return modern.Groups[1].Value;
        }

        var legacy = System.Text.RegularExpressions.Regex.Match(name, @"^\d{2}-\d{2}-\d{2}_\d{2}:\d{2}:\d{2}-(.+)$");
        return legacy.Success ? legacy.Groups[1].Value : name;
    }

    private sealed record DestinationOption(SaveEntry? Save);
}
