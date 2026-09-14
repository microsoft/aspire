// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Tests;

public class AppHostPresentationTests
{
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
