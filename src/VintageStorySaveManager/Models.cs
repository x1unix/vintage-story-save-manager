using System.Globalization;

namespace VintageStorySaveManager;

internal sealed record SaveLocation(
    string Name,
    string ConfigRoot,
    string SavesDirectory,
    string BackupDirectory);

internal sealed record SaveMetadata(
    string WorldName,
    int TotalSecondsPlayed,
    string? CreatedGameVersion,
    string? LastSavedGameVersion,
    string? LastPlayed)
{
    public string PlayedDisplay
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, TotalSecondsPlayed));
            var hours = (int)duration.TotalHours;
            return $"{hours}h {duration.Minutes}m";
        }
    }
}

internal sealed record SaveEntry(
    string Id,
    string Path,
    string FileName,
    SaveLocation Location,
    bool IsBackup,
    DateTime CreatedAt,
    SaveMetadata? Metadata,
    string? Error,
    string? Note = null,
    string? NoteError = null)
{
    public string WorldName => Metadata?.WorldName ?? "<unreadable>";
    public string Played => Metadata?.PlayedDisplay ?? "—";
    public string Created => CreatedAt.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
    public string NoteDisplay => BackupNotes.Display(NoteError ?? Note);

    public string DisplayLine =>
        $"{FileName,-35}  {WorldName,-28}  {Played,9}  {Created,-20}  [{Location.Name}]" + (IsBackup ? $"  {NoteDisplay}" : string.Empty);
}

internal sealed record RestoreRequest(
    SaveEntry Backup,
    string DestinationPath,
    SaveLocation Location,
    string? WorldName,
    bool BackupExisting);

internal sealed class UserFacingException(string message, Exception? inner = null)
    : Exception(message, inner);

