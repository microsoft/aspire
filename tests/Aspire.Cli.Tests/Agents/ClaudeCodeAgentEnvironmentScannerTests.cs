// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class ClaudeCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WhenCliIsInstalled_RecordsVersionWithoutCreatingConfiguration(bool hasProjectConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (hasProjectConfiguration)
        {
            workspace.CreateDirectory(".claude");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner { ClaudeCodeVersion = new SemVersion(2, 1, 0) };
        var agent = CreateAgent(runner, workspace.CreateExecutionContext());
        var context = new AgentEnvironmentScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await agent.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(new AgentClientDetection(AgentClientKind.ClaudeCode, "2.1.0", false), Assert.Single(context.DetectedClients));
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    private static ClaudeCodeAgentEnvironmentScanner CreateAgent(TestAgentCliRunner runner, CliExecutionContext executionContext)
        => new(runner, executionContext, new TestEnvironment(), NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance);
}
