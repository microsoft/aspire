// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Pipelines.GitHubActions.Tests;

public class GitIgnoreTemplateTests
{
    [Fact]
    public void Content_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(GitIgnoreTemplate.Content));
    }

    [Theory]
    [InlineData("bin/")]
    [InlineData("obj/")]
    [InlineData(".vs/")]
    [InlineData("*.user")]
    [InlineData("artifacts/")]
    public void Content_ContainsExpectedPatterns(string pattern)
    {
        Assert.Contains(pattern, GitIgnoreTemplate.Content);
    }
}
