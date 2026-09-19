// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class CopilotAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_DetectsAppAndCliIndependently(bool appInstalled, bool cliInstalled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            CopilotVersion = cliInstalled ? new SemVersion(1, 2, 3) : null
        };
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["AI_AGENT"] = appInstalled ? "github_copilot_app_agent" : null
        });
        var scanner = CreateScanner(workspace, runner, environment);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        var expected = new List<AgentClientDetection>();
        if (appInstalled)
        {
            expected.Add(new(AgentClientKind.CopilotApp, null, false));
        }
        if (cliInstalled)
        {
            expected.Add(new(AgentClientKind.CopilotCli, "1.2.3", false));
        }
        Assert.Equal<AgentClientDetection>(expected, detections);
        Assert.Equal(["copilot"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));

        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(new(AgentClientKind.ClaudeCode, null, false)));
        if (detections.Count > 0)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = new(AgentClientKind.ClaudeCode, null, false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WhenInVsCode_DetectsCliWithoutInvokingInstallationShim(bool appInstalled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) =>
            {
                throw new InvalidOperationException("The Copilot installation shim must not run.");
            }
        };
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["TERM_PROGRAM_VERSION"] = "1.100.0",
            ["AI_AGENT"] = appInstalled ? "github_copilot_app_agent" : null
        });
        var scanner = CreateScanner(workspace, runner, environment);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
            appInstalled
                ? [new(AgentClientKind.CopilotApp, null, false), new(AgentClientKind.CopilotCli, null, false)]
                : [new(AgentClientKind.CopilotCli, null, false)],
            detections);
        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public async Task ScanAsync_PreservesCliPrereleaseVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            CopilotVersion = SemVersion.Parse("1.2.3-preview.1+build.2", SemVersionStyles.Strict)
        };
        var scanner = CreateScanner(workspace, runner, TestEnvironment.CreateWindows());
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.CopilotCli, "1.2.3-preview.1+build.2", false)], detections);
    }

    [Theory]
    [InlineData("""{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""")]
    [InlineData("{ invalid json content")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ScanAsync_LeavesExistingConfigurationUntouched(string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configDirectory = workspace.CreateDirectory("custom-copilot-home");
        var configPath = Path.Combine(configDirectory.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(configPath, content);
        File.SetLastWriteTimeUtc(configPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner { CopilotVersion = new SemVersion(1, 0, 0) };
        var environment = TestEnvironment.CreateWindows(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = configDirectory.FullName
        });
        var scanner = CreateScanner(workspace, runner, environment);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.CopilotCli, "1.0.0", false)], detections);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_DoesNotProbeOrWrite()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(workspace, runner, TestEnvironment.CreateWindows());
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(context, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    private static CopilotAgentEnvironmentScanner CreateScanner(
        TemporaryWorkspace workspace,
        TestAgentCliRunner runner,
        TestEnvironment environment)
        => new(
            runner,
            new CopilotAppInstallationDetector(environment, workspace.CreateExecutionContext()),
            environment,
            NullLogger<CopilotAgentEnvironmentScanner>.Instance);

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo directory)
        => new()
        {
            WorkingDirectory = directory,
            RepositoryRoot = directory
        };
}
