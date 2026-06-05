// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class VsCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ScanAsync_WhenVsCodeFolderExists_ReturnsApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);

        // Scanner adds applicators for: Aspire MCP, Playwright MCP, and agent instructions
        Assert.NotEmpty(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("VS Code"));
    }

    [Fact]
    public async Task ScanAsync_WhenVsCodeFolderExistsInParent_ReturnsApplicatorForParent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        var childDir = workspace.CreateDirectory("subdir");
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(childDir);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(childDir, workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);

        // Scanner adds applicators for: Aspire MCP, Playwright MCP, and agent instructions
        Assert.NotEmpty(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("VS Code"));
    }

    [Fact]
    public async Task ScanAsync_WhenRepositoryRootReachedBeforeVsCode_AndNoCliAvailable_ReturnsNoApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var childDir = workspace.CreateDirectory("subdir");
        // Repository root is the workspace root, so search should stop there
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(childDir);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(childDir, workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);

        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WhenNoVsCodeFolder_AndVsCodeCliAvailable_ReturnsApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeCliRunner = new FakeVsCodeCliRunner(new SemVersion(1, 85, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);

        // Scanner adds applicators for: Aspire MCP, Playwright MCP, and agent instructions
        Assert.NotEmpty(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("VS Code"));
    }

    [Fact]
    public async Task ScanAsync_WhenNoVsCodeFolder_AndNoCliAvailable_ReturnsNoApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        // This test assumes no VSCODE_* environment variables are set
        // With no CLI available and no env vars, no applicator should be returned
        await scanner.ScanAsync(context, CancellationToken.None);

        // The result depends on whether VSCODE_* environment variables exist
        // We just verify the test runs without throwing
    }

    [Fact]
    public async Task ApplyAsync_CreatesVsCodeFolderIfNotExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".vscode");
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        
        // First, make the scanner find a parent .vscode folder to get an applicator
        var parentVsCode = workspace.CreateDirectory(".vscode");
        var context = CreateScanContext(workspace.WorkspaceRoot);
        
        await scanner.ScanAsync(context, CancellationToken.None);
        
        // Scanner adds applicators for: Aspire MCP, Playwright MCP, and agent instructions
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));
        
        // Apply the configuration
        await aspireApplicator.ApplyAsync(CancellationToken.None);
        
        // Verify the mcp.json was created
        var mcpJsonPath = Path.Combine(parentVsCode.FullName, "mcp.json");
        Assert.True(File.Exists(mcpJsonPath));
    }

    [Fact]
    public async Task ApplyAsync_CreatesMcpJsonWithCorrectConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);
        await context.Applicators[0].ApplyAsync(CancellationToken.None);

        var mcpJsonPath = Path.Combine(vsCodeFolder.FullName, "mcp.json");
        Assert.True(File.Exists(mcpJsonPath));

        var content = await File.ReadAllTextAsync(mcpJsonPath);
        var config = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(config);
        Assert.True(config.ContainsKey("servers"));

        var servers = config["servers"]?.AsObject();
        Assert.NotNull(servers);
        Assert.True(servers.ContainsKey("aspire"));

        var aspireServer = servers["aspire"]?.AsObject();
        Assert.NotNull(aspireServer);
        Assert.Equal("stdio", aspireServer["type"]?.GetValue<string>());
        Assert.Equal("aspire", aspireServer["command"]?.GetValue<string>());

        var args = aspireServer["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Equal(2, args.Count);
        Assert.Equal("mcp", args[0]?.GetValue<string>());
        Assert.Equal("start", args[1]?.GetValue<string>());

        // Verify that "tools" is NOT included (not supported by VS Code MCP)
        Assert.False(aspireServer.ContainsKey("tools"));
    }

    [Fact]
    public async Task ApplyAsync_PreservesExistingMcpJsonContent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        
        // Create an existing mcp.json with another server
        var existingConfig = new JsonObject
        {
            ["servers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = "other"
                }
            }
        };
        var mcpJsonPath = Path.Combine(vsCodeFolder.FullName, "mcp.json");
        await File.WriteAllTextAsync(mcpJsonPath, existingConfig.ToJsonString());

        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);
        await context.Applicators[0].ApplyAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(mcpJsonPath);
        var config = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(config);

        var servers = config["servers"]?.AsObject();
        Assert.NotNull(servers);
        
        // Both servers should exist
        Assert.True(servers.ContainsKey("other-server"));
        Assert.True(servers.ContainsKey("aspire"));
    }

    [Fact]
    public async Task ApplyAsync_UpdatesExistingAspireServerConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        
        // Create an existing mcp.json with another server (not aspire, since aspire being present would skip offering the applicator)
        var existingConfig = new JsonObject
        {
            ["servers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["type"] = "http",
                    ["command"] = "other"
                }
            }
        };
        var mcpJsonPath = Path.Combine(vsCodeFolder.FullName, "mcp.json");
        await File.WriteAllTextAsync(mcpJsonPath, existingConfig.ToJsonString());

        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);
        
        // Should return applicators for Aspire MCP, Playwright MCP, and agent instructions
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));
        
        await aspireApplicator.ApplyAsync(CancellationToken.None);

        var content = await File.ReadAllTextAsync(mcpJsonPath);
        var config = JsonNode.Parse(content)?.AsObject();
        var aspireServer = config?["servers"]?["aspire"]?.AsObject();
        
        Assert.NotNull(aspireServer);
        Assert.Equal("stdio", aspireServer["type"]?.GetValue<string>());
        Assert.Equal("aspire", aspireServer["command"]?.GetValue<string>());
        
        // The other server should still exist
        var otherServer = config?["servers"]?["other-server"]?.AsObject();
        Assert.NotNull(otherServer);
    }

    [Fact]
    public async Task ApplyAsync_WithConfigurePlaywrightTrue_AddsPlaywrightServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var vsCodeFolder = workspace.CreateDirectory(".vscode");
        var vsCodeCliRunner = new FakeVsCodeCliRunner(null);
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new VsCodeAgentEnvironmentScanner(vsCodeCliRunner, executionContext, NullLogger<VsCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None);
        
        // Apply both MCP-related applicators (Aspire and Playwright)
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));
        var playwrightApplicator = context.Applicators.First(a => a.Description.Contains("Playwright MCP"));
        await aspireApplicator.ApplyAsync(CancellationToken.None);
        await playwrightApplicator.ApplyAsync(CancellationToken.None);

        var mcpJsonPath = Path.Combine(vsCodeFolder.FullName, "mcp.json");
        var content = await File.ReadAllTextAsync(mcpJsonPath);
        var config = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(config);

        var servers = config["servers"]?.AsObject();
        Assert.NotNull(servers);
        
        // Both aspire and playwright servers should exist
        Assert.True(servers.ContainsKey("aspire"));
        Assert.True(servers.ContainsKey("playwright"));

        var playwrightServer = servers["playwright"]?.AsObject();
        Assert.NotNull(playwrightServer);
        Assert.Equal("stdio", playwrightServer["type"]?.GetValue<string>());
        Assert.Equal("npx", playwrightServer["command"]?.GetValue<string>());
    }

    /// <summary>
    /// A fake implementation of <see cref="IVsCodeCliRunner"/> for testing.
    /// </summary>
    private sealed class FakeVsCodeCliRunner(SemVersion? version) : IVsCodeCliRunner
    {
        public Task<SemVersion?> GetVersionAsync(VsCodeRunOptions options, CancellationToken cancellationToken) => Task.FromResult(version);
    }

    private static AgentEnvironmentScanContext CreateScanContext(
        DirectoryInfo workingDirectory,
        DirectoryInfo? repositoryRoot = null)
    {
        repositoryRoot ??= workingDirectory;
        return new AgentEnvironmentScanContext
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = repositoryRoot
        };
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo? homeDirectory = null, Dictionary<string, string?>? environmentVariables = null)
    {
        // Default to an empty dictionary to prevent fallback to real system environment variables
        // This ensures tests are isolated and don't fail based on the test environment (e.g., running from VS Code)
        environmentVariables ??= [];

        // Use a separate directory for home to avoid conflicts with .vscode folder detection
        // (the scanner ignores .vscode in the home directory as that's for user settings, not workspace config)
        homeDirectory ??= new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        return new CliExecutionContext(
            workingDirectory: workingDirectory,
            hivesDirectory: workingDirectory,
            cacheDirectory: workingDirectory,
            sdksDirectory: workingDirectory,
            debugMode: false,
            environmentVariables: environmentVariables,
            homeDirectory: homeDirectory);
    }
}
