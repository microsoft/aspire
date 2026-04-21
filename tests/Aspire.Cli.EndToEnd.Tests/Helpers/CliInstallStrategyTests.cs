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
