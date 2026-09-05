// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class CopilotAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ScanAsync_WhenCopilotCliInstalled_ReturnsApplicator()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentClientKind.CopilotCli], context.DetectedClients);
        var applicator = Assert.Single(context.Applicators);
        Assert.Contains("GitHub Copilot", applicator.Description);
    }

    [Fact]
    public async Task ApplyAsync_CreatesMcpConfigJsonWithCorrectConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        
        // Create a temporary .copilot folder in the workspace to avoid modifying the user's home directory
        var copilotFolder = workspace.CreateDirectory(".copilot");
        
        // Create a scanner that writes to a known test location
        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();
        
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
    public async Task ApplyAsync_HonorsCopilotHomeEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotHome = workspace.CreateDirectory("custom-copilot");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = copilotHome.FullName,
        });
        var scanner = new CopilotAgentEnvironmentScanner(new FakeCopilotCliRunner(new SemVersion(1, 0, 0)), new FakeCopilotAppInstallationDetector(), CreateExecutionContext(workspace.WorkspaceRoot), environment, NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();
        await context.Applicators.First(applicator => applicator.Description.Contains("Aspire MCP")).ApplyAsync(CancellationToken.None).DefaultTimeout();

        Assert.True(File.Exists(Path.Combine(copilotHome.FullName, "mcp-config.json")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".copilot")));
    }

    [Fact]
    public async Task ApplyAsync_PreservesExistingMcpConfigContent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
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
    public async Task ScanAsync_WhenAspireAlreadyConfigured_DetectsClientWithoutApplicators()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
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

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentClientKind.CopilotCli], context.DetectedClients);
        Assert.Empty(context.Applicators);
    }

    [Fact]
    public async Task ScanAsync_WhenInVSCode_ReturnsApplicatorWithoutCallingRunner()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotCliRunner = new FakeCopilotCliRunner(null); // Return null to verify it's not called
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var vsCodeEnvironment = new TestEnvironment(new Dictionary<string, string?> { ["TERM_PROGRAM"] = "vscode" });
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, vsCodeEnvironment, NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentClientKind.CopilotCli], context.DetectedClients);
        var applicator = Assert.Single(context.Applicators);
        Assert.Contains("GitHub Copilot", applicator.Description);
        Assert.False(copilotCliRunner.WasCalled); // Verify GetVersionAsync was not called
    }

    [Fact]
    public async Task ScanAsync_WhenOnlyCopilotAppIsInstalled_DetectsAppAndReturnsMcpApplicator()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotCliRunner = new FakeCopilotCliRunner(null);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector("AI_AGENT"), CreateExecutionContext(workspace.WorkspaceRoot), new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal([AgentClientKind.CopilotApp], context.DetectedClients);
        var applicator = Assert.Single(context.Applicators);
        Assert.Contains("Aspire MCP", applicator.Description);
    }

    [Fact]
    public async Task ScanAsync_WhenCopilotAppAndCliAreInstalled_DetectsBothAndReturnsMcpApplicatorOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner = new CopilotAgentEnvironmentScanner(new FakeCopilotCliRunner(new SemVersion(1, 0, 0)), new FakeCopilotAppInstallationDetector("AI_AGENT"), CreateExecutionContext(workspace.WorkspaceRoot), new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Equal(
            [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp],
            context.DetectedClients.OrderBy(static client => client));
        var applicator = Assert.Single(context.Applicators);
        Assert.Contains("Aspire MCP", applicator.Description);
    }

    [Fact]
    public async Task ScanAsync_WhenNeitherCopilotClientIsInstalled_DoesNotReturnApplicators()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var scanner = new CopilotAgentEnvironmentScanner(new FakeCopilotCliRunner(null), new FakeCopilotAppInstallationDetector(), CreateExecutionContext(workspace.WorkspaceRoot), new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.Empty(context.DetectedClients);
        Assert.Empty(context.Applicators);
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
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            debugMode: false,
            homeDirectory: workingDirectory);
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create a malformed mcp-config.json
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, "{ invalid json content");

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create an empty mcp-config.json
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, "");

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(mcpConfigPath, ex.Message);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task ApplyAsync_WithNonObjectMcpJson_ThrowsInvalidOperationExceptionAndDoesNotOverwriteFile(string originalContent)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        await File.WriteAllTextAsync(mcpConfigPath, originalContent);
        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();
        var aspireApplicator = context.Applicators.First(applicator => applicator.Description.Contains("Aspire MCP"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, ErrorStrings.ConfigurationFileMustBeJsonObject, mcpConfigPath),
            ex.Message);
        Assert.Null(ex.InnerException);
        Assert.Equal(originalContent, await File.ReadAllTextAsync(mcpConfigPath));
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_DoesNotOverwriteFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilotFolder = workspace.CreateDirectory(".copilot");

        // Create a malformed mcp-config.json with content the user may want to preserve
        var mcpConfigPath = Path.Combine(copilotFolder.FullName, "mcp-config.json");
        var originalContent = "{ \"mcpServers\": { \"my-server\": { \"command\": \"test\" } }";
        await File.WriteAllTextAsync(mcpConfigPath, originalContent);

        var copilotCliRunner = new FakeCopilotCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new CopilotAgentEnvironmentScanner(copilotCliRunner, new FakeCopilotAppInstallationDetector(), executionContext, new TestEnvironment(), NullLogger<CopilotAgentEnvironmentScanner>.Instance);
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

    private sealed class FakeCopilotAppInstallationDetector(string? marker = null) : ICopilotAppInstallationDetector
    {
        public string? GetInstallationMarker() => marker;
    }

    private sealed class FakeCopilotCliRunner(SemVersion? version) : ICopilotCliRunner
    {
        public bool WasCalled { get; private set; }

        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(version);
        }
    }

}
