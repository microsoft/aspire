// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

[Collection(CliInstallEnvironmentCollection.Name)]
public class CliInstallStrategyTests
{
    [Fact]
    public void GetPullRequestInstallArgs_UsesPrNumberWhenWorkflowRunIdIsMissing()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.CliArchiveWorkflowRunIdEnvironmentVariableName, null));

        Assert.Equal("123", AspireCliShellCommandHelpers.GetPullRequestInstallArgs(123));
    }

    [Fact]
    public void GetPullRequestInstallArgs_AppendsWorkflowRunIdWhenProvided()
    {
        using var environment = new EnvironmentVariableScope(
            (CliE2ETestHelpers.CliArchiveWorkflowRunIdEnvironmentVariableName, "987654321"));

        Assert.Equal("123 --run-id 987654321", AspireCliShellCommandHelpers.GetPullRequestInstallArgs(123));
    }

    [Fact]
    public void ConfigureContainer_AddsWorkflowRunIdForPullRequestStrategy()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", "52669a7cac3d4f10c6269909fc38e77124ed177c"),
            (CliE2ETestHelpers.CliArchiveWorkflowRunIdEnvironmentVariableName, "24404068249"));

        var strategy = CliInstallStrategy.Detect();
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.Equal("24404068249", options.Environment[CliE2ETestHelpers.CliArchiveWorkflowRunIdEnvironmentVariableName]);
    }

    [Fact]
    public void Detect_DotnetTool_WhenEnvironmentVariableIsSet()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Null(strategy.Version);
        Assert.Null(strategy.NupkgSourcePath);
    }

    [Fact]
    public void Detect_DotnetTool_WithVersion()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", "9.5.0"),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
        Assert.Equal("9.5.0", strategy.Version);
        Assert.Null(strategy.NupkgSourcePath);
    }

    [Fact]
    public void Detect_DotnetToolLocalSource_WithVersionAndPath()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", tempDir.FullName),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", "13.3.0-preview.1.25175.1"),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
            Assert.Equal("13.3.0-preview.1.25175.1", strategy.Version);
            Assert.Equal(tempDir.FullName, strategy.NupkgSourcePath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_DotnetToolLocalSource_ThrowsWithoutVersion()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", tempDir.FullName),
                ("ASPIRE_E2E_DOTNET_TOOL", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            Assert.Throws<InvalidOperationException>(CliInstallStrategy.Detect);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Detect_DotnetTool_TakesPriorityOverQuality()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_DOTNET_TOOL_SOURCE", null),
            ("ASPIRE_E2E_DOTNET_TOOL", "true"),
            ("ASPIRE_E2E_QUALITY", "dev"),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            ("CI", null),
            ("GITHUB_ACTIONS", null));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.DotnetTool, strategy.Mode);
    }

    [Fact]
    public void ConfigureContainer_MountsNupkgSourceForDotnetToolLocalSource()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            var strategy = CliInstallStrategy.FromDotnetToolLocalSource(tempDir.FullName, "13.3.0");
            var options = new DockerContainerOptions();

            strategy.ConfigureContainer(options);

            Assert.Contains(options.Volumes, v => v.Contains("/tmp/aspire-nupkg-source:ro"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ConfigureContainer_NoVolumeForDotnetToolPublishedFeed()
    {
        var strategy = CliInstallStrategy.FromDotnetTool("9.5.0");
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.DoesNotContain(options.Volumes, v => v.Contains("aspire-nupkg-source"));
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithVersionOnly()
    {
        var strategy = CliInstallStrategy.FromDotnetTool("9.5.0");
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli --version 9.5.0", command);
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithLocalSource()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test-nupkg-");

        try
        {
            var strategy = CliInstallStrategy.FromDotnetToolLocalSource(tempDir.FullName, "13.3.0");
            var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

            Assert.Equal("dotnet tool install --global Aspire.Cli --version 13.3.0 --add-source /tmp/aspire-nupkg-source", command);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetDotnetToolInstallCommandInDocker_WithoutVersion()
    {
        var strategy = CliInstallStrategy.FromDotnetTool();
        var command = AspireCliShellCommandHelpers.GetDotnetToolInstallCommandInDocker(strategy);

        Assert.Equal("dotnet tool install --global Aspire.Cli", command);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues;

        public EnvironmentVariableScope(params (string Name, string? Value)[] variables)
        {
            _originalValues = variables.ToDictionary(
                variable => variable.Name,
                variable => Environment.GetEnvironmentVariable(variable.Name));

            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliInstallEnvironmentCollection
{
    public const string Name = nameof(CliInstallEnvironmentCollection);
}
