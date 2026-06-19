// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class WriteClassModeTestPropsTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public WriteClassModeTestPropsTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "write-class-mode-test-props.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task WritesOverrideProjectToBuildForSplitProjectsWithoutPartitions()
    {
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "RegularProject.tests-metadata.json"),
            projectName: "RegularProject",
            testProjectPath: "tests/RegularProject/RegularProject.csproj");

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "PartitionSplitProject.tests-metadata.json"),
            projectName: "PartitionSplitProject",
            testProjectPath: "tests/PartitionSplitProject/PartitionSplitProject.csproj");

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "PartitionSplitProject.tests-partitions.json"),
            "1");

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "ClassSplitProject.tests-metadata.json"),
            projectName: "ClassSplitProject",
            testProjectPath: "tests/ClassSplitProject/ClassSplitProject.csproj");

        var outputPropsPath = Path.Combine(_tempDir.Path, "ClassModeProjects.props");

        var result = await RunScript(artifactsDir, outputPropsPath);

        result.EnsureSuccessful();
        Assert.Contains("Class-mode split projects: 1", result.Output);

        var propsXml = File.ReadAllText(outputPropsPath);
        Assert.Contains("$(RepoRoot)tests/ClassSplitProject/ClassSplitProject.csproj", propsXml);
        Assert.DoesNotContain("RegularProject.csproj", propsXml);
        Assert.DoesNotContain("PartitionSplitProject.csproj", propsXml);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task WritesZeroCountWhenEverySplitProjectAlreadyHasPartitions()
    {
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "PartitionSplitProject.tests-metadata.json"),
            projectName: "PartitionSplitProject",
            testProjectPath: "tests/PartitionSplitProject/PartitionSplitProject.csproj");

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "PartitionSplitProject.tests-partitions.json"),
            "1");

        var outputPropsPath = Path.Combine(_tempDir.Path, "ClassModeProjects.props");

        var result = await RunScript(artifactsDir, outputPropsPath);

        result.EnsureSuccessful();
        Assert.Contains("Class-mode split projects: 0", result.Output);

        var propsXml = File.ReadAllText(outputPropsPath);
        Assert.DoesNotContain("OverrideProjectToBuild", propsXml);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenClassModeMetadataDoesNotContainProjectPath()
    {
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        File.WriteAllText(
            Path.Combine(artifactsDir, "Broken.tests-metadata.json"),
            """
            {
              "projectName": "Broken",
              "splitTests": "true"
            }
            """);

        var outputPropsPath = Path.Combine(_tempDir.Path, "ClassModeProjects.props");

        var result = await RunScript(artifactsDir, outputPropsPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain testProjectPath", result.Output);
    }

    private async Task<CommandResult> RunScript(string artifactsDir, string outputPropsPath)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await cmd.ExecuteAsync(
            "-ArtifactsDir", $"\"{artifactsDir}\"",
            "-OutputPropsPath", $"\"{outputPropsPath}\"");
    }
}
