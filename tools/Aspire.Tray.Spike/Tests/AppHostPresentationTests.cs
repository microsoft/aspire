// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Tray.Spike.Tests;

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

    [Fact]
    public void LongNamesKeepBothEndsAndPreserveGraphemes()
    {
        const string Grapheme = "\U0001F469\u200D\U0001F4BB";
        var name = string.Concat(Enumerable.Repeat(Grapheme, 60));
        var host = new AppHostInfo(Path.GetFullPath($"{name}/apphost.cs"), 42, null);

        Assert.Equal(name, AppHostPresentation.GetDisplayName(host));
        Assert.Equal(string.Concat(Enumerable.Repeat(Grapheme, 21)) + "\u2026" + string.Concat(Enumerable.Repeat(Grapheme, 22)),
            AppHostPresentation.GetTitle(host));
        Assert.Equal(44, StringInfo.ParseCombiningCharacters(AppHostPresentation.GetTitle(host)).Length);
    }

    [Fact]
    public void MenuLabelsDoNotIntroduceExtraLines()
    {
        var host = new AppHostInfo(Path.GetFullPath("worktree-a/my\nproject\tname.cs"), 42, null);
        Assert.Equal("my project name", AppHostPresentation.GetTitle(host));
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
