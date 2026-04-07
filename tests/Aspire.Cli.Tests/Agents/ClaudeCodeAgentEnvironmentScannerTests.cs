// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class ClaudeCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".claude");

        // Create a malformed .mcp.json at the workspace root
        var mcpJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        await File.WriteAllTextAsync(mcpJsonPath, "{ invalid json content");

        var claudeCodeCliRunner = new FakeClaudeCodeCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new ClaudeCodeAgentEnvironmentScanner(claudeCodeCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // The scan should succeed (HasServerConfigured catches JsonException)
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        // Applying should throw with a descriptive message
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(mcpJsonPath, ex.Message);
        Assert.Contains("malformed JSON", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyMcpJson_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".claude");

        // Create an empty .mcp.json
        var mcpJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        await File.WriteAllTextAsync(mcpJsonPath, "");

        var claudeCodeCliRunner = new FakeClaudeCodeCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new ClaudeCodeAgentEnvironmentScanner(claudeCodeCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(mcpJsonPath, ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedMcpJson_DoesNotOverwriteFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        workspace.CreateDirectory(".claude");

        // Create a malformed .mcp.json with content the user may want to preserve
        var mcpJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        var originalContent = "{ \"mcpServers\": { \"my-server\": { \"command\": \"test\" } }";
        await File.WriteAllTextAsync(mcpJsonPath, originalContent);

        var claudeCodeCliRunner = new FakeClaudeCodeCliRunner(new SemVersion(1, 0, 0));
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var scanner = new ClaudeCodeAgentEnvironmentScanner(claudeCodeCliRunner, CreatePlaywrightCliInstaller(), executionContext, NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();

        // The original file content should be preserved
        var currentContent = await File.ReadAllTextAsync(mcpJsonPath);
        Assert.Equal(originalContent, currentContent);
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

    private sealed class FakeClaudeCodeCliRunner(SemVersion? version) : IClaudeCodeCliRunner
    {
        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(version);
        }
    }
}
