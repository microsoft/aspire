// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("opencode.json", "{ invalid json content")]
    [InlineData("opencode.json", "")]
    [InlineData("opencode.json", """{"mcp":{"aspire":{"command":["custom-aspire"]}}}""")]
    [InlineData("opencode.jsonc", "{ invalid json content")]
    [InlineData("opencode.jsonc", "")]
    [InlineData("opencode.jsonc", """{"mcp":{"aspire":{"command":["custom-aspire"]}}}""")]
    public async Task ScanAsync_WithProjectConfiguration_DetectsOpenCodeWithoutParsingOrModifyingFile(string fileName, string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, fileName);
        await File.WriteAllTextAsync(configPath, content);
        var runner = new FakeOpenCodeCliRunner(null);
        var scanner = new OpenCodeAgentEnvironmentScanner(runner, NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.OpenCode, environments);
        Assert.False(runner.WasCalled);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanAsync_WithoutProjectConfiguration_ProbesCliWithoutCreatingFile(bool cliInstalled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new FakeOpenCodeCliRunner(cliInstalled ? new SemVersion(1, 0, 0) : null);
        var scanner = new OpenCodeAgentEnvironmentScanner(runner, NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };
        var before = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories);

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(cliInstalled ? AgentEnvironment.OpenCode : AgentEnvironment.None, environments);
        Assert.True(runner.WasCalled);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    private sealed class FakeOpenCodeCliRunner(SemVersion? version) : IOpenCodeCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Task.FromResult(version);
        }
    }
}
