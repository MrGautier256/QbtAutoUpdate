using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class QbtVersionTests
{
    [Theory]
    [InlineData("5.3.0", 5, 3, 0, 0)]
    [InlineData("5.1.2.10", 5, 1, 2, 10)]
    [InlineData("v5.3.0", 5, 3, 0, 0)]
    [InlineData(" 4.6.7 ", 4, 6, 7, 0)]
    [InlineData("5.3", 5, 3, 0, 0)]
    public void Parses_release_versions(string text, int major, int minor, int patch, int build)
    {
        Assert.True(QbtVersion.TryParse(text, out QbtVersion version));
        Assert.Equal(new QbtVersion(major, minor, patch, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("5.3.0rc1")]
    [InlineData("5.3.0beta1")]
    [InlineData("5.3.0.0.1")]
    [InlineData("5..0")]
    [InlineData("5.3.-1")]
    [InlineData("5.3.x")]
    [InlineData("1234567890.0.0")]
    public void Rejects_anything_else(string? text) => Assert.False(QbtVersion.TryParse(text, out _));

    [Fact]
    public void Three_and_four_components_compare_as_four()
    {
        Assert.Equal(QbtVersion.Parse("5.3.0"), QbtVersion.Parse("5.3.0.0"));
        Assert.True(QbtVersion.Parse("5.1.2.10") > QbtVersion.Parse("5.1.2"));
        Assert.True(QbtVersion.Parse("5.2.4") < QbtVersion.Parse("5.3.0"));
        Assert.True(QbtVersion.Parse("5.10.0") > QbtVersion.Parse("5.9.9"));
    }

    [Theory]
    [InlineData("5.3.0", "5.3.0")]
    [InlineData("5.3.0.0", "5.3.0")]
    [InlineData("5.1.2.10", "5.1.2.10")]
    public void Formats_like_folder_names(string text, string expected) => Assert.Equal(expected, QbtVersion.Parse(text).ToString());
}
