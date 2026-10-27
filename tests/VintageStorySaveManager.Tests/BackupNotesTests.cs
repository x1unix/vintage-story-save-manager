using System.Text;
using System.Text.Json;
using Terminal.Gui;
using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

[CollectionDefinition("Backup notes console", DisableParallelization = true)]
public sealed class BackupNotesConsoleCollection;

[Collection("Backup notes console")]
public sealed class BackupNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vsm-notes-{Guid.NewGuid():N}");
    private readonly SaveLocation _location;
    private readonly SaveRepository _repository;
    private readonly TestCodec _codec = new();
    private readonly SaveEntry _source;

    static BackupNotesTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());

    public BackupNotesTests()
    {
        var saves = Path.Combine(_root, "Saves");
        Directory.CreateDirectory(saves);
        _location = new SaveLocation("test", _root, saves, Path.Combine(saves, "backup"));
        var path = Path.Combine(saves, "main.vcdbs");
        using (var db = SaveRepository.Open(path, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB); INSERT INTO gamedata VALUES(1, X'010203');";
            command.ExecuteNonQuery();
        }
        _repository = new SaveRepository([_location], _codec);
        _source = _repository.ResolveSave("main.vcdbs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("Before exploring — 世界 🌍\nSecond line")]
    public void NotesRoundTripWithoutChangingGameData(string? note)
    {
        var before = File.ReadAllBytes(_source.Path);
        var backup = _repository.Backup(_source, note);
        Assert.Empty(Directory.GetFiles(_location.BackupDirectory, ".*"));
        var expected = string.IsNullOrWhiteSpace(note) ? null : note;
        var notePath = BackupNotes.PathFor(backup.Path);
        Assert.Equal(Path.Combine(_location.BackupDirectory, Path.GetFileNameWithoutExtension(backup.FileName) + ".note.txt"), notePath);
        Assert.Equal(expected, _repository.ResolveBackup(backup.Id).Note);
        Assert.Equal(expected is not null, File.Exists(notePath));
        if (expected is not null) Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(notePath));
        Assert.Equal(before, File.ReadAllBytes(_source.Path));
        Assert.Equal(SaveRepository.ReadGameData(_source.Path), SaveRepository.ReadGameData(backup.Path));
        var restored = _repository.Restore(new RestoreRequest(backup, _repository.NewSavePath(_location, "restored"), _location, null, false));
        Assert.Null(restored.Note);
        Assert.False(File.Exists(BackupNotes.PathFor(restored.Path)));
        _repository.Restore(new RestoreRequest(backup, _source.Path, _location, null, true));
        Assert.Null(_repository.ListBackups().Single(entry => entry.Path != backup.Path).Note);
    }

    [Fact]
    public void NameSelectionSkipsDatabasesOrphanNotesDirectoriesAndDanglingLinks()
    {
        Directory.CreateDirectory(_location.BackupDirectory);
        var first = Path.Combine(_location.BackupDirectory, "fixed.vcdbs");
        File.WriteAllText(BackupNotes.PathFor(first), "orphan");
        File.WriteAllText(Path.Combine(_location.BackupDirectory, "fixed-2.vcdbs"), "existing");
        Directory.CreateDirectory(Path.Combine(_location.BackupDirectory, "fixed-3.note.txt"));
        File.CreateSymbolicLink(Path.Combine(_location.BackupDirectory, "fixed-4.note.txt"), Path.Combine(_root, "missing"));
        Assert.EndsWith("fixed-5.vcdbs", SaveRepository.UniqueBackupPath(_location.BackupDirectory, "fixed"));
        Assert.Equal("orphan", File.ReadAllText(BackupNotes.PathFor(first)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletionSucceedsWithoutNoteOrWhenNoteCannotBeDeleted(bool failedCleanup)
    {
        var backup = _repository.Backup(_source);
        if (failedCleanup) Directory.CreateDirectory(BackupNotes.PathFor(backup.Path));
        var output = Capture(() => _repository.DeleteBackup(backup));
        Assert.False(File.Exists(backup.Path));
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void DeletionRemovesNoteAndRefusesUndiscoveredFiles()
    {
        var backup = _repository.Backup(_source, "delete me");
        _repository.DeleteBackup(backup);
        Assert.False(File.Exists(BackupNotes.PathFor(backup.Path)));
        var unrelated = Path.Combine(_location.BackupDirectory, "other.txt");
        File.WriteAllText(unrelated, "preserve");
        File.WriteAllText(BackupNotes.PathFor(unrelated), "preserve note");
        Assert.Throws<UserFacingException>(() => _repository.DeleteBackup(backup with { Path = unrelated }));
        Assert.Equal("preserve note", File.ReadAllText(BackupNotes.PathFor(unrelated)));
        Assert.Throws<UserFacingException>(() => _repository.DeleteBackup(_source));
    }

    [Fact]
    public void FailedDatabaseDeletionPreservesNote()
    {
        var backup = _repository.Backup(_source, "preserve");
        File.Delete(backup.Path);
        Directory.CreateDirectory(backup.Path);
        Assert.Throws<UserFacingException>(() => _repository.DeleteBackup(backup));
        Assert.Equal("preserve", File.ReadAllText(BackupNotes.PathFor(backup.Path)));
    }

    [Fact]
    public void LinkedOrUnreadableNoteDoesNotBlockRestoreOrExposeTarget()
    {
        var backup = _repository.Backup(_source);
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(BackupNotes.PathFor(backup.Path), outside);
        var entry = _repository.ResolveBackup(backup.Id);
        Assert.Null(entry.Note);
        Assert.NotNull(entry.NoteError);
        Assert.Null(entry.Error);
        _repository.Restore(new RestoreRequest(entry, _repository.NewSavePath(_location, "restored"), _location, null, false));
        _repository.DeleteBackup(entry);
        Assert.Equal("secret", File.ReadAllText(outside));
    }

    [Fact]
    public void LinkedBackupDirectoryIsRejected()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(_location.BackupDirectory, outside);
        Assert.Throws<UserFacingException>(() => _repository.Backup(_source, "unsafe"));
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void InvalidUtf8IsReportedSeparately()
    {
        var backup = _repository.Backup(_source);
        File.WriteAllBytes(BackupNotes.PathFor(backup.Path), [0xff]);
        var entry = _repository.ResolveBackup(backup.Id);
        Assert.NotNull(entry.NoteError);
        Assert.Null(entry.Error);
    }

    [Fact]
    public void FailedValidationAndNoteEncodingLeaveNoOutput()
    {
        _codec.OnRead = () => throw new UserFacingException("invalid metadata");
        Assert.Throws<UserFacingException>(() => _repository.Backup(_source, "note"));
        Assert.Empty(Directory.GetFileSystemEntries(_location.BackupDirectory));
        _codec.OnRead = null;
        Assert.Throws<UserFacingException>(() => _repository.Backup(_source, "\ud800"));
        Assert.Empty(Directory.GetFileSystemEntries(_location.BackupDirectory));
    }

    [Fact]
    public void PublicationCollisionPreservesExistingFileAndCleansStaging()
    {
        string? collision = null;
        _codec.OnRead = () =>
        {
            _codec.OnRead = null;
            // The repository has selected its name and is validating the staged database.
            var now = DateTime.Now;
            var stem = $"{now:yyyy-MM-dd_HH-mm-ss}-main";
            // Cover a second boundary without relying on the test's clock timing.
            foreach (var seconds in new[] { -1, 0, 1 })
            {
                var path = Path.Combine(_location.BackupDirectory, $"{now.AddSeconds(seconds):yyyy-MM-dd_HH-mm-ss}-main.vcdbs");
                File.WriteAllText(path, "concurrent backup");
            }
            collision = Path.Combine(_location.BackupDirectory, stem + ".vcdbs");
        };
        Assert.Throws<UserFacingException>(() => _repository.Backup(_source, "note"));
        Assert.Equal("concurrent backup", File.ReadAllText(collision!));
        Assert.Equal(3, Directory.GetFiles(_location.BackupDirectory).Length);
        Assert.Empty(Directory.GetFiles(_location.BackupDirectory, "*.note.txt"));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void PromptOnlyForInteractiveBackup(bool hasNote, bool json, bool redirected, bool expected) =>
        Assert.Equal(expected, CommandLine.ShouldPromptForNote(hasNote, json, redirected));

    [Fact]
    public void CliListsNotesAsTextAndJsonAndSupportsExplicitEmptyNotes()
    {
        var cli = new CommandLine(_repository);
        var note = "Before 🌍\n\u001b[31m second";
        var output = Capture(() => cli.Run(["backup", "--save", _source.Id, "--note", note, "--json"]));
        using var result = JsonDocument.Parse(output);
        Assert.Equal(note, result.RootElement.GetProperty("Note").GetString());
        output = Capture(() => cli.Run(["list", "backups", "--json"]));
        using var list = JsonDocument.Parse(output);
        Assert.Equal(note, list.RootElement[0].GetProperty("Note").GetString());
        output = Capture(() => cli.Run(["list", "backups"]));
        Assert.Contains("Note: Before 🌍", output);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("🌍\n", output);
        output = Capture(() => cli.Run(["backup", "--save", _source.Id, "--note", "", "--json"]));
        using var empty = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("Note").ValueKind);
        output = Capture(() => cli.Run(["backup", "--save", _source.Id, "--json"]));
        using var omitted = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Null, omitted.RootElement.GetProperty("Note").ValueKind);
    }

    [Fact]
    public void BackupTablesExposeNotesWhileSaveTablesKeepTheirColumns()
    {
        var backup = _repository.Backup(_source, new string('n', 1000) + "\n\u001b");
        using var table = TuiApplication.EntryTable([backup]);
        var data = ((DataTableSource)table.Table).DataTable;
        Assert.Equal("Note", data.Columns[4].ColumnName);
        Assert.Equal("Installation", data.Columns[5].ColumnName);
        Assert.Equal(backup.NoteDisplay, data.Rows[0][4]);
        Assert.Equal(backup.Location.Name, data.Rows[0][5]);
        Assert.DoesNotContain("\u001b", backup.NoteDisplay, StringComparison.Ordinal);
        using var saves = TuiApplication.EntryTable([_source]);
        Assert.False(((DataTableSource)saves.Table).DataTable.Columns.Contains("Note"));
    }

    [Fact]
    public void NoteFillsRemainingWidthOnResizeWhileInstallationStaysCompact()
    {
        var backup = _repository.Backup(_source, "Short note");
        using var table = TuiApplication.EntryTable([backup]);
        table.Frame = new System.Drawing.Rectangle(0, 0, 160, 20);
        var note = table.Style.GetOrCreateColumnStyle(4);
        var installation = table.Style.GetOrCreateColumnStyle(5);
        var initialNoteWidth = note.MinWidth;
        var initialInstallationWidth = installation.MinWidth;
        Assert.True(initialNoteWidth > backup.Note!.Length);
        Assert.Equal("Installation".Length, initialInstallationWidth);
        Assert.False(table.Style.ExpandLastColumn);

        table.Frame = new System.Drawing.Rectangle(0, 0, 220, 20);
        Assert.Equal(initialNoteWidth + 60, note.MinWidth);
        Assert.Equal(initialInstallationWidth, installation.MinWidth);
        table.Frame = new System.Drawing.Rectangle(0, 0, 80, 20);
        Assert.True(note.MinWidth >= "Note".Length);
        Assert.Equal(initialInstallationWidth, installation.MinWidth);
    }

    private static string Capture(Action action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            action();
            Assert.Equal(string.Empty, error.ToString());
            return output.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class TestCodec : ISaveCodec
    {
        public Action? OnRead { get; set; }
        public SaveMetadata Read(ReadOnlySpan<byte> payload)
        {
            OnRead?.Invoke();
            return new SaveMetadata("World", 100, null, null, null);
        }
        public byte[] SetWorldName(ReadOnlySpan<byte> payload, string worldName) => throw new NotSupportedException();
        public RepairIdentity ReadRepairIdentity(ReadOnlySpan<byte> payload) => throw new NotSupportedException();
        public IRepairValidator CreateRepairValidator(string gameVersion) => throw new NotSupportedException();
    }
}
