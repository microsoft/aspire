// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class AgentProjectDiscoveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

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
        var working = _context.Project.CreateSubdirectory("nested");
        await CreateMarkerAsync(inParent ? _context.Project : working, marker);
        var client = _context.Environments.Single(client => client.Id == clientId);
        var entries = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var scanContext = new AgentEnvironmentScanContext(working, _context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExpectedDetection(clientId), Assert.Single(scanContext.DetectedClients));
        Assert.Equal(clientId == "vscode" ? [] : new[] { clientId == "opencode" ? "opencode" : "claude" }, _context.CliRunner.Commands);
        Assert.Equal(entries, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [MemberData(nameof(MarkerLocations))]
    public async Task ScanAsync_DoesNotSearchAboveWorkspace(string clientId, string marker, bool trailingSeparator)
    {
        await CreateMarkerAsync(_context.Workspace.WorkspaceRoot, marker);
        var working = _context.Project.CreateSubdirectory("nested");
        var root = trailingSeparator ? new DirectoryInfo(_context.Project.FullName + Path.DirectorySeparatorChar) : _context.Project;
        var client = _context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(working, root);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Empty(scanContext.DetectedClients);
    }

    [Theory]
    [MemberData(nameof(OutsideLocations))]
    public async Task ScanAsync_OutsideWorkingDirectoryUsesSelectedWorkspace(string clientId, string marker, bool workingDirectoryIsParent, bool hasTargetConfiguration)
    {
        var outside = workingDirectoryIsParent ? _context.Workspace.WorkspaceRoot : _context.Workspace.CreateDirectory("outside");
        await CreateMarkerAsync(outside, marker);
        var working = workingDirectoryIsParent ? outside : outside.CreateSubdirectory("nested");
        if (hasTargetConfiguration)
        {
            await CreateMarkerAsync(_context.Project, marker);
        }
        var client = _context.Environments.Single(client => client.Id == clientId);
        var entries = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var scanContext = new AgentEnvironmentScanContext(working, _context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(hasTargetConfiguration ? [ExpectedDetection(clientId)] : [], scanContext.DetectedClients);
        Assert.Equal(entries, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
    }

    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("vscode", ".vscode")]
    public async Task ScanAsync_HomeDirectoriesAreNotProjectEvidence(string clientId, string marker)
    {
        await CreateMarkerAsync(_context.Home, marker);
        var working = _context.Home.CreateSubdirectory("nested");
        var client = _context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(working, _context.Home);
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
        var client = _context.Environments.Single(client => client.Id == clientId);
        var scanContext = new AgentEnvironmentScanContext(_context.Project, _context.Project);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ScanAsync(scanContext, new CancellationToken(canceled: true))).DefaultTimeout();
        Assert.Empty(_context.CliRunner.Commands);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Empty(scanContext.DetectedClients);
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [MemberData(nameof(ExistingConfigurations))]
    public async Task ScanAsync_ExistingConfigurationBytesAndTimestampsAreUntouched(string clientId, string marker, string content)
    {
        var file = Path.Combine(_context.Project.FullName, marker.Replace('\\', Path.DirectorySeparatorChar),
            marker is ".claude" or ".vscode" ? "settings.json" : "");
        await AgentConfigurationTestContext.WriteAsync(file, content);
        File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(file);
        var entries = Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var client = _context.Environments.Single(client => client.Id == clientId);

        var scanContext = new AgentEnvironmentScanContext(_context.Project, _context.Project);
        await client.ScanAsync(scanContext, CancellationToken.None).DefaultTimeout();

        Assert.Equal(ExpectedDetection(clientId), Assert.Single(scanContext.DetectedClients));
        Assert.Equal(content, await File.ReadAllTextAsync(file));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
        Assert.Equal(entries, Directory.GetFileSystemEntries(_context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
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
    public void Dispose() => _context.Dispose();

}
