using System.Text.Json;
using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vsm-config-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DefaultPathUsesPlatformApplicationDataWithoutCreatingDirectories()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "vs-save-manager", "config.json");
        Assert.Equal(expected, new ConfigurationStore().FilePath);
    }

    [Fact]
    public void MissingConfigurationUsesDefaultsWithoutWriting()
    {
        var store = new ConfigurationStore(_root);
        Assert.Equal(Path.Combine(_root, "vs-save-manager", "config.json"), store.FilePath);
        Assert.Equal("Default", store.Load().Theme);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void SavesAndReplacesThemeWhilePreservingLatestUnknownSettings()
    {
        var store = new ConfigurationStore(_root);
        store.SaveTheme("Dark");
        Assert.Equal("Dark", new ConfigurationStore(_root).Load().Theme);
        File.WriteAllText(store.FilePath, """{"theme":"Dark","future":{"label":"世界 🌲","flags":[true,3]},"count":7} """);

        store.SaveTheme("Gray Scale");

        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        var root = document.RootElement;
        Assert.Equal("Gray Scale", root.GetProperty("theme").GetString());
        Assert.Equal("世界 🌲", root.GetProperty("future").GetProperty("label").GetString());
        Assert.Equal(3, root.GetProperty("future").GetProperty("flags")[1].GetInt32());
        Assert.Equal(7, root.GetProperty("count").GetInt32());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"theme\":null}")]
    [InlineData("{\"theme\":42}")]
    [InlineData("{\"theme\":\" \"}")]
    public void InvalidConfigurationIsReportedAndNeverOverwritten(string json)
    {
        var store = new ConfigurationStore(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, json);

        Assert.Contains(store.FilePath, Assert.Throws<UserFacingException>(() => store.Load()).Message);
        Assert.Throws<UserFacingException>(() => store.SaveTheme("Dark"));
        Assert.Equal(json, File.ReadAllText(store.FilePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!));
    }

    [Fact]
    public void MissingThemeDefaultsAndUnknownThemeNamesRemainAvailableForUiValidation()
    {
        var store = new ConfigurationStore(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{}");
        Assert.Equal("Default", store.Load().Theme);
        File.WriteAllText(store.FilePath, """{"theme":"Future theme"}""");
        Assert.Equal("Future theme", store.Load().Theme);
    }

    [Fact]
    public void FailedUpdatePreservesPreviousFileAndLeavesNoTemporaryFiles()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var store = new ConfigurationStore(_root);
        store.SaveTheme("Dark");
        var directory = Path.GetDirectoryName(store.FilePath)!;
        var mode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Contains(store.FilePath, Assert.Throws<UserFacingException>(() => store.SaveTheme("Light")).Message);
            Assert.Equal("Dark", store.Load().Theme);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { File.SetUnixFileMode(directory, mode); }
    }

    [Fact]
    public void ConfigurationDirectoryAtFilePathProducesActionableError()
    {
        var store = new ConfigurationStore(_root);
        Directory.CreateDirectory(store.FilePath);
        Assert.Contains(store.FilePath, Assert.Throws<UserFacingException>(() => store.Load()).Message);
        Assert.Throws<UserFacingException>(() => store.SaveTheme("Dark"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
