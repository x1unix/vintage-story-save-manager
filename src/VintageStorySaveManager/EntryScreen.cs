using Terminal.Gui;

namespace VintageStorySaveManager;

/// <summary>A persistent file list and actions for one main-screen workflow.</summary>
internal sealed class EntryScreen : View
{
    private readonly Func<IReadOnlyList<SaveEntry>> _load;
    private readonly bool _backups;
    private readonly Action<Action> _guard;
    private readonly List<SaveEntry> _entries = [];
    private readonly List<Button> _entryActions = [];
    private readonly Label _status;
    private View? _lastButton;

    internal TableView Table { get; }

    internal EntryScreen(Func<IReadOnlyList<SaveEntry>> load, string actionText, Action<SaveEntry> action, bool backups, Action<Action>? guard = null)
    {
        _load = load;
        _guard = guard ?? (work => work());
        _backups = backups;
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();
        Table = TuiApplication.EntryTable(_entries);
        Table.Y = 2;
        Table.Height = Dim.Fill(2);
        TuiApplication.UpdateEntryTable(Table, _entries, backups);
        _status = new Label { X = 1, Y = 0, Width = Dim.Fill(1) };
        Add(_status, Table);
        AddAction(actionText, action);
        Table.CellActivated += (_, _) => Perform(action);
        var refresh = TuiApplication.DialogButton("Re_fresh");
        refresh.Accepting += (_, _) => Refresh();
        AddButton(refresh);
        Refresh();
    }

    internal void AddAction(string text, Action<SaveEntry> action)
    {
        var button = TuiApplication.DialogButton(text);
        button.Accepting += (_, _) => Perform(action);
        _entryActions.Add(button);
        button.Enabled = _entries.Count > 0;
        AddButton(button);
    }

    internal void AddNoteAction()
    {
        var button = TuiApplication.NoteButton(_entries, Table);
        button.Enabled = _entries.Count > 0;
        _entryActions.Add(button);
        AddButton(button);
    }

    private void AddButton(Button button)
    {
        button.X = _lastButton is null ? 1 : Pos.Right(_lastButton) + 1;
        button.Y = Pos.AnchorEnd(1);
        _lastButton = button;
        Add(button);
    }

    private void Perform(Action<SaveEntry> action)
    {
        if (Table.SelectedRow < 0 || Table.SelectedRow >= _entries.Count) return;
        _guard(() => action(_entries[Table.SelectedRow]));
        Refresh();
    }

    internal void Refresh() => _guard(RefreshEntries);

    private void RefreshEntries()
    {
        var row = Table.SelectedRow;
        var path = row >= 0 && row < _entries.Count ? _entries[row].Path : null;
        var entries = _load();
        _entries.Clear();
        _entries.AddRange(entries);
        TuiApplication.UpdateEntryTable(Table, _entries, _backups);
        if (_entries.Count > 0)
        {
            var selected = _entries.FindIndex(entry => entry.Path == path);
            Table.SetSelection(0, selected >= 0 ? selected : Math.Clamp(row, 0, _entries.Count - 1), false);
        }
        _status.Text = _entries.Count == 0
            ? (_backups ? "No backups found. Create one in Backup save, then refresh." : "No saves found in the configured data roots.")
            : "Select a file and press Enter, or choose an action below.";
        foreach (var button in _entryActions) button.Enabled = _entries.Count > 0;
    }
}
