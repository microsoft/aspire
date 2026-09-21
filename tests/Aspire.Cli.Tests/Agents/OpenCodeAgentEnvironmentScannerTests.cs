// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("opencode.json", false, false)]
    [InlineData("opencode.json", false, true)]
    [InlineData("opencode.json", true, false)]
    [InlineData("opencode.json", true, true)]
    [InlineData("opencode.jsonc", false, false)]
    [InlineData("opencode.jsonc", false, true)]
    [InlineData("opencode.jsonc", true, false)]
    [InlineData("opencode.jsonc", true, true)]
    public async Task ScanAsync_WithProjectConfiguration_DetectsWithoutExecutable(string fileName, bool inParent, bool useOpenCodeDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var workingDirectory = workspace.CreateDirectory("project");
        var configDirectory = inParent ? workspace.WorkspaceRoot : workingDirectory;
        if (useOpenCodeDirectory)
        {
            configDirectory = configDirectory.CreateSubdirectory(".opencode");
        }
        var configPath = Path.Combine(configDirectory.FullName, fileName);
        await File.WriteAllTextAsync(configPath, "{}");
        File.SetLastWriteTimeUtc(configPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner);
        var context = CreateScanContext(workingDirectory, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.OpenCode, null, false)], detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Equal("{}", await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3", true)]
    [InlineData("2.0.0-beta.1", false)]
    [InlineData("2.0.0-beta.1", true)]
    public async Task ScanAsync_WhenCliIsInstalled_RecordsVersionWithoutChangingConfiguration(string version, bool hasProjectConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (hasProjectConfiguration)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc"), "{}");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner { OpenCodeVersion = SemVersion.Parse(version, SemVersionStyles.Strict) };
        var scanner = CreateScanner(runner);
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.OpenCode, version, false)], detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
        var list = Assert.IsAssignableFrom<IList<AgentClientDetection>>(detections);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = new(AgentClientKind.CopilotCli, null, false));
        Assert.Throws<NotSupportedException>(list.Clear);
    }

    [Fact]
    public async Task ScanAsync_WithNoEvidence_ReturnsReadOnlyEmptyList()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner);
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
        var collection = Assert.IsAssignableFrom<ICollection<AgentClientDetection>>(detections);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(new(AgentClientKind.OpenCode, null, false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanAsync_WithOpenCodeDirectoryButNoConfigFiles_DoesNotDetectClient(bool hasUnrelatedFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configDirectory = workspace.CreateDirectory(".opencode");
        if (hasUnrelatedFile)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory.FullName, "other.json"), "{}");
        }
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner);
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        Assert.Equal(["opencode"], runner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("opencode.json", false, false)]
    [InlineData("opencode.json", false, true)]
    [InlineData("opencode.json", true, false)]
    [InlineData("opencode.json", true, true)]
    [InlineData("opencode.jsonc", false, false)]
    [InlineData("opencode.jsonc", false, true)]
    [InlineData("opencode.jsonc", true, false)]
    [InlineData("opencode.jsonc", true, true)]
    public async Task ScanAsync_DoesNotSearchAboveRepositoryRoot(string fileName, bool trailingSeparator, bool useOpenCodeDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configDirectory = useOpenCodeDirectory ? workspace.CreateDirectory(".opencode") : workspace.WorkspaceRoot;
        var configPath = Path.Combine(configDirectory.FullName, fileName);
        await File.WriteAllTextAsync(configPath, "{}");
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var repositoryRoot = workspace.CreateDirectory("repo");
        var workingDirectory = repositoryRoot.CreateSubdirectory("src");
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var scanner = CreateScanner(new TestAgentCliRunner());
        var context = CreateScanContext(
            workingDirectory,
            trailingSeparator ? new DirectoryInfo(repositoryRoot.FullName + Path.DirectorySeparatorChar) : repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(detections);
        Assert.Equal("{}", await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("opencode.json", false, false, false)]
    [InlineData("opencode.json", false, false, true)]
    [InlineData("opencode.json", false, true, false)]
    [InlineData("opencode.json", false, true, true)]
    [InlineData("opencode.json", true, false, false)]
    [InlineData("opencode.json", true, false, true)]
    [InlineData("opencode.json", true, true, false)]
    [InlineData("opencode.json", true, true, true)]
    [InlineData("opencode.jsonc", false, false, false)]
    [InlineData("opencode.jsonc", false, false, true)]
    [InlineData("opencode.jsonc", false, true, false)]
    [InlineData("opencode.jsonc", false, true, true)]
    [InlineData("opencode.jsonc", true, false, false)]
    [InlineData("opencode.jsonc", true, false, true)]
    [InlineData("opencode.jsonc", true, true, false)]
    [InlineData("opencode.jsonc", true, true, true)]
    public async Task ScanAsync_WithWorkingDirectoryOutsideRepository_OnlyUsesSelectedRoot(string fileName, bool workingDirectoryIsParent, bool hasTargetConfiguration, bool useOpenCodeDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        var unrelatedParent = workingDirectoryIsParent ? workspace.WorkspaceRoot : workspace.CreateDirectory("unrelated");
        var unrelatedConfigDirectory = useOpenCodeDirectory ? unrelatedParent.CreateSubdirectory(".opencode") : unrelatedParent;
        var unrelatedConfigPath = Path.Combine(unrelatedConfigDirectory.FullName, fileName);
        await File.WriteAllTextAsync(unrelatedConfigPath, "{}");
        var configPaths = new List<string> { unrelatedConfigPath };
        var workingDirectory = workingDirectoryIsParent ? unrelatedParent : unrelatedParent.CreateSubdirectory("nested");
        if (hasTargetConfiguration)
        {
            var targetConfigDirectory = useOpenCodeDirectory ? repositoryRoot.CreateSubdirectory(".opencode") : repositoryRoot;
            var targetConfigPath = Path.Combine(targetConfigDirectory.FullName, fileName);
            await File.WriteAllTextAsync(targetConfigPath, "{}");
            configPaths.Add(targetConfigPath);
        }
        var lastWriteTimes = configPaths.ToDictionary(static path => path, File.GetLastWriteTimeUtc);
        var entries = Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var scanner = CreateScanner(new TestAgentCliRunner());
        var context = CreateScanContext(workingDirectory, repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>(
            hasTargetConfiguration ? [new(AgentClientKind.OpenCode, null, false)] : [],
            detections);
        Assert.Equal(entries, Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories).Order());
        foreach (var (configPath, lastWriteTime) in lastWriteTimes)
        {
            Assert.Equal("{}", await File.ReadAllTextAsync(configPath));
            Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        }
    }

    [Theory]
    [InlineData("opencode.json")]
    [InlineData("opencode.jsonc")]
    public async Task ScanAsync_WithWorkspaceRootOverride_UsesSelectedRoot(string fileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var repositoryRoot = workspace.CreateDirectory("repo");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot.FullName, fileName), "{}");
        var workingDirectory = workspace.CreateDirectory("outside");
        var scanner = CreateScanner(new TestAgentCliRunner());
        var context = CreateScanContext(workingDirectory, repositoryRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.OpenCode, null, false)], detections);
    }

    [Theory]
    [InlineData("opencode.json", """{"mcp":{"aspire":{"command":["aspire","mcp","start"]}}}""")]
    [InlineData("opencode.json", "{ invalid json content")]
    [InlineData("opencode.jsonc", "{\n// Comment\n\"mcp\": {},\n}")]
    [InlineData("opencode.jsonc", "{ invalid json content")]
    [InlineData("opencode.jsonc", "")]
    [InlineData("opencode.jsonc", "[]")]
    [InlineData("opencode.jsonc", "null")]
    public async Task ScanAsync_LeavesExistingConfigurationUntouched(string fileName, string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, fileName);
        await File.WriteAllTextAsync(configPath, content);
        File.SetLastWriteTimeUtc(configPath, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWriteTime = File.GetLastWriteTimeUtc(configPath);
        var scanner = CreateScanner(new TestAgentCliRunner());
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        var detections = await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal<AgentClientDetection>([new(AgentClientKind.OpenCode, null, false)], detections);
        Assert.Equal(content, await File.ReadAllTextAsync(configPath));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(configPath));
        Assert.Equal([configPath], Directory.GetFileSystemEntries(workspace.WorkspaceRoot.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_DoesNotProbeOrWrite()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runner = new TestAgentCliRunner();
        var scanner = CreateScanner(runner);
        var context = CreateScanContext(workspace.WorkspaceRoot, workspace.WorkspaceRoot);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(context, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(runner.Commands);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.WorkspaceRoot.FullName));
    }

    private static OpenCodeAgentEnvironmentScanner CreateScanner(TestAgentCliRunner runner)
        => new(runner, NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);

    private static AgentEnvironmentScanContext CreateScanContext(DirectoryInfo workingDirectory, DirectoryInfo repositoryRoot)
        => new()
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = repositoryRoot
        };
}
