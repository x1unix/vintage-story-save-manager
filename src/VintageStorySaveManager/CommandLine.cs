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
        var result = _repository.Backup(source);
        WriteResult(result, options.Has("json"), $"Backup created: {result.Path}");
        return 0;
    }

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
        entry.Error
    };

    private static int Help()
    {
        Console.WriteLine("""
            Vintage Story Save Manager

            Usage:
              vsm                                      Open the interactive TUI
              vsm list [saves|backups] [--json]
              vsm backup --save <id|filename|path> [--json]
              vsm restore --backup <id|filename|path> --destination <id|filename|path>
                          [--backup-existing] [--world-name <name>] [--yes] [--json]
              vsm restore --backup <id|filename|path> --new <filename>
                          [--location <name>] [--world-name <name>] [--json]
              vsm delete-backup --backup <id|filename|path> [--yes]
              vsm rename --save <id|filename|path> --world-name <name> [--yes] [--json]

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
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public string Required(string name) => Get(name) ?? throw new UserFacingException($"Missing required option --{name}.");
}
