// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Tests;

public class AppHostPresentationTests
{
    [Theory]
    [InlineData("Stopped")]
    [InlineData("Stopping AppHost...")]
    [InlineData("Starting AppHost; waiting for discovery...")]
    [InlineData("Shop \u00b7 PID 42")]
    [InlineData("AppHost no longer available")]
    public void FullMenuDetailsCombineOriginalCasePathAndDetailsOnOneLine(string subtitle)
    {
        const string Path = "C:\\src\\A&B\\Shop.AppHost.cs";

        Assert.Equal($"{Path} \u00b7 {subtitle}", AppHostPresentation.GetMenuDetailsText(Path, subtitle));
    }

    [Fact]
    public void MenuDetailsSanitizeLineBreaksAndKeepLiteralAmpersands()
    {
        Assert.Equal("C:\\src\\A&B Shop\\AppHost.cs \u00b7 Stop failed. Try again. More details.",
            AppHostPresentation.GetMenuDetailsText("C:\\src\\A&B\tShop\\AppHost.cs", "Stop failed.\r\nTry again.\u2028More details."));
    }

    [Theory]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    [InlineData(100)]
    public void MenuDetailsLimitTheEntirePathAndStatusTo45Characters(int length)
    {
        const string Suffix = " \u00b7 Stopped";
        var path = "C:\\" + new string('a', length - 3 - Suffix.Length);
        var full = AppHostPresentation.GetMenuDetailsText(path, "Stopped");
        var expected = length <= 45 ? full : full[..22] + "\u2026" + full[^22..];
        var label = AppHostPresentation.GetMenuDetailsLabel(full);

        Assert.Equal(path + Suffix, full);
        Assert.Equal(expected, label);
        Assert.Equal(Math.Min(length, 45), label.Length);
    }

    [Theory]
    [InlineData("e\u0301")]
    [InlineData("\U0001F600")]
    [InlineData("\U0001F469\u200D\U0001F4BB")]
    public void MenuDetailsPreserveUnicodeAtBoth45CharacterTruncationBoundaries(string grapheme)
    {
        var path = string.Concat(Enumerable.Repeat(grapheme, 50));
        var full = AppHostPresentation.GetMenuDetailsText(path, "Stopped");
        var expected = string.Concat(Enumerable.Repeat(grapheme, 22)) + "\u2026"
            + string.Concat(Enumerable.Repeat(grapheme, 12)) + " \u00b7 Stopped";
        var label = AppHostPresentation.GetMenuDetailsLabel(full);

        Assert.Equal(path + " \u00b7 Stopped", full);
        Assert.Equal(expected, label);
        Assert.Equal(45, StringInfo.ParseCombiningCharacters(label).Length);
    }

    [Theory]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(100)]
    public void PathLabelsLimitLengthAndKeepBothEnds(int length)
    {
        const string Prefix = "C:\\";
        const string Suffix = "\\apphost.cs";
        var path = Prefix + new string('a', length - Prefix.Length - Suffix.Length) + Suffix;
        var expected = length <= 44 ? path : path[..21] + "\u2026" + path[^22..];

        var label = AppHostPresentation.GetPathLabel(path);

        Assert.Equal(expected, label);
        Assert.Equal(Math.Min(length, 44), StringInfo.ParseCombiningCharacters(label).Length);
    }

    [Theory]
    [InlineData("e\u0301")]
    [InlineData("\U0001F600")]
    [InlineData("\U0001F469\u200D\U0001F4BB")]
    public void PathLabelsPreserveGraphemesAtBothTruncationBoundaries(string grapheme)
    {
        var path = "C:\\" + string.Concat(Enumerable.Repeat(grapheme, 60)) + "\\apphost.cs";
        var expected = "C:\\" + string.Concat(Enumerable.Repeat(grapheme, 18)) + "\u2026"
            + string.Concat(Enumerable.Repeat(grapheme, 11)) + "\\apphost.cs";

        var label = AppHostPresentation.GetPathLabel(path);

        Assert.Equal(expected, label);
        Assert.Equal(44, StringInfo.ParseCombiningCharacters(label).Length);
    }

    [Theory]
    [InlineData("C:\\src\\Shop\\AppHost.cs", "C:\\src\\Shop\\AppHost.cs")]
    [InlineData("/src/my\nproject\tname/apphost.cs", "/src/my project name/apphost.cs")]
    [InlineData("C:\\src\\A&B\\AppHost.cs", "C:\\src\\A&B\\AppHost.cs")]
    public void PathLabelsKeepShortPathsOnOneLine(string path, string expected)
        => Assert.Equal(expected, AppHostPresentation.GetPathLabel(path));

    [Theory]
    [InlineData(true, false, false, null, nameof(AppHostHealth.Healthy))]
    [InlineData(false, false, false, null, nameof(AppHostHealth.Unknown))]
    [InlineData(false, true, false, null, nameof(AppHostHealth.Unknown))]
    [InlineData(true, false, true, null, nameof(AppHostHealth.Healthy))]
    [InlineData(false, false, false, "Start failed.", nameof(AppHostHealth.Unknown))]
    [InlineData(true, false, false, null, nameof(AppHostHealth.Unhealthy))]
    [InlineData(true, false, false, null, nameof(AppHostHealth.Warning))]
    [InlineData(true, false, false, null, nameof(AppHostHealth.Unknown))]
    public void CompactMenuLabelsKeepOnlyTheNameForEveryState(bool running, bool starting, bool stopping,
        string? error, string health)
    {
        var host = new AppHostMenuItem(default, "Shop", "Long directory context and PID", "Shop",
            false, false, stopping, error)
        {
            IsRunning = running,
            IsStarting = starting,
            Health = Enum.Parse<AppHostHealth>(health)
        };

        Assert.Equal("Shop", AppHostPresentation.GetCompactMenuLabel(host));
    }

    [Theory]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(60)]
    public void CompactMenuLabelsLimitNameAndPreserveGraphemes(int count)
    {
        const string Grapheme = "e\u0301";
        var name = string.Concat(Enumerable.Repeat(Grapheme, count));
        var host = new AppHostMenuItem(default, name, "Context", name, false, false, false, null)
        {
            IsRunning = true
        };
        var expectedName = count <= 44 ? name
            : string.Concat(Enumerable.Repeat(Grapheme, 21)) + "\u2026" + string.Concat(Enumerable.Repeat(Grapheme, 22));

        var label = AppHostPresentation.GetCompactMenuLabel(host);

        Assert.Equal(expectedName, label);
        Assert.Equal(Math.Min(count, 44), StringInfo.ParseCombiningCharacters(label).Length);
    }

    [Fact]
    public void MenuSeparatesProjectNameFromDirectoryContextAndInstancePid()
    {
        var host = new AppHostInfo(Path.GetFullPath("worktree-a/apphost/apphost.cs"), 42, null);

        Assert.Equal("worktree-a", AppHostPresentation.GetTitle(host));
        Assert.Equal($"{Path.Combine("worktree-a", "apphost")} \u00b7 PID 42", AppHostPresentation.GetSubtitle(host));
        Assert.Equal($"apphost - {Path.Combine("worktree-a", "apphost")} (PID 42)", AppHostPresentation.GetLabel(host));
        Assert.Equal(Path.GetFullPath("worktree-a/apphost"), AppHostPresentation.GetDirectory(host));
    }

    [Theory]
    [InlineData("worktree-a/apphost.mts", "worktree-a")]
    [InlineData("worktree-a/AppHost/AppHost.csproj", "worktree-a")]
    [InlineData("worktree-a/Shop.AppHost/Shop.AppHost.csproj", "Shop")]
    [InlineData("worktree-a/Shop.AppHost/apphost.cs", "Shop")]
    [InlineData("worktree-a/custom-app.cs", "custom-app")]
    [InlineData("worktree\nbranch/AppHost/apphost.cs", "worktree branch")]
    [InlineData("worktree-a/my\nproject.AppHost/my\nproject.AppHost.csproj", "my project")]
    public void NamesPreferTheProjectOrWorktreeOverGenericAppHostNames(string path, string expected)
    {
        var host = new AppHostInfo(Path.GetFullPath(path), 42, null);

        Assert.Equal(expected, AppHostPresentation.GetDisplayName(host));
        Assert.Equal(expected, AppHostPresentation.GetTitle(host));
    }

    [Fact]
    public void SameProjectInDifferentWorktreesKeepsItsLocationVisible()
    {
        var first = new AppHostInfo(Path.GetFullPath("worktree-a/Shop.AppHost/Shop.AppHost.csproj"), 42, null);
        var second = new AppHostInfo(Path.GetFullPath("worktree-b/Shop.AppHost/Shop.AppHost.csproj"), 43, null);

        Assert.Equal("Shop", AppHostPresentation.GetTitle(first));
        Assert.Equal("Shop", AppHostPresentation.GetTitle(second));
        Assert.Equal($"{Path.Combine("worktree-a", "Shop.AppHost")} \u00b7 PID 42", AppHostPresentation.GetSubtitle(first));
        Assert.Equal($"{Path.Combine("worktree-b", "Shop.AppHost")} \u00b7 PID 43", AppHostPresentation.GetSubtitle(second));
    }

    [Theory]
    [InlineData("\U0001F469\u200D\U0001F4BB", 44)]
    [InlineData("\U0001F469\u200D\U0001F4BB", 45)]
    [InlineData("\U0001F469\u200D\U0001F4BB", 60)]
    [InlineData("e\u0301", 60)]
    [InlineData("\U0001F600", 60)]
    public void LongNamesKeepBothEndsAndPreserveGraphemes(string grapheme, int count)
    {
        var name = string.Concat(Enumerable.Repeat(grapheme, count));
        var host = new AppHostInfo(Path.GetFullPath($"{name}/apphost.cs"), 42, null);
        var expected = count <= 44
            ? name
            : string.Concat(Enumerable.Repeat(grapheme, 21)) + "\u2026" + string.Concat(Enumerable.Repeat(grapheme, 22));

        Assert.Equal(name, AppHostPresentation.GetDisplayName(host));
        Assert.Equal(expected, AppHostPresentation.GetTitle(host));
        Assert.Equal(Math.Min(count, 44), StringInfo.ParseCombiningCharacters(AppHostPresentation.GetTitle(host)).Length);
    }

    [Theory]
    [InlineData("my\nproject\tname", "my project name")]
    [InlineData("my\r\nproject\tname", "my project name")]
    [InlineData("my\u0085project\u2028name\u2029end", "my project name end")]
    public void MenuLabelsDoNotIntroduceExtraLines(string name, string expected)
    {
        var path = Path.GetFullPath($"worktree\nbranch/apphost/{name}.cs");
        var host = new AppHostInfo(path, 42, null);
        var location = Path.Combine("worktree branch", "apphost");

        Assert.Equal(expected, AppHostPresentation.GetDisplayName(host));
        Assert.Equal(expected, AppHostPresentation.GetTitle(host));
        Assert.Equal($"{location} \u00b7 PID 42", AppHostPresentation.GetSubtitle(host));
        Assert.Equal($"{expected} - {location} (PID 42)", AppHostPresentation.GetLabel(host));
        Assert.Equal(path, host.AppHostPath);
        Assert.Equal(Path.GetDirectoryName(path), AppHostPresentation.GetDirectory(host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("file:///tmp/example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@localhost/")]
    [InlineData("/relative")]
    public void NonBrowserAndCredentialBearingUrlsAreDisabled(string? url)
    {
        Assert.Null(new AppHostInfo(Path.GetFullPath("apphost.cs"), 42, url).DashboardUri);
    }

    [Fact]
    public void DashboardLoginQueryIsPreservedForBrowserNavigation()
    {
        const string Url = "https://localhost:1234/login?t=example-test-token";
        Assert.Equal(Url, new AppHostInfo(Path.GetFullPath("apphost.cs"), 42, Url).DashboardUri?.AbsoluteUri);
    }
}
