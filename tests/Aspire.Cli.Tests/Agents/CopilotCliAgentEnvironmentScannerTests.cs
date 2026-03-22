// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.CopilotCli;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class CopilotCliAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ScanAsync_WhenCopilotCliInstalled_ReturnsApplicator()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // Scanner adds applicators for: Aspire MCP, Playwright CLI, and agent instructions
        Assert.NotEmpty(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("GitHub Copilot CLI"));
    }

    [Fact]
    public async Task ApplyAsync_CreatesMcpConfigJsonWithCorrectConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        
        // Create a temporary .copilot folder in the workspace to avoid modifying the user's home directory
        var copilotFolder = workspace.CreateDirectory(".copilot");
        
        // Create a scanner that writes to a known test location
        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();
        
        // Scanner adds applicators for: Aspire MCP, Playwright CLI, and agent instructions
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));
        
        await aspireApplicator.ApplyAsync(CancellationToken.None).DefaultTimeout();

        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        Assert.True(File.Exists(mcpConfigPath));

        var content = await File.ReadAllTextAsync(mcpConfigPath);
        var config = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(config);
        Assert.True(config.ContainsKey("mcpServers"));

        var servers = config["mcpServers"]?.AsObject();
        Assert.NotNull(servers);
        Assert.True(servers.ContainsKey("aspire"));

        var aspireServer = servers["aspire"]?.AsObject();
        Assert.NotNull(aspireServer);
        Assert.Equal("local", aspireServer["type"]?.GetValue<string>());
        Assert.Equal("aspire", aspireServer["command"]?.GetValue<string>());

        var args = aspireServer["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Equal(2, args.Count);
        Assert.Equal("agent", args[0]?.GetValue<string>());
        Assert.Equal("mcp", args[1]?.GetValue<string>());

        // Verify env contains DOTNET_ROOT
        var env = aspireServer["env"]?.AsObject();
        Assert.NotNull(env);
        Assert.True(env.ContainsKey("DOTNET_ROOT"));
        Assert.Equal("${DOTNET_ROOT}", env["DOTNET_ROOT"]?.GetValue<string>());

        // Verify tools contains "*"
        var tools = aspireServer["tools"]?.AsArray();
        Assert.NotNull(tools);
        Assert.Single(tools);
        Assert.Equal("*", tools[0]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyAsync_PreservesExistingMcpConfigContent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");
        
        // Create an existing mcp-config.json with another server
        var existingConfig = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["command"] = "other"
                }
            }
        };
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, existingConfig.ToJsonString());

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();
        await context.Applicators[0].ApplyAsync(CancellationToken.None).DefaultTimeout();

        var content = await File.ReadAllTextAsync(mcpConfigPath);
        var config = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(config);

        var servers = config["mcpServers"]?.AsObject();
        Assert.NotNull(servers);
        
        // Both servers should exist
        Assert.True(servers.ContainsKey("other-server"));
        Assert.True(servers.ContainsKey("aspire"));
    }

    [Fact]
    public async Task ScanAsync_WhenAspireAlreadyConfigured_ReturnsPlaywrightCliApplicatorOnly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");
        
        // Create an existing mcp-config.json with aspire already configured
        var existingConfig = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["aspire"] = new JsonObject
                {
                    ["command"] = "aspire"
                }
            }
        };
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, existingConfig.ToJsonString());
        
        // Also create the skill file with the SAME content as SkillFileContent to prevent update applicator
        var skillFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "skills", "aspire", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFilePath)!);
        await File.WriteAllTextAsync(skillFilePath, CommonAgentApplicators.SkillFileContent);

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // Only the Playwright CLI applicator should be offered (Aspire MCP is configured, skill file is up to date)
        Assert.Single(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("Playwright CLI"));
    }

    [Fact]
    public async Task ScanAsync_WhenInVSCode_ReturnsApplicatorWithoutCallingRunner()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotCliRunner = new FakeCopilotCliRunner(null); // Return null to verify it's not called
        var executionContext = CreateExecutionContextWithVSCode(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // Scanner adds applicators for: Aspire MCP, Playwright CLI, and agent instructions
        Assert.NotEmpty(context.Applicators);
        Assert.Contains(context.Applicators, a => a.Description.Contains("GitHub Copilot"));
        Assert.False(copilotCliRunner.WasCalled); // Verify GetVersionAsync was not called
    }

    private static AgentEnvironmentScanContext CreateScanContext(
        DirectoryInfo workingDirectory)
    {
        return new AgentEnvironmentScanContext
        {
            WorkingDirectory = workingDirectory,
            RepositoryRoot = workingDirectory
        };
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        return new CliExecutionContext(
            workingDirectory: workingDirectory,
            hivesDirectory: workingDirectory,
            cacheDirectory: workingDirectory,
            sdksDirectory: workingDirectory,
            logsDirectory: workingDirectory,
            logFilePath: "test.log",
            debugMode: false,
            environmentVariables: new Dictionary<string, string?>(),
            homeDirectory: workingDirectory);
    }

    private static CliExecutionContext CreateExecutionContextWithVSCode(DirectoryInfo workingDirectory)
    {
        var environmentVariables = new Dictionary<string, string?>
        {
            ["TERM_PROGRAM"] = "vscode"
        };
        
        return new CliExecutionContext(
            workingDirectory: workingDirectory,
            hivesDirectory: workingDirectory,
            cacheDirectory: workingDirectory,
            sdksDirectory: workingDirectory,
            logsDirectory: workingDirectory,
            logFilePath: "test.log",
            debugMode: false,
            environmentVariables: environmentVariables,
            homeDirectory: workingDirectory);
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create a malformed mcp-config.json
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, "{ invalid json content");

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // The scan should succeed (HasServerConfigured catches JsonException)
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        // Applying should throw with a descriptive message
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(mcpConfigPath, ex.Message);
        Assert.Contains("malformed JSON", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyMcpJson_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create an empty mcp-config.json
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, "");

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(mcpConfigPath, ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_DoesNotOverwriteFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create a malformed mcp-config.json with content the user may want to preserve
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        var originalContent = "{ \"mcpServers\": { \"my-server\": { \"command\": \"test\" } }";
        await File.WriteAllTextAsync(mcpConfigPath, originalContent);

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotCliAgentEnvironmentScanner(copilotCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<CopilotCliAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();

        // The original file content should be preserved
        var currentContent = await File.ReadAllTextAsync(mcpConfigPath);
        Assert.Equal(originalContent, currentContent);
    }

    /// <summary>
    /// A fake implementation of <see cref="ICopilotCliRunner"/> for testing.
    /// </summary>
    private sealed class FakeCopilotCliRunner(SemVersion? version) : ICopilotCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(version);
        }
    }

    private static PlaywrightCliInstaller CreatePlaywrightCliInstaller()
    {
        return new PlaywrightCliInstaller(
            new FakeNpmRunner(),
            new FakeNpmProvenanceChecker(),
            new FakePlaywrightCliRunner(),
            new TestInteractionService(),
            new ConfigurationBuilder().Build(),
            NullLogger<PlaywrightCliInstaller>.Instance);
    }
}
