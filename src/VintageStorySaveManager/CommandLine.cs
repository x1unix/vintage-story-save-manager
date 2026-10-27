using System.Text.Json;

namespace VintageStorySaveManager;

internal sealed class CommandLine
{
    private readonly SaveRepository _repository;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CommandLine(SaveRepository repository) => _repository = repository;

    public int Run(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("No command supplied.");
        }

        var command = args[0].ToLowerInvariant();
        var options = ParsedOptions.Parse(args[1..]);
        return command switch
        {
            "list" => List(options),
            "backup" => Backup(options),
            "restore" => Restore(options),
            "delete-backup" => DeleteBackup(options),
            "rename" => Rename(options),
            "repair" => Repair(options),
            "help" or "--help" or "-h" => Help(),
            _ => throw new UserFacingException($"Unknown command '{args[0]}'. Run 'vsm help' for usage.")
        };
    }

    private int List(ParsedOptions options)
    {
        var target = options.Positionals.SingleOrDefault() ?? "saves";
        var entries = target.ToLowerInvariant() switch
        {
            "saves" => _repository.ListSaves(),
            "backups" => _repository.ListBackups(),
            _ => throw new UserFacingException("List target must be 'saves' or 'backups'.")
        };

        WriteEntries(entries, options.Has("json"));
        return 0;
    }

    private int Backup(ParsedOptions options)
    {
        var source = _repository.ResolveSave(options.Required("save"));
        EnsureReadable(source);
        var note = options.Has("note") ? options.Required("note") : null;
        if (ShouldPromptForNote(options.Has("note"), options.Has("json"), Console.IsInputRedirected))
        {
            Console.Error.Write("Backup note (optional; Enter to skip): ");
            note = Console.ReadLine();
        }

        var result = _repository.Backup(source, note);
        WriteResult(result, options.Has("json"), $"Backup created: {result.Path}");
        return 0;
    }

    internal static bool ShouldPromptForNote(bool hasNote, bool json, bool inputRedirected) =>
        !hasNote && !json && !inputRedirected;

    private int Restore(ParsedOptions options)
    {
        var backup = _repository.ResolveBackup(options.Required("backup"));
        EnsureReadable(backup);
        var destinationId = options.Get("destination");
        var newName = options.Get("new");
        if ((destinationId is null) == (newName is null))
        {
            throw new UserFacingException("Specify exactly one of --destination or --new.");
        }

        string destinationPath;
        SaveLocation location;
        if (destinationId is not null)
        {
            var destination = _repository.ResolveSave(destinationId);
            destinationPath = destination.Path;
            location = destination.Location;
            RequireConfirmation(options, $"Replace '{destination.FileName}' with '{backup.FileName}'?");
        }
        else
        {
            location = ResolveLocation(options.Get("location"), backup.Location);
            destinationPath = _repository.NewSavePath(location, newName!);
        }

        var worldName = options.Get("world-name");
        var result = _repository.Restore(new RestoreRequest(
            backup,
            destinationPath,
            location,
            worldName,
            options.Has("backup-existing")));
        WriteResult(result, options.Has("json"), $"Restored save: {result.Path}");
        return 0;
    }

    private int DeleteBackup(ParsedOptions options)
    {
        var backup = _repository.ResolveBackup(options.Required("backup"));
        RequireConfirmation(options, $"Permanently delete backup '{backup.FileName}'?");
        _repository.DeleteBackup(backup);
        Console.WriteLine($"Deleted backup: {backup.Path}");
        return 0;
    }

    private int Rename(ParsedOptions options)
    {
        var save = _repository.ResolveSave(options.Required("save"));
        EnsureReadable(save);
        var worldName = options.Required("world-name");
        RequireConfirmation(options, $"Rename world '{save.WorldName}' to '{worldName}' in {save.FileName}?");
        var result = _repository.RenameWorld(save, worldName);
        WriteResult(result, options.Has("json"), $"World renamed to '{result.WorldName}'.");
        return 0;
    }

    private int Repair(ParsedOptions options)
    {
        options.ValidateAllowed("save", "backup", "new", "json");
        var source = _repository.ResolveSave(options.Required("save"));
        var backup = options.Has("backup") ? _repository.ResolveBackup(options.Required("backup")) : null;
        var newName = options.Has("new") ? options.Required("new") : null;
        if (newName is not null)
        {
            if (backup is null)
            {
                throw new UserFacingException("Creating a repaired save requires --backup. Omit --new to scan only.");
            }

            _ = _repository.NewSavePath(source.Location, newName);
        }

        using var session = _repository.PrepareRepair(source, backup);
        var result = newName is null ? null : _repository.CompleteRepair(session, newName);
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Mode = result is null ? "scan" : "repair",
                session.Report,
                Output = result is null ? null : ToJson(result),
                Limitations = RepairReport.Limitations
            }, JsonOptions));
        }
        else
        {
            Console.WriteLine(session.Report.Summary);
            Console.WriteLine($"Source played: {session.Report.SourcePlayedSeconds} seconds; backup played: {session.Report.BackupPlayedSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not selected"} seconds.");
            foreach (var issue in session.Report.Issues)
            {
                Console.WriteLine($"  {issue.Table} {issue.Position}: {issue.Reason} Replacement: {(issue.ReplacementAvailable ? "available" : issue.BackupError)}");
            }

            Console.WriteLine(result is null ? "Scan only; no save was created. Supply --backup and --new to repair." : $"Repaired save created: {result.Path}");
            Console.WriteLine(RepairReport.Limitations);
        }

        return 0;
    }

    private SaveLocation ResolveLocation(string? name, SaveLocation fallback)
    {
        if (name is null)
        {
            return fallback;
        }

        return _repository.Locations
            .SingleOrDefault(location => string.Equals(location.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new UserFacingException($"No detected save location is named '{name}'.");
    }

    private static void RequireConfirmation(ParsedOptions options, string prompt)
    {
        if (options.Has("yes"))
        {
            return;
        }

        if (Console.IsInputRedirected)
        {
            throw new UserFacingException("Confirmation is required; pass --yes for non-interactive use.");
        }

        Console.Error.Write($"{prompt} [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException("Operation cancelled.");
        }
    }

    private static void EnsureReadable(SaveEntry entry)
    {
        if (entry.Error is not null)
        {
            throw new UserFacingException($"Cannot use '{entry.FileName}': {entry.Error}");
        }
    }

    private static void WriteEntries(IReadOnlyList<SaveEntry> entries, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(entries.Select(ToJson), JsonOptions));
            return;
        }

        if (entries.Count == 0)
        {
            Console.WriteLine("No entries found.");
            return;
        }

        Console.WriteLine($"{"ID",-46}  {"FILENAME",-35}  {"WORLD",-28}  {"PLAYED",9}  {"CREATED",-20}");
        foreach (var entry in entries)
        {
            Console.WriteLine($"{entry.Id,-46}  {entry.FileName,-35}  {entry.WorldName,-28}  {entry.Played,9}  {entry.Created,-20}");
            if (entry.IsBackup)
            {
                Console.WriteLine($"  Note: {entry.NoteDisplay}");
            }

            if (entry.Error is not null)
            {
                Console.WriteLine($"  error: {entry.Error}");
            }
        }
    }

    private static void WriteResult(SaveEntry entry, bool json, string message)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(ToJson(entry), JsonOptions) : message);
    }

    private static object ToJson(SaveEntry entry) => new
    {
        entry.Id,
        entry.Path,
        entry.FileName,
        entry.WorldName,
        TotalSecondsPlayed = entry.Metadata?.TotalSecondsPlayed,
        Played = entry.Played,
        CreatedAt = entry.CreatedAt,
        Created = entry.Created,
        Location = entry.Location.Name,
        entry.IsBackup,
        entry.Note,
        entry.NoteError,
        entry.Error
    };

    private static int Help()
    {
        Console.WriteLine("""
            Vintage Story Save Manager

            Usage:
              vsm                                      Open the interactive TUI
              vsm list [saves|backups] [--json]
              vsm backup --save <id|filename|path> [--note <text>] [--json]
              vsm restore --backup <id|filename|path> --destination <id|filename|path>
                          [--backup-existing] [--world-name <name>] [--yes] [--json]
              vsm restore --backup <id|filename|path> --new <filename>
                          [--location <name>] [--world-name <name>] [--json]
              vsm delete-backup --backup <id|filename|path> [--yes]
              vsm rename --save <id|filename|path> --world-name <name> [--yes] [--json]
              vsm repair --save <id|filename|path> [--backup <id|filename|path>]
                         [--new <filename>] [--json]

            Backup prompts for an optional note in interactive use. --json and redirected input
            skip the prompt; use --note to supply a note explicitly.

            Repair scans only unless --new is supplied. Writing requires a compatible backup
            and a new filename; source saves are never replaced. Close the world before repair.

            Environment:
              VSM_DATA_ROOTS   Colon-separated config roots (or Saves directories)
              VSM_GAME_DIR     Game directory containing VintagestoryLib.dll
              VINTAGE_STORY    Alternate game-directory override
            """);
        return 0;
    }
}

internal sealed class ParsedOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];

    public static ParsedOptions Parse(IEnumerable<string> arguments)
    {
        var parsed = new ParsedOptions();
        var args = arguments.ToArray();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positionals.Add(argument);
                continue;
            }

            var name = argument[2..];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new UserFacingException("Invalid empty option.");
            }

            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            if (!parsed._values.TryAdd(name, value))
            {
                throw new UserFacingException($"Option --{name} was supplied more than once.");
            }
        }

        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);
    public void ValidateAllowed(params string[] names)
    {
        if (Positionals.Count != 0 || _values.Keys.Any(key => !names.Contains(key, StringComparer.OrdinalIgnoreCase)))
        {
            throw new UserFacingException("Unknown repair argument. Use --save, --backup, --new, and --json; repair never replaces an existing save.");
        }

        if (_values.TryGetValue("json", out var value) && value is not null)
        {
            throw new UserFacingException("--json does not take a value.");
        }
    }
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Required(string name) => Get(name) ?? throw new UserFacingException($"Missing required option --{name}.");
}
