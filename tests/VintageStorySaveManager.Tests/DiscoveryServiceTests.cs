using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class DiscoveryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vsm-tests-{Guid.NewGuid():N}");

    public DiscoveryServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FindsBothFlatpakAndNativeRoots()
    {
        Directory.CreateDirectory(Path.Combine(_directory, ".var", "app", "at.vintagestory.VintageStory", "config", "VintagestoryData", "Saves"));
        Directory.CreateDirectory(Path.Combine(_directory, ".config", "VintagestoryData", "Saves"));

        var locations = new DiscoveryService(_directory, new Dictionary<string, string?>()).FindSaveLocations();

        Assert.Equal(["flatpak", "native"], locations.Select(location => location.Name));
    }

    [Fact]
    public void OverrideAcceptsSavesDirectoryDirectly()
    {
        var saves = Path.Combine(_directory, "custom", "Saves");
        Directory.CreateDirectory(saves);
        var environment = new Dictionary<string, string?> { ["VSM_DATA_ROOTS"] = saves };

        var location = Assert.Single(new DiscoveryService(_directory, environment).FindSaveLocations());

        Assert.Equal(Path.GetFullPath(saves), location.SavesDirectory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

