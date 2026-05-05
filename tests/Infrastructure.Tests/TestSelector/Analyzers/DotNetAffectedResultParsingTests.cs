// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class DotNetAffectedResultParsingTests
{
    [Fact]
    public void ParseOutput_JsonStringArray_ExtractsProjects()
    {
        var output = """
            ["src/Proj1.csproj", "src/Proj2.csproj", "tests/Proj1.Tests.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(3, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("src/Proj2.csproj", projects);
        Assert.Contains("tests/Proj1.Tests.csproj", projects);
    }

    [Fact]
    public void ParseOutput_JsonObjectArrayWithPath_ExtractsProjects()
    {
        var output = """
            [
                {"path": "src/Proj1.csproj"},
                {"path": "src/Proj2.csproj"}
            ]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("src/Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_JsonObjectArrayWithProjectPath_ExtractsProjects()
    {
        var output = """
            [
                {"ProjectPath": "src/Proj1.csproj"},
                {"ProjectPath": "src/Proj2.csproj"}
            ]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("src/Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_NestedObjectWithProjects_ExtractsProjects()
    {
        var output = """
            {
                "projects": ["src/Proj1.csproj", "src/Proj2.csproj"]
            }
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("src/Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_NestedObjectWithAffectedProjects_ExtractsProjects()
    {
        var output = """
            {
                "affectedProjects": ["src/Proj1.csproj", "tests/Proj1.Tests.csproj"]
            }
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("tests/Proj1.Tests.csproj", projects);
    }

    [Fact]
    public void ParseOutput_InvalidJson_FallsBackToLineByLine()
    {
        var output = """
            src/Proj1.csproj
            src/Proj2.csproj
            tests/Proj1.Tests.csproj
            Some other output line
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(3, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("src/Proj2.csproj", projects);
        Assert.Contains("tests/Proj1.Tests.csproj", projects);
    }

    [Fact]
    public void ParseOutput_EmptyString_ReturnsEmptyList()
    {
        var projects = DotNetAffectedRunner.ParseOutput("");

        Assert.Empty(projects);
    }

    [Fact]
    public void ParseOutput_WhitespaceOnly_ReturnsEmptyList()
    {
        var projects = DotNetAffectedRunner.ParseOutput("   \n\t  ");

        Assert.Empty(projects);
    }

    [Fact]
    public void ParseOutput_EmptyJsonArray_ReturnsEmptyList()
    {
        var projects = DotNetAffectedRunner.ParseOutput("[]");

        Assert.Empty(projects);
    }

    [Fact]
    public void ParseOutput_EmptyJsonObject_ReturnsEmptyList()
    {
        var projects = DotNetAffectedRunner.ParseOutput("{}");

        Assert.Empty(projects);
    }

    [Fact]
    public void ParseOutput_MixedObjectArray_ExtractsAllValidPaths()
    {
        var output = """
            [
                {"path": "src/Proj1.csproj"},
                "src/Proj2.csproj",
                {"ProjectPath": "src/Proj3.csproj"}
            ]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(3, projects.Count);
    }

    [Fact]
    public void ParseOutput_ObjectWithPathInNestedProjects_ExtractsProjects()
    {
        var output = """
            {
                "projects": [
                    {"path": "src/Proj1.csproj"},
                    {"path": "src/Proj2.csproj"}
                ]
            }
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
    }

    [Fact]
    public void ParseOutput_LineByLine_IgnoresNonCsprojLines()
    {
        var output = """
            Starting analysis...
            src/Proj1.csproj
            Processing complete.
            tests/Proj1.Tests.csproj
            Done!
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Proj1.csproj", projects);
        Assert.Contains("tests/Proj1.Tests.csproj", projects);
    }

    [Fact]
    public void ParseOutput_CsprojWithAbsolutePath_ExtractsProject()
    {
        var output = """
            ["/full/path/to/src/Proj1.csproj", "C:\\Windows\\path\\Proj2.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("/full/path/to/src/Proj1.csproj", projects);
        Assert.Contains("C:\\Windows\\path\\Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_EmptyPathsInArray_AreSkipped()
    {
        var output = """
            ["src/Proj1.csproj", "", "src/Proj2.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
    }
}
