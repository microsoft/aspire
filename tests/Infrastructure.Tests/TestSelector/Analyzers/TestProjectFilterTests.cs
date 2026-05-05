// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class TestProjectFilterTests
{
    [Fact]
    public void IsTestProject_MatchesIncludePattern()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.True(filter.IsTestProject("tests/MyProject.Tests/MyProject.Tests.csproj"));
        Assert.True(filter.IsTestProject("tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj"));
    }

    [Fact]
    public void IsTestProject_DoesNotMatchNonTestProjects()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.False(filter.IsTestProject("src/MyProject/MyProject.csproj"));
        Assert.False(filter.IsTestProject("tools/MyTool/MyTool.csproj"));
    }

    [Fact]
    public void IsTestProject_RespectsExcludePatterns()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = ["tests/testproject/**"]
        };

        var filter = new TestProjectFilter(patterns);

        Assert.True(filter.IsTestProject("tests/MyProject.Tests/MyProject.Tests.csproj"));
        Assert.False(filter.IsTestProject("tests/testproject/TestApp/TestApp.csproj"));
    }

    [Fact]
    public void IsTestProject_NormalizesPathSeparators()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.True(filter.IsTestProject("tests\\MyProject.Tests\\MyProject.Tests.csproj"));
    }

    [Fact]
    public void FilterTestProjects_ReturnsOnlyMatchingProjects()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        var projects = new[]
        {
            "tests/MyProject.Tests/MyProject.Tests.csproj",
            "src/MyProject/MyProject.csproj",
            "tests/Another.Tests/Another.Tests.csproj"
        };

        var filtered = filter.FilterTestProjects(projects);

        Assert.Equal(2, filtered.Count);
        Assert.Contains("tests/MyProject.Tests/MyProject.Tests.csproj", filtered);
        Assert.Contains("tests/Another.Tests/Another.Tests.csproj", filtered);
    }

    [Fact]
    public void FilterWithDetails_ProvidesClassificationReasons()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = ["tests/testproject/**"]
        };

        var filter = new TestProjectFilter(patterns);

        var projects = new[]
        {
            "tests/MyProject.Tests/MyProject.Tests.csproj",
            "tests/testproject/App/App.csproj",
            "src/MyProject/MyProject.csproj"
        };

        var result = filter.FilterWithDetails(projects);

        Assert.Single(result.TestProjects);
        Assert.Equal("tests/MyProject.Tests/MyProject.Tests.csproj", result.TestProjects[0].Path);
        Assert.Contains("include", result.TestProjects[0].ClassificationReason.ToLower());

        Assert.Single(result.ExcludedProjects);
        Assert.Contains("exclude", result.ExcludedProjects[0].ClassificationReason.ToLower());

        Assert.Single(result.OtherProjects);
        Assert.Contains("did not match", result.OtherProjects[0].ClassificationReason.ToLower());
    }

    [Fact]
    public void IncludePatterns_ReturnsConfiguredPatterns()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj", "integration/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.Equal(2, filter.IncludePatterns.Count);
        Assert.Contains("tests/**/*.csproj", filter.IncludePatterns);
        Assert.Contains("integration/**/*.csproj", filter.IncludePatterns);
    }

    [Fact]
    public void ExcludePatterns_ReturnsConfiguredPatterns()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = ["tests/testproject/**", "tests/samples/**"]
        };

        var filter = new TestProjectFilter(patterns);

        Assert.Equal(2, filter.ExcludePatterns.Count);
        Assert.Contains("tests/testproject/**", filter.ExcludePatterns);
        Assert.Contains("tests/samples/**", filter.ExcludePatterns);
    }

    [Fact]
    public void IsTestProject_EmptyIncludePatterns_NeverMatches()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = [],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.False(filter.IsTestProject("tests/MyProject.Tests/MyProject.Tests.csproj"));
        Assert.False(filter.IsTestProject("any/path.csproj"));
    }

    [Fact]
    public void FilterTestProjects_EmptyInput_ReturnsEmpty()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        var filtered = filter.FilterTestProjects([]);

        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterWithDetails_ExtractsProjectName()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        var result = filter.FilterWithDetails(["tests/MyProject.Tests/MyProject.Tests.csproj"]);

        Assert.Single(result.TestProjects);
        Assert.Equal("MyProject.Tests", result.TestProjects[0].Name);
    }

    [Fact]
    public void IsTestProject_MultipleIncludePatterns_MatchesAny()
    {
        var patterns = new IncludeExcludePatterns
        {
            Include = ["tests/**/*.csproj", "integration-tests/**/*.csproj"],
            Exclude = []
        };

        var filter = new TestProjectFilter(patterns);

        Assert.True(filter.IsTestProject("tests/Unit.Tests/Unit.Tests.csproj"));
        Assert.True(filter.IsTestProject("integration-tests/E2E/E2E.csproj"));
        Assert.False(filter.IsTestProject("src/App/App.csproj"));
    }
}
