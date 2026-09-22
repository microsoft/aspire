// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentProjectDiscoveryTests(ITestOutputHelper output)
{
    private static readonly (string Client, string Path)[] s_markers =
    [
        ("claude", ".claude"),
        ("claude", ".mcp.json"),
        ("vscode", ".vscode"),
        ("opencode", "opencode.json"),
        ("opencode", "opencode.jsonc"),
        ("opencode", @".opencode\opencode.json"),
        ("opencode", @".opencode\opencode.jsonc")
    ];

    public static IEnumerable<object[]> MarkerLocations =>
        from marker in s_markers
        from flag in new[] { false, true }
        select new object[] { marker.Client, marker.Path, flag };

    public static IEnumerable<object[]> OutsideLocations =>
        from marker in s_markers
        from parent in new[] { false, true }
        from present in new[] { false, true }
        select new object[] { marker.Client, marker.Path, parent, present };

    public static IEnumerable<object[]> ExistingConfigurations =>
        from marker in s_markers
        from content in new[] { "{ invalid json", "", "[]", "null", "{/* JSONC */\"mcpServers\":{\"aspire\":{\"command\":\"aspire\",\"args\":[\"mcp\",\"start\"]}},}" }
        select new object[] { marker.Client, marker.Path, content };

    [Theory]
    [MemberData(nameof(MarkerLocations))]
    public async Task ScanAsync_ProjectMarkersAreReadOnly(string clientId, string marker, bool inParent)
    {
        using var context = new AgentConfigurationTestContext(output);
        var working = context.Project.CreateSubdirectory("nested");
        await CreateMarkerAsync(inParent ? context.Project : working, marker);
        var client = context.Environments.Single(client => client.Id == clientId);
        var entries = Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var scanContext = new AgentEnvironmentScanContext(working, context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExpectedDetection(clientId), Assert.Single(scanContext.DetectedClients));
        Assert.Equal(clientId == "vscode" ? [] : new[] { clientId == "opencode" ? "opencode" : "claude" }, context.CliRunner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [MemberData(nameof(MarkerLocations))]
    public async Task ScanAsync_DoesNotSearchAboveWorkspace(string clientId, string marker, bool trailingSeparator)
    {
        using var context = new AgentConfigurationTestContext(output);
        await CreateMarkerAsync(context.Workspace.WorkspaceRoot, marker);
        var working = context.Project.CreateSubdirectory("nested");
        var root = trailingSeparator ? new DirectoryInfo(context.Project.FullName + Path.DirectorySeparatorChar) : context.Project;
        var client = context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(working, root);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Empty(scanContext.DetectedClients);
    }

    [Theory]
    [MemberData(nameof(OutsideLocations))]
    public async Task ScanAsync_OutsideWorkingDirectoryUsesSelectedWorkspace(string clientId, string marker, bool workingDirectoryIsParent, bool hasTargetConfiguration)
    {
        using var context = new AgentConfigurationTestContext(output);
        var outside = workingDirectoryIsParent ? context.Workspace.WorkspaceRoot : context.Workspace.CreateDirectory("outside");
        await CreateMarkerAsync(outside, marker);
        var working = workingDirectoryIsParent ? outside : outside.CreateSubdirectory("nested");
        if (hasTargetConfiguration)
        {
            await CreateMarkerAsync(context.Project, marker);
        }
        var client = context.Environments.Single(client => client.Id == clientId);
        var entries = Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var scanContext = new AgentEnvironmentScanContext(working, context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(hasTargetConfiguration ? [ExpectedDetection(clientId)] : [], scanContext.DetectedClients);
        Assert.Equal(entries, Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("vscode", ".vscode")]
    public async Task ScanAsync_HomeDirectoriesAreNotProjectEvidence(string clientId, string marker)
    {
        using var context = new AgentConfigurationTestContext(output);
        await CreateMarkerAsync(context.Home, marker);
        var working = context.Home.CreateSubdirectory("nested");
        var client = context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(working, context.Home);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Empty(scanContext.DetectedClients);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    [InlineData("vscode")]
    [InlineData("opencode")]
    public async Task ScanAsync_NoEvidenceIsReadOnlyAndCancellationDoesNotProbe(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        var client = context.Environments.Single(client => client.Id == clientId);
        var scanContext = new AgentEnvironmentScanContext(context.Project, context.Project);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ScanAsync(scanContext, new CancellationToken(canceled: true))).DefaultTimeout();
        Assert.Empty(context.CliRunner.Commands);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Empty(scanContext.DetectedClients);
        Assert.Empty(context.Project.EnumerateFileSystemInfos());
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [MemberData(nameof(ExistingConfigurations))]
    public async Task ScanAsync_ExistingConfigurationBytesAndTimestampsAreUntouched(string clientId, string marker, string content)
    {
        using var context = new AgentConfigurationTestContext(output);
        var file = Path.Combine(context.Project.FullName, marker.Replace('\\', Path.DirectorySeparatorChar),
            marker is ".claude" or ".vscode" ? "settings.json" : "");
        await AgentConfigurationTestContext.WriteAsync(file, content);
        File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(file);
        var entries = Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var client = context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(context.Project, context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExpectedDetection(clientId), Assert.Single(scanContext.DetectedClients));
        Assert.Equal(content, await File.ReadAllTextAsync(file));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
        Assert.Equal(entries, Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    private static AgentClientDetection ExpectedDetection(string clientId)
        => new(clientId switch
        {
            "claude" => AgentClientKind.ClaudeCode,
            "vscode" => AgentClientKind.VsCode,
            "opencode" => AgentClientKind.OpenCode,
            _ => throw new ArgumentOutOfRangeException(nameof(clientId))
        }, null, false);

    private static Task CreateMarkerAsync(DirectoryInfo root, string marker)
    {
        var path = Path.Combine(root.FullName, marker.Replace('\\', Path.DirectorySeparatorChar));
        if (marker is ".claude" or ".vscode")
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        return AgentConfigurationTestContext.WriteAsync(path, "{}");
    }
}
