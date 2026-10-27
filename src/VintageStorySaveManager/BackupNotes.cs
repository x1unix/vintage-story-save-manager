using System.Globalization;
using System.Text;

namespace VintageStorySaveManager;

internal static class BackupNotes
{
    internal static readonly UTF8Encoding Encoding = new(false, true);

    public static string PathFor(string backupPath) => Path.ChangeExtension(backupPath, ".note.txt");

    // Check ancestors too: a regular sidecar inside a linked directory can escape containment.
    public static void EnsureDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null)
            {
                throw new UserFacingException("Backup notes require a managed backup directory without symbolic links in its path.");
            }
        }
    }

    public static (string? Note, string? Error) Read(string backupPath, SaveLocation location)
    {
        try
        {
            EnsureDirectory(location.BackupDirectory);
            if (!string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(backupPath)),
                    System.IO.Path.GetFullPath(location.BackupDirectory), StringComparison.Ordinal))
            {
                throw new UserFacingException("Note path is outside the managed backup directory.");
            }

            var path = PathFor(backupPath);
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new UserFacingException("Refusing to read a symbolic link as a backup note.");
            }

            var note = File.ReadAllText(path, Encoding);
            return (string.IsNullOrWhiteSpace(note) ? null : note, null);
        }
        catch (FileNotFoundException)
        {
            return (null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or UserFacingException)
        {
            return (null, $"Could not read backup note: {exception.Message}");
        }
    }

    public static void DeleteQuietly(string backupPath, SaveLocation location)
    {
        try
        {
            EnsureDirectory(location.BackupDirectory);
            // Deleting a link removes the link itself, never its target.
            File.Delete(PathFor(backupPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UserFacingException)
        {
            // Note cleanup must not turn a successful backup deletion into a failure.
        }
    }

    public static string Display(string? text, bool multiline = false) => text is null ? string.Empty :
        string.Concat(text.EnumerateRunes().Select(rune =>
            Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? multiline && rune.Value == '\n' ? "\n" : " "
                : rune.ToString()));
}
