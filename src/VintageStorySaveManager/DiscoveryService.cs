namespace VintageStorySaveManager;

internal sealed class DiscoveryService
{
    private const string FlatpakId = "at.vintagestory.VintageStory";
    private readonly string _home;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    public DiscoveryService(
        string? home = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _environment = environment ?? Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value?.ToString(), StringComparer.Ordinal);
    }

    public IReadOnlyList<SaveLocation> FindSaveLocations()
    {
        var overrideRoots = GetEnvironment("VSM_DATA_ROOTS");
        var candidates = new List<(string Name, string Root)>();

        if (!string.IsNullOrWhiteSpace(overrideRoots))
        {
            var roots = overrideRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            candidates.AddRange(roots.Select((root, index) => ($"custom-{index + 1}", ExpandHome(root))));
        }
        else
        {
            candidates.Add(("flatpak", Path.Combine(_home, ".var", "app", FlatpakId, "config")));
            candidates.Add(("native", Path.Combine(_home, ".config")));
        }

        var results = new List<SaveLocation>();
        foreach (var (name, configRoot) in candidates)
        {
            var saves = ResolveSavesDirectory(configRoot);
            if (!Directory.Exists(saves))
            {
                continue;
            }

            results.Add(new SaveLocation(
                name,
                Path.GetFullPath(configRoot),
                Path.GetFullPath(saves),
                Path.GetFullPath(Path.Combine(saves, "backup"))));
        }

        return results;
    }

    public string FindGameDirectory()
    {
        foreach (var variable in new[] { "VSM_GAME_DIR", "VINTAGE_STORY" })
        {
            var value = GetEnvironment(variable);
            if (!string.IsNullOrWhiteSpace(value) && IsGameDirectory(ExpandHome(value)))
            {
                return Path.GetFullPath(ExpandHome(value));
            }
        }

        var directCandidates = new[]
        {
            Path.Combine(_home, ".local", "share", "vintagestory"),
            Path.Combine(_home, "Vintagestory"),
            "/opt/vintagestory",
            "/usr/share/vintagestory"
        };

        foreach (var candidate in directCandidates.Where(IsGameDirectory))
        {
            return Path.GetFullPath(candidate);
        }

        var flatpakRoots = new[]
        {
            Path.Combine(_home, ".local", "share", "flatpak", "app", FlatpakId),
            Path.Combine("/var/lib/flatpak/app", FlatpakId)
        };

        foreach (var root in flatpakRoots)
        {
            var found = FindFlatpakGameDirectory(root);
            if (found is not null)
            {
                return found;
            }
        }

        throw new UserFacingException(
            "Vintage Story's installation directory was not found. Set VSM_GAME_DIR to the directory containing VintagestoryLib.dll.");
    }

    private string? FindFlatpakGameDirectory(string appRoot)
    {
        if (!Directory.Exists(appRoot))
        {
            return null;
        }

        try
        {
            foreach (var arch in Directory.EnumerateDirectories(appRoot))
                foreach (var branch in Directory.EnumerateDirectories(arch))
                {
                    var active = Path.Combine(branch, "active", "files", "extra", "vintagestory");
                    if (IsGameDirectory(active))
                    {
                        return Path.GetFullPath(active);
                    }

                    foreach (var deployment in Directory.EnumerateDirectories(branch))
                    {
                        var candidate = Path.Combine(deployment, "files", "extra", "vintagestory");
                        if (IsGameDirectory(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
        }
        catch (UnauthorizedAccessException)
        {
            // Continue to the actionable error in FindGameDirectory.
        }

        return null;
    }

    private static bool IsGameDirectory(string path) =>
        File.Exists(Path.Combine(path, "VintagestoryLib.dll"));

    private static string ResolveSavesDirectory(string root)
    {
        if (string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "Saves", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return Path.Combine(root, "VintagestoryData", "Saves");
    }

    private string ExpandHome(string path) => path switch
    {
        "~" => _home,
        _ when path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal) =>
            Path.Combine(_home, path[2..]),
        _ => path
    };

    private string? GetEnvironment(string name) =>
        _environment.TryGetValue(name, out var value) ? value : null;
}
