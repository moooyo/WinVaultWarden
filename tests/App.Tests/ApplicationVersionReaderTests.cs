using App.Services;
using Xunit;

namespace App.Tests;

public sealed class ApplicationVersionReaderTests
{
    [Theory]
    [InlineData("2.3.4+0123abc", "2.3.4")]
    [InlineData("2.3.4-preview.2+source", "2.3.4-preview.2")]
    [InlineData("10.0.0", "10.0.0")]
    public void Select_UsesReleaseMetadataInsteadOfADevelopmentConstant(string value, string expected)
    {
        Assert.Equal(expected, ApplicationVersionReader.Select(value, new Version(0, 1, 0, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2.3.4\n")]
    public void Select_FallsBackToAssemblyVersionForMissingOrInvalidMetadata(string? value)
    {
        Assert.Equal("2.3.4.0", ApplicationVersionReader.Select(value, new Version(2, 3, 4, 0)));
    }

    [Fact]
    public void Select_DoesNotInventAVersionWhenBothSourcesAreAbsent() =>
        Assert.Equal("unknown", ApplicationVersionReader.Select(null, null));
}
