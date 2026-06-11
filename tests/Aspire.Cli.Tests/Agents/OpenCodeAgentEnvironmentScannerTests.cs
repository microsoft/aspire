// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeAgentEnvironmentScannerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ApplyAsync_WithMalformedOpenCodeJsonc_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a malformed opencode.jsonc at the workspace root
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc");
        await File.WriteAllTextAsync(configPath, "{ invalid json content");

        var openCodeCliRunner = new FakeOpenCodeCliRunner(new SemVersion(1, 0, 0));
        var scanner = new OpenCodeAgentEnvironmentScanner(openCodeCliRunner, CreatePlaywrightCliInstaller(), NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        // The scan should succeed (HasServerConfigured catches JsonException)
        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        // Applying should throw with a descriptive message
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(configPath, ex.Message);
        Assert.Contains("malformed JSON", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyOpenCodeJsonc_ThrowsInvalidOperationException()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create an empty opencode.jsonc
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc");
        await File.WriteAllTextAsync(configPath, "");

        var openCodeCliRunner = new FakeOpenCodeCliRunner(new SemVersion(1, 0, 0));
        var scanner = new OpenCodeAgentEnvironmentScanner(openCodeCliRunner, CreatePlaywrightCliInstaller(), NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Contains(configPath, ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WithMalformedOpenCodeJsonc_DoesNotOverwriteFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a malformed opencode.jsonc with content the user may want to preserve
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc");
        var originalContent = "{ \"mcp\": { \"my-server\": { \"command\": [\"test\"] } }";
        await File.WriteAllTextAsync(configPath, originalContent);

        var openCodeCliRunner = new FakeOpenCodeCliRunner(new SemVersion(1, 0, 0));
        var scanner = new OpenCodeAgentEnvironmentScanner(openCodeCliRunner, CreatePlaywrightCliInstaller(), NullLogger<OpenCodeAgentEnvironmentScanner>.Instance);
        var context = CreateScanContext(workspace.WorkspaceRoot);

        await scanner.ScanAsync(context, CancellationToken.None).DefaultTimeout();

        Assert.NotEmpty(context.Applicators);
        var aspireApplicator = context.Applicators.First(a => a.Description.Contains("Aspire MCP"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => aspireApplicator.ApplyAsync(CancellationToken.None)).DefaultTimeout();

        // The original file content should be preserved
        var currentContent = await File.ReadAllTextAsync(configPath);
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

    private sealed class FakeOpenCodeCliRunner(SemVersion? version) : IOpenCodeCliRunner
    {
        public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(version);
        }
    }
}
