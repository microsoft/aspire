// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Scaffolding;

namespace Aspire.Cli.Tests.Scaffolding;

public class GitIgnorePatternMatcherTests
{
    [Theory]
    [InlineData(".aspire*/", ".aspire/", true)]
    [InlineData(".aspir?/", ".aspire/", true)]
    [InlineData(".aspir[e]/", ".aspire/", true)]
    [InlineData(".aspir[!x]/", ".aspire/", true)]
    [InlineData("[[:punct:]]aspire/", ".aspire/", true)]
    [InlineData("**/.aspire*/settings.json", ".aspire/", true)]
    [InlineData("**/.aspire*/", ".aspire/", true)]
    [InlineData("**/.config*/", ".aspire/", false)]
    [InlineData("src/**/.aspire/settings.json", ".aspire/", true)]
    [InlineData("src/.aspire*/settings.json", ".aspire/", true)]
    [InlineData("/.aspire*/settings.json", "/.aspire/", true)]
    [InlineData("/src/.aspire*/settings.json", "/.aspire/", false)]
    [InlineData(".aspire\\*/", ".aspire/", false)]
    [InlineData("[[:.]aspire/", ".aspire/", true)]
    [InlineData(".ASPIRE*/", ".aspire/", true)]
    [InlineData(".config*/", ".aspire/", false)]
    [InlineData("config/.aspire/", "/.aspire/", false)]
    [InlineData("invalid\\", ".aspire/", false)]
    public void CanMatchDirectoryOrDescendant_ReturnsExpectedResult(
        string negationPattern,
        string directoryPattern,
        bool expected)
    {
        var result = GitIgnorePatternMatcher.CanMatchDirectoryOrDescendant(negationPattern, directoryPattern);

        Assert.Equal(expected, result);
    }
}
