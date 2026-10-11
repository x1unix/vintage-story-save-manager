using Microsoft.Data.Sqlite;

namespace VintageStorySaveManager;

// A plan owns consistent snapshots. Applying it never re-reads a changing live save or backup.
internal sealed class RepairSession : IDisposable
{
    private static readonly string[] Tables = ["mapregion", "mapchunk", "chunk"];
    private readonly string _directory;
    private readonly ISaveCodec _codec;
    private readonly string _version;
    private bool _disposed;
    private string SourceSnapshot => Path.Combine(_directory, "source.vcdbs");
    private string BackupSnapshot => Path.Combine(_directory, "backup.vcdbs");

    public SaveEntry Source { get; }
    public RepairReport Report { get; private set; }

    private RepairSession(SaveEntry source, SaveEntry? backup, ISaveCodec codec, string directory,
        SaveMetadata metadata, SaveMetadata? backupMetadata, string version)
    {
        Source = source;
        _directory = directory;
        _codec = codec;
        _version = version;
        Report = new RepairReport(source.Id, backup?.Id, metadata.TotalSecondsPlayed, backupMetadata?.TotalSecondsPlayed, [], []);
    }

    public static RepairSession Prepare(SaveEntry source, SaveEntry? backup, ISaveCodec codec, CancellationToken cancellationToken)
    {
        if (source.IsBackup || (backup is not null && !backup.IsBackup))
        {
            throw new UserFacingException("Repair requires a live save and, optionally, a managed backup.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"vsm-repair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            cancellationToken.ThrowIfCancellationRequested();
            var sourceSnapshot = Path.Combine(directory, "source.vcdbs");
            SaveRepository.SnapshotDatabase(source.Path, sourceSnapshot);
            ValidateDatabase(sourceSnapshot, "Source save");
            var data = SaveRepository.ReadGameData(sourceSnapshot);
            var metadata = codec.Read(data);
            var identity = codec.ReadRepairIdentity(data);
            SaveMetadata? backupMetadata = null;
            if (backup is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backupSnapshot = Path.Combine(directory, "backup.vcdbs");
                SaveRepository.SnapshotDatabase(backup.Path, backupSnapshot);
                ValidateDatabase(backupSnapshot, "Backup");
                var backupData = SaveRepository.ReadGameData(backupSnapshot);
                backupMetadata = codec.Read(backupData);
                if (identity != codec.ReadRepairIdentity(backupData))
                {
                    throw new UserFacingException("The backup has a different world identifier, seed, dimensions, or game/world-generation version. Choose a compatible backup of this world.");
                }

                if (backupMetadata.TotalSecondsPlayed > metadata.TotalSecondsPlayed)
                {
                    throw new UserFacingException("The backup is ahead of the source's playtime. Choose an earlier backup to avoid importing future world state.");
                }
            }

            var session = new RepairSession(source, backup, codec, directory, metadata, backupMetadata, identity.LastSavedGameVersion);
            session.Scan(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch (Exception exception)
        {
            DeleteTemporaryDirectory(directory);
            throw Translate(exception);
        }
    }

    private void Scan(CancellationToken cancellationToken)
    {
        using var validator = _codec.CreateRepairValidator(_version);
        using var source = SaveRepository.Open(SourceSnapshot, SqliteOpenMode.ReadOnly);
        using var backup = Report.BackupId is null ? null : SaveRepository.Open(BackupSnapshot, SqliteOpenMode.ReadOnly);
        var issues = new List<RepairIssue>();
        var summaries = new List<RepairTableSummary>();
        foreach (var table in Tables)
        {
            using var command = source.CreateCommand();
            command.CommandText = $"SELECT position, data FROM {table} ORDER BY position";
            using var reader = command.ExecuteReader();
            int total = 0, damaged = 0, repairable = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
                var reason = validator.Validate(table, Payload(reader, 1));
                if (reason is null)
                {
                    continue;
                }

                damaged++;
                var position = reader.GetInt64(0);
                var replacement = backup is null ? null : ReadRecord(backup, table, position);
                var backupError = backup is null ? "No backup selected." : replacement is null
                    ? "Record is missing or NULL in the backup." : validator.Validate(table, replacement);
                if (backupError is null)
                {
                    repairable++;
                }

                issues.Add(new RepairIssue(table, position, reason, backupError is null, backupError));
            }

            summaries.Add(new RepairTableSummary(table, total, damaged, repairable));
        }

        Report = Report with { Tables = summaries.AsReadOnly(), Issues = issues.AsReadOnly() };
    }

    internal void WriteRepairedSave(string destinationPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Report.CanRepair)
        {
            throw new UserFacingException(Report.DamagedRecords == 0
                ? "No damaged records were detected; no repaired save was created."
                : $"{Report.UnresolvedRecords} damaged records have no valid backup replacement. No repaired save was created.");
        }

        SaveRepository.EnsureManagedDestination(destinationPath, Source.Location);
        if (File.Exists(destinationPath))
        {
            throw new UserFacingException("The repair destination already exists. Choose a new filename.");
        }

        var temporary = Path.Combine(Source.Location.SavesDirectory, $".repair-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveRepository.SnapshotDatabase(SourceSnapshot, temporary);
            using (var output = SaveRepository.Open(temporary, SqliteOpenMode.ReadWrite))
            using (var backup = SaveRepository.Open(BackupSnapshot, SqliteOpenMode.ReadOnly))
            using (var transaction = output.BeginTransaction())
            {
                foreach (var issue in Report.Issues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var replacement = ReadRecord(backup, issue.Table, issue.Position)
                        ?? throw new UserFacingException("A backup replacement disappeared from the repair snapshot.");
                    using var command = output.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"UPDATE {issue.Table} SET data = $data WHERE position = $position";
                    command.Parameters.AddWithValue("$data", replacement);
                    command.Parameters.AddWithValue("$position", issue.Position);
                    if (command.ExecuteNonQuery() != 1)
                    {
                        throw new UserFacingException("Expected to repair exactly one record.");
                    }
                }

                transaction.Commit();
            }

            ValidateDatabase(temporary, "Repaired save");
            _ = _codec.Read(SaveRepository.ReadGameData(temporary));
            VerifyOutput(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destinationPath, overwrite: false);
        }
        catch (Exception exception)
        {
            throw Translate(exception);
        }
        finally
        {
            DeleteTemporaryFiles(temporary);
        }
    }

    private void VerifyOutput(string path, CancellationToken cancellationToken)
    {
        using var output = SaveRepository.Open(path, SqliteOpenMode.ReadOnly);
        using var source = SaveRepository.Open(SourceSnapshot, SqliteOpenMode.ReadOnly);
        using var backup = SaveRepository.Open(BackupSnapshot, SqliteOpenMode.ReadOnly);
        using var validator = _codec.CreateRepairValidator(_version);
        using (var attach = output.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS original";
            attach.Parameters.AddWithValue("$path", SourceSnapshot);
            attach.ExecuteNonQuery();
        }

        foreach (var table in new[] { "gamedata", "playerdata" })
        {
            using var command = output.CreateCommand();
            command.CommandText = $"SELECT EXISTS(SELECT * FROM main.{table} EXCEPT SELECT * FROM original.{table}) " +
                $"OR EXISTS(SELECT * FROM original.{table} EXCEPT SELECT * FROM main.{table})";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                throw new UserFacingException($"Repair unexpectedly changed {table}; the output was discarded.");
            }
        }

        var replaced = Report.Issues.Select(issue => (issue.Table, issue.Position)).ToHashSet();
        foreach (var table in Tables)
        {
            using var before = source.CreateCommand();
            using var after = output.CreateCommand();
            before.CommandText = after.CommandText = $"SELECT position, data FROM {table} ORDER BY position";
            using var oldRows = before.ExecuteReader();
            using var newRows = after.ExecuteReader();
            while (oldRows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = oldRows.GetInt64(0);
                if (!newRows.Read() || newRows.GetInt64(0) != position)
                {
                    throw new UserFacingException("Repair unexpectedly changed the set of world records.");
                }

                var actual = Payload(newRows, 1);
                var expected = replaced.Contains((table, position)) ? ReadRecord(backup, table, position) : Payload(oldRows, 1);
                if (actual is null || expected is null || !actual.AsSpan().SequenceEqual(expected) || validator.Validate(table, actual) is not null)
                {
                    throw new UserFacingException($"Repaired {table} record {position} failed final validation.");
                }
            }

            if (newRows.Read())
            {
                throw new UserFacingException("Repair unexpectedly added world records.");
            }
        }
    }

    private static byte[]? ReadRecord(SqliteConnection connection, string table, long position)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT data FROM {table} WHERE position = $position";
        command.Parameters.AddWithValue("$position", position);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Payload(reader, 0) : null;
    }

    private static byte[]? Payload(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null :
        reader.GetValue(ordinal) as byte[] ?? throw new UserFacingException("A world record contains non-BLOB data; this database format cannot be repaired.");

    private static void ValidateDatabase(string path, string label)
    {
        using var connection = SaveRepository.Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
            {
                throw new UserFacingException($"{label} has SQLite structural corruption. Repair only handles game payloads; recover the database structure on a separate copy first, or choose another backup.");
            }
        }

        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger'";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
        {
            throw new UserFacingException($"{label} contains unexpected database triggers; repair is not supported.");
        }

        foreach (var table in Tables)
        {
            command.CommandText = $"PRAGMA table_info({table})";
            using var reader = command.ExecuteReader();
            var columns = new List<(string Name, string Type, long PrimaryKey)>();
            while (reader.Read())
            {
                columns.Add((reader.GetString(1), reader.GetString(2).ToUpperInvariant(), reader.GetInt64(5)));
            }

            if (columns.Count != 2 || !columns.Contains(("position", "INTEGER", 1)) || !columns.Contains(("data", "BLOB", 0)))
            {
                throw new UserFacingException($"{label} has an unsupported {table} schema.");
            }
        }

        command.CommandText = "SELECT playerid, playeruid, data FROM playerdata LIMIT 1";
        using var player = command.ExecuteReader();
    }

    private static Exception Translate(Exception exception) => exception is SqliteException or IOException or UnauthorizedAccessException
        ? new UserFacingException($"Could not repair the save: {exception.Message}. Source files were preserved.", exception) : exception;

    private static void DeleteTemporaryFiles(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(path + suffix); }
            catch (IOException) { /* Do not hide the operation error. */ }
            catch (UnauthorizedAccessException) { /* Do not hide the operation error. */ }
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { /* Do not hide the operation error. */ }
        catch (UnauthorizedAccessException) { /* Do not hide the operation error. */ }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            DeleteTemporaryDirectory(_directory);
        }
    }
}
