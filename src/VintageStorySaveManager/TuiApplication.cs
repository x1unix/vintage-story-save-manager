using System.Data;
using Terminal.Gui;

namespace VintageStorySaveManager;

internal sealed class TuiApplication
{
    private readonly SaveRepository _repository;

    public TuiApplication(SaveRepository repository) => _repository = repository;

    public void Run()
    {
        Application.Init();
        try
        {
            Button.DefaultShadow = ShadowStyle.None;
            Dialog.DefaultShadow = ShadowStyle.None;
            Window.DefaultShadow = ShadowStyle.None;

            using var window = new Window
            {
                Title = "Vintage Story Save Manager (Esc to quit)",
                Arrangement = ViewArrangement.Fixed,
                ShadowStyle = ShadowStyle.None
            };
            AddVimFocusBindings(window);

            var heading = new Label
            {
                Text = "Choose an action",
                X = Pos.Center(),
                Y = 1
            };
            var backup = MenuButton("_Backup save", 3, BackupWorkflow);
            var restore = MenuButton("_Restore backup", 5, RestoreWorkflow);
            var rename = MenuButton("_Rename world", 7, RenameWorkflow);
            var quit = MenuButton("_Quit", 9, () => Application.RequestStop(window));

            var hint = new Label
            {
                Text = "Tables show filename, world, play time, creation time, and installation.",
                X = Pos.Center(),
                Y = 12
            };

            window.Add(heading, backup, restore, rename, quit, hint);
            Application.Run(window);
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private static Button MenuButton(string text, int y, Action action)
    {
        var button = new Button
        {
            Text = text,
            X = Pos.Center(),
            Y = y,
            Width = 26,
            ShadowStyle = ShadowStyle.None
        };
        button.Accepting += (_, _) =>
        {
            action();
        };
        return button;
    }

    private void BackupWorkflow() => Guard(() =>
    {
        var selected = SelectEntry("Select save to back up", _repository.ListSaves(), "_Back up");
        if (selected is null)
        {
            return;
        }

        EnsureReadable(selected);
        if (!Confirm("Create backup", $"Back up {selected.FileName}\nWorld: {selected.WorldName}?", "Back up"))
        {
            return;
        }

        var result = _repository.Backup(selected);
        MessageBox.Query("Backup complete", $"Created:\n{result.Path}", "OK");
    });

    private void RestoreWorkflow() => Guard(() =>
    {
        var backup = SelectBackupForRestore();
        if (backup is null)
        {
            return;
        }

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
    });

    private void RenameWorkflow() => Guard(() =>
    {
        var selected = SelectEntry("Select save whose world should be renamed", _repository.ListSaves(), "_Rename");
        if (selected is null)
        {
            return;
        }

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
    });

    private SaveEntry? SelectBackupForRestore()
    {
        var entries = _repository.ListBackups();
        if (entries.Count == 0)
        {
            MessageBox.Query("No backups", "No backups were found.", "OK");
            return null;
        }

        SaveEntry? selected = null;
        using var dialog = SelectionDialog("Select backup to restore");
        var items = entries.ToList();
        var table = EntryTable(items);
        dialog.Add(table);
        FocusTableWhenReady(dialog, table);
        var cancel = DialogButton("_Cancel");
        var delete = DialogButton("_Delete");
        var restore = DialogButton("_Restore", isDefault: true);
        dialog.AddButton(cancel);
        dialog.AddButton(delete);
        dialog.AddButton(restore);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        restore.Accepting += (_, _) =>
        {
            selected = Selected(items, table);
            Application.RequestStop(dialog);
        };
        delete.Accepting += (_, _) =>
        {
            var item = Selected(items, table);
            if (item is not null && Confirm("Delete backup", $"Permanently delete {item.FileName}?", "Delete"))
            {
                _repository.DeleteBackup(item);
                var index = table.SelectedRow;
                items.RemoveAt(index);
                ((DataTableSource)table.Table).DataTable.Rows.RemoveAt(index);
                table.Update();
                if (items.Count == 0)
                {
                    Application.RequestStop(dialog);
                }
                else
                {
                    table.SetSelection(0, Math.Min(index, items.Count - 1), false);
                }
            }

        };
        table.CellActivated += (_, _) =>
        {
            selected = Selected(items, table);
            Application.RequestStop(dialog);
        };
        Application.Run(dialog);
        return selected;
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
        Application.Run(dialog);
        return result;
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

    private static TableView EntryTable(IEnumerable<SaveEntry> entries)
    {
        var data = SaveTableSchema();
        foreach (var entry in entries)
        {
            data.Rows.Add(entry.FileName, entry.WorldName, entry.Played, entry.Created, entry.Location.Name);
        }

        return CreateTable(data);
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

    private static DataTable SaveTableSchema()
    {
        var data = new DataTable();
        data.Columns.Add("Filename");
        data.Columns.Add("World");
        data.Columns.Add("Played");
        data.Columns.Add("Created");
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

    private static Button DialogButton(string text, bool isDefault = false) => new()
    {
        Text = text,
        IsDefault = isDefault,
        ShadowStyle = ShadowStyle.None
    };

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
