using VintageStorySaveManager;

namespace VintageStorySaveManager.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData(0, "0h 0m")]
    [InlineData(17_760, "4h 56m")]
    [InlineData(41_280, "11h 28m")]
    public void FormatsPlayedTime(int seconds, string expected)
    {
        var metadata = new SaveMetadata("Outer Worlds", seconds, null, null, null);
        Assert.Equal(expected, metadata.PlayedDisplay);
    }

    [Theory]
    [InlineData("my save", "my save")]
    [InlineData("my save.vcdbs", "my save")]
    public void ValidatesFilename(string input, string expected) =>
        Assert.Equal(expected, SaveRepository.ValidateFileName(input));

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("nested/save")]
    public void RejectsUnsafeFilename(string input) =>
        Assert.Throws<UserFacingException>(() => SaveRepository.ValidateFileName(input));
}

