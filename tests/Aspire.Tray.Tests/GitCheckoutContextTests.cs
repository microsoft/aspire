// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class GitCheckoutContextTests
{
    [Fact]
    public void NamesIncludeCurrentBranchAndRefreshAfterCheckout()
    {
        using var fixture = new TestTrayStateDirectory();
        var path = fixture.CreateAppHost("repo/src/Shop.AppHost/Shop.AppHost.csproj");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)))!;
        var git = Directory.CreateDirectory(Path.Combine(root, ".git")).FullName;
        var head = Path.Combine(git, "HEAD");
        File.WriteAllText(head, "ref: refs/heads/feature/payments\n");
        var host = new AppHostInfo(path, 42, null);

        Assert.Equal("Shop · feature/payments", AppHostPresentation.GetDisplayName(host));
        File.WriteAllText(head, "ref: refs/heads/main\n");
        Assert.Equal("Shop · main", AppHostPresentation.GetTitle(host));
    }

    [Theory]
    [InlineData("ref: refs/heads/feature/orders\n", "feature/orders")]
    [InlineData("0123456789012345678901234567890123456789\n", "checkout-a")]
    public void LinkedWorktreesUseBranchOrDetachedCheckoutFolder(string head, string expected)
    {
        using var fixture = new TestTrayStateDirectory();
        var path = fixture.CreateAppHost("checkout-a/src/Shop.AppHost.cs");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var git = Directory.CreateDirectory(Path.Combine(root, "..", "repo", ".git", "worktrees", "checkout-a")).FullName;
        var pointer = Path.Combine(root, ".git");
        File.WriteAllText(pointer, "gitdir: " + Path.GetRelativePath(root, git));
        File.WriteAllText(Path.Combine(git, "HEAD"), head);
        File.WriteAllText(Path.Combine(git, "gitdir"), pointer);

        Assert.Equal($"Shop · {expected}", AppHostPresentation.GetTitle(new(path, 42, null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("0123456789012345678901234567890123456789")]
    public void MissingMalformedOrDetachedPrimaryMetadataKeepsProjectName(string? head)
    {
        using var fixture = new TestTrayStateDirectory();
        var path = fixture.CreateAppHost("repo/Shop.AppHost.cs");
        if (head is not null)
        {
            var git = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(path)!, ".git")).FullName;
            File.WriteAllText(Path.Combine(git, "HEAD"), head);
        }

        Assert.Equal("Shop", AppHostPresentation.GetDisplayName(new(path, 42, null)));
    }

    [Fact]
    public void NestedRepositoryDoesNotInheritOuterBranch()
    {
        using var fixture = new TestTrayStateDirectory();
        var path = fixture.CreateAppHost("repo/nested/Shop.AppHost.cs");
        var inner = Path.GetDirectoryName(path)!;
        var outer = Directory.CreateDirectory(Path.Combine(inner, "..", ".git")).FullName;
        File.WriteAllText(Path.Combine(outer, "HEAD"), "ref: refs/heads/main");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));

        Assert.Equal("Shop", AppHostPresentation.GetTitle(new(path, 42, null)));
    }
}
