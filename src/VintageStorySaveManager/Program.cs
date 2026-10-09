namespace VintageStorySaveManager;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "help" or "--help" or "-h")
        {
            return PrintHelpWithoutInitialization();
        }

        try
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
            var discovery = new DiscoveryService();
            var locations = discovery.FindSaveLocations();
            if (locations.Count == 0)
            {
                throw new UserFacingException(
                    "No Vintage Story Saves directory was found under the Flatpak or native configuration roots. " +
                    "Set VSM_DATA_ROOTS to the configuration root or Saves directory.");
            }

            using var codec = new DynamicSaveCodec(discovery.FindGameDirectory());
            var repository = new SaveRepository(locations, codec);
            if (args.Length == 0)
            {
                new TuiApplication(repository).Run();
                return 0;
            }

            return new CommandLine(repository).Run(args);
        }
        catch (UserFacingException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"unexpected error: {exception.Message}");
            return 2;
        }
    }

    private static int PrintHelpWithoutInitialization()
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
            """);
        return 0;
    }
}
