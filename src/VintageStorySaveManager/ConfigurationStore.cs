using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintageStorySaveManager;

internal sealed record ApplicationSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; init; } = "Default";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; init; }
}

internal sealed class ConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public string FilePath { get; }

    public ConfigurationStore(string? configRoot = null)
    {
        configRoot ??= Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(configRoot) || !Path.IsPathFullyQualified(configRoot))
            throw new UserFacingException("Cannot locate the user configuration directory. Set XDG_CONFIG_HOME to an absolute path.");
        _directory = Path.Combine(configRoot, "vs-save-manager");
        FilePath = Path.Combine(_directory, "config.json");
    }

    public ApplicationSettings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<ApplicationSettings>(stream, JsonOptions)
                ?? throw new JsonException("Expected a JSON object.");
            if (string.IsNullOrWhiteSpace(settings.Theme))
                throw new JsonException("'theme' must be a nonempty string.");
            return settings;
        }
        catch (FileNotFoundException) { return new ApplicationSettings(); }
        catch (DirectoryNotFoundException) { return new ApplicationSettings(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new UserFacingException($"Cannot read configuration '{FilePath}': {exception.Message} Fix the file or move it aside to use defaults.", exception);
        }
    }

    public void SaveTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
            throw new UserFacingException("The theme name must be a nonempty string.");
        // Read again to preserve other settings, including edits made since startup.
        var settings = Load() with { Theme = theme };
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException($"Cannot save configuration '{FilePath}': {exception.Message} Check directory permissions and available space. The theme was not changed.", exception);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
