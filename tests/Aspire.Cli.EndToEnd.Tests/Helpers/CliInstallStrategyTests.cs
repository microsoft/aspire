// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

[Collection(CliInstallEnvironmentCollection.Name)]
public class CliInstallStrategyTests
{
    [Fact]
    public void GetPullRequestInstallArgs_ReturnsPrNumber()
    {
        Assert.Equal("123", AspireCliShellCommandHelpers.GetPullRequestInstallArgs(123));
    }

    [Fact]
    public void GetLocalArchiveInstallCommand_FormatsCorrectly()
    {
        var command = AspireCliShellCommandHelpers.GetLocalArchiveInstallCommand("/tmp/cli-archives", "/opt/aspire-scripts/get-aspire-cli-pr.sh");
        Assert.Equal("/opt/aspire-scripts/get-aspire-cli-pr.sh --local-dir '/tmp/cli-archives'", command);
    }

    [Fact]
    public void Detect_ReturnsLocalArchive_WhenArchiveDirIsSet()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.LocalArchive, strategy.Mode);
            Assert.Equal(tempDir.FullName, strategy.ArchiveDir);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Detect_ReturnsPullRequest_WhenBothPrMetadataAndArchiveDirAreSet()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", "16131"),
                ("GITHUB_PR_HEAD_SHA", "abc123"),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName),
                ("CI", null),
                ("GITHUB_ACTIONS", null));

            var strategy = CliInstallStrategy.Detect();

            Assert.Equal(CliInstallMode.PullRequest, strategy.Mode);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void Detect_FallsBackToDevQuality_WhenNoArchiveContextInCI()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("ASPIRE_E2E_PREINSTALLED", null),
            ("GITHUB_PR_NUMBER", null),
            ("GITHUB_PR_HEAD_SHA", null),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null),
            ("CI", null),
            ("GITHUB_ACTIONS", "true"));

        var strategy = CliInstallStrategy.Detect();

        Assert.Equal(CliInstallMode.InstallScript, strategy.Mode);
    }

    [Fact]
    public void ConfigureContainer_MountsArchiveDirForLocalArchive()
    {
        var tempDir = Directory.CreateTempSubdirectory("cli-archives-test");
        try
        {
            using var environment = new EnvironmentVariableScope(
                ("ASPIRE_E2E_ARCHIVE", null),
                ("ASPIRE_E2E_QUALITY", null),
                ("ASPIRE_E2E_VERSION", null),
                ("ASPIRE_E2E_PREINSTALLED", null),
                ("GITHUB_PR_NUMBER", null),
                ("GITHUB_PR_HEAD_SHA", null),
                (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, tempDir.FullName));

            var strategy = CliInstallStrategy.Detect();
            var options = new DockerContainerOptions();

            strategy.ConfigureContainer(options);

            Assert.Contains($"{tempDir.FullName}:/tmp/aspire-cli-archives:ro", options.Volumes);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public void ConfigureContainer_AddsPrMetadataForPullRequest()
    {
        using var environment = new EnvironmentVariableScope(
            ("ASPIRE_E2E_ARCHIVE", null),
            ("ASPIRE_E2E_QUALITY", null),
            ("ASPIRE_E2E_VERSION", null),
            ("GITHUB_PR_NUMBER", "16131"),
            ("GITHUB_PR_HEAD_SHA", "52669a7cac3d4f10c6269909fc38e77124ed177c"),
            (CliE2ETestHelpers.CliArchiveDirEnvironmentVariableName, null));

        var strategy = CliInstallStrategy.Detect();
        var options = new DockerContainerOptions();

        strategy.ConfigureContainer(options);

        Assert.Equal("16131", options.Environment["GITHUB_PR_NUMBER"]);
        Assert.Equal("52669a7cac3d4f10c6269909fc38e77124ed177c", options.Environment["GITHUB_PR_HEAD_SHA"]);
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
