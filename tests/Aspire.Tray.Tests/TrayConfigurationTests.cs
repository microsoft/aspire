// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayConfigurationTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("10", 10)]
    [InlineData("50", 50)]
    [InlineData("\"0\"", 0)]
    [InlineData("\"1\"", 1)]
    [InlineData("\"10\"", 10)]
    [InlineData("\"50\"", 50)]
    public void LoadsNumericAndCliStringLimitsWithoutChangingOtherSettings(string value, int expected)
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");
        var contents = $$"""
            {
              // Existing CLI configuration is not owned by the tray.
              "features": { "example": "preserve" },
              "tray": { "recentAppHostLimit": {{value}}, },
            }
            """;
        File.WriteAllText(path, contents);

        Assert.Equal(expected, TrayConfiguration.LoadRecentAppHostLimit(path));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tray\":{}}")]
    [InlineData("{\"features\":{\"example\":\"preserve\"}}")]
    public void MissingSettingUsesTen(string contents)
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");
        File.WriteAllText(path, contents);

        Assert.Equal(10, TrayConfiguration.LoadRecentAppHostLimit(path));
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Fact]
    public void MissingFileUsesTenWithoutCreatingIt()
    {
        using var directory = new TestTrayStateDirectory();
        Assert.Equal(10, TrayConfiguration.LoadRecentAppHostLimit(directory.StatePath));
        Assert.False(File.Exists(directory.StatePath));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("51")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    [InlineData("\"-1\"")]
    [InlineData("\"51\"")]
    [InlineData("\"1.5\"")]
    [InlineData("\"2147483648\"")]
    [InlineData("\"invalid\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void InvalidPresentSettingIsActionableAndNeverFallsBack(string value)
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");
        var contents = $$"""{ "tray": { "recentAppHostLimit": {{value}} } }""";
        File.WriteAllText(path, contents);

        AssertConfigurationError(path);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"tray\":null}")]
    [InlineData("{\"tray\":10}")]
    [InlineData("{\"tray\":{\"recentAppHostLimit\":1,\"recentAppHostLimit\":2}}")]
    [InlineData("{\"tray\":{\"recentAppHostLimit\":1},\"tray:recentAppHostLimit\":2}")]
    public void MalformedConfigurationIsActionableAndPreserved(string contents)
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");
        File.WriteAllText(path, contents);

        AssertConfigurationError(path);
        Assert.Equal(contents, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\"TRAY\":{\"RECENTAPPHOSTLIMIT\":\"1\"}}")]
    [InlineData("{\"tray:recentAppHostLimit\":1}")]
    public void SupportsCliCaseInsensitiveAndFlattenedConfigurationKeys(string contents)
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");
        File.WriteAllText(path, contents);

        Assert.Equal(1, TrayConfiguration.LoadRecentAppHostLimit(path));
    }

    [Fact]
    public void UnreadableConfigurationIsNotTreatedAsMissing()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.CreateAppHost("config/aspire.config.json");

        AssertConfigurationError(Path.GetDirectoryName(path)!);
    }

    [Theory]
    [InlineData("script", "installed/bin/aspire", "installed")]
    [InlineData("localhive", "installed/bin/aspire", "installed")]
    [InlineData("pr", "installed/dogfood/pr-123/bin/aspire", "installed")]
    [InlineData("brew", "installed/bin/aspire", "configured")]
    [InlineData("unknown", "installed/bin/aspire", "configured")]
    [InlineData("pr", "installed/not-dogfood/pr-123/bin/aspire", "configured")]
    public void SelectsTheCliCanonicalGlobalSettingsPath(string source, string relativeBinary, string expectedHome)
    {
        using var directory = new TestTrayStateDirectory();
        var marker = directory.CreateAppHost("marker");
        var root = Path.GetDirectoryName(marker)!;
        var binary = directory.CreateAppHost(relativeBinary);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(binary)!, ".aspire-install.json"),
            $$"""{"source":"{{source}}"}""");

        Assert.Equal(Path.Combine(root, expectedHome, "aspire.config.json"),
            TrayConfiguration.GetSettingsPath(binary, Path.Combine(root, "configured"), Path.Combine(root, "user")));
    }

    [Fact]
    public void DefaultGlobalSettingsPathIsInExplicitUserProfile()
    {
        using var directory = new TestTrayStateDirectory();
        var binary = directory.CreateAppHost("bin/aspire");
        var user = Path.GetDirectoryName(binary)!;

        Assert.Equal(Path.Combine(user, ".aspire", "aspire.config.json"),
            TrayConfiguration.GetSettingsPath(binary, null, user));
    }

    private static void AssertConfigurationError(string path)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => TrayConfiguration.LoadRecentAppHostLimit(path));
        Assert.Equal($"Unable to read tray.recentAppHostLimit from '{path}'. The file must contain a valid JSON object "
            + "and tray.recentAppHostLimit must be an integer from 0 to 50. "
            + "Fix the file or run 'aspire config set tray.recentAppHostLimit 10 --global', then restart the tray.", exception.Message);
    }
}
