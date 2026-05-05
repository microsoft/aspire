// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class IgnorePathFilterTests
{
    [Theory]
    [InlineData("**/*.md", "README.md", true)]
    [InlineData("**/*.md", "docs/guide.md", true)]
    [InlineData("**/*.md", "src/code.cs", false)]
    [InlineData("docs/**", "docs/guide.md", true)]
    [InlineData("docs/**", "docs/api/reference.md", true)]
    [InlineData("docs/**", "src/docs/file.cs", false)]
    [InlineData("*.md", "README.md", true)]
    [InlineData("*.md", "docs/guide.md", false)]
    public void ShouldIgnore_WithGlobPattern_ReturnsExpected(string pattern, string filePath, bool expected)
    {
        var filter = new IgnorePathFilter([pattern]);

        var result = filter.ShouldIgnore(filePath);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("docs\\guide.md")]
    [InlineData("docs/guide.md")]
    public void ShouldIgnore_NormalizesPathSeparators(string filePath)
    {
        var filter = new IgnorePathFilter(["docs/**"]);

        var result = filter.ShouldIgnore(filePath);

        Assert.True(result);
    }

    [Fact]
    public void SplitFiles_SeparatesIgnoredAndActiveFiles()
    {
        var filter = new IgnorePathFilter(["**/*.md", "docs/**"]);

        var files = new[]
        {
            "README.md",
            "src/code.cs",
            "docs/guide.md",
            "tests/Test.cs",
            "CHANGELOG.md"
        };

        var (ignored, active) = filter.SplitFiles(files);

        Assert.Equal(3, ignored.Count);
        Assert.Contains("README.md", ignored);
        Assert.Contains("docs/guide.md", ignored);
        Assert.Contains("CHANGELOG.md", ignored);

        Assert.Equal(2, active.Count);
        Assert.Contains("src/code.cs", active);
        Assert.Contains("tests/Test.cs", active);
    }

    [Fact]
    public void ShouldIgnore_WithNoPatterns_ReturnsFalse()
    {
        var filter = new IgnorePathFilter([]);

        Assert.False(filter.ShouldIgnore("any/file.cs"));
        Assert.False(filter.ShouldIgnore("README.md"));
    }

    [Fact]
    public void SplitFiles_WithEmptyInput_ReturnsBothEmpty()
    {
        var filter = new IgnorePathFilter(["**/*.md"]);

        var (ignored, active) = filter.SplitFiles([]);

        Assert.Empty(ignored);
        Assert.Empty(active);
    }

    [Fact]
    public void Patterns_ReturnsConfiguredPatterns()
    {
        var patterns = new[] { "**/*.md", "docs/**", "*.txt" };
        var filter = new IgnorePathFilter(patterns);

        Assert.Equal(3, filter.Patterns.Count);
        Assert.Contains("**/*.md", filter.Patterns);
        Assert.Contains("docs/**", filter.Patterns);
        Assert.Contains("*.txt", filter.Patterns);
    }

    [Theory]
    [InlineData("src/**/*.cs", "src/Components/File.cs", true)]
    [InlineData("src/**/*.cs", "tests/File.cs", false)]
    [InlineData("**/bin/**", "src/Project/bin/Debug/file.dll", true)]
    [InlineData("**/obj/**", "obj/Release/file.dll", true)]
    public void ShouldIgnore_ComplexGlobPatterns_WorkCorrectly(string pattern, string filePath, bool expected)
    {
        var filter = new IgnorePathFilter([pattern]);

        var result = filter.ShouldIgnore(filePath);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldIgnore_MultiplePatterns_MatchesAny()
    {
        var filter = new IgnorePathFilter(["**/*.md", "**/*.txt", "docs/**"]);

        Assert.True(filter.ShouldIgnore("README.md"));
        Assert.True(filter.ShouldIgnore("notes.txt"));
        Assert.True(filter.ShouldIgnore("docs/api.json"));
        Assert.False(filter.ShouldIgnore("src/code.cs"));
    }
}
