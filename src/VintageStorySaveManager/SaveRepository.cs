using Microsoft.Data.Sqlite;

namespace VintageStorySaveManager;

internal sealed class SaveRepository
{
    private readonly IReadOnlyList<SaveLocation> _locations;
    private readonly ISaveCodec _codec;

    public SaveRepository(IReadOnlyList<SaveLocation> locations, ISaveCodec codec)
    {
        _locations = locations;
        _codec = codec;
    }

    public IReadOnlyList<SaveLocation> Locations => _locations;

    public IReadOnlyList<SaveEntry> ListSaves() => ListEntries(backups: false);

    public IReadOnlyList<SaveEntry> ListBackups() => ListEntries(backups: true);

    public SaveEntry ResolveSave(string identifier) => Resolve(identifier, ListSaves(), "save");

    public SaveEntry ResolveBackup(string identifier) => Resolve(identifier, ListBackups(), "backup");

    public RepairSession PrepareRepair(SaveEntry source, SaveEntry? backup = null, CancellationToken cancellationToken = default) =>
        RepairSession.Prepare(source, backup, _codec, cancellationToken);

    public SaveEntry CompleteRepair(RepairSession session, string newName, CancellationToken cancellationToken = default)
    {
        var destination = NewSavePath(session.Source.Location, newName);
        session.WriteRepairedSave(destination, cancellationToken);
        return ReadEntry(destination, session.Source.Location, isBackup: false);
    }

    public SaveEntry Backup(SaveEntry source, string? note = null)
    {
        if (source.IsBackup)
        {
            throw new UserFacingException("Only a live save can be backed up.");
        }

        var directory = source.Location.BackupDirectory;
        var stem = $"{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}-{Path.GetFileNameWithoutExtension(source.FileName)}";
        var destination = Path.Combine(directory, stem + ".vcdbs");
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        var temporaryNote = temporary + ".note";
        var notePath = BackupNotes.PathFor(destination);
        var databasePublished = false;
        var notePublished = false;
        note = string.IsNullOrWhiteSpace(note) ? null : note;
        try
        {
            BackupNotes.EnsureDirectory(directory);
            Directory.CreateDirectory(directory);
            destination = UniqueBackupPath(directory, stem);
            notePath = BackupNotes.PathFor(destination);
            SnapshotDatabase(source.Path, temporary);
            using (var connection = Open(temporary, SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                // A staged snapshot is exclusively ours. Close WAL mode before publication
                // so validation does not leave temporary -wal/-shm files behind.
                command.CommandText = "PRAGMA journal_mode=DELETE";
                command.ExecuteScalar();
                command.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                {
                    throw new UserFacingException("Backup failed SQLite integrity validation.");
                }
            }

            _ = ReadMetadata(temporary);
            if (note is not null)
            {
                using var stream = new FileStream(temporaryNote, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, BackupNotes.Encoding);
                writer.Write(note);
            }

            BackupNotes.EnsureDirectory(directory);
            if (PathOccupied(notePath))
            {
                throw new UserFacingException("A note already exists for the chosen backup filename. Try creating the backup again.");
            }

            File.Move(temporary, destination, overwrite: false);
            databasePublished = true;
            if (note is not null)
            {
                File.Move(temporaryNote, notePath, overwrite: false);
                notePublished = true;
            }

            return ReadEntry(destination, source.Location, isBackup: true);
        }
        catch (Exception exception)
        {
            if (notePublished) TryDelete(notePath);
            if (databasePublished) TryDelete(destination);
            if (exception is IOException or UnauthorizedAccessException or SqliteException or System.Text.EncoderFallbackException)
            {
                throw new UserFacingException($"Could not create backup: {exception.Message}", exception);
            }

            throw;
        }
        finally
        {
            TryDelete(temporary);
            TryDelete(temporaryNote);
            TryDelete(temporary + "-wal");
            TryDelete(temporary + "-shm");
        }
    }

    public SaveEntry Restore(RestoreRequest request)
    {
        EnsureManagedDestination(request.DestinationPath, request.Location);
        var exists = File.Exists(request.DestinationPath);

        if (exists && request.BackupExisting)
        {
            Backup(ReadEntry(request.DestinationPath, request.Location, isBackup: false));
        }

        var destinationDirectory = Path.GetDirectoryName(request.DestinationPath)
            ?? throw new UserFacingException("Restore destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(request.DestinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            SnapshotDatabase(request.Backup.Path, temporary);
            if (request.WorldName is not null)
            {
                SetWorldNameInDatabase(temporary, request.WorldName);
            }

            _ = ReadMetadata(temporary);
            File.Move(temporary, request.DestinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return ReadEntry(request.DestinationPath, request.Location, isBackup: false);
    }

    public SaveEntry RenameWorld(SaveEntry save, string newWorldName)
    {
        if (save.IsBackup)
        {
            throw new UserFacingException("Select a live save to rename its world.");
        }

        ValidateWorldName(newWorldName);
        var directory = Path.GetDirectoryName(save.Path)!;
        var temporary = Path.Combine(directory, $".{save.FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            SnapshotDatabase(save.Path, temporary);
            SetWorldNameInDatabase(temporary, newWorldName);
            _ = ReadMetadata(temporary);
            File.Move(temporary, save.Path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        return ReadEntry(save.Path, save.Location, isBackup: false);
    }

    public void DeleteBackup(SaveEntry backup)
    {
        if (!backup.IsBackup || !IsPathInside(backup.Path, backup.Location.BackupDirectory))
        {
            throw new UserFacingException("Refusing to delete a file outside a managed backup directory.");
        }

        if (!_locations.Contains(backup.Location) ||
            !ListBackups().Any(entry => string.Equals(entry.Path, backup.Path, StringComparison.Ordinal)))
        {
            throw new UserFacingException("Refusing to delete a file that was not discovered as a managed backup.");
        }

        try
        {
            BackupNotes.EnsureDirectory(backup.Location.BackupDirectory);
            File.Delete(backup.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException($"Could not delete backup '{backup.FileName}': {exception.Message}", exception);
        }

        BackupNotes.DeleteQuietly(backup.Path, backup.Location);
    }

    public string NewSavePath(SaveLocation location, string fileNameWithoutExtension)
    {
        var name = ValidateFileName(fileNameWithoutExtension);
        var path = Path.Combine(location.SavesDirectory, $"{name}.vcdbs");
        EnsureManagedDestination(path, location);
        if (File.Exists(path))
        {
            throw new UserFacingException($"A save named '{name}.vcdbs' already exists.");
        }

        return path;
    }

    internal static string ValidateFileName(string value)
    {
        var name = value.Trim();
        if (name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^6];
        }

        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            throw new UserFacingException("Enter a valid filename without directories or the .vcdbs extension.");
        }

        return name;
    }

    internal static void ValidateWorldName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UserFacingException("World name cannot be empty.");
        }

        if (value.Length > 200)
        {
            throw new UserFacingException("World name cannot exceed 200 characters.");
        }
    }

    private IReadOnlyList<SaveEntry> ListEntries(bool backups)
    {
        var results = new List<SaveEntry>();
        foreach (var location in _locations)
        {
            var directory = backups ? location.BackupDirectory : location.SavesDirectory;
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.vcdbs", SearchOption.TopDirectoryOnly))
            {
                results.Add(ReadEntry(path, location, backups));
            }
        }

        return results
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenBy(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SaveEntry ReadEntry(string path, SaveLocation location, bool isBackup)
    {
        SaveMetadata? metadata = null;
        string? error = null;
        try
        {
            metadata = ReadMetadata(path);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        var (note, noteError) = isBackup ? BackupNotes.Read(path, location) : (null, (string?)null);
        var fileName = Path.GetFileName(path);
        var prefix = isBackup ? "backup/" : string.Empty;
        return new SaveEntry(
            $"{location.Name}:{prefix}{fileName}",
            Path.GetFullPath(path),
            fileName,
            location,
            isBackup,
            GetCreationTime(path),
            metadata,
            error,
            note,
            noteError);
    }

    private SaveMetadata ReadMetadata(string path) => _codec.Read(ReadGameData(path));

    internal static byte[] ReadGameData(string path)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM gamedata ORDER BY savegameid LIMIT 2";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                throw new UserFacingException("Database has no gamedata payload.");
            }

            var payload = (byte[])reader.GetValue(0);
            if (reader.Read())
            {
                throw new UserFacingException("Database contains multiple gamedata rows.");
            }

            return payload;
        }
        catch (SqliteException exception)
        {
            throw new UserFacingException($"'{Path.GetFileName(path)}' is not a readable Vintage Story database: {exception.Message}", exception);
        }
    }

    private void SetWorldNameInDatabase(string path, string worldName)
    {
        ValidateWorldName(worldName);
        var updated = _codec.SetWorldName(ReadGameData(path), worldName.Trim());
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE gamedata SET data = $data";
        command.Parameters.AddWithValue("$data", updated);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new UserFacingException("Expected to update exactly one gamedata row.");
        }

        transaction.Commit();
    }

    internal static void SnapshotDatabase(string sourcePath, string destinationPath)
    {
        var temporary = $"{destinationPath}.{Guid.NewGuid():N}.snapshot";
        try
        {
            using (var source = Open(sourcePath, SqliteOpenMode.ReadOnly))
            using (var destination = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(destination);
            }

            File.Move(temporary, destinationPath, overwrite: true);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new UserFacingException($"Could not copy '{Path.GetFileName(sourcePath)}': {exception.Message}", exception);
        }
    }

    internal static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static SaveEntry Resolve(string identifier, IReadOnlyList<SaveEntry> entries, string kind)
    {
        var fullPath = Path.IsPathFullyQualified(identifier) ? Path.GetFullPath(identifier) : null;
        var matches = entries.Where(entry =>
                (fullPath is not null && string.Equals(entry.Path, fullPath, StringComparison.Ordinal)) ||
                string.Equals(entry.Id, identifier, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FileName, identifier, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new UserFacingException($"No {kind} matches '{identifier}'. Run 'vsm list {kind}s' to see valid IDs."),
            _ => throw new UserFacingException($"'{identifier}' is ambiguous. Use the displayed ID or full path.")
        };
    }

    internal static string UniqueBackupPath(string directory, string stem)
    {
        var candidate = Path.Combine(directory, stem + ".vcdbs");
        for (var suffix = 2; PathOccupied(candidate) || PathOccupied(BackupNotes.PathFor(candidate)); suffix++)
        {
            candidate = Path.Combine(directory, $"{stem}-{suffix}.vcdbs");
        }

        return candidate;
    }

    private static bool PathOccupied(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static DateTime GetCreationTime(string path)
    {
        var created = File.GetCreationTime(path);
        return created.Year > 1971 ? created : File.GetLastWriteTime(path);
    }

    internal static void EnsureManagedDestination(string path, SaveLocation location)
    {
        if (!IsPathInside(path, location.SavesDirectory) || IsPathInside(path, location.BackupDirectory))
        {
            throw new UserFacingException("Save destinations must be directly inside a detected Saves directory.");
        }

        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(location.SavesDirectory), StringComparison.Ordinal))
        {
            throw new UserFacingException("Save destinations cannot contain subdirectories.");
        }
    }

    private static bool IsPathInside(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original operation error.
        }
    }
}
