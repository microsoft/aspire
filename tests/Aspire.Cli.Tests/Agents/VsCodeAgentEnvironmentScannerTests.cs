// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class VsCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(".")]
    [InlineData("subdir")]
    public async Task ScanAsync_WithProjectDirectory_DetectsVsCodeWithoutCallingRunner(string relativeWorkingDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        workspace.CreateDirectory(".vscode");
        var workingDirectory = relativeWorkingDirectory == "." ? workspace.WorkspaceRoot : workspace.CreateDirectory(relativeWorkingDirectory);
        var runner = new FakeVsCodeCliRunner(null);
        var scanner = new VsCodeAgentEnvironmentScanner(runner, CreateExecutionContext(workingDirectory), new TestEnvironment(),
            NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = workspace.WorkspaceRoot
        };
        var before = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories);

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.VsCode, environments);
        Assert.False(runner.WasCalled);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanAsync_IgnoresHomeAndDirectoriesAboveRepositoryRoot(bool directoryIsHome)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        workspace.CreateDirectory(".vscode");
        var childDirectory = workspace.CreateDirectory("subdir");
        var homeDirectory = directoryIsHome ? workspace.WorkspaceRoot : workspace.CreateDirectory("fake-home");
        var runner = new FakeVsCodeCliRunner(null);
        var scanner = new VsCodeAgentEnvironmentScanner(runner,
            TestExecutionContextHelper.CreateExecutionContext(childDirectory, homeDirectory: homeDirectory),
            new TestEnvironment(), NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = childDirectory,
            RepositoryRoot = directoryIsHome ? workspace.WorkspaceRoot : childDirectory
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.None, environments);
        Assert.True(runner.WasCalled);
    }

    [Theory]
    [InlineData("{ invalid json content")]
    [InlineData("")]
    [InlineData("""{"servers":{"aspire":{"command":"custom-aspire"}}}""")]
    public async Task ScanAsync_WithMcpConfiguration_DetectsVsCodeWithoutParsingOrModifyingFile(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory(".vscode");
        var configPath = Path.Combine(configDirectory.FullName, "mcp.json");
        await File.WriteAllTextAsync(configPath, content);
        var scanner = new VsCodeAgentEnvironmentScanner(new FakeVsCodeCliRunner(null),
            CreateExecutionContext(workspace.WorkspaceRoot), new TestEnvironment(),
            NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.VsCode, environments);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanAsync_WithoutProjectDirectory_ProbesCliWithoutCreatingConfiguration(bool cliInstalled)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new FakeVsCodeCliRunner(cliInstalled ? new SemVersion(1, 85, 0) : null);
        var scanner = new VsCodeAgentEnvironmentScanner(runner, CreateExecutionContext(workspace.WorkspaceRoot), new TestEnvironment(),
            NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };
        var before = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories);

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(cliInstalled ? AgentEnvironment.VsCode : AgentEnvironment.None, environments);
        Assert.True(runner.WasCalled);
        Assert.Equal(before, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ScanAsync_WhenInVsCode_DetectsEditorWithoutCallingRunner()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new FakeVsCodeCliRunner(null);
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["TERM_PROGRAM"] = "vscode" });
        var scanner = new VsCodeAgentEnvironmentScanner(runner, CreateExecutionContext(workspace.WorkspaceRoot), environment,
            NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = workspace.WorkspaceRoot,
            RepositoryRoot = workspace.WorkspaceRoot
        };

        var environments = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(AgentEnvironment.VsCode, environments);
        Assert.False(runner.WasCalled);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(workingDirectory,
            homeDirectory: new DirectoryInfo(Path.Combine(workingDirectory.FullName, "fake-home")));
    }

    private sealed class FakeVsCodeCliRunner(SemVersion? version) : IVsCodeCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SemVersion?> GetVersionAsync(VsCodeRunOptions options, CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Task.FromResult(version);
        }
    }
}
