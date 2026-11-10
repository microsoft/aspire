// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;

namespace Aspire.Cli.Tests.Git;

public class GitHelpersTests
{
    [Theory]
    [InlineData("https://example.com/team/repository.git", true, true)]
    [InlineData("https://example.com/team\\repository.git", false, false)]
    [InlineData("ssh://git@example.com/team/repository.git", true, true)]
    [InlineData("git@example.com:team/repository.git", true, false)]
    [InlineData("git-user@example.com:team/repository.git", true, false)]
    [InlineData("", false, false)]
    [InlineData(" ", false, false)]
    [InlineData("relative/repository", false, false)]
    [InlineData("../repository", false, false)]
    [InlineData("--upload-pack=command", false, false)]
    [InlineData("http://example.com/repository", false, false)]
    [InlineData("file:///repository", false, false)]
    [InlineData("https://user@example.com/repository", false, false)]
    [InlineData("https://user:password@example.com/repository", false, false)]
    [InlineData("ssh://git:password@example.com/repository", false, false)]
    [InlineData("https://example.com/repository?query", false, false)]
    [InlineData("https://example.com/repository#fragment", false, false)]
    [InlineData("https://example.com/repository name", false, false)]
    [InlineData("https://example.com/repository\n", false, false)]
    [InlineData("git@example.com:", false, false)]
    [InlineData("git@host/path:repository", false, false)]
    [InlineData("git@example.com:repository?query", false, false)]
    [InlineData("git@example.com:repository#fragment", false, false)]
    [InlineData("git@example.com:repository\\path", false, false)]
    [InlineData("-git@example.com:repository", false, false)]
    public void RepositoryValidation_PreservesRepositoryAndUrlRules(string repository, bool validRepository, bool validUrl)
    {
        Assert.Equal(validRepository, GitHelpers.IsValidRepository(repository));
        Assert.Equal(validUrl, GitHelpers.IsValidRepositoryUrl(repository));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("repository with spaces")]
    [InlineData("repository#fragment")]
    public void RepositoryValidation_AcceptsFullyQualifiedLocalPathsOnlyForRepositories(string name)
    {
        var repository = Path.GetFullPath(name);

        Assert.True(GitHelpers.IsValidRepository(repository));
        Assert.False(GitHelpers.IsValidRepositoryUrl(repository));
        Assert.False(GitHelpers.IsValidRepository(repository + '\n'));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("main", true)]
    [InlineData("teams/preview", true)]
    [InlineData("refs/heads/teams/preview", true)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123", true)]
    [InlineData("", false)]
    [InlineData("@", false)]
    [InlineData("HEAD", false)]
    [InlineData("--upload-pack=command", false)]
    [InlineData("refs/heads/", false)]
    [InlineData("refs/tags/release", false)]
    [InlineData("refs/heads/refs/tags/release", false)]
    [InlineData("main..other", false)]
    [InlineData("main@{1}", false)]
    [InlineData("branch with spaces", false)]
    [InlineData("branch\nother", false)]
    [InlineData("branch\u0001", false)]
    [InlineData("branch\\other", false)]
    [InlineData("main:other", false)]
    [InlineData("main?other", false)]
    [InlineData("main*other", false)]
    [InlineData("main[other", false)]
    [InlineData("main~1", false)]
    [InlineData("main^1", false)]
    [InlineData("/branch", false)]
    [InlineData("branch/", false)]
    [InlineData("branch//child", false)]
    [InlineData(".hidden/branch", false)]
    [InlineData("branch./child", false)]
    [InlineData("branch.lock", false)]
    [InlineData("branch.LOCK/child", false)]
    public void IsValidRef_PreservesBranchAndCommitRules(string? reference, bool expected)
    {
        Assert.Equal(expected, GitHelpers.IsValidRef(reference));
    }
}
