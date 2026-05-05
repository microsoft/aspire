// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Integration;

/// <summary>
/// Integration tests for dotnet-affected result handling.
/// Tests how the test selection feature handles various dotnet-affected outcomes.
/// </summary>
public class DotNetAffectedIntegrationTests
{
    #region Result Model Tests

    [Fact]
    public void DotNetAffectedResult_SuccessWithProjects_HasCorrectState()
    {
        var result = new DotNetAffectedResult
        {
            Success = true,
            AffectedProjects = ["src/Proj1.csproj", "tests/Proj1.Tests.csproj"],
            ExitCode = 0
        };

        Assert.True(result.Success);
        Assert.Equal(2, result.AffectedProjects.Count);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DotNetAffectedResult_FailureWithError_HasCorrectState()
    {
        var result = new DotNetAffectedResult
        {
            Success = false,
            Error = "dotnet-affected not found",
            ExitCode = 127
        };

        Assert.False(result.Success);
        Assert.Empty(result.AffectedProjects);
        Assert.Equal("dotnet-affected not found", result.Error);
        Assert.Equal(127, result.ExitCode);
    }

    [Fact]
    public void DotNetAffectedResult_EmptyProjects_IsValid()
    {
        var result = new DotNetAffectedResult
        {
            Success = true,
            AffectedProjects = [],
            ExitCode = 0
        };

        Assert.True(result.Success);
        Assert.Empty(result.AffectedProjects);
    }

    [Fact]
    public void DotNetAffectedResult_RawOutputForDebugging_IsPreserved()
    {
        var rawOutput = """
            [
                "src/Proj1.csproj",
                "tests/Proj1.Tests.csproj"
            ]
            """;

        var result = new DotNetAffectedResult
        {
            Success = false,
            Error = "Parse error",
            RawOutput = rawOutput,
            ExitCode = 1
        };

        Assert.Equal(rawOutput, result.RawOutput);
    }

    #endregion

    #region Fallback Trigger Tests

    [Fact]
    public void DotNetAffected_Failure_ShouldTriggerRunAll()
    {
        // When dotnet-affected fails, the test selection system should trigger run_all
        // This test documents the expected behavior

        var failedResult = new DotNetAffectedResult
        {
            Success = false,
            Error = "Tool execution failed",
            ExitCode = 1
        };

        // Business rule: failure should cause run_all=true
        Assert.False(failedResult.Success);
        // The actual triggering of run_all happens in the orchestration layer
    }

    [Fact]
    public void DotNetAffected_TimeoutSimulation_ShouldTriggerRunAll()
    {
        // A timeout scenario would be handled at the orchestration layer
        // Here we just test that a failed result is created correctly

        var timeoutResult = new DotNetAffectedResult
        {
            Success = false,
            Error = "Operation timed out after 60 seconds",
            ExitCode = -1
        };

        Assert.False(timeoutResult.Success);
        Assert.Contains("timed out", timeoutResult.Error);
    }

    [Fact]
    public void DotNetAffected_EmptyResults_IsValidSuccessCase()
    {
        // Empty results mean no projects were affected - this is valid
        var emptyResult = new DotNetAffectedResult
        {
            Success = true,
            AffectedProjects = [],
            ExitCode = 0
        };

        Assert.True(emptyResult.Success);
        Assert.Empty(emptyResult.AffectedProjects);
        // Empty results should NOT trigger run_all - no tests affected
    }

    #endregion

    #region Parse Output Edge Cases

    [Fact]
    public void ParseOutput_ArrayWithStringsOnly_HandledCorrectly()
    {
        // Test valid arrays without null values (null handling falls back to line parsing)
        var output = """
            ["src/Valid.csproj", "tests/Valid.Tests.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("src/Valid.csproj", projects);
        Assert.Contains("tests/Valid.Tests.csproj", projects);
    }

    [Fact]
    public void ParseOutput_DeepNestedStructure_IsHandled()
    {
        var output = """
            {
                "metadata": {
                    "version": "1.0"
                },
                "projects": ["src/Proj1.csproj"]
            }
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Single(projects);
        Assert.Contains("src/Proj1.csproj", projects);
    }

    [Fact]
    public void ParseOutput_WindowsPaths_ArePreserved()
    {
        var output = """
            ["C:\\repo\\src\\Proj1.csproj", "D:\\code\\Proj2.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("C:\\repo\\src\\Proj1.csproj", projects);
        Assert.Contains("D:\\code\\Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_UnixPaths_ArePreserved()
    {
        var output = """
            ["/home/user/repo/src/Proj1.csproj", "/var/projects/Proj2.csproj"]
            """;

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(2, projects.Count);
        Assert.Contains("/home/user/repo/src/Proj1.csproj", projects);
        Assert.Contains("/var/projects/Proj2.csproj", projects);
    }

    [Fact]
    public void ParseOutput_MixedNewlines_AreHandled()
    {
        var output = "src/Proj1.csproj\r\nsrc/Proj2.csproj\nsrc/Proj3.csproj";

        var projects = DotNetAffectedRunner.ParseOutput(output);

        Assert.Equal(3, projects.Count);
    }

    #endregion
}
