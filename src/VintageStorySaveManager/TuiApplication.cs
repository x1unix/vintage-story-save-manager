using System.Collections.ObjectModel;
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
            using var window = new Window { Title = "Vintage Story Save Manager (Esc to quit)" };

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
                Text = "Lists show filename, world, play time, creation time, and installation.",
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
        var button = new Button { Text = text, X = Pos.Center(), Y = y, Width = 26 };
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
            .Select(save => new DestinationOption(save.DisplayLine, save))
            .Append(new DestinationOption("<Save as new file>", null))
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
        using var dialog = new Dialog { Title = "Select backup to restore", Width = Dim.Percent(95), Height = Dim.Percent(85) };
        var items = new ObservableCollection<EntryListItem>(entries.Select(entry => new EntryListItem(entry)));
        var list = EntryList(items);
        var cancel = new Button { Text = "_Cancel" };
        var delete = new Button { Text = "_Delete" };
        var restore = new Button { Text = "_Restore", IsDefault = true };
        dialog.AddButton(cancel);
        dialog.AddButton(delete);
        dialog.AddButton(restore);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        restore.Accepting += (_, _) =>
        {
            selected = Selected(items, list);
            Application.RequestStop(dialog);
        };
        delete.Accepting += (_, _) =>
        {
            var item = Selected(items, list);
            if (item is not null && Confirm("Delete backup", $"Permanently delete {item.FileName}?", "Delete"))
            {
                _repository.DeleteBackup(item);
                var index = list.SelectedItem;
                items.RemoveAt(index);
                list.SetSource(items);
                if (items.Count == 0)
                {
                    Application.RequestStop(dialog);
                }
            }

        };
        dialog.Add(list);
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
        using var dialog = new Dialog { Title = title, Width = Dim.Percent(95), Height = Dim.Percent(85) };
        var items = new ObservableCollection<EntryListItem>(entries.Select(entry => new EntryListItem(entry)));
        var list = EntryList(items);
        var cancel = new Button { Text = "_Cancel" };
        var accept = new Button { Text = actionText, IsDefault = true };
        dialog.AddButton(cancel);
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            selected = Selected(items, list);
            Application.RequestStop(dialog);
        };
        list.Accepting += (_, _) =>
        {
            selected = Selected(items, list);
            Application.RequestStop(dialog);
        };
        dialog.Add(list);
        Application.Run(dialog);
        return selected;
    }

    private DestinationOption? SelectDestination(IReadOnlyList<DestinationOption> options)
    {
        DestinationOption? selected = null;
        using var dialog = new Dialog { Title = "Select restore destination", Width = Dim.Percent(95), Height = Dim.Percent(85) };
        var items = new ObservableCollection<DestinationOption>(options);
        var list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            BorderStyle = LineStyle.Single
        };
        list.SetSource(items);
        var cancel = new Button { Text = "_Cancel" };
        var accept = new Button { Text = "_Select", IsDefault = true };
        dialog.AddButton(cancel);
        dialog.AddButton(accept);
        cancel.Accepting += (_, _) => Application.RequestStop(dialog);
        accept.Accepting += (_, _) =>
        {
            selected = items[list.SelectedItem];
            Application.RequestStop(dialog);
        };
        dialog.Add(list);
        Application.Run(dialog);
        return selected;
    }

    private (string FileName, string WorldName)? PromptNewSave(SaveEntry backup)
    {
        (string FileName, string WorldName)? result = null;
        using var dialog = new Dialog { Title = "Restore as new save", Width = 70, Height = 11 };
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
        var cancel = new Button { Text = "_Cancel" };
        var restore = new Button { Text = "_Restore", IsDefault = true };
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
        using var dialog = new Dialog { Title = title, Width = 70, Height = 8 };
        var label = new Label { Text = labelText, X = 1, Y = 1 };
        var field = new TextField { Text = initial, X = 1, Y = 2, Width = Dim.Fill(1) };
        var cancel = new Button { Text = "_Cancel" };
        var accept = new Button { Text = $"_{actionText}", IsDefault = true };
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

    private static ListView EntryList(ObservableCollection<EntryListItem> items)
    {
        var list = new ListView
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            BorderStyle = LineStyle.Single
        };
        list.SetSource(items);
        return list;
    }

    private static SaveEntry? Selected(ObservableCollection<EntryListItem> items, ListView list) =>
        items.Count == 0 ? null : items[list.SelectedItem].Entry;

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

    private sealed record EntryListItem(SaveEntry Entry)
    {
        public override string ToString() => Entry.DisplayLine;
    }

    private sealed record DestinationOption(string Display, SaveEntry? Save)
    {
        public override string ToString() => Display;
    }
}

