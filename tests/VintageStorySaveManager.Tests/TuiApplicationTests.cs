using Terminal.Gui;
using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class TuiApplicationTests
{
    [Fact]
    public void MainTabsExposeInlineListsAndBracketNavigation()
    {
        var application = new TuiApplication(new SaveRepository([], new UnusedCodec()));
        using var tabs = application.CreateTabs();
        tabs.Width = 100;
        tabs.Height = 25;
        tabs.BeginInit();
        tabs.EndInit();
        tabs.Layout();
        var pages = tabs.Tabs.ToArray();
        Assert.Equal(new[] { "Backup save", "Restore backup", "Rename world", "Repair save" },
            pages.Select(tab => tab.DisplayText));
        Assert.All(pages, tab => Assert.IsType<EntryScreen>(tab.View));

        var screen = Assert.IsType<EntryScreen>(pages[0].View);
        screen.Table.SetFocus();
        Assert.True(tabs.NewKeyDownEvent(new Key(']')));
        Assert.Same(pages[1], tabs.SelectedTab);
        Assert.True(tabs.NewKeyDownEvent(new Key('[')));
        Assert.Same(pages[0], tabs.SelectedTab);
        tabs.NewKeyDownEvent(new Key('['));
        Assert.Same(pages[0], tabs.SelectedTab);

        pages[0].SetFocus();
        tabs.NewKeyDownEvent(Key.CursorRight);
        Assert.Same(pages[1], tabs.SelectedTab);
    }

    [Fact]
    public void RefreshPreservesSelectedFileAndHandlesLastEntryRemoval()
    {
        var location = new SaveLocation("test", "/tmp", "/tmp/Saves", "/tmp/Saves/backup");
        SaveEntry Entry(string name) => new(name, "/tmp/Saves/backup/" + name, name, location, true,
            DateTime.UnixEpoch, new SaveMetadata("World", 1, null, null, null), null);
        var entries = new List<SaveEntry> { Entry("a.vcdbs"), Entry("b.vcdbs") };
        var calls = 0;
        using var screen = new EntryScreen(() => entries.ToArray(), "_Restore", _ => calls++, true);
        screen.Table.SetSelection(0, 1, false);
        entries.Insert(0, Entry("new.vcdbs"));
        screen.Refresh();
        Assert.Equal(2, screen.Table.SelectedRow);

        var action = Assert.Single(screen.Subviews.OfType<Button>(), button => button.Text == "_Restore");
        action.InvokeCommand(Command.Accept);
        Assert.Equal(1, calls);
        entries.Clear();
        screen.Refresh();
        Assert.Equal(0, screen.Table.Table.Rows);
        Assert.False(action.Enabled);
        Assert.Contains("No backups", Assert.Single(screen.Subviews.OfType<Label>()).Text);
        Assert.Contains("Note", ((DataTableSource)screen.Table.Table).DataTable.Columns.Cast<System.Data.DataColumn>().Select(column => column.ColumnName));
        Assert.True(Assert.Single(screen.Subviews.OfType<Button>(), button => button.Text == "Re_fresh").Enabled);

        entries.Add(Entry("back.vcdbs"));
        screen.Refresh();
        Assert.True(action.Enabled);
        Assert.Equal(0, screen.Table.SelectedRow);
    }

    private sealed class UnusedCodec : ISaveCodec
    {
        public SaveMetadata Read(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public byte[] SetWorldName(ReadOnlySpan<byte> payload, string worldName) => throw new NotSupportedException();
        public RepairIdentity ReadRepairIdentity(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public IRepairValidator CreateRepairValidator(string gameVersion) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("_Delete")]
    [InlineData("_Cancel")]
    public void DialogActionDoesNotActivateDefaultRestoreButton(string actionText)
    {
        using var dialog = new Dialog();
        var action = TuiApplication.DialogButton(actionText);
        var restore = TuiApplication.DialogButton("_Restore", isDefault: true);
        dialog.AddButton(action);
        dialog.AddButton(restore);
        var actionCount = 0;
        var restoreCount = 0;
        action.Accepting += (_, _) => actionCount++;
        restore.Accepting += (_, _) => restoreCount++;

        action.InvokeCommand(Command.Accept);

        Assert.Equal(1, actionCount);
        Assert.Equal(0, restoreCount);

        restore.InvokeCommand(Command.Accept);

        Assert.Equal(1, restoreCount);
    }
}
