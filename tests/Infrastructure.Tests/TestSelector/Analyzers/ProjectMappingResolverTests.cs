// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using TestSelector.Models;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class ProjectMappingResolverTests
{
    private static ProjectMappingResolver CreateResolver()
    {
        var mappings = new List<SourceToTestMapping>
        {
            new()
            {
                Source = "src/Components/{name}/**",
                Test = "tests/{name}.Tests/"
            },
            new()
            {
                Source = "src/Aspire.Hosting.{name}/**",
                Test = "tests/Aspire.Hosting.{name}.Tests/",
                Exclude = ["src/Aspire.Hosting.Testing/**"]
            },
            new()
            {
                Source = "tests/{name}.Tests/**",
                Test = "tests/{name}.Tests/"
            }
        };

        return new ProjectMappingResolver(mappings);
    }

    [Theory]
    [InlineData("src/Components/Aspire.Redis/Client.cs", "tests/Aspire.Redis.Tests/")]
    [InlineData("src/Components/Aspire.Dashboard/Components/Button.cs", "tests/Aspire.Dashboard.Tests/")]
    [InlineData("src/Aspire.Hosting.Azure/AzureResource.cs", "tests/Aspire.Hosting.Azure.Tests/")]
    [InlineData("src/Aspire.Hosting.Docker/DockerResource.cs", "tests/Aspire.Hosting.Docker.Tests/")]
    [InlineData("tests/Aspire.Dashboard.Tests/SomeTest.cs", "tests/Aspire.Dashboard.Tests/")]
    public void ResolveTestProjects_MatchesPatternAndSubstitutes(string changedFile, string expectedTestProject)
    {
        var resolver = CreateResolver();

        var results = resolver.ResolveTestProjects(changedFile);

        Assert.Single(results);
        Assert.Equal(expectedTestProject, results[0]);
    }

    [Fact]
    public void ResolveTestProjects_ExcludePattern_ReturnsEmpty()
    {
        var resolver = CreateResolver();

        // src/Aspire.Hosting.Testing/** is excluded from the second mapping
        var results = resolver.ResolveTestProjects("src/Aspire.Hosting.Testing/TestHost.cs");

        Assert.Empty(results);
    }

    [Fact]
    public void ResolveTestProjects_NoMatch_ReturnsEmpty()
    {
        var resolver = CreateResolver();

        var results = resolver.ResolveTestProjects("docs/README.md");

        Assert.Empty(results);
    }

    [Fact]
    public void ResolveTestProjects_NormalizesPathSeparators()
    {
        var resolver = CreateResolver();

        var results = resolver.ResolveTestProjects("src\\Components\\Aspire.Redis\\Client.cs");

        Assert.Single(results);
        Assert.Equal("tests/Aspire.Redis.Tests/", results[0]);
    }

    [Fact]
    public void ResolveAllTestProjects_ReturnsUniqueProjects()
    {
        var resolver = CreateResolver();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs",
            "src/Components/Aspire.Redis/Server.cs", // Same project as first
            "src/Components/Aspire.Dashboard/Dashboard.cs"
        };

        var results = resolver.ResolveAllTestProjects(files);

        Assert.Equal(2, results.Count);
        Assert.Contains("tests/Aspire.Redis.Tests/", results);
        Assert.Contains("tests/Aspire.Dashboard.Tests/", results);
    }

    [Fact]
    public void ResolveAllTestProjects_EmptyInput_ReturnsEmpty()
    {
        var resolver = CreateResolver();

        var results = resolver.ResolveAllTestProjects([]);

        Assert.Empty(results);
    }

    [Fact]
    public void Matches_FileMatchesPattern_ReturnsTrue()
    {
        var resolver = CreateResolver();

        Assert.True(resolver.Matches("src/Components/Aspire.Redis/Client.cs"));
        Assert.True(resolver.Matches("src/Aspire.Hosting.Azure/Resource.cs"));
        Assert.True(resolver.Matches("tests/Aspire.Dashboard.Tests/Test.cs"));
    }

    [Fact]
    public void Matches_FileDoesNotMatch_ReturnsFalse()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.Matches("docs/README.md"));
        Assert.False(resolver.Matches("extension/package.json"));
    }

    [Fact]
    public void Matches_ExcludedFile_ReturnsFalse()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.Matches("src/Aspire.Hosting.Testing/TestHost.cs"));
    }

    [Fact]
    public void MappingCount_ReturnsCorrectCount()
    {
        var resolver = CreateResolver();

        Assert.Equal(3, resolver.MappingCount);
    }

    [Fact]
    public void Constructor_EmptyMappings_DoesNotThrow()
    {
        var resolver = new ProjectMappingResolver([]);

        Assert.Equal(0, resolver.MappingCount);
        Assert.Empty(resolver.ResolveTestProjects("any/file.cs"));
    }

    [Fact]
    public void ResolveTestProjects_PatternWithoutCapture_ReturnsStaticPattern()
    {
        var mappings = new List<SourceToTestMapping>
        {
            new()
            {
                Source = "playground/**",
                Test = "tests/Aspire.EndToEnd.Tests/"
            }
        };

        var resolver = new ProjectMappingResolver(mappings);

        var results = resolver.ResolveTestProjects("playground/SampleApp/Program.cs");

        Assert.Single(results);
        Assert.Equal("tests/Aspire.EndToEnd.Tests/", results[0]);
    }

    [Fact]
    public void ResolveTestProjects_MultipleMatches_ReturnsAll()
    {
        var mappings = new List<SourceToTestMapping>
        {
            new()
            {
                Source = "src/**/*.cs",
                Test = "tests/Unit.Tests/"
            },
            new()
            {
                Source = "src/Components/{name}/**",
                Test = "tests/{name}.Tests/"
            }
        };

        var resolver = new ProjectMappingResolver(mappings);

        var results = resolver.ResolveTestProjects("src/Components/Aspire.Redis/Client.cs");

        Assert.Equal(2, results.Count);
        Assert.Contains("tests/Unit.Tests/", results);
        Assert.Contains("tests/Aspire.Redis.Tests/", results);
    }

    [Theory]
    [InlineData("src/Aspire.Hosting.Azure.Storage/Client.cs", "tests/Aspire.Hosting.Azure.Storage.Tests/")]
    [InlineData("src/Aspire.Hosting.Kafka/Producer.cs", "tests/Aspire.Hosting.Kafka.Tests/")]
    public void ResolveTestProjects_CapturesCorrectName(string changedFile, string expectedTestProject)
    {
        var resolver = CreateResolver();

        var results = resolver.ResolveTestProjects(changedFile);

        Assert.Single(results);
        Assert.Equal(expectedTestProject, results[0]);
    }

    [Fact]
    public void ResolveAllTestProjects_MixOfMatchedAndUnmatched()
    {
        var resolver = CreateResolver();

        var files = new[]
        {
            "src/Components/Aspire.Redis/Client.cs", // matches
            "docs/README.md",                          // no match
            "extension/package.json",                  // no match
            "tests/Aspire.Dashboard.Tests/Test.cs"    // matches
        };

        var results = resolver.ResolveAllTestProjects(files);

        Assert.Equal(2, results.Count);
        Assert.Contains("tests/Aspire.Redis.Tests/", results);
        Assert.Contains("tests/Aspire.Dashboard.Tests/", results);
    }
}
