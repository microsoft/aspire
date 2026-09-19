// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class VsCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WithProjectConfiguration_DetectsWithoutExecutable(bool inParent)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var workingDirectory = workspace.CreateDirectory("project");
        var configDirectory = inParent ? workspace.WorkspaceRoot : workingDirectory;
        configDirectory.CreateSubdirectory(".vscode");
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workingDirectory, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.VsCode, null, false)], detections);
        Assert.Equal(["code", "code-insiders"], runner.Commands);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_RecordsInstalledStableAndInsidersVersions(bool stableInstalled, bool insidersInstalled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            VsCodeVersion = stableInstalled ? new SemVersion(1, 100, 0) : null,
            VsCodeInsidersVersion = insidersInstalled ? SemVersion.Parse("1.101.0-insider", SemVersionStyles.Strict) : null
        };
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        var expected = new List<AgentClientDetection>();
        if (stableInstalled)
        {
            expected.Add(new(AgentClientKind.VsCode, "1.100.0", false));
        }
        if (insidersInstalled)
        {
            expected.Add(new(AgentClientKind.VsCode, "1.101.0-insider", true));
        }
        Assert.Equal<AgentClientDetection>(expected, detections);
        Assert.Equal(["code", "code-insiders"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));

        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(new(AgentClientKind.CopilotCli, null, false)));
        if (detections.Count > 0)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = new(AgentClientKind.CopilotCli, null, false));
        }
    }

    [Fact]
    public async Task ScanAsync_WithProjectConfigurationAndInsiders_PreservesInsidersEvidence()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".vscode");
        var runner = new TestAgentCliRunner
        {
            VsCodeInsidersVersion = SemVersion.Parse("1.101.0-insider", SemVersionStyles.Strict)
        };
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.VsCode, "1.101.0-insider", true)], detections);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("  ", null, false)]
    [InlineData("1.100.0", "1.100.0", false)]
    [InlineData(" 1.100.0 ", "1.100.0", false)]
    [InlineData("1.101.0-insider", "1.101.0-insider", true)]
    public async Task ScanAsync_WhenInVsCode_RecordsTerminalEvidenceWithoutProbing(string? terminalVersion, string? expectedVersion, bool isInsiders)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) =>
            {
                throw new InvalidOperationException("Editor probes must not run in a VS Code terminal.");
            }
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode",
            ["TERM_PROGRAM_VERSION"] = terminalVersion
        });
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), environment);
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.VsCode, expectedVersion, isInsiders)], detections);
        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_DoesNotSearchAboveRepositoryRoot(bool trailingSeparator)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".vscode");
        var repositoryRoot = workspace.CreateDirectory("repo");
        var workingDirectory = repositoryRoot.CreateSubdirectory("src");
        var scanner = CreateScanner(new TestAgentCliRunner(), workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(
            workingDirectory,
            trailingSeparator ? new DirectoryInfo(repositoryRoot.FullName + Path.DirectorySeparatorChar) : repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
    }

    [Fact]
    public async Task ScanAsync_DoesNotTreatHomeExtensionsAsProjectConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var homeDirectory = workspace.CreateDirectory("home");
        homeDirectory.CreateSubdirectory(".vscode");
        var workingDirectory = homeDirectory.CreateSubdirectory("project");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workingDirectory, homeDirectory: homeDirectory);
        var scanner = CreateScanner(new TestAgentCliRunner(), executionContext, new TestEnvironment());
        var context = CreateScanContext(workingDirectory, homeDirectory);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanAsync_WithWorkingDirectoryOutsideRepository_OnlyUsesSelectedRoot(bool workingDirectoryIsParent, bool hasTargetConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        var unrelatedParent = workingDirectoryIsParent ? workspace.WorkspaceRoot : workspace.CreateDirectory("unrelated");
        unrelatedParent.CreateSubdirectory(".vscode");
        var workingDirectory = workingDirectoryIsParent ? unrelatedParent : unrelatedParent.CreateSubdirectory("nested");
        if (hasTargetConfiguration)
        {
            repositoryRoot.CreateSubdirectory(".vscode");
        }
        var scanner = CreateScanner(new TestAgentCliRunner(), workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workingDirectory, repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
            hasTargetConfiguration ? [new(AgentClientKind.VsCode, null, false)] : [],
            detections);
    }

    [Fact]
    public async Task ScanAsync_WithWorkspaceRootOverride_UsesSelectedRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        repositoryRoot.CreateSubdirectory(".vscode");
        var workingDirectory = workspace.CreateDirectory("outside");
        var scanner = CreateScanner(new TestAgentCliRunner(), workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workingDirectory, repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.VsCode, null, false)], detections);
    }

    [Theory]
    [InlineData("""{"servers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""")]
    [InlineData("{ invalid json content")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ScanAsync_LeavesExistingConfigurationUntouched(string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configDirectory = workspace.CreateDirectory(".vscode");
        var configPath = Path.Combine(configDirectory.FullName, "mcp.json");
        await File.WriteAllTextAsync(configPath, content);
        File.SetLastWriteTimeUtc(configPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var scanner = CreateScanner(new TestAgentCliRunner(), workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.VsCode, null, false)], detections);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_DoesNotProbeOrWrite()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(context, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public async Task ScanAsync_WhenCancelledByStableProbe_DoesNotProbeInsiders()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationSource = new CancellationTokenSource();
        var runner = new TestAgentCliRunner
        {
            GetVersionAsyncCallback = (_, _) =>
            {
                cancellationSource.Cancel();
                return Task.FromResult<SemVersion?>(new SemVersion(1, 100, 0));
            }
        };
        var scanner = CreateScanner(runner, workspace.CreateExecutionContext(), new TestEnvironment());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(context, cancellationSource.Token)).DefaultTimeout();

        Assert.Equal(["code"], runner.Commands);
    }

    private static VsCodeAgentEnvironmentScanner CreateScanner(
        TestAgentCliRunner runner,
        CliExecutionContext executionContext,
        TestEnvironment environment)
        => new(runner, executionContext, environment, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo workingDirectory, DirectoryInfo repositoryRoot)
        => new()
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = repositoryRoot
        };
}
